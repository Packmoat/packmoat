using System.Globalization;
using Microsoft.Data.Sqlite;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

/// <summary><see cref="DecisionRecordStoreContract"/> on SQLite, and the tests only SQLite has (#232 S1).</summary>
[Collection("sqlite-pools")]
public class DecisionRecordStoreTests : DecisionRecordStoreContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    private static async Task<object?> Scalar(SqliteDatabase db, string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task Exec(SqliteDatabase db, string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> Columns(SqliteDatabase db, string table)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid";
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list;
    }

    [Fact]
    public async Task Migration_17_on_a_copy_of_a_v16_database_adds_an_empty_decision_table_and_changes_no_row()
    {
        await using var t = new TestDb();
        await t.Db.MigrateAsync(16);
        await t.AddCoreTenantAsync("acme");
        await t.Tokens.IssueAsync("acme", "ci", Ct);
        await t.Audit.AppendAsync(new AuditEvent(T0, "r1", "acme", "tok", AuditKind.Download, "moq", "4.21.0", Decision.Hide, VerdictReason.Cooldown, null), Ct);
        var tablesBefore = await Scalar(t.Db, "SELECT group_concat(name, ',') FROM (SELECT name FROM sqlite_master WHERE type='table' ORDER BY name)");
        const string rows = "SELECT (SELECT group_concat(id || cooldown_days || policy_revision, ';') FROM tenants) || '|' || (SELECT group_concat(id || label, ';') FROM tokens) || '|' || (SELECT group_concat(id || reason, ';') FROM audit_events)";
        var before = await Scalar(t.Db, rows);

        // On a copy, as on the server (the deploy takes a backup first): the original stays at 16.
        var copyPath = Path.Combine(t.Dir, "copy.db");
        await Exec(t.Db, $"VACUUM INTO '{copyPath}'");
        var copy = new SqliteDatabase(copyPath);
        await copy.MigrateAsync(17);

        Assert.True(SqliteDatabase.LatestVersion >= 17);
        Assert.Equal(17L, await Scalar(copy, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal("ok", await Scalar(copy, "PRAGMA integrity_check"));
        Assert.Null(await Scalar(copy, "PRAGMA foreign_key_check"));
        Assert.Equal(before, await Scalar(copy, rows));
        Assert.Equal(tablesBefore + ",", ((string)(await Scalar(copy, "SELECT group_concat(name, ',') FROM (SELECT name FROM sqlite_master WHERE type='table' AND name <> 'decision_records' ORDER BY name)"))!) + ",");
        Assert.Equal(0L, await Scalar(copy, "SELECT COUNT(*) FROM decision_records"));
        Assert.Equal(new[] { "tenant_id", "ecosystem", "package_id", "version", "decision", "reason", "waiver_id", "cooldown_days", "published_at",
                             "policy_revision", "first_seen_at", "last_seen_at", "seen_count" }, await Columns(copy, "decision_records"));
        Assert.Contains("WITHOUT ROWID", (string)(await Scalar(copy, "SELECT sql FROM sqlite_master WHERE name='decision_records'"))!);
        Assert.Equal("tenant_id,last_seen_at", await Scalar(copy, "SELECT group_concat(name, ',') FROM (SELECT name FROM pragma_index_info('ix_decision_records_tenant_seen') ORDER BY seqno)"));
        Assert.Equal("last_seen_at", await Scalar(copy, "SELECT group_concat(name, ',') FROM pragma_index_info('ix_decision_records_seen')"));
        Assert.Contains("ix_decision_records_seen", await PlanAsync(copy, "DELETE FROM decision_records WHERE last_seen_at < '2026'"));   // the prune uses it

        await copy.MigrateAsync(17);   // again: nothing changes
        Assert.Equal(17L, await Scalar(copy, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(16L, await Scalar(t.Db, "SELECT MAX(version) FROM schema_migrations"));
    }

    [Fact]
    public async Task A_batch_is_written_in_short_chunked_transactions_and_an_empty_batch_is_a_no_op()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync("acme");
        var store = new SqliteDecisionRecordLog(t.Db);
        await store.UpsertAsync(Array.Empty<DecisionRecord>(), Ct);

        var size = SqliteDecisionRecordLog.ChunkSize;
        Assert.InRange(size, 1000, 2000);
        var many = Enumerable.Range(0, size * 2 + 500).Select(i => Rec(version: $"1.0.{i}")).ToList();
        await store.UpsertAsync(many, Ct);
        Assert.Equal((long)many.Count, await Scalar(t.Db, "SELECT COUNT(*) FROM decision_records"));

        // One bad row (seen_count must be at least 1) in the second chunk: the first chunk is kept, the bad chunk is rolled back whole
        // and nothing after it is written.
        var bad = Enumerable.Range(0, size * 3).Select(i => Rec(version: $"2.0.{i}", count: i == size + 5 ? 0 : 1)).ToList();
        await Assert.ThrowsAsync<SqliteException>(() => store.UpsertAsync(bad, Ct));
        Assert.Equal((long)size, await Scalar(t.Db, "SELECT COUNT(*) FROM decision_records WHERE version LIKE '2.0.%'"));
        Assert.Equal(0L, await Scalar(t.Db, $"SELECT COUNT(*) FROM decision_records WHERE version = '2.0.{size}'"));
    }

    private static async Task<string> PlanAsync(SqliteDatabase db, string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        var lines = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) lines.Add(r.GetString(3));
        return string.Join("; ", lines);
    }
}
