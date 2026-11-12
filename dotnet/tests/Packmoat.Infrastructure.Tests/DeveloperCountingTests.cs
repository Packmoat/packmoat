using Packmoat.Application;
using Packmoat.Infrastructure;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// <see cref="DeveloperCountingContract"/> on PostgreSQL, each test on a database of its own. The session's time zone is
/// Pacific/Chatham (UTC+12:45 or +13:45) on every test connection, so a day that moved with it would be off by one here.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresDeveloperCountingTests : DeveloperCountingContract
{
    protected override IStoreEngine Engine => PostgresStoreEngine.Instance;
}

/// <summary><see cref="DeveloperCountingContract"/> on SQLite, and the tests only SQLite has.</summary>
[Collection("sqlite-pools")]
public class DeveloperCountingTests : DeveloperCountingContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    [Fact]
    public async Task A_row_written_without_a_kind_column_reads_back_as_developer()
    {
        // What an existing database looks like after the migration: the column's default fills every old row.
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync();
        await using (var c = await t.Db.OpenAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO tokens(id, tenant_id, token_hash, display_prefix, label, created_at) VALUES('tok_old','acme','h','pmt_xxxx','old','2026-01-01T00:00:00.0000000+00:00')";
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Equal(TokenKind.Developer, (await t.Tokens.ListAsync("acme", default)).Single().Kind);
    }

    [Fact]
    public async Task The_kind_column_refuses_values_other_than_the_two_kinds()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync();
        await using var c = await t.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO tokens(id, tenant_id, token_hash, display_prefix, label, created_at, kind) VALUES('tok_x','acme','h','p','l','2026-01-01','robot')";
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => cmd.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Only_a_token_id_and_a_date_are_stored_and_audit_rows_carry_exactly_the_known_columns()
    {
        await using var t = await TestDb.CreateAsync();
        await using var c = await t.Db.OpenAsync();
        // Exact lists: adding a column (an address, a user agent, a label) must be a deliberate change to this test.
        Assert.Equal(new[] { "token_id", "day" }, await Columns(c, "token_activity"));
        Assert.Equal(new[] { "id", "time", "request_id", "tenant_id", "token_id", "kind", "package_id", "version", "decision", "reason", "waiver_id" }, await Columns(c, "audit_events"));
    }

    private static async Task<List<string>> Columns(Microsoft.Data.Sqlite.SqliteConnection c, string table)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid";
        var cols = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) cols.Add(r.GetString(0));
        return cols;
    }
}
