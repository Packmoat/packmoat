using System.Collections.Concurrent;
using System.Threading.Channels;
using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>
/// What a token is for. A <c>Developer</c> token is one person's (or one workstation's) credential and counts towards the active-developer
/// number; an <c>Automation</c> token (build agents, CI) is shared by machines and never counts.
/// </summary>
public enum TokenKind
{
    Developer = 0,
    Automation = 1,
}

/// <summary>Remembers that a token was used on a given UTC day. Stores the token id and the day, nothing else.</summary>
public interface ITokenActivityLog
{
    /// <summary>Idempotent: recording the same token and day twice leaves one row.</summary>
    ValueTask RecordAsync(string tokenId, DateOnly day, CancellationToken cancellationToken);
}

/// <summary>The token_activity retention prune (#136): days are kept for <see cref="AuditPolicy.TokenActivityRetentionDays"/>.</summary>
public interface ITokenActivityRetention
{
    /// <summary>Deletes every day before <paramref name="keepFrom"/> (that day itself is kept). Returns how many rows.</summary>
    Task<int> PruneAsync(DateOnly keepFrom, CancellationToken cancellationToken);
}

public sealed record ActiveToken(string TokenId, string Label, DateOnly LastActiveDay);

/// <summary>The distinct developer-kind tokens that were used in a window. <paramref name="Count"/> equals <c>Tokens.Count</c>.</summary>
public sealed record ActiveDevelopers(string TenantId, int Days, int Count, IReadOnlyList<ActiveToken> Tokens);

/// <summary>
/// Read side for "how many developers used Packmoat lately". Counts developer-kind, non-revoked tokens of one tenant that were used on
/// at least one of the last <c>days</c> UTC days (today included). A label can be personal data: callers show it to the tenant's own
/// actors and the operator only.
/// </summary>
public interface IActiveDeveloperQuery
{
    Task<ActiveDevelopers> GetAsync(string tenantId, int days, CancellationToken cancellationToken);
}

