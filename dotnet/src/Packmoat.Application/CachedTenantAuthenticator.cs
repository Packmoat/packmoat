using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>
/// Remembers successful token lookups for a few seconds so the database is not hit on every request.
/// This bounds how long a revoked token, a changed waiver or a suspended tenant stays in effect. A token's expiry is exact: an entry is
/// never kept past the token's own <c>expires_at</c>, nor past a known end the serving gate gave (<see cref="AuthResult.ServeUntil"/>, #174).
/// </summary>
public sealed class CachedTenantAuthenticator : ITenantAuthenticator
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(5);

    private sealed record Entry(AuthResult Result, DateTimeOffset ExpiresAt);

    private readonly ITenantAuthenticator _inner;
    private readonly IClock _clock;
    private readonly TimeSpan _lifetime;
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public CachedTenantAuthenticator(ITenantAuthenticator inner, IClock clock, TimeSpan? lifetime = null)
    {
        _inner = inner;
        _clock = clock;
        _lifetime = lifetime ?? DefaultLifetime;
    }

    public async ValueTask<AuthResult?> AuthenticateAsync(string token, CancellationToken cancellationToken)
    {
        var key = TokenHasher.Hash(token); // the plaintext token is never used as a dictionary key
        var now = _clock.UtcNow;
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var e) && e.ExpiresAt > now) return e.Result;
        }
        // (An entry never outlives its token or the gate's known end: ExpiresAt below is capped at AuthResult.StopsAt, so a hit above is never past either.)

        var result = await _inner.AuthenticateAsync(token, cancellationToken);
        lock (_lock)
        {
            if (result is null) _cache.Remove(key);
            else if (result.StopsAt is { } stops && stops <= now) { _cache.Remove(key); result = null; }   // belt and braces: never hand out an expired token or one past the gate's end
            else _cache[key] = new Entry(result, result.StopsAt is { } te && te < now + _lifetime ? te : now + _lifetime);
            if (_cache.Count > 10_000) foreach (var k in _cache.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList()) _cache.Remove(k);
        }
        return result;
    }

    /// <summary>Drops everything now. Used after an in-process revocation.</summary>
    public void Clear()
    {
        lock (_lock) _cache.Clear();
    }
}
