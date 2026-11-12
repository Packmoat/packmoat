using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>
/// The feed setup wizard's scratch: what it keeps for one person of one tenant between two pages (the last check they ran, the last
/// developer proof, the step 1 draft), for a short life, where every copy of the server sees it. So a sign-in round trip that comes back
/// to another copy, or a restart, finds what the person left. One row per (tenant, person, kind): a put replaces it. The payload is the
/// wizard's own text; the store never reads it.
/// <para>It may hold personal data (the names of a feed's administrators), so a tenant purge deletes the tenant's rows, and the retention
/// pass deletes expired ones. Every method throws when the store cannot be reached: the caller decides what that means, and must never
/// treat it as a result.</para>
/// </summary>
public interface IFeedSetupScratch
{
    /// <summary>Keeps <paramref name="payload"/> for (<paramref name="tenantId"/>, <paramref name="personId"/>, <paramref name="kind"/>) until <paramref name="life"/> from now, replacing what was kept.</summary>
    Task PutAsync(string tenantId, Guid personId, string kind, string payload, TimeSpan life, CancellationToken ct = default);

    /// <summary>What is kept for exactly this tenant, person and kind, or null when there is nothing or it has expired.</summary>
    Task<string?> GetAsync(string tenantId, Guid personId, string kind, CancellationToken ct = default);

    /// <summary>Forgets what is kept for exactly this tenant, person and kind (nothing kept: no change).</summary>
    Task DeleteAsync(string tenantId, Guid personId, string kind, CancellationToken ct = default);

    /// <summary>Deletes every row that expired at or before <paramref name="now"/>. A live one is kept. Returns how many.</summary>
    Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>The kinds of row the wizard keeps per person: each is its own row, so one never replaces another.</summary>
public static class FeedSetupScratchKinds
{
    /// <summary>The last settings check the person ran, with the named administrators to confirm.</summary>
    public const string Settings = "settings";
    /// <summary>The last developer proof the person ran.</summary>
    public const string Developer = "developer";
    /// <summary>The step 1 draft: the typed names only.</summary>
    public const string Draft = "draft";

    /// <summary>Every kind.</summary>
    public static IReadOnlyList<string> All { get; } = [Settings, Developer, Draft];
}

/// <summary>The scratch's rules, the same for every adapter.</summary>
public static class FeedSetupScratchRules
{
    /// <summary>The most a payload may hold, in characters: far above a check's results, so only a bug reaches it.</summary>
    public const int MaxPayload = 256 * 1024;

    /// <summary>Checks a key, as every adapter must before it reads or writes: a tenant, a person (not the empty id) and a known kind.</summary>
    public static void RequireKey(string tenantId, Guid personId, string kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        if (personId == Guid.Empty) throw new ArgumentException("A person is never the empty id.", nameof(personId));
        if (!FeedSetupScratchKinds.All.Contains(kind, StringComparer.Ordinal))
            throw new ArgumentException($"The feed setup scratch keeps only the kinds {string.Join(", ", FeedSetupScratchKinds.All)}.", nameof(kind));
    }

    /// <summary>Checks a put's arguments, as every adapter must before it writes anything.</summary>
    public static void RequirePut(string tenantId, Guid personId, string kind, string payload, TimeSpan life)
    {
        RequireKey(tenantId, personId, kind);
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > MaxPayload) throw new ArgumentOutOfRangeException(nameof(payload), $"A feed setup scratch payload holds at most {MaxPayload} characters.");
        if (life <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(life), "A feed setup scratch row must live for some time.");
    }

    /// <summary>The person as stored: the lower-case "D" form.</summary>
    public static string Person(Guid personId) => personId.ToString("D");
}

/// <summary>
/// The scratch in this process alone, for where there is no database (test hosts with fakes). Every server has a database and keeps it
/// there: the composition root registers this only when there is none.
/// </summary>
public sealed class InProcessFeedSetupScratch : IFeedSetupScratch
{
    private readonly IClock _clock;
    private readonly Dictionary<(string Tenant, Guid Person, string Kind), (string Payload, DateTimeOffset Expires)> _rows = new();
    private readonly object _lock = new();

    public InProcessFeedSetupScratch(IClock clock) => _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public Task PutAsync(string tenantId, Guid personId, string kind, string payload, TimeSpan life, CancellationToken ct = default)
    {
        FeedSetupScratchRules.RequirePut(tenantId, personId, kind, payload, life);
        var now = _clock.UtcNow;
        lock (_lock)
        {
            _rows[(tenantId, personId, kind)] = (payload, now + life);
            if (_rows.Count > 2_000)
                foreach (var k in _rows.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList()) _rows.Remove(k);
        }
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string tenantId, Guid personId, string kind, CancellationToken ct = default)
    {
        FeedSetupScratchRules.RequireKey(tenantId, personId, kind);
        lock (_lock)
            return Task.FromResult(_rows.TryGetValue((tenantId, personId, kind), out var row) && row.Expires > _clock.UtcNow ? row.Payload : null);
    }

    public Task DeleteAsync(string tenantId, Guid personId, string kind, CancellationToken ct = default)
    {
        FeedSetupScratchRules.RequireKey(tenantId, personId, kind);
        lock (_lock) _rows.Remove((tenantId, personId, kind));
        return Task.CompletedTask;
    }

    public Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var expired = _rows.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList();
            foreach (var k in expired) _rows.Remove(k);
            return Task.FromResult(expired.Count);
        }
    }
}
