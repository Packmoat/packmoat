namespace Packmoat.Host;

/// <summary>
/// Whether this server runs as production: exactly when a database is configured (PACKMOAT_DB set), as before the
/// switch had a name. In production the server answers only its own hostnames, so PACKMOAT_HOSTS must be set. Not a setting of its own
/// and not registered in DI; separating it from PACKMOAT_DB comes later.
/// </summary>
public static class ProductionMode
{
    /// <summary>True when <paramref name="options"/> configure a database (<see cref="PackmoatOptions.Database"/> is not null).</summary>
    public static bool On(PackmoatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Database is not null;
    }
}
