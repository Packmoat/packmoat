using Microsoft.Data.Sqlite;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// A migration track's statements per engine: every engine has the same versions, a missing engine is refused, and
/// the SQLite-only constructor is the per-engine one with SQLite alone.
/// </summary>
[Collection("sqlite-pools")]
public class MigrationTrackEngineTests
{
    private static readonly string[][] Three = [["CREATE TABLE a(x)"], [], ["CREATE TABLE b(x)"]];
    private static readonly string[][] ThreePg = [["CREATE TABLE a(x int)"], [], ["CREATE TABLE b(x int)"]];

    [Fact]   // mutation: the count check deleted
    public void Unequal_counts_are_refused_naming_the_track_and_the_counts()
    {
        var ex = Assert.Throws<ArgumentException>(() => new MigrationTrack("probe", new Dictionary<DatabaseEngine, string[][]>
        {
            [DatabaseEngine.Sqlite] = Three,
            [DatabaseEngine.Postgres] = [["CREATE TABLE a(x int)"]],
        }));
        Assert.Contains("probe", ex.Message);
        Assert.Contains("3", ex.Message);
        Assert.Contains("1", ex.Message);
    }

    [Fact]
    public void A_track_with_no_engine_is_refused() =>
        Assert.Throws<ArgumentException>(() => new MigrationTrack("probe", new Dictionary<DatabaseEngine, string[][]>()));

    [Fact]   // mutation: StatementsFor returns the SQLite statements for any engine
    public void A_missing_engine_throws_and_each_engine_gets_its_own()
    {
        var sqliteOnly = new MigrationTrack("probe", Three);
        var ex = Assert.Throws<SchemaTrackException>(() => sqliteOnly.StatementsFor(DatabaseEngine.Postgres));
        Assert.Contains("probe", ex.Message);
        Assert.Contains("postgres", ex.Message);

        var both = new MigrationTrack("probe", new Dictionary<DatabaseEngine, string[][]> { [DatabaseEngine.Sqlite] = Three, [DatabaseEngine.Postgres] = ThreePg });
        Assert.Same(Three, both.StatementsFor(DatabaseEngine.Sqlite));
        Assert.Same(ThreePg, both.StatementsFor(DatabaseEngine.Postgres));
        Assert.Equal([DatabaseEngine.Sqlite, DatabaseEngine.Postgres], both.Engines);   // mutation: Engines always [Sqlite]

        var postgresOnly = new MigrationTrack("probe", new Dictionary<DatabaseEngine, string[][]> { [DatabaseEngine.Postgres] = ThreePg });
        Assert.Throws<SchemaTrackException>(() => postgresOnly.StatementsFor(DatabaseEngine.Sqlite));
    }

    [Fact]
    public void The_old_constructor_is_a_sqlite_only_track()
    {
        var baseline = new TrackBaseline(2, 1, 3, ["a"], []);
        var old = new MigrationTrack("probe", Three, baseline);
        var perEngine = new MigrationTrack("probe", new Dictionary<DatabaseEngine, string[][]> { [DatabaseEngine.Sqlite] = Three }, baseline);

        Assert.Equal([DatabaseEngine.Sqlite], old.Engines);
        Assert.Equal(perEngine.Engines, old.Engines);
        Assert.Same(perEngine.StatementsFor(DatabaseEngine.Sqlite), old.StatementsFor(DatabaseEngine.Sqlite));
        Assert.Equal(perEngine.Name, old.Name);
        Assert.Equal(perEngine.Table, old.Table);
        Assert.Equal(perEngine.LatestVersion, old.LatestVersion);
        Assert.Same(perEngine.Baseline, old.Baseline);
    }

    [Fact]
    public void The_latest_version_is_the_count()
    {
        Assert.Equal(3, new MigrationTrack("probe", Three).LatestVersion);
        Assert.Equal(3, new MigrationTrack("probe", new Dictionary<DatabaseEngine, string[][]> { [DatabaseEngine.Sqlite] = Three, [DatabaseEngine.Postgres] = ThreePg }).LatestVersion);
        Assert.Equal(0, new MigrationTrack("probe", []).LatestVersion);
        Assert.Equal(SqliteDatabase.LatestVersion, SqliteDatabase.Core.LatestVersion);
    }

    [Fact]   // mutation: the per-engine baseline check counts something else
    public void A_baseline_past_the_count_is_still_refused() =>
        Assert.Throws<ArgumentException>(() => new MigrationTrack("probe",
            new Dictionary<DatabaseEngine, string[][]> { [DatabaseEngine.Sqlite] = Three, [DatabaseEngine.Postgres] = ThreePg }, new TrackBaseline(4, 1, 3, [], [])));

    [Fact]   // mutation: MigrateAsync asks for a track's SQLite statements only when it reaches it
    public async Task Migrating_a_track_with_no_sqlite_statements_throws_before_any_track_table_is_made()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-track-engine-").FullName;
        try
        {
            var db = new SqliteDatabase(Path.Combine(dir, "packmoat.db"));
            var postgresOnly = new MigrationTrack("probe", new Dictionary<DatabaseEngine, string[][]> { [DatabaseEngine.Postgres] = ThreePg });

            var ex = await Assert.ThrowsAsync<SchemaTrackException>(() => db.MigrateAsync([postgresOnly]));
            Assert.Contains("probe", ex.Message);

            await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "packmoat.db"), Pooling = false }.ToString());
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table'";
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);   // not even the core's schema_migrations
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }
}
