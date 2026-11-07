using System.Collections.Concurrent;
using Packmoat.Domain;

namespace Packmoat.Application.Tests;

/// <summary>
/// #136: the audit log is off the request path. An append queues and returns; one background writer stores batches. A full queue or a
/// failed write loses audit rows (counted, logged without any token, package or version), never a request. Each test that kills one of
/// the plan's mutations says which.
/// </summary>
public class QueuedAuditLogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private const string SecretPackage = "Secret.Package.Name";
    private const string SecretVersion = "6.6.6-leak";
    private const string SecretToken = "tok_leak_0123";

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = T0;
    }

    private sealed class Store : IAuditBatchLog
    {
        public ConcurrentQueue<IReadOnlyList<AuditEvent>> Batches { get; } = new();
        public Exception? Throw { get; set; }
        public TaskCompletionSource? Hold { get; set; }
        public int Calls;

        public async Task AppendBatchAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct)
        {
            var copy = events.ToList();   // the caller may reuse its list
            Interlocked.Increment(ref Calls);
            if (Hold is { } h) await h.Task;
            if (Throw is { } e) throw e;
            Batches.Enqueue(copy);
        }

        public List<AuditEvent> Stored => Batches.SelectMany(b => b).ToList();
    }

    private sealed class Warnings
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public void Add(string m) => Messages.Enqueue(m);
    }

    private static AuditEvent Download(int i = 0, Decision d = Decision.Allow) =>
        new(T0, "req-" + i, "acme", SecretToken, AuditKind.Download, SecretPackage, SecretVersion + "." + i, d,
            d == Decision.Allow ? VerdictReason.CooldownElapsed : VerdictReason.Cooldown, null);

    /// <summary>The append must be finished when it returns. Never awaited, so a log that waits for the store fails here instead of hanging.</summary>
    private static void Append(QueuedAuditLog log, AuditEvent e) =>
        Assert.True(log.AppendAsync(e, default).IsCompletedSuccessfully, "the append waited for, or failed with, the store");

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "timed out waiting");
    }

    private static void AssertNothingIdentifying(IEnumerable<string> messages)
    {
        foreach (var m in messages)
        {
            Assert.DoesNotContain(SecretPackage, m, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SecretVersion, m, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SecretToken, m, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("req-", m, StringComparison.Ordinal);
        }
    }

    [Fact]   // mutation: the append awaits the store again
    public async Task An_append_returns_at_once_while_the_store_hangs()
    {
        var store = new Store { Hold = new TaskCompletionSource() };
        using var log = new QueuedAuditLog(store, new Clock());

        for (var i = 0; i < 3; i++)
            Assert.True(log.AppendAsync(Download(i), default).IsCompletedSuccessfully);

        store.Hold.SetResult();
        Assert.True(await log.DrainAsync(Wait));
        Assert.Equal(new[] { "req-0", "req-1", "req-2" }, store.Stored.Select(e => e.RequestId));
    }

    [Fact]   // mutation: the append awaits the store again (the store's exception reaches the caller)
    public async Task An_append_never_throws_when_the_store_fails_and_the_loss_is_counted()
    {
        var store = new Store { Throw = new IOException("disk full at " + SecretPackage) };
        var warnings = new Warnings();
        using var log = new QueuedAuditLog(store, new Clock(), warnings.Add);

        var append = log.AppendAsync(Download(), default);
        Assert.True(append.IsCompletedSuccessfully);
        await append;

        Assert.True(await log.DrainAsync(Wait));
        Assert.Equal((1L, 1L, 0L), (log.FailedWrites, log.LostEvents, log.DroppedEvents));
        var message = Assert.Single(warnings.Messages);
        Assert.Contains("IOException", message);
        Assert.Contains("1 download record(s) lost", message);
        AssertNothingIdentifying(warnings.Messages);   // not even the exception's message, which here names the package
    }

    [Fact]   // mutations: drops not counted; the package, version or token in the log
    public async Task A_full_queue_drops_new_events_counts_them_and_says_so_without_naming_a_package()
    {
        var store = new Store { Hold = new TaskCompletionSource() };
        var warnings = new Warnings();
        using var log = new QueuedAuditLog(store, new Clock(), warnings.Add, capacity: 2);

        Append(log, Download(0));
        await Until(() => Volatile.Read(ref store.Calls) == 1);      // the writer has taken event 0 and hangs in the store
        for (var i = 1; i <= 5; i++) Append(log, Download(i, Decision.Hide));   // 1 and 2 fit; 3, 4, 5 do not

        Assert.Equal(3, log.DroppedEvents);
        var message = Assert.Single(warnings.Messages);               // once per window, not once per drop
        Assert.Contains("Audit queue is full; 1 download record(s) dropped", message);
        AssertNothingIdentifying(warnings.Messages);

        store.Hold.SetResult();
        Assert.True(await log.DrainAsync(Wait));
        Assert.Equal(new[] { "req-0", "req-1", "req-2" }, store.Stored.Select(e => e.RequestId));
    }

    [Fact]
    public async Task Warnings_are_limited_to_one_per_window()
    {
        var store = new Store { Throw = new IOException("down") };
        var clock = new Clock();
        var warnings = new Warnings();
        using var log = new QueuedAuditLog(store, clock, warnings.Add);

        for (var i = 0; i < 3; i++)
        {
            Append(log, Download(i));
            Assert.True(await log.DrainAsync(Wait));
        }
        Assert.Single(warnings.Messages);
        Assert.Equal(3, log.FailedWrites);

        clock.UtcNow += QueuedAuditLog.WarnInterval;
        Append(log, Download(9));
        Assert.True(await log.DrainAsync(Wait));
        Assert.Equal(2, warnings.Messages.Count);
        Assert.Contains("4 failed write(s) so far, 4 download record(s) lost", warnings.Messages.Last());
        AssertNothingIdentifying(warnings.Messages);
    }

    [Fact]
    public async Task Queued_events_are_written_in_order_in_bounded_batches()
    {
        var store = new Store { Hold = new TaskCompletionSource() };
        using var log = new QueuedAuditLog(store, new Clock(), batchSize: 4);

        Append(log, Download(0));
        await Until(() => Volatile.Read(ref store.Calls) == 1);
        for (var i = 1; i <= 9; i++) Append(log, Download(i));
        store.Hold.SetResult();

        Assert.True(await log.DrainAsync(Wait));
        Assert.Equal(Enumerable.Range(0, 10).Select(i => "req-" + i), store.Stored.Select(e => e.RequestId));
        Assert.Equal(new[] { 1, 4, 4, 1 }, store.Batches.Select(b => b.Count));
    }

    [Fact]
    public async Task A_failed_batch_does_not_stop_the_writer()
    {
        var store = new Store { Throw = new IOException("blip") };
        using var log = new QueuedAuditLog(store, new Clock());
        Append(log, Download(0));
        Assert.True(await log.DrainAsync(Wait));

        store.Throw = null;
        Append(log, Download(1));
        Assert.True(await log.DrainAsync(Wait));
        Assert.Equal("req-1", Assert.Single(store.Stored).RequestId);
    }

    [Fact]
    public async Task Dispose_writes_what_is_queued_and_later_appends_are_dropped_without_throwing()
    {
        var store = new Store();
        var log = new QueuedAuditLog(store, new Clock());
        for (var i = 0; i < 50; i++) Append(log, Download(i));

        log.Dispose();
        log.Dispose();   // idempotent

        Assert.Equal(50, store.Stored.Count);
        Assert.True(log.AppendAsync(Download(99), default).IsCompletedSuccessfully);
        Assert.Equal(1, log.DroppedEvents);
    }

    [Fact]
    public void The_defaults_are_the_plans()
    {
        Assert.Equal(10_000, QueuedAuditLog.DefaultCapacity);
        Assert.Equal(TimeSpan.FromSeconds(30), QueuedAuditLog.WarnInterval);
        Assert.Equal(90, AuditPolicy.RetentionDays);
        Assert.Equal(TimeSpan.FromDays(90), AuditPolicy.Retention);
        Assert.Equal(365, AuditPolicy.TokenActivityRetentionDays);
        Assert.Equal(AuditPolicy.TokenActivityRetentionDays, AdminQueries.MaxActiveDays);
    }
}
