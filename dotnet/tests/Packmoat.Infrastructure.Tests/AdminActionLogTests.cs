using Microsoft.Data.Sqlite;
using Packmoat.Application;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// <see cref="AdminActionLogContract"/> on PostgreSQL, each test on a database of its own. Its prune tests are the
/// one-transaction prune's: a refusal by the 365-day floor reaches the caller and deletes nothing.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAdminActionLogTests : AdminActionLogContract
{
    protected override IStoreEngine Engine => PostgresStoreEngine.Instance;
}

/// <summary><see cref="AdminActionLogContract"/> on SQLite, and the tests only SQLite has.</summary>
[Collection("sqlite-pools")]
public class AdminActionLogTests : AdminActionLogContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    [Fact]
    public async Task The_table_is_append_only()
    {
        await using var db = await TestDb.CreateAsync();
        var log = new SqliteAdminActionLog(db.Db);
        await log.AppendAsync(Row(1), default);
        await using var c = await db.Db.OpenAsync();
        foreach (var sql in new[] { "UPDATE admin_actions SET detail='x'", "DELETE FROM admin_actions" })
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            var ex = await Assert.ThrowsAsync<SqliteException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Contains("append-only", ex.Message);
        }
        Assert.Single(await log.LatestAsync(10, default));
    }

    [Fact]
    public async Task Migration_five_adds_the_table_to_an_older_database_without_touching_it()
    {
        await using var db = await TestDb.CreateAsync();
        await db.AddTenantAsync();
        await db.Db.MigrateAsync(TestTracks.Modules);   // again: already applied, a no-op
        await using var c = await db.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM schema_migrations WHERE version=5), (SELECT COUNT(*) FROM tenants)";
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal((1L, 1L), (r.GetInt64(0), r.GetInt64(1)));
    }
}