/// <summary>
/// Wraps the authenticator and notes, at most once per token per UTC day, that the token was used. The request path only does an
/// in-memory check and, on a token's first request of the day, a non-blocking <c>TryWrite</c> into a bounded queue: it never waits for
/// storage. One background writer drains the queue into <see cref="ITokenActivityLog"/>. A full queue drops the item (counted), a failed
/// write is counted, logged through <c>warn</c> (never with a token or label) and retried after a pause. The count can therefore be
/// too low after an outage, never a failed request. The write is separate from the audit log, so audit de-duplication can never hide use.
/// </summary>
public sealed class ActivityRecordingAuthenticator : ITenantAuthenticator, IDisposable
{
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);
    public const int QueueCapacity = 1024;

    private readonly ITenantAuthenticator _inner;
    private readonly ITokenActivityLog _log;
    private readonly IClock _clock;
    private readonly Action<string>? _warn;
    private readonly ConcurrentDictionary<string, DateOnly> _gate = new(StringComparer.Ordinal);   // token id -> day already queued or recorded
    private readonly Channel<(string TokenId, DateOnly Day)> _queue =
        Channel.CreateBounded<(string, DateOnly)>(new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopToken;
    private readonly Task _writer;
    private long _retryNotBeforeTicks, _nextWarnTicks;
    private long _failed, _dropped, _pending;
    private volatile bool _closed;

    public ActivityRecordingAuthenticator(ITenantAuthenticator inner, ITokenActivityLog log, IClock clock, Action<string>? warn = null)
    {
        _inner = inner;
        _log = log;
        _clock = clock;
        _warn = warn;
        _stopToken = _stop.Token;
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Activity writes that failed. A nonzero value means the active-developer count may be too low.</summary>
    public long FailedWrites => Interlocked.Read(ref _failed);

    /// <summary>Activity notes dropped because the queue was full.</summary>
    public long DroppedWrites => Interlocked.Read(ref _dropped);

    public async ValueTask<AuthResult?> AuthenticateAsync(string token, CancellationToken cancellationToken)
    {
        var result = await _inner.AuthenticateAsync(token, cancellationToken);
        if (result is not null) Note(result.TokenId);
        return result;
    }

    private void Note(string tokenId)
    {
        try
        {
            var now = _clock.UtcNow;
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var known = _gate.TryGetValue(tokenId, out var seen);
            if (known && seen == today) return;                                                      // the common case: nothing to do
            if (now.UtcTicks < Interlocked.Read(ref _retryNotBeforeTicks)) return;                    // recently failed: not now
            if (known ? !_gate.TryUpdate(tokenId, today, seen) : !_gate.TryAdd(tokenId, today)) return;   // a concurrent request already queued it

            Interlocked.Increment(ref _pending);                  // before TryWrite, so the writer can never decrement it first
            if (!_closed && _queue.Writer.TryWrite((tokenId, today))) return;

            Interlocked.Decrement(ref _pending);
            Release(tokenId, today);                              // not queued: a later request may note it again
            var n = Interlocked.Increment(ref _dropped);
            WarnLimited(_closed
                ? $"Token activity is shutting down; {n} note(s) dropped so far. The active-developer count may be too low."
                : $"Token activity queue is full; {n} note(s) dropped so far. The active-developer count may be too low.");
        }
        catch (Exception ex)
        {
            // Bookkeeping must never fail a request, but it must not vanish either.
            var n = Interlocked.Increment(ref _failed);
            WarnLimited($"Token activity bookkeeping failed ({ex.GetType().Name}); {n} failure(s) so far. The active-developer count may be too low.");
        }
    }

    private void Release(string tokenId, DateOnly day) => _gate.TryRemove(new KeyValuePair<string, DateOnly>(tokenId, day));

    private long NowTicks()
    {
        try { return _clock.UtcNow.UtcTicks; } catch { return DateTime.UtcNow.Ticks; }
    }

    private void StartBackoff() => Interlocked.Exchange(ref _retryNotBeforeTicks, NowTicks() + RetryAfterFailure.Ticks);

    /// <summary>At most one warning per backoff window, so an outage does not log once per queued item. Never throws.</summary>
    private void WarnLimited(string message)
    {
        try
        {
            if (_warn is null) return;
            var now = NowTicks();
            var next = Interlocked.Read(ref _nextWarnTicks);
            if (now < next || Interlocked.CompareExchange(ref _nextWarnTicks, now + RetryAfterFailure.Ticks, next) != next) return;
            _warn(message);
        }
        catch { /* a broken logger must not kill the writer or a request */ }
    }

    private async Task WriteLoopAsync()
    {
        while (!_stopToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var (tokenId, day) in _queue.Reader.ReadAllAsync(_stopToken))
                {
                    try
                    {
                        await _log.RecordAsync(tokenId, day, _stopToken);   // the shutdown token, never a request's: a client abort cannot reach here
                    }
                    catch (OperationCanceledException) when (_stopToken.IsCancellationRequested) { return; }
                    catch (OperationCanceledException) { Release(tokenId, day); }   // an abort is not a storage failure: no count, no backoff
                    catch (Exception ex)
                    {
                        Release(tokenId, day);   // a later request may try again
                        var n = Interlocked.Increment(ref _failed);
                        StartBackoff();
                        WarnLimited($"Token activity write failed ({ex.GetType().Name}); {n} failure(s) so far. The active-developer count may be too low.");
                    }
                    finally { Interlocked.Decrement(ref _pending); }
                }
                return;   // the queue was completed and is empty: shutting down
            }
            catch (OperationCanceledException) when (_stopToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Should be unreachable (every item is guarded). If it happens anyway, count it, pause, and carry on rather than die silently.
                var n = Interlocked.Increment(ref _failed);
                WarnLimited($"Token activity writer restarted after {ex.GetType().Name}; {n} failure(s) so far. The active-developer count may be too low.");
                try { await Task.Delay(TimeSpan.FromSeconds(1), _stopToken); } catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Waits until everything queued so far has been written or has failed. For tests and orderly shutdown.</summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (Interlocked.Read(ref _pending) > 0 && DateTime.UtcNow < until) await Task.Delay(5);
    }

    /// <summary>Stops taking notes, gives the writer a moment to finish what is already queued, then stops it.</summary>
    public void Dispose()
    {
        if (_closed) return;   // idempotent
        _closed = true;
        _queue.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        _stop.Cancel();
        _stop.Dispose();
    }
}
