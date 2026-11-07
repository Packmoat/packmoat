namespace Packmoat.Infrastructure;

/// <summary>
/// Which database the server and the admin tool use (#232 P1, PACKMOAT_DB_ENGINE). Only <see cref="Sqlite"/> works in this build;
/// <see cref="Postgres"/> is named so the setting's meaning is fixed before its store exists, and is refused at start.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public enum DatabaseEngine
{
    /// <summary>SQLite on local disk (PACKMOAT_DB). The default, and the only engine this build can run.</summary>
    Sqlite,

    /// <summary>PostgreSQL. Not available in this build yet: the server and the admin tool refuse to start on it.</summary>
    Postgres,
}

/// <summary>
/// Reads PACKMOAT_DB_ENGINE (#232 P1): <c>sqlite</c> or <c>postgres</c>, sqlite when unset or empty, which is exactly the behaviour
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

    /// <summary>The engine <paramref name="value"/> names, when this build can run it (see <see cref="Parse"/>); a known engine this build
    /// cannot run stops the start too.</summary>
    public static DatabaseEngine RequireAvailable(string? value)
    {
        var engine = Parse(value);
        if (engine != DatabaseEngine.Sqlite) throw new SettingsException(NotAvailable(engine));
        return engine;
    }

    /// <summary>"PACKMOAT_DB_ENGINE=postgres is not available in this build yet."</summary>
    public static string NotAvailable(DatabaseEngine engine) => $"{Variable}={Name(engine)} is not available in this build yet.";

    /// <summary>The setting's own word for <paramref name="engine"/>.</summary>
    public static string Name(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.Sqlite => "sqlite",
        DatabaseEngine.Postgres => "postgres",
        _ => throw new ArgumentOutOfRangeException(nameof(engine)),
    };
}
