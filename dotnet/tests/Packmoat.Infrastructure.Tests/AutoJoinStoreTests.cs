using Microsoft.Data.Sqlite;
using Packmoat.Application;

namespace Packmoat.Infrastructure.Tests;

/// <summary><see cref="AutoJoinStoreContract"/> on SQLite, and the tests only SQLite has (#232 S1).</summary>
[Collection("sqlite-pools")]
public class AutoJoinStoreTests : AutoJoinStoreContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    [Fact]
    public async Task Migration_11_on_a_v10_database_adds_an_empty_table_so_every_existing_tenant_is_on()
    {
        await using var t = new TestDb();
        await t.Db.MigrateAsync(10);
        foreach (var id in new[] { "canary", "e2e-test", "e2e-shared" }) await t.AddLegacyTenantAsync(id);
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='auto_join_settings'"));

        await t.Db.MigrateAsync([]);
        Assert.True(SqliteDatabase.LatestVersion >= 11);
        Assert.Equal((long)SqliteDatabase.LatestVersion, await ScalarAsync(t, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='auto_join_settings'"));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM auto_join_settings"));
        var store = new SqliteAutoJoinSettingStore(t.Db);
        foreach (var id in new[] { "canary", "e2e-test", "e2e-shared" }) Assert.True(await store.IsOnAsync(id, default));
        await t.Db.MigrateAsync([]);   // running again changes nothing
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM auto_join_settings"));
    }

    // Moved from the contract (#303): SQLite stores 'on' as text, so its CHECK refuses it. PostgreSQL cannot read 'on' as a bigint at all
    // (22P02, before any CHECK), which is not a refusal there.
    [Fact]
    public async Task The_schema_refuses_on_as_the_flag()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        await RefusedAsync(() => ScalarAsync(t, "INSERT INTO auto_join_settings(tenant_id, enabled, changed_at, changed_by) VALUES('acme', 'on', '2026-01-01T00:00:00.0000000+00:00', 'y')"));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM auto_join_settings"));
    }

    [Fact]
    public async Task A_read_that_fails_throws_and_is_never_read_as_on()
    {
        await using var t = new TestDb();
        await t.Db.MigrateAsync(10);   // no table: every read fails
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteAutoJoinSettingStore(t.Db).IsOnAsync("acme", default));
    }
}
