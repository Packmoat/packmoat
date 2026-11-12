using System.Security.Cryptography;
using System.Text;
using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>
/// The shared rate counter: a fixed-window count per (scope, key), kept where every copy of the server sees it, so N copies
/// allow what one copy allows, not N times it. Only signed-in people and admins are counted here: traffic nobody has signed in to keeps its
/// limiters per copy, so it never becomes writes to the store.
/// <para>A hit is one statement: two copies counting the same key at once cannot both take the last place in its window.</para>
/// </summary>
public interface IRateCounter
{
    /// <summary>
    /// Counts one attempt by <paramref name="key"/> in <paramref name="scope"/>. The key's window starts at its first attempt and runs for
    /// <paramref name="window"/>; at most <paramref name="max"/> attempts in it are allowed, and the attempt after that is refused with the
    /// time left until the window ends. A window that has ended starts again at this attempt. A refused attempt does not move the window.
    /// <para>The key is kept only as its pseudonymous hash (<see cref="RateCounters.Subject"/>: an unsalted SHA-256), and only for one window
    /// plus about a day. Throws when the store cannot be reached: the caller must
    /// then refuse the action (<see cref="RateLimit"/> does).</para>
    /// </summary>
    Task<RateDecision> HitAsync(string scope, string key, int max, TimeSpan window, CancellationToken ct = default);

    /// <summary>Deletes every window that ended at or before <paramref name="now"/>. A window still running is kept. Returns how many.</summary>
    Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>One counted attempt: allowed, or refused with the time left until the window ends (always above zero when refused).</summary>
public readonly record struct RateDecision(bool Allowed, TimeSpan RetryAfter)
{
    public static RateDecision Allow { get; } = new(true, TimeSpan.Zero);

    /// <summary>The retry-after as the 429 pages give it: whole seconds, rounded up, at least 1.</summary>
    public int RetryAfterSeconds => Math.Max(1, (int)Math.Ceiling(RetryAfter.TotalSeconds));
}

/// <summary>
/// The core's limits' scopes, one per limit: two limits never share a scope, so each counts only its own attempts. A module
/// names its own scopes, starting with its name (the hosted service's start "hosted-").
/// </summary>
public static class RateScopes
{
    /// <summary>A person's customer waiver adds (<see cref="CustomerWaiverRules.MaxAddsPerWindow"/>).</summary>
    public const string WaiverAdd = "waiver-add";
    /// <summary>A person's customer waiver revokes (<see cref="CustomerWaiverRules.MaxRevokesPerWindow"/>).</summary>
    public const string WaiverRevoke = "waiver-revoke";
    /// <summary>A tenant's waiver-cap alerts (<see cref="AdminCommands.CapAlertInterval"/>): one per tenant per interval.</summary>
    public const string WaiverCapAlert = "waiver-cap-alert";
    /// <summary>A person's token issues (<see cref="CustomerTokenRules.MaxIssuesPerWindow"/>).</summary>
    public const string TokenIssue = "token-issue";
    /// <summary>A person's token revokes, their own and others' (<see cref="CustomerTokenRules.MaxRevokesPerWindow"/>).</summary>
    public const string TokenRevoke = "token-revoke";
    /// <summary>A person's auto-join changes (<see cref="AutoJoinCommands.MaxChangesPerWindow"/>).</summary>
    public const string AutoJoinChange = "auto-join-change";
    /// <summary>A person's feed checks and developer proofs (<see cref="AzureSetupRules.MaxChecksPerWindow"/>).</summary>
    public const string FeedCheck = "feed-check";
    /// <summary>An address's admin-site writes, after the admin's sign-in and the allowlist (the admin site's write limiter).</summary>
    public const string AdminWrite = "admin-write";

    /// <summary>Every core scope.</summary>
    public static IReadOnlyList<string> All { get; } = [WaiverAdd, WaiverRevoke, WaiverCapAlert, TokenIssue, TokenRevoke, AutoJoinChange, FeedCheck, AdminWrite];
}

/// <summary>The counter's rules, the same for every adapter.</summary>
public static class RateCounters
{
    /// <summary>A scope: lower-case letters, digits and '-', 1 to 40 characters.</summary>
    public static bool IsValidScope(string? scope) =>
        scope is { Length: > 0 and <= 40 } && scope.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    /// <summary>
    /// What the store keeps for <paramref name="key"/>: the lower-case hex SHA-256 of its UTF-8 bytes. The keys name people (directory and
    /// object ids), admins' addresses and tenants, so what is kept is pseudonymous (an unsalted SHA-256: anyone holding a key can find its
    /// row), not anonymous; it is short-lived, deleted within one window plus about a day (the next retention pass).
    /// </summary>
    public static string Subject(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    /// <summary>Checks a hit's arguments, as every adapter must before it counts anything.</summary>
    public static void Require(string scope, string key, int max, TimeSpan window)
    {
        if (!IsValidScope(scope)) throw new ArgumentException($"A rate scope is 1 to 40 lower-case letters, digits or '-' (\"{scope}\").", nameof(scope));
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (max < 1) throw new ArgumentOutOfRangeException(nameof(max), "A rate limit allows at least one attempt.");
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window), "A rate window must run for some time.");
    }

