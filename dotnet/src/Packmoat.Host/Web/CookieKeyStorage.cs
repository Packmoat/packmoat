using Packmoat.Infrastructure;

namespace Packmoat.Host.Web;

/// <summary>Where the signed-in sites keep the keys that protect their cookies and antiforgery tokens (#232 B1, PACKMOAT_COOKIE_KEYS).</summary>
public enum CookieKeyStorage
{
    /// <summary>The key folder (PACKMOAT_ADMIN_KEYS_DIR, else admin-keys next to the database). The default, and exactly the behaviour
    /// before the setting existed.</summary>
    Folder,

    /// <summary>The database's cookie_keys table, encrypted with the master key, so every copy of the server shares the keys. The key folder
    /// is read once, as the import source, and never written.</summary>
    Database,
}

/// <summary>
/// Reads PACKMOAT_COOKIE_KEYS (#232 B1): <c>folder</c> or <c>database</c>, folder when unset or empty. Strict: anything else stops the
/// start, naming the setting and never the value. Not registered in DI.
/// </summary>
public static class CookieKeyStorages
{
    public const string Variable = "PACKMOAT_COOKIE_KEYS";

    /// <summary>The storage <paramref name="value"/> names; unset or empty is <see cref="CookieKeyStorage.Folder"/>. Anything else throws.</summary>
    public static CookieKeyStorage Parse(string? value) => value switch
    {
        null or "" or "folder" => CookieKeyStorage.Folder,
        "database" => CookieKeyStorage.Database,
        _ => throw new SettingsException($"{Variable} must be folder or database."),
    };

    /// <summary>
    /// The rule for where the keys may live (#278 step 0, D11), checked at start before anything is written: database storage needs a
    /// database, and an engine whose PACKMOAT_DB is no file needs database storage (there is no folder next to it,
    /// and its copies must share one set of keys). Pure: it reads nothing and writes nothing.
    /// </summary>
    public static void Require(CookieKeyStorage storage, DatabaseEngine engine, bool databaseConfigured)
    {
        if (storage == CookieKeyStorage.Database && !databaseConfigured)
            throw new SettingsException($"{Variable}=database needs a database: PACKMOAT_DB is not set.");
        if (storage == CookieKeyStorage.Folder && !DatabaseEngines.IsFile(engine))
            throw new SettingsException($"{DatabaseEngines.Variable}={DatabaseEngines.Name(engine)} needs {Variable}=database: its PACKMOAT_DB is no file to keep a key folder next to.");
    }
}
