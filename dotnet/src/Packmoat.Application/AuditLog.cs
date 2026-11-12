using System.Threading.Channels;
using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>
/// What the audit trail keeps, and for how long. The figures are product decisions: change them here and nowhere else (the
/// database's own floor for audit_events, migration 19, must never be longer than <see cref="RetentionDays"/>; a test checks it).
/// Only package downloads are recorded (allowed and hidden); listings and metadata are not (the decision record covers those).
/// Recording is off the request path and fails open: a full queue or a storage failure loses audit rows, never a download.
/// </summary>
public static class AuditPolicy
{
    /// <summary>audit_events rows older than this many days are deleted by the daily retention pass.</summary>
    public const int RetentionDays = 90;

    public static TimeSpan Retention => TimeSpan.FromDays(RetentionDays);

    /// <summary>
    /// token_activity days older than this are deleted by the daily retention pass, and the active-developer count cannot look further
    /// back (<see cref="AdminQueries.MaxActiveDays"/>).
    /// </summary>
    public const int TokenActivityRetentionDays = 365;
}

/// <summary>Where audit events are stored, many at a time. Used only by <see cref="QueuedAuditLog"/>'s background writer.</summary>
public interface IAuditBatchLog
{
    /// <summary>Stores the events in one transaction. A purged tenant's events are dropped.</summary>
    Task AppendBatchAsync(IReadOnlyList<AuditEvent> events, CancellationToken cancellationToken);
}

/// <summary>The audit trail's retention prune.</summary>
public interface IAuditRetention
{
    /// <summary>
    /// Deletes the events written before <paramref name="before"/>, in chunks (other writers wait for one chunk at most). Returns how many. The database
    /// refuses to delete an event less than <see cref="AuditPolicy.RetentionDays"/> days old by its own clock, so a clock running ahead
    /// throws instead.
    /// </summary>
    Task<int> PruneAsync(DateTimeOffset before, CancellationToken cancellationToken);
}

/// <summary>
/// The audit log the proxy writes to: a bounded queue (<see cref="DefaultCapacity"/> events) and one background writer that stores
/// what is queued in batches through <see cref="IAuditBatchLog"/>. <see cref="AppendAsync"/> never waits and never throws, so storage
/// can neither slow nor fail a download. The price, by design (fail-open): a full queue drops events, a failed batch loses its events,
/// and a crash loses what was still queued. Drops and failures are counted and logged through <c>warn</c> at most once per
/// <see cref="WarnInterval"/>, with counts and an exception type only: never a token, package or version.
/// </summary>
public sealed class QueuedAuditLog : IAuditLog, IDisposable
{
    public const int DefaultCapacity = 10_000;
    /// <summary>The most events one write stores.</summary>
    public const int DefaultBatchSize = 500;
    public static readonly TimeSpan WarnInterval = TimeSpan.FromSeconds(30);

    private readonly IAuditBatchLog _store;
    private readonly IClock _clock;
    private readonly Action<string>? _warn;
    private readonly int _batchSize;
    private readonly Channel<AuditEvent> _queue;
    private readonly Task _writer;
    private long _dropped, _failedWrites, _lostEvents, _pending, _nextWarnTicks;
    private volatile bool _closed;

    /// <param name="capacity">A test seam: production uses <see cref="DefaultCapacity"/>.</param>
    /// <param name="batchSize">A test seam: production uses <see cref="DefaultBatchSize"/>.</param>
    public QueuedAuditLog(IAuditBatchLog store, IClock clock, Action<string>? warn = null, int capacity = DefaultCapacity, int batchSize = DefaultBatchSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        _store = store;
        _clock = clock;
        _warn = warn;
        _batchSize = batchSize;
        _queue = Channel.CreateBounded<AuditEvent>(new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Events not queued: the queue was full, or the log was shutting down.</summary>
    public long DroppedEvents => Interlocked.Read(ref _dropped);

    /// <summary>Batches whose write failed.</summary>
    public long FailedWrites => Interlocked.Read(ref _failedWrites);

    /// <summary>Events lost in failed writes.</summary>
    public long LostEvents => Interlocked.Read(ref _lostEvents);

    /// <summary>Queues the event and returns at once. Never throws: a full queue drops it (counted).</summary>
    public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        try
        {
            Interlocked.Increment(ref _pending);   // before TryWrite, so the writer can never decrement it first
            if (!_closed && _queue.Writer.TryWrite(auditEvent)) return ValueTask.CompletedTask;
            Interlocked.Decrement(ref _pending);
            var n = Interlocked.Increment(ref _dropped);
            WarnLimited(_closed
                ? $"Audit log is shutting down; {n} download record(s) dropped so far. The audit trail is incomplete."
                : $"Audit queue is full; {n} download record(s) dropped so far. The audit trail is incomplete.");
        }
        catch (Exception ex)
        {
            WarnLimited($"Audit bookkeeping failed ({ex.GetType().Name}). The audit trail may be incomplete.");
        }
        return ValueTask.CompletedTask;
    }

    private async Task WriteLoopAsync()
    {
        var reader = _queue.Reader;
        var batch = new List<AuditEvent>(_batchSize);
        while (true)
        {
            try
            {
                if (!await reader.WaitToReadAsync()) return;   // completed and empty: shut down
                while (batch.Count < _batchSize && reader.TryRead(out var e)) batch.Add(e);
                if (batch.Count == 0) continue;
                try
                {
                    // No cancellation: a write already under way is left to finish, so shutdown never throws away a batch it took.
                    await _store.AppendBatchAsync(batch, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    var n = Interlocked.Increment(ref _failedWrites);
                    var lost = Interlocked.Add(ref _lostEvents, batch.Count);
                    WarnLimited($"Audit write failed ({ex.GetType().Name}); {n} failed write(s) so far, {lost} download record(s) lost. The audit trail is incomplete.");
                }
                finally
                {
                    Interlocked.Add(ref _pending, -batch.Count);
                    batch.Clear();
                }
            }
            catch (Exception ex)
            {
                // Should be unreachable (the write is guarded). Count it and carry on rather than die silently.
                Interlocked.Increment(ref _failedWrites);
                WarnLimited($"Audit writer restarted after {ex.GetType().Name}. The audit trail may be incomplete.");
                try { await Task.Delay(TimeSpan.FromSeconds(1)); } catch { /* never */ }
            }
        }
    }

    private long NowTicks()
    {
        try { return _clock.UtcNow.UtcTicks; } catch { return DateTime.UtcNow.Ticks; }
    }

    /// <summary>At most one warning per <see cref="WarnInterval"/>, so an outage does not log once per download. Never throws.</summary>
    private void WarnLimited(string message)
    {
        try
        {
            if (_warn is null) return;
            var now = NowTicks();
            var next = Interlocked.Read(ref _nextWarnTicks);
            if (now < next || Interlocked.CompareExchange(ref _nextWarnTicks, now + WarnInterval.Ticks, next) != next) return;
            _warn(message);
        }
        catch { /* a broken logger must not kill the writer or a request */ }
    }

    /// <summary>Waits until everything queued so far has been written or has failed, or the timeout passes. Returns whether it drained.</summary>
    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (Interlocked.Read(ref _pending) > 0)
        {
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(5);
        }
        return true;
    }

    /// <summary>Stops taking events and gives the writer a few seconds to store what is queued. What is left after that is lost.</summary>
    public void Dispose()
    {
        if (_closed) return;   // idempotent
        _closed = true;
        _queue.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
    }
}
