using Packmoat.Application;
using Packmoat.Infrastructure;

namespace Packmoat.Infrastructure.Tests;

/// <summary>The token-activity contract (#232 S1): kinds, the active-developer count and the recording decorator over the store.</summary>
public abstract class DeveloperCountingContract : StoreContract
{
    protected TokenActivityPorts Activity(TestDb t) => Engine.TokenActivity(t);
    protected static DateOnly Today(TestDb t) => DateOnly.FromDateTime(t.Clock.UtcNow.UtcDateTime);

    [Fact]
    public async Task A_token_with_no_kind_is_a_developer_token_and_automation_round_trips()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();
        var dev = await t.Tokens.IssueAsync("acme", "ann", default);
        var bot = await t.Tokens.IssueAsync("acme", "build agents", default, TokenKind.Automation);

        var list = await t.Tokens.ListAsync("acme", default);
        Assert.Equal(TokenKind.Developer, list.Single(x => x.TokenId == dev.TokenId).Kind);
        Assert.Equal(TokenKind.Automation, list.Single(x => x.TokenId == bot.TokenId).Kind);
    }

    [Fact]
    public async Task Counts_distinct_developer_tokens_with_label_and_last_active_day()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();
        var a = Activity(t);
        var ann = await t.Tokens.IssueAsync("acme", "ann", default);
        var bob = await t.Tokens.IssueAsync("acme", "bob", default);
        await a.RecordAsync(ann.TokenId, Today(t).AddDays(-3), default);
        await a.RecordAsync(ann.TokenId, Today(t).AddDays(-1), default);
        await a.RecordAsync(ann.TokenId, Today(t).AddDays(-1), default);   // idempotent
        await a.RecordAsync(bob.TokenId, Today(t), default);

        var r = await a.GetAsync("acme", 30, default);

        Assert.Equal(2, r.Count);
        Assert.Equal(new[] { ("bob", Today(t)), ("ann", Today(t).AddDays(-1)) }, r.Tokens.Select(x => (x.Label, x.LastActiveDay)));
    }

    [Fact]
    public async Task Automation_tokens_are_not_counted()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();
        var a = Activity(t);
        var dev = await t.Tokens.IssueAsync("acme", "ann", default);
        var bot = await t.Tokens.IssueAsync("acme", "ci", default, TokenKind.Automation);
        await a.RecordAsync(dev.TokenId, Today(t), default);
        await a.RecordAsync(bot.TokenId, Today(t), default);

        var r = await a.GetAsync("acme", 30, default);

        Assert.Equal(1, r.Count);
        Assert.Equal("ann", r.Tokens.Single().Label);
    }

    [Fact]
    public async Task A_revoked_token_is_not_counted()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();
        var a = Activity(t);
        var old = await t.Tokens.IssueAsync("acme", "ann (old laptop)", default);
        var fresh = await t.Tokens.IssueAsync("acme", "ann", default);
        await a.RecordAsync(old.TokenId, Today(t), default);
        await a.RecordAsync(fresh.TokenId, Today(t), default);
        await t.Tokens.RevokeAsync(old.TokenId, default);

        var r = await a.GetAsync("acme", 30, default);

        Assert.Equal(1, r.Count);   // a rotated token does not make one person count twice
        Assert.Equal("ann", r.Tokens.Single().Label);
    }

    [Fact]
    public async Task Another_tenants_tokens_are_never_counted_or_listed()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        await t.AddTenantAsync("other");
        var a = Activity(t);
        var mine = await t.Tokens.IssueAsync("acme", "ann", default);
        var theirs = await t.Tokens.IssueAsync("other", "zed", default);
        await a.RecordAsync(mine.TokenId, Today(t), default);
        await a.RecordAsync(theirs.TokenId, Today(t), default);

        var acme = await a.GetAsync("acme", 30, default);
        var other = await a.GetAsync("other", 30, default);

        Assert.Equal(new[] { "ann" }, acme.Tokens.Select(x => x.Label));
        Assert.Equal(new[] { "zed" }, other.Tokens.Select(x => x.Label));
        Assert.Equal(0, (await a.GetAsync("nobody", 30, default)).Count);
    }

    [Fact]
    public async Task The_window_includes_today_and_n_minus_one_days_back_and_no_further()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();
        var a = Activity(t);
        var edge = await t.Tokens.IssueAsync("acme", "on the edge", default);
        var out_ = await t.Tokens.IssueAsync("acme", "just outside", default);
        await a.RecordAsync(edge.TokenId, Today(t).AddDays(-29), default);
        await a.RecordAsync(out_.TokenId, Today(t).AddDays(-30), default);

        Assert.Equal(new[] { "on the edge" }, (await a.GetAsync("acme", 30, default)).Tokens.Select(x => x.Label));
        Assert.Equal(0, (await a.GetAsync("acme", 29, default)).Count);
        Assert.Equal(2, (await a.GetAsync("acme", 31, default)).Count);
    }

    [Fact]
    public async Task The_decorator_writes_once_per_token_per_day_and_never_surfaces_a_failure()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();
        var issued = await t.Tokens.IssueAsync("acme", "ann", default);
        var cached = new CachedTenantAuthenticator(t.Tokens, t.Clock);
        var counting = new CountingLog(Activity(t));
        using var auth = new ActivityRecordingAuthenticator(cached, counting, t.Clock);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ => Assert.NotNull(await auth.AuthenticateAsync(issued.Plaintext, default))));
        await auth.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, counting.Calls);   // concurrent first requests queue one write, not twenty
        t.Clock.Advance(TimeSpan.FromDays(1));
        Assert.NotNull(await auth.AuthenticateAsync(issued.Plaintext, default));
        await auth.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, counting.Calls);

        var warnings = new List<string>();
        using var failing = new ActivityRecordingAuthenticator(cached, new ThrowingLog(), t.Clock, warnings.Add);
        Assert.NotNull(await failing.AuthenticateAsync(issued.Plaintext, default));                // no exception reaches the caller
        await failing.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await failing.AuthenticateAsync("pmt_" + new string('0', 48), default));       // a bad token is still just refused
        Assert.Equal(1, failing.FailedWrites);
        var warning = Assert.Single(warnings);
        // The exact text: nothing about the token, its id, its label or the exception message can be in it.
        Assert.Equal("Token activity write failed (InvalidOperationException); 1 failure(s) so far. The active-developer count may be too low.", warning);
    }

    [Fact]
    public async Task The_request_path_does_not_wait_for_storage()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();
        var issued = await t.Tokens.IssueAsync("acme", "ann", default);
        var blocked = new BlockingLog();
        using var auth = new ActivityRecordingAuthenticator(new CachedTenantAuthenticator(t.Tokens, t.Clock), blocked, t.Clock);

        var request = auth.AuthenticateAsync(issued.Plaintext, default).AsTask();
        Assert.Same(request, await Task.WhenAny(request, Task.Delay(TimeSpan.FromSeconds(5))));   // returned while the write is still stuck
        Assert.NotNull(await request);
        blocked.Release.SetResult();
        await auth.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, blocked.Calls);
        Assert.Equal(0, auth.FailedWrites);
    }

    [Fact]
    public async Task A_full_queue_drops_notes_without_failing_requests_and_counts_them()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();
        var blocked = new BlockingLog();
        var warnings = new List<string>();
        using var auth = new ActivityRecordingAuthenticator(new FixedAuthenticator(), blocked, t.Clock, warnings.Add);

        for (var i = 0; i < ActivityRecordingAuthenticator.QueueCapacity + 50; i++) Assert.NotNull(await auth.AuthenticateAsync("tok" + i, default));

        Assert.True(auth.DroppedWrites >= 1);
        Assert.NotEmpty(warnings);
        blocked.Release.SetResult();
    }

    [Fact]
    public async Task A_cancelled_write_is_not_a_failure_and_does_not_start_the_backoff()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();
        var cancelling = new CancellingLog();
        using var auth = new ActivityRecordingAuthenticator(new FixedAuthenticator(), cancelling, t.Clock);

        Assert.NotNull(await auth.AuthenticateAsync("a", default));
        await auth.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(await auth.AuthenticateAsync("b", default));   // would be skipped during a backoff
        await auth.DrainAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, auth.FailedWrites);
        Assert.Equal(2, cancelling.Calls);
    }

    protected static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(5);
        Assert.True(condition());
    }

    [Fact]
    public async Task A_token_dropped_on_a_full_queue_can_be_noted_again_once_there_is_room()
    {
        await using var t = await CreateDbAsync();
        var blocked = new BlockingLog();
        var warnings = new List<string>();
        using var auth = new ActivityRecordingAuthenticator(new FixedAuthenticator(), blocked, t.Clock, warnings.Add);

        await auth.AuthenticateAsync("first", default);
        await WaitUntilAsync(() => blocked.Calls == 1);   // the writer holds "first" and is stuck
        for (var i = 0; i < ActivityRecordingAuthenticator.QueueCapacity; i++) await auth.AuthenticateAsync("fill" + i, default);
        await auth.AuthenticateAsync("overflow", default);   // no room: dropped
        Assert.Equal(1, auth.DroppedWrites);
        Assert.Equal("Token activity queue is full; 1 note(s) dropped so far. The active-developer count may be too low.", Assert.Single(warnings));

        blocked.Release.SetResult();
        await auth.DrainAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("overflow", blocked.Ids);

        await auth.AuthenticateAsync("overflow", default);   // room now: it must not be remembered as done
        await auth.DrainAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("overflow", blocked.Ids);
    }

    [Fact]
    public async Task A_throwing_warn_callback_does_not_stop_the_writer()
    {
        await using var t = await CreateDbAsync();
        var log = new FailForLog("bad");
        using var auth = new ActivityRecordingAuthenticator(new FixedAuthenticator(), log, t.Clock, _ => throw new InvalidOperationException("logger is broken"));

        await auth.AuthenticateAsync("bad", default);
        await auth.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, auth.FailedWrites);

        t.Clock.Advance(ActivityRecordingAuthenticator.RetryAfterFailure + TimeSpan.FromSeconds(1));
        for (var i = 0; i < 5; i++) await auth.AuthenticateAsync("ok" + i, default);
        await auth.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(5, log.Recorded.Count);   // the writer is alive and draining
    }

    [Fact]
    public async Task A_token_whose_write_failed_is_retried_after_the_pause_and_again_on_the_next_day()
    {
        await using var t = await CreateDbAsync();
        var log = new FailForLog("flaky") { Failing = true };
        using var auth = new ActivityRecordingAuthenticator(new FixedAuthenticator(), log, t.Clock);

        await auth.AuthenticateAsync("flaky", default);
        await auth.DrainAsync(TimeSpan.FromSeconds(5));
        await auth.AuthenticateAsync("flaky", default);   // inside the pause: not even queued
        await auth.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, log.Attempts);

        t.Clock.Advance(ActivityRecordingAuthenticator.RetryAfterFailure + TimeSpan.FromSeconds(1));
        log.Failing = false;
        await auth.AuthenticateAsync("flaky", default);   // same day, after the pause: tried again and recorded
        await auth.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.Single(log.Recorded);

        t.Clock.Advance(TimeSpan.FromDays(1));
        await auth.AuthenticateAsync("flaky", default);   // next day: recorded again
        await auth.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, log.Recorded.Count);
    }

    [Fact]
    public async Task During_an_outage_there_is_one_warning_per_pause_not_one_per_item()
    {
        await using var t = await CreateDbAsync();
        var warnings = new List<string>();
        var log = new FailForLog("*") { Failing = true };
        using var auth = new ActivityRecordingAuthenticator(new FixedAuthenticator(), log, t.Clock, warnings.Add);

        for (var i = 0; i < 50; i++) await auth.AuthenticateAsync("t" + i, default);   // all queued before the first failure is seen, or skipped by the pause
        await auth.DrainAsync(TimeSpan.FromSeconds(5));

        Assert.Single(warnings);
        Assert.True(auth.FailedWrites >= 1);
    }

    [Fact]
    public async Task Disposal_writes_what_is_queued_and_later_notes_are_reported_as_shutting_down()
    {
        await using var t = await CreateDbAsync();
        var log = new FailForLog("none");
        var warnings = new List<string>();
        var auth = new ActivityRecordingAuthenticator(new FixedAuthenticator(), log, t.Clock, warnings.Add);

        for (var i = 0; i < 5; i++) await auth.AuthenticateAsync("t" + i, default);
        auth.Dispose();
        Assert.Equal(5, log.Recorded.Count);

        await auth.AuthenticateAsync("late", default);   // a request arriving after disposal still succeeds
        Assert.Equal(1, auth.DroppedWrites);
        Assert.Equal("Token activity is shutting down; 1 note(s) dropped so far. The active-developer count may be too low.", Assert.Single(warnings));
        auth.Dispose();   // idempotent
    }

    protected sealed class FailForLog(string failFor) : ITokenActivityLog
    {
        public bool Failing { get; set; } = true;
        public int Attempts;
        public System.Collections.Concurrent.ConcurrentQueue<string> Recorded { get; } = new();
        public ValueTask RecordAsync(string tokenId, DateOnly day, CancellationToken ct)
        {
            Interlocked.Increment(ref Attempts);
            if (Failing && (failFor == "*" || failFor == tokenId)) throw new InvalidOperationException("boom: " + tokenId);
            Recorded.Enqueue(tokenId);
            return ValueTask.CompletedTask;
        }
    }

    protected sealed class FixedAuthenticator : ITenantAuthenticator
    {
        private static readonly Packmoat.Domain.Tenant T = new("acme", "Acme", "https://x/", "o/", "https://x/i.json", new Packmoat.Domain.TenantPolicy(14));
        public ValueTask<AuthResult?> AuthenticateAsync(string token, CancellationToken ct) => ValueTask.FromResult<AuthResult?>(new AuthResult(T, token));
    }

    protected sealed class BlockingLog : ITokenActivityLog
    {
        public int Calls;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Collections.Concurrent.ConcurrentQueue<string> Ids { get; } = new();
        public async ValueTask RecordAsync(string tokenId, DateOnly day, CancellationToken ct) { Interlocked.Increment(ref Calls); await Release.Task; Ids.Enqueue(tokenId); }
    }

    protected sealed class CancellingLog : ITokenActivityLog
    {
        public int Calls;
        public ValueTask RecordAsync(string tokenId, DateOnly day, CancellationToken ct) { Interlocked.Increment(ref Calls); throw new OperationCanceledException(); }
    }

    protected sealed class CountingLog(ITokenActivityLog inner) : ITokenActivityLog
    {
        public int Calls;
        public ValueTask RecordAsync(string tokenId, DateOnly day, CancellationToken ct) { Interlocked.Increment(ref Calls); return inner.RecordAsync(tokenId, day, ct); }
    }

    protected sealed class ThrowingLog : ITokenActivityLog
    {
        public ValueTask RecordAsync(string tokenId, DateOnly day, CancellationToken ct) => throw new InvalidOperationException("boom");
    }
}
