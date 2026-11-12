using Packmoat.Application;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// <see cref="JobLeaseContract"/> on PostgreSQL, each test on a database of its own. Its race test is the lease's two-writer
/// race: the one statement's ON CONFLICT lets exactly one of eight holders win.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresJobLeaseTests : JobLeaseContract
{
    protected override IStoreEngine Engine => PostgresStoreEngine.Instance;
}

/// <summary><see cref="JobLeaseContract"/> on SQLite, and migration 23, which only SQLite has.</summary>
[Collection("sqlite-pools")]
public class JobLeaseTests : JobLeaseContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    [Fact]   // mutations: put a row in either table; add a column; leave a table out of the purge's classification
    public async Task Migration_23_on_a_v22_database_adds_two_empty_tables_and_changes_no_row()
    {
        await using var t = new TestDb();
        await t.Db.MigrateAsync(22);
        await t.AddTenantAsync("acme");
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('job_leases','cookie_keys')"));

        await t.Db.MigrateAsync(TestTracks.Modules);
        Assert.Equal(26L, await ScalarAsync(t, "SELECT MAX(version) FROM schema_migrations"));   // and 24 (the rate counter), 25 (the form nonces), 26 (the feed setup scratch)
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM job_leases"));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM cookie_keys"));
        Assert.Equal(["job|TEXT|1|1", "holder|TEXT|1|0", "expires_at|TEXT|1|0"],
            await ColumnAsync(t, "SELECT name || '|' || type || '|' || \"notnull\" || '|' || pk FROM pragma_table_info('job_leases') ORDER BY cid"));
        Assert.Equal(["id|TEXT|1|1", "friendly_name|TEXT|1|0", "created|TEXT|1|0", "xml_encrypted|BLOB|1|0"],
            await ColumnAsync(t, "SELECT name || '|' || type || '|' || \"notnull\" || '|' || pk FROM pragma_table_info('cookie_keys') ORDER BY cid"));
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM tenants WHERE id='acme'"));
        // Neither holds tenant data: the purge's guard knows both (an unclassified table refuses every purge).
        foreach (var table in new[] { "job_leases", "cookie_keys" })
            Assert.Equal(Lifecycle.PurgeTableFate.NoTenantData, Assert.Single(SqliteTenantPurgeStore.CoreTables, c => c.Table == table).Fate);
    }

    [Fact]   // a lease taken with the real clock: its time is UTC "O" text, one hour on
    public async Task The_lease_time_is_utc_O_text_an_hour_on()
    {
        await using var t = await TestDb.CreateAsync();
        t.Clock.UtcNow = new DateTimeOffset(2026, 11, 6, 9, 30, 0, TimeSpan.FromHours(-5));   // a local time: stored as UTC
        Assert.True(await new SqliteJobLease(t.Db, t.Clock).TryAcquireAsync(JobLeases.Retention, JobLeases.ProcessHolder, JobLeases.Ttl));
        Assert.Equal("2026-11-06T15:30:00.0000000+00:00", await ScalarAsync(t, "SELECT expires_at FROM job_leases"));
        Assert.Equal(TimeSpan.FromHours(1), JobLeases.Ttl);
        Assert.Matches("^[0-9a-f]{16}$", JobLeases.ProcessHolder);
    }
}
