using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using Packmoat.Application;

namespace Packmoat.Host.Web;

/// <summary>
/// The one-time move of the cookie keys from the key folder into the database (#232 B1), at the start of a server with
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
    /// </summary>
    public static async Task<int> RunAsync(ICookieKeyStore store, string? keysDir, ILogger log, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);
        var held = await ReadAllAsync(store, ct);
        if (held > 0)
        {
            log.LogInformation("Cookie keys are in the database, which holds {Count}: nothing imported from the key folder.", held);
            return 0;
        }
        var keys = Read(keysDir);
        var imported = await store.ImportIfEmptyAsync(keys, ct);
        // A warning: on a server that had signed-in users, an empty folder here usually means the folder was not mounted, and everyone
        // signed in before must sign in again.
        if (keys.Count == 0)
            log.LogWarning("Cookie keys are in the database, which holds none, and the key folder {Folder} has no key files: a new key is made, so " +
                "anyone signed in before signs in again. Start the first server with PACKMOAT_COOKIE_KEYS=database where the existing key folder is mounted.",
                keysDir ?? "(none)");
        else if (imported > 0) log.LogInformation("Cookie keys are in the database. Imported {Count} key files from the key folder {Folder}; the folder is left as it was.", imported, keysDir);
        else log.LogInformation("Cookie keys are in the database, which another server filled first: nothing imported from the key folder {Folder}.", keysDir);
        await ReadAllAsync(store, ct);
        return imported;
    }

    private static async Task<int> ReadAllAsync(ICookieKeyStore store, CancellationToken ct)
    {
        try { return (await store.GetAllAsync(ct)).Count; }
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
