using System.Security.Cryptography;
using System.Text;
using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>
/// The single-use form nonces, kept where every copy of the server sees them, so a form shown by one copy can be posted to another, and
/// used once only across all of them. A nonce lives in one scope (a site's own: <see cref="FormNonceScopes"/>) and is issued for one key
/// (a tenant, or a person). Neither the nonce nor the key is kept: only their SHA-256 (<see cref="FormNonceRules.Hash"/>).
/// <para>A consume is one statement that deletes the nonce and returns what it was issued for, so of two copies consuming one nonce at
/// once exactly one can succeed. Every method throws when the store cannot be reached: the caller must then refuse the form.</para>
/// </summary>
public interface IFormNonceStore
{
    /// <summary>
    /// Keeps <paramref name="nonce"/>, issued for <paramref name="key"/> in <paramref name="scope"/>, until <paramref name="life"/> from now.
    /// In the same transaction, the key's oldest nonces in the scope (by expiry) are dropped first, so it holds at most
    /// <paramref name="maxPerKey"/> after this one is added.
    /// </summary>
    Task IssueAsync(string scope, string nonce, string key, TimeSpan life, int maxPerKey, CancellationToken ct = default);

    /// <summary>
    /// Deletes <paramref name="nonce"/> from <paramref name="scope"/>, whatever it was issued for, and answers true only when it was there,
    /// issued for <paramref name="key"/>, and not yet expired. Of many consuming one nonce at once, at most one is answered true.
    /// </summary>
    Task<bool> ConsumeAsync(string scope, string nonce, string key, CancellationToken ct = default);

    /// <summary>Deletes every nonce that expired at or before <paramref name="now"/>. A live one is kept. Returns how many.</summary>
    Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>The scopes of the form nonces, one per site: a nonce one site issued is never consumed on the other.</summary>
public static class FormNonceScopes
{
    /// <summary>The admin site's forms (keyed by tenant id).</summary>
    public const string Admin = "admin";
    /// <summary>The customer site's forms, a module's pages too (keyed by person).</summary>
    public const string App = "app";

    /// <summary>Every scope.</summary>
    public static IReadOnlyList<string> All { get; } = [Admin, App];
}

/// <summary>The form nonces' rules, the same for every adapter.</summary>
public static class FormNonceRules
{
    /// <summary>
    /// What the store keeps of a nonce or a key: the lower-case hex SHA-256 of its UTF-8 bytes. A nonce is random, so its hash tells
    /// nothing; a key names a tenant or a person (directory and object ids), so its hash is pseudonymous (unsalted: anyone holding the ids
    /// can find its rows), not anonymous. Either is short-lived: deleted when used, or within its life plus about a day (the next
    /// retention pass).
    /// </summary>
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>A scope: lower-case letters, digits and '-', 1 to 40 characters.</summary>
    public static bool IsValidScope(string? scope) =>
        scope is { Length: > 0 and <= 40 } && scope.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    /// <summary>Checks an issue's arguments, as every adapter must before it writes anything.</summary>
    public static void RequireIssue(string scope, string nonce, string key, TimeSpan life, int maxPerKey)
    {
        RequireConsume(scope, nonce, key);
        if (life <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(life), "A form nonce must live for some time.");
        if (maxPerKey < 1) throw new ArgumentOutOfRangeException(nameof(maxPerKey), "A key holds at least one form nonce.");
    }

    /// <summary>Checks a consume's arguments, as every adapter must before it deletes anything.</summary>
    public static void RequireConsume(string scope, string nonce, string key)
    {
        if (!IsValidScope(scope)) throw new ArgumentException($"A form nonce scope is 1 to 40 lower-case letters, digits or '-' (\"{scope}\").", nameof(scope));
        ArgumentException.ThrowIfNullOrEmpty(nonce);
        ArgumentException.ThrowIfNullOrEmpty(key);
    }

    /// <summary>
    /// Whether a consumed nonce's row, issued for the key whose hash is <paramref name="subject"/> and expiring at
    /// <paramref name="expiresAt"/>, accepts the form posted by <paramref name="key"/> at <paramref name="now"/>: the same key, and not yet
    /// expired. The row is gone either way.
    /// </summary>
    public static bool Accepts(string subject, DateTimeOffset expiresAt, string key, DateTimeOffset now) =>
        string.Equals(subject, Hash(key), StringComparison.Ordinal) && expiresAt > now;
}

/// <summary>
/// Form nonces in this process alone, for where there is no database (test hosts with fakes). Every server has a database and keeps
/// them there: the composition root registers this only when there is none.
/// </summary>
public sealed class InProcessFormNonceStore : IFormNonceStore
{
    private readonly IClock _clock;
    private readonly Dictionary<(string Scope, string Hash), (string Subject, DateTimeOffset Expires)> _nonces = new();
    private readonly object _lock = new();

    public InProcessFormNonceStore(IClock clock) => _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public Task IssueAsync(string scope, string nonce, string key, TimeSpan life, int maxPerKey, CancellationToken ct = default)
    {
        FormNonceRules.RequireIssue(scope, nonce, key, life, maxPerKey);
        var now = _clock.UtcNow;
        var subject = FormNonceRules.Hash(key);
        lock (_lock)
        {
            var mine = _nonces.Where(e => e.Key.Scope == scope && e.Value.Subject == subject).OrderBy(e => e.Value.Expires).Select(e => e.Key).ToList();
            foreach (var k in mine.Take(Math.Max(0, mine.Count - (maxPerKey - 1)))) _nonces.Remove(k);
            _nonces[(scope, FormNonceRules.Hash(nonce))] = (subject, now + life);
            if (_nonces.Count > 20_000)
                foreach (var k in _nonces.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList()) _nonces.Remove(k);
        }
        return Task.CompletedTask;
    }

    public Task<bool> ConsumeAsync(string scope, string nonce, string key, CancellationToken ct = default)
    {
        FormNonceRules.RequireConsume(scope, nonce, key);
        lock (_lock)
        {
            if (!_nonces.Remove((scope, FormNonceRules.Hash(nonce)), out var e)) return Task.FromResult(false);
            return Task.FromResult(FormNonceRules.Accepts(e.Subject, e.Expires, key, _clock.UtcNow));
        }
    }

    public Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var expired = _nonces.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList();
            foreach (var k in expired) _nonces.Remove(k);
            return Task.FromResult(expired.Count);
        }
    }
}
