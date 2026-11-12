using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Npgsql;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Postgres;
using Packmoat.Infrastructure.Tests.Postgres;
using static Packmoat.Infrastructure.Tests.Postgres.PostgresLockProbe;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// <see cref="AuditRetentionStoreContract"/> on PostgreSQL, each test on a database of its own, and what only PostgreSQL
/// has: the batch's tenant locks taken shared, sorted, before any row; its retry, each attempt one whole transaction; the prune's chunks,
/// each its own transaction; and the database's clock skew.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresAuditRetentionStoreTests : AuditRetentionStoreContract
{
    protected override IStoreEngine Engine => PostgresStoreEngine.Instance;

    private static PostgresDatabase Db(IEngineDb t) => ((PostgresTestDb)t).Db;

    [PostgresFact]
    public async Task A_batch_that_fails_stores_nothing()
    {
        await using var t = await CreateDbAsync();
        await ExecAsync(t, """
            CREATE FUNCTION test_refuse() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.request_id = 'bad' THEN RAISE EXCEPTION 'refused'; END IF;
                RETURN NEW;
            END
            $$
            """);
        await ExecAsync(t, "CREATE TRIGGER test_refuse BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION test_refuse()");
        var now = DateTimeOffset.UtcNow;

        var refusal = await RefusedAsync(() => Engine.AuditBatch(t).AppendBatchAsync(new[] { Event(now, "a"), Event(now, "bad") }, Ct));

        Assert.Equal(PostgresErrorCodes.RaiseException, ((PostgresException)refusal).SqlState);   // a refusal is not retried
        Assert.Empty(await RequestsAsync(t));
    }

    /// <summary>
    /// The batch's retry: a test trigger makes the second row of the first attempt fail as a deadlock (40P01). The
    /// whole transaction is run again, and the batch is stored once, in order. Each attempt must be one whole transaction with nothing done
    /// outside it: a row written outside the attempt's transaction would be there twice, and a transaction carried over from the failed
    /// attempt would refuse every statement (25P02). The sequence counts the rows tried: two in the failed attempt, three in the second.
    /// </summary>
    [PostgresFact]   // mutations: the retry dropped; a write outside the attempt's transaction; the attempt reusing a transaction opened outside it
    public async Task A_batch_that_deadlocks_is_retried_as_a_whole_and_stored_once()
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
        await ExecAsync(t, "CREATE TRIGGER test_deadlock_once BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION test_deadlock_once()");
        var now = DateTimeOffset.UtcNow;

        await Engine.AuditBatch(t).AppendBatchAsync(new[] { Event(now, "a"), Event(now, "b", "globex"), Event(now, "c") }, Ct);

        Assert.Equal(new[] { "a", "b", "c" }, await RequestsAsync(t));
        Assert.Equal(5L, await PostgresSqlEngine.TestServerScalarAsync(t, "SELECT last_value FROM test_rows_tried"));   // the probe's own sequence: the app role may only use it
    }

    /// <summary>
    /// The race the sorted pre-lock is for: a purge holds tenant "acme"'s lock exclusively (lock_tenant_id('acme', true), as the
    /// purge takes it first). Two batches start, one with events for (acme, Zed), the other for (Zed, acme). Each takes its tenants'
    /// locks shared, in ordinal order (<see cref="PostgresTenantLocks"/>: "Zed" before "acme", where a case-blind order puts "acme" first;
    /// the build's invariant globalization makes a culture's order ordinal too), before it writes a row: so while the purge holds "acme", both hold "Zed" shared, both wait for "acme", and neither has
    /// touched audit_events. Once the purge lets go, both finish; neither deadlocks. The waits are read from pg_locks, never slept for.
    /// </summary>
    [PostgresFact]   // mutations: the pre-lock dropped (a row is written before "acme" is waited for); taken in the batch's order, unsorted; sorted by another comparer
    public async Task Two_batches_in_opposite_orders_wait_for_a_purges_tenant_holding_no_row_and_both_finish()
    {
        await using var t = await CreateDbAsync();
        var now = DateTimeOffset.UtcNow;
        await using var purge = await Db(t).OpenAsync();
        await using var holding = await purge.BeginTransactionAsync();
        await using (var take = new NpgsqlCommand("SELECT lock_tenant_id('acme', true)", purge, holding)) await take.ExecuteNonQueryAsync();

        var az = Task.Run(() => Engine.AuditBatch(t).AppendBatchAsync(new[] { Event(now, "az-acme", "acme"), Event(now, "az-zed", "Zed") }, Ct));
        var za = Task.Run(() => Engine.AuditBatch(t).AppendBatchAsync(new[] { Event(now, "za-zed", "Zed"), Event(now, "za-acme", "acme") }, Ct));

        // Both batches wait for "acme" (a lock not granted), or one has ended, which the asserts below then report.
        await WaitUntilAsync(purge, holding, "SELECT COUNT(DISTINCT l.pid) FROM pg_catalog.pg_locks l WHERE NOT l.granted AND " + Others + " AND " + TenantLock("acme"),
            2, az, za);
        Assert.False(az.IsCompleted || za.IsCompleted, "a batch finished while the purge held tenant acme's lock");
        Assert.Equal(2L, await CountAsync(purge, holding, "SELECT COUNT(DISTINCT l.pid) FROM pg_catalog.pg_locks l WHERE l.granted AND l.mode = 'ShareLock' AND "
            + Others + " AND " + TenantLock("Zed")));   // each took "Zed" first, so in ordinal order
        Assert.Equal(0L, await CountAsync(purge, holding, "SELECT COUNT(*) FROM pg_catalog.pg_locks l WHERE l.relation = 'audit_events'::regclass AND " + Others));
        Assert.Empty(await RequestsAsync(t));

        await holding.CommitAsync();
        await Task.WhenAll(az, za).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(new[] { "az-acme", "az-zed", "za-acme", "za-zed" }, (await RequestsAsync(t)).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The prune's chunks, as SQLite's: each its own statement and transaction, oldest first. With the app's clock a day ahead,
    /// the last chunk holds a row the database still holds young: the trigger refuses it, the refusal reaches the caller, and the chunks
    /// before it stay deleted.
    /// </summary>
    [PostgresFact]   // mutations: only one chunk per pass; all the chunks in one transaction; the refusal caught
    public async Task The_prune_works_in_chunks_each_its_own_transaction_and_a_refused_chunk_keeps_only_itself()
    {
        await using var t = await CreateDbAsync();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 10; i++) await WriteAsync(t, now.AddDays(-200 - i), "old-" + i);
        await WriteAsync(t, now.AddDays(-2), "recent");

        Assert.Equal(10, await new PostgresAuditLog(Db(t), pruneChunk: 3).PruneAsync(now - AuditPolicy.Retention, Ct));
        Assert.Equal(new[] { "recent" }, await RequestsAsync(t));
        Assert.Equal(5_000, PostgresAuditLog.DefaultPruneChunk);

        for (var i = 0; i < 4; i++) await WriteAsync(t, now.AddDays(-200 - i), "older-" + i);
        await RefusedAsync(() => new PostgresAuditLog(Db(t), pruneChunk: 2).PruneAsync(now.AddDays(1), Ct));
        Assert.Equal(new[] { "recent" }, await RequestsAsync(t));   // two chunks of two deleted, then the young row refused
    }

    /// <summary>
    /// The database's clock against the app's: ClockSkewAsync is statement_timestamp() minus the app's time, so with
    /// the app's clock two hours ahead it reads about minus two hours, and with it three hours behind about plus three. The test server's
    /// clock is this machine's, so a minute either way is plenty.
    /// </summary>
    [PostgresFact]   // mutation: the skew's sign reversed (app minus database)
    public async Task The_clock_skew_is_the_databases_clock_minus_the_apps()
    {
        await using var t = await CreateDbAsync();
        t.Clock.UtcNow = DateTimeOffset.UtcNow.AddHours(2);
        var ahead = await Db(t).ClockSkewAsync(t.Clock);
        Assert.InRange(ahead, TimeSpan.FromHours(-2) - TimeSpan.FromMinutes(1), TimeSpan.FromHours(-2) + TimeSpan.FromMinutes(1));

        t.Clock.UtcNow = DateTimeOffset.UtcNow.AddHours(-3);
        var behind = await Db(t).ClockSkewAsync(t.Clock);
        Assert.InRange(behind, TimeSpan.FromHours(3) - TimeSpan.FromMinutes(1), TimeSpan.FromHours(3) + TimeSpan.FromMinutes(1));

        Assert.InRange(await Db(t).ClockSkewAsync(new SystemClock()), TimeSpan.FromMinutes(-1), TimeSpan.FromMinutes(1));
    }
}

