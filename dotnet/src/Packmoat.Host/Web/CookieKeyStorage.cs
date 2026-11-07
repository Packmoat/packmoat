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
}
