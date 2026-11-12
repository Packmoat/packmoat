using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;
using Packmoat.Application;

namespace Packmoat.Host.Web;

/// <summary>
/// The one-time move of the cookie keys from the key folder into the database, at the start of a server with
/// PACKMOAT_COOKIE_KEYS=database. The folder is only read: never created, changed or deleted, so switching back to folder mode keeps the
/// keys it had. Once the database holds any key, nothing is imported again. No key's XML is ever logged or put in an error.
/// </summary>
internal static class CookieKeyImport
{
    /// <summary>The log category of the import's lines.</summary>
    public const string LogCategory = "Packmoat.CookieKeys";

    /// <summary>
    /// Reads every key in the database once, so a key the master key cannot read stops the start; when there is none, imports the folder's
    /// keys (the store imports only into an empty table, so two servers starting at once leave one set) and reads them back. The folder is
    /// read only while the database holds no key. A missing or empty folder imports nothing; a file that is not XML stops the start,
    /// naming the file.
    /// <para>When the import leaves the database empty, the first key is made here, before any key ring is read, and stored the same way:
    /// only into an empty table. Of several servers starting at once on an empty database, one stores its key and the others store nothing;
    /// every one then reads the keys back, so each key ring starts with the one key. Left to the key ring, each server would make its own
    /// first key and keep a ring without the others' (until it is read again, up to a day later), so a cookie made by one would not be read
    /// by another. <paramref name="newKey"/> makes the key (<see cref="NewKey"/> when null).</para>
    /// <para>The same holds when the database has keys but every one has expired (no server ran for longer than a key lives): the key
    /// ring would again make a key of its own in each server. So a new key is made here the same way, stored only if the database still
    /// holds no unexpired key (<see cref="Usable"/>, judged at <paramref name="now"/>, the time now when null).</para>
    /// </summary>
    public static async Task<int> RunAsync(ICookieKeyStore store, string? keysDir, ILogger log, CancellationToken ct = default, Func<CookieKey>? newKey = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);
        var held = await ReadAllAsync(store, ct);
        var imported = 0;
        if (held.Count > 0)
        {
            log.LogInformation("Cookie keys are in the database, which holds {Count}: nothing imported from the key folder.", held.Count);
        }
        else
        {
            var keys = Read(keysDir);
            imported = await store.ImportIfEmptyAsync(keys, ct);
            // A warning: on a server that had signed-in users, an empty folder here usually means the folder was not mounted, and everyone
            // signed in before must sign in again.
            if (keys.Count == 0)
                log.LogWarning("Cookie keys are in the database, which holds none, and the key folder {Folder} has no key files: a new key is made, so " +
                    "anyone signed in before signs in again. Start the first server with PACKMOAT_COOKIE_KEYS=database where the existing key folder is mounted.",
                    keysDir ?? "(none)");
            else if (imported > 0) log.LogInformation("Cookie keys are in the database. Imported {Count} key files from the key folder {Folder}; the folder is left as it was.", imported, keysDir);
            else log.LogInformation("Cookie keys are in the database, which another server filled first: nothing imported from the key folder {Folder}.", keysDir);
            held = await ReadAllAsync(store, ct);
        }
        // No key the ring can use (none at all, or every one expired): a new one, stored only if the database still holds no unexpired key
        // (another server's may be there already).
        var at = now ?? DateTimeOffset.UtcNow;
        if (!held.Any(k => Usable(k, at)))
        {
            if (held.Count > 0)
                log.LogWarning("Every one of the database's {Count} cookie keys has expired: a new key is made, so anyone signed in before signs in again.", held.Count);
            var key = (newKey ?? NewKey)();
            await Readable(() => store.AddUnlessAnyAsync(key, k => Usable(k, at), ct));   // it reads every key too
            // Read back: the key this server stored, or the one another stored first. A key this master key cannot read stops the start.
            await ReadAllAsync(store, ct);
        }
        return imported;
    }

    /// <summary>
    /// Whether the key ring can use <paramref name="key"/> at <paramref name="now"/> to protect: a key element whose expiration is after
    /// now and whose activation is no later than the ring's allowed clock difference (5 minutes) after now. Anything else (an expired
    /// key, a revocation, XML that is not a key) is not. A key the ring would use as its default needs no new one.
    /// </summary>
    internal static bool Usable(CookieKey key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(key);
        XElement element;
        try { element = XElement.Parse(key.Xml); }
        catch (XmlException) { return false; }
        if (element.Name.LocalName != "key") return false;
        return Date(element, "activationDate") is { } activation && activation <= now + RingClockSkew
            && Date(element, "expirationDate") is { } expiration && expiration > now;
    }

    /// <summary>How far ahead of now the key ring takes a key's activation as already active (its default resolver's clock allowance).</summary>
    internal static readonly TimeSpan RingClockSkew = TimeSpan.FromMinutes(5);

    private static DateTimeOffset? Date(XElement key, string name) =>
        key.Element(name)?.Value is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : null;

    /// <summary>
    /// A new cookie key, as the key ring would make its first one (the default algorithms, active now, for the default 90 days), made with a
    /// key manager of its own that only hands the key back: nothing is stored by it.
    /// </summary>
    internal static CookieKey NewKey() => NewKey(null);

    /// <summary>As <see cref="NewKey()"/>, active from <paramref name="activation"/> (now when null) for the default life (tests make expired keys with it).</summary>
    internal static CookieKey NewKey(DateTimeOffset? activation)
    {
        var made = new KeptElement();
        var services = new ServiceCollection();
        services.AddDataProtection();
        services.Configure<KeyManagementOptions>(o => o.XmlRepository = made);
        using var provider = services.BuildServiceProvider();
        var lifetime = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.NewKeyLifetime;
        var now = activation ?? DateTimeOffset.UtcNow;
        provider.GetRequiredService<IKeyManager>().CreateNewKey(now, now + lifetime);
        var (element, name) = made.Single ?? throw new InvalidOperationException("The key manager stored no key.");
        return new CookieKey(name, element.ToString(SaveOptions.DisableFormatting));
    }

    /// <summary>A repository that keeps the one element stored to it, and reads as empty.</summary>
    private sealed class KeptElement : IXmlRepository
    {
        public (XElement Element, string Name)? Single { get; private set; }
        public IReadOnlyCollection<XElement> GetAllElements() => [];
        public void StoreElement(XElement element, string friendlyName) =>
            Single = Single is null ? (element, friendlyName) : throw new InvalidOperationException("The key manager stored more than one element.");
    }

    private static Task<IReadOnlyList<CookieKey>> ReadAllAsync(ICookieKeyStore store, CancellationToken ct) => Readable(() => store.GetAllAsync(ct));

    /// <summary>A store call that reads every key, with a key this master key cannot read made the start's one refusal.</summary>
    private static async Task<T> Readable<T>(Func<Task<T>> read)
    {
        try { return await read(); }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("A cookie key in the database cannot be read with this master key. Refusing to start: use the master key this database was made with.");
        }
    }

    /// <summary>The folder's *.xml files, by name (ordinal), each as its file name without the extension and its XML.</summary>
    internal static IReadOnlyList<CookieKey> Read(string? keysDir)
    {
        if (keysDir is null || !Directory.Exists(keysDir)) return [];
        var keys = new List<CookieKey>();
        foreach (var file in Directory.GetFiles(keysDir, "*.xml").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            XElement element;
            try { element = XElement.Parse(File.ReadAllText(file)); }
            catch (XmlException)
            {
                // The parser's message can quote the file, so it is left out (and so is the exception): the file's name only.
                throw new InvalidOperationException($"The cookie key file {name} in {keysDir} is not valid XML. Refusing to start: restore it from a backup or remove it, then start again.");
            }
            keys.Add(new CookieKey(Path.GetFileNameWithoutExtension(file), element.ToString(SaveOptions.DisableFormatting)));
        }
        return keys;
    }
}
