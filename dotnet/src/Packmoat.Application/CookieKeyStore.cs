namespace Packmoat.Application;

/// <summary>
/// One cookie protection key: the name the key ring gives it and its XML. The XML is a secret (it can forge sessions), so the
/// record never prints it.
/// </summary>
public sealed record CookieKey(string FriendlyName, string Xml)
{
    public override string ToString() => $"CookieKey({FriendlyName}, <redacted>)";
}

/// <summary>
/// Where a server that keeps its cookie protection keys in the database (PACKMOAT_COOKIE_KEYS=database) stores them, so every
/// copy of the server reads the same keys. The adapter encrypts each key with the master key; a key it cannot read is an error, never
/// skipped. Plain strings only: the host parses the XML.
/// </summary>
public interface ICookieKeyStore
{
    /// <summary>Every key, oldest first, then by id, so the order is fixed. Throws when any key cannot be read: it never skips one.</summary>
    Task<IReadOnlyList<CookieKey>> GetAllAsync(CancellationToken ct);

    /// <summary>Stores one new key.</summary>
    Task AddAsync(string friendlyName, string xml, CancellationToken ct);

    /// <summary>
    /// The one-time import from the key folder: in one transaction, stores all of <paramref name="keys"/> when the store holds none and
    /// returns how many; when it holds any, stores nothing and returns 0. Two servers importing at once leave one set.
    /// </summary>
    Task<int> ImportIfEmptyAsync(IReadOnlyList<CookieKey> keys, CancellationToken ct);

    /// <summary>
    /// A new key, made only when the store has none the key ring can use: in one transaction, under the same rule as
    /// <see cref="ImportIfEmptyAsync"/>, reads every key and stores <paramref name="key"/> when <paramref name="usable"/> accepts none of
    /// them (an empty store, or every key expired), returning true; otherwise stores nothing and returns false. Of several servers doing
    /// this at once, one stores its key and the rest store nothing. Throws, storing nothing, when any key cannot be read.
    /// </summary>
    Task<bool> AddUnlessAnyAsync(CookieKey key, Func<CookieKey, bool> usable, CancellationToken ct);
}
