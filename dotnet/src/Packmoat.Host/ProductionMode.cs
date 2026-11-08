namespace Packmoat.Host;

/// <summary>
/// Whether this server runs as production (#278 step 0, A5): exactly when a database is configured (PACKMOAT_DB set), as before the
/// switch had a name. In production the server answers only its own hostnames, so PACKMOAT_HOSTS must be set. Not a setting of its own
/// and not registered in DI; separating it from PACKMOAT_DB comes later (#232 P2).
/// </summary>
public static class ProductionMode
{
    /// <summary>True when <paramref name="options"/> configure a database (<see cref="PackmoatOptions.DatabasePath"/> is not null).</summary>
    public static bool On(PackmoatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.DatabasePath is not null;
    }
}
