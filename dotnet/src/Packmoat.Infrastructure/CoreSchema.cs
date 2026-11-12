namespace Packmoat.Infrastructure;

/// <summary>
/// The core's migration track on every engine: SQLite's statements (<see cref="SqliteDatabase"/>, frozen by hash, never moved)
/// and PostgreSQL's (<see cref="PostgresCoreMigrations"/>), with the same 26 versions. <see cref="SqliteDatabase.Core"/> is this track.
/// A core migration added for one engine and not the other stops the track being made (the constructor refuses unequal counts), so no
/// build can ship one.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public static class CoreSchema
{
    /// <summary>The core's track (table schema_migrations), with statements for every engine.</summary>
    public static MigrationTrack Track { get; } = new(MigrationTrack.CoreName, new Dictionary<DatabaseEngine, string[][]>
    {
        [DatabaseEngine.Sqlite] = SqliteDatabase.Migrations,
        [DatabaseEngine.Postgres] = PostgresCoreMigrations.Statements,
    });
}
