using System.Globalization;
using Microsoft.Data.Sqlite;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// <see cref="WaiverStoreContract"/> on PostgreSQL, each test on a database of its own. Its cap test
/// (<see cref="WaiverStoreContract.Twenty_six_concurrent_customer_inserts_leave_exactly_twenty_five"/>) is the waiver cap's race test:
/// it fails without the tenant row's FOR NO KEY UPDATE.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresWaiverStoreTests : WaiverStoreContract
{
    protected override IStoreEngine Engine => PostgresStoreEngine.Instance;
}

/// <summary><see cref="WaiverStoreContract"/> on SQLite, and the tests only SQLite has.</summary>
[Collection("sqlite-pools")]
public class WaiverStoreTests : WaiverStoreContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    private static async Task<object?> ScalarAsync(SqliteDatabase db, string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task ExecAsync(SqliteDatabase db, string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> ColumnAsync(SqliteDatabase db, string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var rows = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add(r.IsDBNull(0) ? "NULL" : Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture)!);
        return rows;
    }

    [Fact]   // mutation: migration 20's default 'customer'
    public async Task Migration_20_on_a_copy_of_a_v19_database_changes_no_row_and_makes_every_old_waiver_operator()
    {
        await using var t = new TestDb();
        await t.Db.MigrateAsync(19);
        await t.AddCoreTenantAsync();
        await t.Tenants.AddInternalPrefixAsync("acme", "Contoso.", Ct);
        await ExecAsync(t.Db, """
            INSERT INTO waivers(id, tenant_id, package_id, version, expires_at, reason, created_by, created_at, revoked_at) VALUES
              ('wvr_000000000001','acme','moq','4.21.0','2026-12-01T00:00:00.0000000+00:00','hotfix','olivia','2026-10-01T00:00:00.0000000+00:00',NULL),
              ('wvr_000000000002','acme','left.pad','*','2026-12-01T00:00:00.0000000+00:00','why','olivia','2026-10-02T00:00:00.0000000+00:00','2026-10-03T00:00:00.0000000+00:00');
            """);
        const string everyRow = "SELECT id || '|' || tenant_id || '|' || package_id || '|' || version || '|' || expires_at || '|' || reason || '|' || created_by || '|' || created_at || '|' || IFNULL(revoked_at, 'NULL') FROM waivers ORDER BY id";
        var before = await ColumnAsync(t.Db, everyRow);
        var revision = await ScalarAsync(t.Db, "SELECT policy_revision FROM tenants WHERE id='acme'");

        var copyPath = Path.Combine(t.Dir, "copy.db");
        await ExecAsync(t.Db, $"VACUUM INTO '{copyPath}'");
        var copy = new SqliteDatabase(copyPath);
        await copy.MigrateAsync(20);
        Assert.True(SqliteDatabase.LatestVersion >= 20);
        Assert.Equal(20L, await ScalarAsync(copy, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal("ok", await ScalarAsync(copy, "PRAGMA integrity_check"));

        Assert.Equal(before, await ColumnAsync(copy, everyRow));
        Assert.Equal(new[] { "operator", "operator" }, await ColumnAsync(copy, "SELECT source FROM waivers ORDER BY id"));
        Assert.Equal(new[] { "NULL", "NULL" }, await ColumnAsync(copy, "SELECT revoked_by FROM waivers ORDER BY id"));
        Assert.Equal(revision, await ScalarAsync(copy, "SELECT policy_revision FROM tenants WHERE id='acme'"));
        Assert.Equal(1L, await ScalarAsync(copy, "SELECT \"notnull\" FROM pragma_table_info('waivers') WHERE name='source'"));
        Assert.Equal(0L, await ScalarAsync(copy, "SELECT \"notnull\" FROM pragma_table_info('waivers') WHERE name='revoked_by'"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecAsync(copy, "UPDATE waivers SET source='someone' WHERE id='wvr_000000000001'"));   // the CHECK

        var store = new SqliteTenantStore(copy, t.Clock, t.Protector, TestLifecycle.Participants);
        var tenant = (await store.GetAsync("acme", Ct))!;
        Assert.Equal(WaiverSource.Operator, Assert.Single(tenant.Policy.Waivers).Source);

        await copy.MigrateAsync(20);   // again: nothing to do
        Assert.Equal(20L, await ScalarAsync(copy, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(19L, await ScalarAsync(t.Db, "SELECT MAX(version) FROM schema_migrations"));   // the original is untouched
    }

    [Fact]   // mutation: migration 20 applied statement by statement outside one transaction
    public async Task Migration_20_is_atomic_a_failure_part_way_leaves_no_source_column_and_no_version()
    {
        await using var t = new TestDb();
        await t.Db.MigrateAsync(19);
        // A column already holding source's name makes the migration's second statement fail, after revoked_by was added.
        await ExecAsync(t.Db, "ALTER TABLE waivers ADD COLUMN source TEXT NULL");

        await Assert.ThrowsAsync<SqliteException>(() => t.Db.MigrateAsync(20));

        Assert.Equal(19L, await ScalarAsync(t.Db, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(0L, await ScalarAsync(t.Db, "SELECT COUNT(*) FROM pragma_table_info('waivers') WHERE name='revoked_by'"));
    }

    [Fact]
    public async Task Why_migration_20_adds_the_checked_column_last_an_error_after_a_checked_add_column_is_not_reported()
    {
        // Microsoft.Data.Sqlite does not report an error from a statement that follows an ADD COLUMN with a CHECK in the same command text
        // (found while writing the test above). So a migration puts such an ADD COLUMN last, where its own failure is reported. If this
        // ever starts throwing, the library changed and the ordering rule can be relaxed.
        await using var t = new TestDb();
        await t.Db.MigrateAsync(19);
        await ExecAsync(t.Db, "ALTER TABLE waivers ADD COLUMN probe TEXT CHECK(probe IN ('a')); ALTER TABLE waivers ADD COLUMN reason TEXT;");   // reason exists: should fail
        Assert.Equal(1L, await ScalarAsync(t.Db, "SELECT COUNT(*) FROM pragma_table_info('waivers') WHERE name='probe'"));
    }

    [Fact]
    public async Task This_build_refuses_a_database_one_migration_newer_than_twenty_six()
    {
        await using var t = await TestDb.CreateAsync();
        Assert.Equal(26, SqliteDatabase.LatestVersion);   // migration 22 was added, then 23, then 24 (the rate counter), 25 (the form nonces), 26 (the feed setup scratch)
        await ExecAsync(t.Db, "INSERT INTO schema_migrations(version, applied_at) VALUES(27, 'later')");
        var ex = await Assert.ThrowsAsync<SchemaTooNewException>(() => t.Db.MigrateAsync(TestTracks.Modules));
        Assert.Contains("version 27, newer than this build knows (26)", ex.Message);
    }
}
