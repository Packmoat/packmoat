using Packmoat.Domain;

namespace Packmoat.Host.Web;

/// <summary>
/// Single-use form nonces, kept in memory with a short life. Antiforgery tokens can be reused; a nonce makes one rendered form act at
/// most once, so a reload or back-and-resubmit cannot quietly create a second live token. A restart forgets them all, which only means
/// the form must be opened again. Each site has its own instance (see <see cref="Packmoat.Host.Admin.AdminFormNonces"/> and
/// <see cref="Packmoat.Host.Customer.AppFormNonces"/>), so one site's visitors can never push out another site's nonces, and each key
/// (a tenant, or a person) holds at most <see cref="MaxPerKey"/> live ones, so one key cannot push out the others' either.
/// </summary>
public abstract class FormNonces
{
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(30);
    public const int Max = 2000;
    public const int MaxPerKey = 20;
    private readonly IClock _clock;
    private readonly Dictionary<string, (string Key, DateTimeOffset Expires)> _live = new();
    private readonly object _lock = new();

    protected FormNonces(IClock clock) => _clock = clock;

    public string Issue(string key)
    {
        var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var now = _clock.UtcNow;
        lock (_lock)
        {
            // This key's own oldest go first: opening a form many times only ever replaces the same key's earlier nonces.
            var mine = _live.Where(kv => kv.Value.Key == key).OrderBy(kv => kv.Value.Expires).Select(kv => kv.Key).ToList();
            foreach (var k in mine.Take(Math.Max(0, mine.Count - (MaxPerKey - 1)))) _live.Remove(k);
            if (_live.Count >= Max)
            {
                foreach (var k in _live.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList()) _live.Remove(k);
                if (_live.Count >= Max) foreach (var k in _live.OrderBy(kv => kv.Value.Expires).Take(Max / 2).Select(kv => kv.Key).ToList()) _live.Remove(k);
            }
            _live[nonce] = (key, now + Life);
        }
        return nonce;
    }

    /// <summary>True once for a live nonce issued for this key; false for anything else (and the nonce is gone either way).</summary>
    public bool Consume(string? nonce, string key)
    {
        if (string.IsNullOrEmpty(nonce)) return false;
        lock (_lock)
        {
            if (!_live.Remove(nonce, out var e)) return false;
            return e.Key == key && e.Expires > _clock.UtcNow;
        }
    }
}