    /// <summary>
    /// The decision for a hit whose window (after the hit) ends at <paramref name="windowEnd"/> and holds <paramref name="hits"/> attempts,
    /// this one included: allowed while <paramref name="hits"/> is at most <paramref name="max"/>.
    /// </summary>
    public static RateDecision Decide(long hits, int max, DateTimeOffset windowEnd, DateTimeOffset now) =>
        hits <= max ? RateDecision.Allow : new RateDecision(false, windowEnd - now);
}

/// <summary>
/// One limit: at most <c>max</c> attempts per key in a fixed <c>window</c>, in one scope of the shared
/// <see cref="IRateCounter"/>. It fails closed: when the counter cannot be reached, the attempt is refused, never let through.
/// </summary>
public sealed class RateLimit
{
    private readonly IRateCounter _counter;

    public RateLimit(IRateCounter counter, string scope, int max, TimeSpan window)
    {
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));
        RateCounters.Require(scope, "-", max, window);
        Scope = scope;
        Max = max;
        Window = window;
    }

    public string Scope { get; }
    public int Max { get; }
    public TimeSpan Window { get; }

    /// <summary>
    /// Counts this attempt by <paramref name="key"/>. Refused (with the whole window to wait) when the counter fails; a cancellation is
    /// not a failure and is thrown.
    /// </summary>
    public async Task<RateDecision> HitAsync(string key, CancellationToken ct)
    {
        try
        {
            return await _counter.HitAsync(Scope, key, Max, Window, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new RateDecision(false, Window);
        }
    }

    /// <summary>Counts this attempt; false when the key already used all of this window's attempts, or the counter failed.</summary>
    public async Task<bool> TryAcquireAsync(string key, CancellationToken ct) => (await HitAsync(key, ct)).Allowed;
}

/// <summary>
/// The store's counter, with each failure reported : a hit that throws is reported once, as one line naming the scope
/// and the error's type only (never the key, its hash or the error's message), then thrown on, so <see cref="RateLimit"/> refuses the
/// attempt as before. The caller's own cancellation is not a failure and is not reported. The server wraps the database's counter in one.
/// </summary>
public sealed class ReportingRateCounter : IRateCounter
{
    private readonly Action<string> _warn;

    /// <param name="inner">The store's counter.</param>
    /// <param name="warn">Where a failure's line goes (the server's warning log).</param>
    public ReportingRateCounter(IRateCounter inner, Action<string> warn)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _warn = warn ?? throw new ArgumentNullException(nameof(warn));
    }

    /// <summary>The counter whose failures are reported.</summary>
    public IRateCounter Inner { get; }

    /// <summary>The line a failed hit in <paramref name="scope"/> is reported with: the scope and the error's type, nothing of the key.</summary>
    public static string FailureLine(string scope, Exception error) =>
        $"Rate limit {scope}: the shared counter failed ({error.GetType().Name}); the attempt was refused (fail closed)";

    public async Task<RateDecision> HitAsync(string scope, string key, int max, TimeSpan window, CancellationToken ct = default)
    {
        try
        {
            return await Inner.HitAsync(scope, key, max, window, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            try { _warn(FailureLine(scope, ex)); }
            catch (Exception) { /* reporting never changes the outcome: the attempt is refused either way */ }
            throw;
        }
    }

    public Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default) => Inner.PruneAsync(now, ct);
}

/// <summary>
/// A counter in this process alone, for where there is no database (test hosts with fakes, and the application tests). Every server has a
/// database and counts in it: the composition root registers this only when there is none.
/// </summary>
public sealed class InProcessRateCounter : IRateCounter
{
    private readonly IClock _clock;
    private readonly Dictionary<(string Scope, string Subject), (DateTimeOffset Start, DateTimeOffset End, long Hits)> _windows = new();
    private readonly object _lock = new();

    public InProcessRateCounter(IClock clock) => _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public Task<RateDecision> HitAsync(string scope, string key, int max, TimeSpan window, CancellationToken ct = default)
    {
        RateCounters.Require(scope, key, max, window);
        var now = _clock.UtcNow;
        var id = (scope, RateCounters.Subject(key));
        lock (_lock)
        {
            if (!_windows.TryGetValue(id, out var w) || w.End <= now || w.Start > now + window) w = (now, now + window, 1);
            else w = (w.Start, w.End, Math.Min(w.Hits + 1, (long)max + 1));
            _windows[id] = w;
            if (_windows.Count > 20_000)
                foreach (var k in _windows.Where(e => e.Value.End <= now).Select(e => e.Key).ToList()) _windows.Remove(k);
            return Task.FromResult(RateCounters.Decide(w.Hits, max, w.End, now));
        }
    }

    public Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var ended = _windows.Where(e => e.Value.End <= now).Select(e => e.Key).ToList();
            foreach (var k in ended) _windows.Remove(k);
            return Task.FromResult(ended.Count);
        }
    }
}
