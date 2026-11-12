namespace Packmoat.Infrastructure;

/// <summary>
/// Which database the server and the admin tool use (PACKMOAT_DB_ENGINE): <see cref="Sqlite"/> (the default) or
/// <see cref="Postgres"/>.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public enum DatabaseEngine
{
    /// <summary>SQLite on local disk (PACKMOAT_DB names the file). The default.</summary>
    Sqlite,

    /// <summary>
    /// PostgreSQL 17 (PACKMOAT_DB is a connection string): the server signs in as an app role that the owner
    /// role's <c>packmoat-admin db migrate --grant-to</c> granted, and refuses to start on any of the start's refusals (PostgresStartupChecks).
    /// </summary>
    Postgres,
}

/// <summary>
/// Reads PACKMOAT_DB_ENGINE: <c>sqlite</c> or <c>postgres</c>, sqlite when unset or empty, which is exactly the behaviour
/// before the setting existed. Strict: anything else stops the start, naming the setting and never the value. Not registered in DI.
/// </summary>
public static class DatabaseEngines
{
    public const string Variable = "PACKMOAT_DB_ENGINE";

    /// <summary>The engine <paramref name="value"/> names; unset or empty is <see cref="DatabaseEngine.Sqlite"/>. Anything else throws.</summary>
    public static DatabaseEngine Parse(string? value) => value switch
    {
        null or "" or "sqlite" => DatabaseEngine.Sqlite,
        "postgres" => DatabaseEngine.Postgres,
        _ => throw new SettingsException($"{Variable} must be sqlite or postgres."),
    };

    /// <summary>
    /// The engine <paramref name="value"/> names, when this build can run it (see <see cref="Parse"/>). It runs both,
    /// sqlite and postgres; the name stays so an engine named before its store exists can be refused here again.
    /// </summary>
    public static DatabaseEngine RequireAvailable(string? value) => Parse(value);

    /// <summary>Whether <paramref name="engine"/>'s PACKMOAT_DB names a file on local disk (SQLite's does; a connection string does not), so
    /// a folder can sit next to it. Exhaustive: a new engine must decide.</summary>
    public static bool IsFile(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.Sqlite => true,
        DatabaseEngine.Postgres => false,
        _ => throw new ArgumentOutOfRangeException(nameof(engine)),
    };

    /// <summary>The setting's own word for <paramref name="engine"/>.</summary>
    public static string Name(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.Sqlite => "sqlite",
        DatabaseEngine.Postgres => "postgres",
        _ => throw new ArgumentOutOfRangeException(nameof(engine)),
    };
}
