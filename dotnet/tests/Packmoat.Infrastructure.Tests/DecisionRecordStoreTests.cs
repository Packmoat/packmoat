using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Postgres;
using Packmoat.Infrastructure.Tests.Postgres;
using static Packmoat.Infrastructure.Tests.Postgres.PostgresLockProbe;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// <see cref="DecisionRecordStoreContract"/> on PostgreSQL, each test on a database of its own, and what only PostgreSQL
/// has: the chunks, each its own transaction and retried whole; each chunk's rows taken in conflict-key order; and its tenants' locks
/// taken shared, in the audit batch's order, before any row.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresDecisionRecordStoreTests : DecisionRecordStoreContract
{
    protected override IStoreEngine Engine => PostgresStoreEngine.Instance;

    private static PostgresDatabase Db(IEngineDb t) => ((PostgresTestDb)t).Db;

    /// <summary>
    /// The chunks, as SQLite's (<see cref="DecisionRecordStoreTests"/>): the same size, each its own transaction. A bad record (seen_count
    /// must be at least 1) in the second chunk: the first chunk is kept, the second is rolled back whole, nothing after it is written, and
    /// the refusal is thrown, not retried.
    /// </summary>
    [PostgresFact]   // mutations: all the chunks in one transaction; the refusal caught; a chunk size other than SQLite's
    public async Task A_batch_is_written_in_chunks_each_its_own_transaction_and_a_failed_chunk_keeps_only_the_chunks_before_it()
    {
        Assert.Equal(SqliteDecisionRecordLog.ChunkSize, PostgresDecisionRecordLog.DefaultChunkSize);
        await using var t = await CreateDbAsync();
        var store = new PostgresDecisionRecordLog(Db(t), chunkSize: 3);
        await store.UpsertAsync(Array.Empty<DecisionRecord>(), Ct);
        Assert.Empty(await ListAsync(t, "acme"));

        await store.UpsertAsync(Enumerable.Range(0, 7).Select(i => Rec(version: $"1.0.{i}")).ToList(), Ct);
        Assert.Equal(7, (await ListAsync(t, "acme")).Count);

        var bad = Enumerable.Range(0, 9).Select(i => Rec(version: $"2.0.{i}", count: i == 4 ? 0 : 1)).ToList();
        var refusal = await RefusedAsync(() => store.UpsertAsync(bad, Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ((PostgresException)refusal).SqlState);
        Assert.Equal(new[] { "2.0.0", "2.0.1", "2.0.2" },
            (await ListAsync(t, "acme")).Select(r => r.Version).Where(v => v.StartsWith("2.", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A chunk's retry: a test trigger makes the second row of the first attempt fail as a deadlock (40P01). The
    /// whole chunk is run again and stored once: an upsert done outside the attempt's transaction would be counted twice. The sequence
    /// counts the rows tried: two in the failed attempt, three in the second.
    /// </summary>
    [PostgresFact]   // mutations: the retry dropped; an upsert outside the attempt's transaction
    public async Task A_chunk_that_deadlocks_is_retried_as_a_whole_and_stored_once()
    {
        await using var t = await CreateDbAsync();
        await ExecAsync(t, "CREATE SEQUENCE test_rows_tried");
        await ExecAsync(t, """
            CREATE FUNCTION test_deadlock_once() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF pg_catalog.nextval('test_rows_tried') = 2 THEN
                    RAISE EXCEPTION USING ERRCODE = 'deadlock_detected', MESSAGE = 'test: the first attempt deadlocks on its second row';
                END IF;
                RETURN NEW;
            END
            $$
            """);
        await ExecAsync(t, "CREATE TRIGGER test_deadlock_once BEFORE INSERT ON decision_records FOR EACH ROW EXECUTE FUNCTION test_deadlock_once()");

        await Engine.DecisionRecords(t).UpsertAsync(new[] { Rec(version: "1.0.0", count: 2), Rec(version: "2.0.0", count: 3), Rec(tenant: "globex", count: 4) }, Ct);

        Assert.Equal(new[] { ("1.0.0", 2L), ("2.0.0", 3L) },
            (await ListAsync(t, "acme")).Select(r => (r.Version, r.SeenCount)).OrderBy(r => r.Version, StringComparer.Ordinal));
        Assert.Equal(4L, Assert.Single(await ListAsync(t, "globex")).SeenCount);
        Assert.Equal(5L, await PostgresSqlEngine.TestServerScalarAsync(t, "SELECT last_value FROM test_rows_tried"));   // the probe's own sequence: the app role may only use it
    }

    /// <summary>
    /// A chunk's sort: rows "aaa" and "bbb" exist, and another session holds "bbb" FOR UPDATE. A chunk given (bbb, aaa) upserts
    /// in conflict-key order, so it has already updated "aaa", and holds it, when it waits for "bbb" (a probe with SKIP LOCKED finds "aaa"
    /// taken). Upserted in the order given, it would wait for "bbb" holding nothing, and two chunks given the two orders could each hold the
    /// row the other waits for. Once the holder lets go, the chunk finishes and both counts are added.
    /// </summary>
    [PostgresFact]   // mutation: the chunk upserted in the order given, unsorted
    public async Task A_chunk_takes_its_rows_in_conflict_key_order_whatever_order_it_was_given()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.DecisionRecords(t);
        await store.UpsertAsync(new[] { Rec(package: "aaa"), Rec(package: "bbb") }, Ct);
        await using var holder = await Db(t).OpenAsync();
        await using var holding = await holder.BeginTransactionAsync();
        await using (var take = new NpgsqlCommand("SELECT 1 FROM decision_records WHERE package_id = 'bbb' FOR UPDATE", holder, holding)) await take.ExecuteNonQueryAsync();

        var chunk = Task.Run(() => store.UpsertAsync(new[] { Rec(package: "bbb", count: 2), Rec(package: "aaa", count: 2) }, Ct));

        await WaitUntilAsync(holder, holding, "SELECT COUNT(DISTINCT l.pid) FROM pg_catalog.pg_locks l WHERE NOT l.granted AND " + Others, 1, chunk);
        Assert.False(chunk.IsCompleted, "the chunk finished while another session held the row bbb");
        Assert.Equal(0L, await CountAsync(holder, holding,
            "SELECT COUNT(*) FROM (SELECT 1 FROM decision_records WHERE package_id = 'aaa' FOR UPDATE SKIP LOCKED) AS free"));   // the chunk holds aaa

        await holding.CommitAsync();
        await chunk.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(new[] { ("aaa", 3L), ("bbb", 3L) },
            (await ListAsync(t, "acme")).Select(r => (r.PackageId, r.SeenCount)).OrderBy(r => r.PackageId, StringComparer.Ordinal));
    }

    /// <summary>
    /// The chunks' tenant locks, as the audit batch's: a purge holds tenant "acme"'s lock exclusively (lock_tenant_id('acme',
    /// true), as the purge takes it first). Two chunks start, one with records for (acme, Zed), the other for (Zed, acme). Each takes
    /// its tenants' locks shared, in ordinal order (<see cref="PostgresTenantLocks"/>: "Zed" before "acme", where a case-blind order puts
    /// "acme" first; the build's invariant globalization makes a culture's order ordinal too), before it writes a row: so while the purge holds "acme", both hold "Zed" shared, both wait for "acme", and
    /// neither has touched decision_records. Once the purge lets go, both finish. The waits are read from pg_locks, never slept for.
    /// </summary>
    [PostgresFact]   // mutations: the pre-lock dropped (a row is written before "acme" is waited for); the tenants sorted by another comparer
    public async Task Two_chunks_in_opposite_orders_wait_for_a_purges_tenant_holding_no_row_and_both_finish()
    {
        await using var t = await CreateDbAsync();
        await using var purge = await Db(t).OpenAsync();
        await using var holding = await purge.BeginTransactionAsync();
        await using (var take = new NpgsqlCommand("SELECT lock_tenant_id('acme', true)", purge, holding)) await take.ExecuteNonQueryAsync();

        var az = Task.Run(() => Engine.DecisionRecords(t).UpsertAsync(new[] { Rec(tenant: "acme", package: "az"), Rec(tenant: "Zed", package: "az") }, Ct));
        var za = Task.Run(() => Engine.DecisionRecords(t).UpsertAsync(new[] { Rec(tenant: "Zed", package: "za"), Rec(tenant: "acme", package: "za") }, Ct));

        // Both chunks wait for "acme" (a lock not granted), or one has ended, which the asserts below then report.
        await WaitUntilAsync(purge, holding, "SELECT COUNT(DISTINCT l.pid) FROM pg_catalog.pg_locks l WHERE NOT l.granted AND " + Others + " AND " + TenantLock("acme"),
            2, az, za);
        Assert.False(az.IsCompleted || za.IsCompleted, "a chunk finished while the purge held tenant acme's lock");
        Assert.Equal(2L, await CountAsync(purge, holding, "SELECT COUNT(DISTINCT l.pid) FROM pg_catalog.pg_locks l WHERE l.granted AND l.mode = 'ShareLock' AND "
            + Others + " AND " + TenantLock("Zed")));   // each took "Zed" first, so in ordinal order
        Assert.Equal(0L, await CountAsync(purge, holding, "SELECT COUNT(*) FROM pg_catalog.pg_locks l WHERE l.relation = 'decision_records'::regclass AND " + Others));

        await holding.CommitAsync();
        await Task.WhenAll(az, za).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(new[] { "az", "za" }, (await ListAsync(t, "acme")).Select(r => r.PackageId).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "az", "za" }, (await ListAsync(t, "Zed")).Select(r => r.PackageId).Order(StringComparer.Ordinal));
    }
}

/// <summary><see cref="DecisionRecordStoreContract"/> on SQLite, and the tests only SQLite has.</summary>
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
