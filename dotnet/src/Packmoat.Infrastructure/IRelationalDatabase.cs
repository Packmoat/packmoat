namespace Packmoat.Infrastructure;

/// <summary>
/// The core's database as the module seams give it (#278 step 0): engine-neutral, so a module's signatures name no engine's types. It says
/// only which engine it is. A module whose stores run on one engine turns it back into that engine's own database with the engine's one
/// checked conversion (for SQLite, <see cref="SqliteDatabase.From"/>), which refuses any other engine, naming it.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public interface IRelationalDatabase
{
    /// <summary>Which engine this database is (PACKMOAT_DB_ENGINE).</summary>
    DatabaseEngine Engine { get; }
}