/// <summary><see cref="AuditRetentionStoreContract"/> on SQLite, and the tests only SQLite has.</summary>
[Collection("sqlite-pools")]
public class AuditRetentionStoreTests : AuditRetentionStoreContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    private static async Task ExecAsync(SqliteDatabase db, string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteDatabase db, string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
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

    /// <summary>Writes through the real writer and checks the time text it stored is the real "O" format.</summary>
    private static async Task WriteAsync(SqliteDatabase db, DateTimeOffset at, string request, string tenant = "acme")
    {
        await new SqliteAuditLog(db).AppendAsync(Event(at, request, tenant), Ct);
        var time = (string)(await ScalarAsync(db, $"SELECT time FROM audit_events WHERE request_id='{request}'"))!;
        Assert.Matches(OFormat, time);
    }

    /// <summary>A row with this exact time text, for the times no build writes.</summary>
    private static Task RawAsync(SqliteDatabase db, string time, string request) => ExecAsync(db,
        $"INSERT INTO audit_events(time, request_id, tenant_id, token_id, kind, package_id, version, decision, reason, waiver_id) VALUES('{time}', '{request}', 'acme', 'tok_1', 'Download', 'moq', '1.0.0', 'Allow', 'CooldownElapsed', NULL)");

    private static Task<List<string>> RequestsAsync(SqliteDatabase db) => ColumnAsync(db, "SELECT request_id FROM audit_events ORDER BY id");

    [Fact]
    public async Task A_batch_that_fails_stores_nothing()
    {
        await using var t = await TestDb.CreateAsync();
        await ExecAsync(t.Db, "CREATE TRIGGER test_refuse BEFORE INSERT ON audit_events WHEN NEW.request_id = 'bad' BEGIN SELECT RAISE(ABORT, 'refused'); END;");
        var now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<SqliteException>(() => new SqliteAuditLog(t.Db).AppendBatchAsync(new[] { Event(now, "a"), Event(now, "bad") }, Ct));

        Assert.Empty(await RequestsAsync(t.Db));
    }

    [Fact]   // mutation: the trigger's floor made longer than the code's figure (900 days), or the code's figure shortened below it
    public async Task The_databases_floor_is_never_longer_than_the_codes_retention()
    {
        await using var t = await TestDb.CreateAsync();
        var sql = (string)(await ScalarAsync(t.Db, "SELECT sql FROM sqlite_master WHERE type='trigger' AND name='audit_no_delete'"))!;
        var floors = Regex.Matches(sql, @"julianday\('now',\s*'-(\d+) days'\)").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        var floor = Assert.Single(floors);
        Assert.True(floor <= AuditPolicy.RetentionDays, $"the database keeps rows {floor} days, longer than the code's {AuditPolicy.RetentionDays}: the prune would always be refused");
        Assert.True(floor >= AuditPolicy.RetentionDays - 1, $"the database floor ({floor} days) is far below the code's retention");
        Assert.DoesNotContain("localtime", sql);
        Assert.DoesNotContain("start of", sql);
    }

    [Theory]   // mutations: the "time cannot be read" guard dropped (julianday NULL compares as not-young); the date-and-time GLOB dropped ('12', '2026-01-01')
    [InlineData("not a time")]
    [InlineData("")]
    [InlineData("12")]            // SQLite reads a bare number as a Julian day: thousands of years old
    [InlineData("2000000")]
    [InlineData("2026-01-01")]    // a date with no time: no build writes it, so it is not trusted either
    [InlineData("2000-99-99T00:00:00")]   // shaped like a date and time, but julianday cannot read it
    public async Task A_row_whose_time_cannot_be_read_is_never_deleted(string time)
    {
        await using var t = await TestDb.CreateAsync();
        await RawAsync(t.Db, time, "unreadable");
        await Assert.ThrowsAsync<SqliteException>(() => ExecAsync(t.Db, "DELETE FROM audit_events"));
        Assert.Equal(new[] { "unreadable" }, await RequestsAsync(t.Db));
    }

    [Fact]   // mutation: only one chunk deleted per pass
    public async Task The_prune_works_in_chunks_until_nothing_old_is_left()
    {
        await using var t = await TestDb.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 10; i++) await WriteAsync(t.Db, now.AddDays(-200 - i), "old-" + i);
        await WriteAsync(t.Db, now.AddDays(-2), "recent");

        Assert.Equal(10, await new SqliteAuditLog(t.Db, pruneChunk: 3).PruneAsync(now - AuditPolicy.Retention, Ct));

        Assert.Equal(new[] { "recent" }, await RequestsAsync(t.Db));
        Assert.Equal(5_000, SqliteAuditLog.DefaultPruneChunk);
    }

    [Theory]   // mutations: the prune's julianday or GLOB condition dropped, so a row the trigger refuses is selected and nothing is ever pruned
    [InlineData("")]
    [InlineData("12")]
    [InlineData("2026-01-01")]
    [InlineData("2000-99-99T00:00:00")]
    public async Task A_row_the_trigger_refuses_does_not_block_the_prune(string time)
    {
        await using var t = await TestDb.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await RawAsync(t.Db, time, "unreadable");
        await WriteAsync(t.Db, now.AddDays(-500), "old");

        Assert.Equal(1, await new SqliteAuditLog(t.Db).PruneAsync(now - AuditPolicy.Retention, Ct));

        Assert.Equal(new[] { "unreadable" }, await RequestsAsync(t.Db));
    }

    [Fact]   // mutation: the "time < @b" filter dropped (every prune then reads the whole table)
    public async Task The_prune_finds_old_rows_through_the_time_index()
    {
        await using var t = await TestDb.CreateAsync();
        await using var c = await t.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + SqliteAuditLog.PruneSql;
        cmd.Parameters.AddWithValue("@b", Iso(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("@n", 10);
        var plan = new List<string>();
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) plan.Add(r.GetString(3));

        Assert.Contains(plan, step => step.Contains("USING INDEX ix_audit_time (time<?)", StringComparison.Ordinal)
                                      || step.Contains("USING COVERING INDEX ix_audit_time (time<?)", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN audit_events", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migration_19_on_a_copy_of_a_v18_database_changes_no_row_adds_the_time_index_and_lets_only_old_rows_be_deleted()
    {
        await using var t = new TestDb();
        await t.Db.MigrateAsync(18);
        await t.AddCoreTenantAsync();
        var now = DateTimeOffset.UtcNow;
        await WriteAsync(t.Db, now.AddDays(-200), "old");
        await WriteAsync(t.Db, now.AddDays(-30), "young");
        const string everyRow = "SELECT id || '|' || time || '|' || request_id || '|' || tenant_id || '|' || token_id || '|' || kind || '|' || package_id || '|' || version || '|' || decision || '|' || reason || '|' || IFNULL(waiver_id, 'NULL') FROM audit_events ORDER BY id";
        var before = await ColumnAsync(t.Db, everyRow);
        var noUpdate = await ScalarAsync(t.Db, "SELECT sql FROM sqlite_master WHERE name='audit_no_update'");
        await Assert.ThrowsAsync<SqliteException>(() => ExecAsync(t.Db, "DELETE FROM audit_events WHERE request_id='old'"));   // v18: nothing, however old

        var copyPath = Path.Combine(t.Dir, "copy.db");
        await ExecAsync(t.Db, $"VACUUM INTO '{copyPath}'");
        var copy = new SqliteDatabase(copyPath);
        await copy.MigrateAsync(19);
        Assert.True(SqliteDatabase.LatestVersion >= 19);
        Assert.Equal(19L, await ScalarAsync(copy, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal("ok", await ScalarAsync(copy, "PRAGMA integrity_check"));

        Assert.Equal(before, await ColumnAsync(copy, everyRow));
        Assert.Equal(noUpdate, await ScalarAsync(copy, "SELECT sql FROM sqlite_master WHERE name='audit_no_update'"));
        Assert.Equal(1L, await ScalarAsync(copy, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='audit_no_delete'"));
        Assert.Equal("CREATE INDEX ix_audit_time ON audit_events(time)", await ScalarAsync(copy, "SELECT sql FROM sqlite_master WHERE type='index' AND name='ix_audit_time'"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecAsync(copy, "DELETE FROM audit_events"));   // the young row stops the lot
        await ExecAsync(copy, "DELETE FROM audit_events WHERE request_id='old'");
        Assert.Equal(new[] { "young" }, await RequestsAsync(copy));

        await copy.MigrateAsync(19);   // again: nothing to do
        Assert.Equal(19L, await ScalarAsync(copy, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(18L, await ScalarAsync(t.Db, "SELECT MAX(version) FROM schema_migrations"));   // the original is untouched
    }

    [Fact]   // mutation: migration 19 applied statement by statement outside one transaction
    public async Task Migration_19_is_atomic_a_failure_part_way_leaves_the_v18_trigger_and_no_version()
    {
        await using var t = new TestDb();
        await t.Db.MigrateAsync(18);
        var v18Trigger = await ScalarAsync(t.Db, "SELECT sql FROM sqlite_master WHERE name='audit_no_delete'");
        // An object already holding the index's name makes the migration's last statement fail, after the trigger was replaced.
        await ExecAsync(t.Db, "CREATE INDEX ix_audit_time ON audit_events(id)");

        await Assert.ThrowsAsync<SqliteException>(() => t.Db.MigrateAsync(19));

        Assert.Equal(18L, await ScalarAsync(t.Db, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(v18Trigger, await ScalarAsync(t.Db, "SELECT sql FROM sqlite_master WHERE name='audit_no_delete'"));
        Assert.Equal("CREATE INDEX ix_audit_time ON audit_events(id)", await ScalarAsync(t.Db, "SELECT sql FROM sqlite_master WHERE name='ix_audit_time'"));
    }
}
