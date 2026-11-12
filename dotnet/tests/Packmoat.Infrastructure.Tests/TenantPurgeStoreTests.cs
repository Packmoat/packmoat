using Npgsql;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Lifecycle;
using Packmoat.Infrastructure.Postgres;
using Packmoat.Infrastructure.Tests.Postgres;
using static Packmoat.Infrastructure.Tests.Postgres.PostgresLockProbe;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// <see cref="TenantPurgeStoreContract"/> on PostgreSQL, each test on a database of its own, and what only PostgreSQL
/// has: the purge's locks, taken before anything else (the tenant id's lock exclusively, then the tenant's row FOR UPDATE, then its tokens FOR
/// UPDATE), so a late decision record waits and is dropped, a late token waits and is refused, and a late token activity note waits and
/// is refused; a deadlock that becomes a refusal and is never retried;
/// and the first guard over the schema's own tables. The race tests pause the purge inside a contributor, after the core's child deletes
/// and before the tenant row's, and read the other sessions' waits from pg_locks and pg_stat_activity; they never sleep a fixed time.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresTenantPurgeStoreTests : TenantPurgeStoreContract
{
    protected override IStoreEngine Engine => PostgresStoreEngine.Instance;

    private static PostgresDatabase Db(IEngineDb t) => ((PostgresTestDb)t).Db;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>A session waiting for a lock: another session of this test's database whose wait is a heavyweight lock's.</summary>
    private const string Waiting =
        "SELECT COUNT(*) FROM pg_catalog.pg_stat_activity WHERE datname = pg_catalog.current_database() AND pid <> pg_catalog.pg_backend_pid() AND wait_event_type = 'Lock'";

    /// <summary>
    /// A contributor that stops the purge where it calls it: after the core's child deletes, before the tenant row's delete and the
    /// purge record. <see cref="Paused"/> completes there; the purge goes on once <see cref="Release"/> does.
    /// </summary>
    private sealed class Pause : IPurgeContributor
    {
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<PurgeTable> Tables => [];
        public IReadOnlyList<string> NotReached => [];

        public async Task<IReadOnlyList<PurgedTable>> DeleteAsync(System.Data.Common.DbTransaction transaction, string tenantId, CancellationToken ct)
        {
            Paused.TrySetResult();
            await Release.Task;
            return [];
        }
    }

    /// <summary>A session of the test's own, outside any transaction, so each read of pg_stat_activity is fresh.</summary>
    private static async Task<NpgsqlConnection> ProbeAsync(IEngineDb t) => await Db(t).OpenAsync();

    /// <summary>Lets the purge go and waits for it to end, however it ends, so the database can be dropped.</summary>
    private static async Task LetGoAsync(Pause pause, Task purge)
    {
        pause.Release.TrySetResult();
        await Task.WhenAny(purge, Task.Delay(Patience));
    }

    /// <summary>
    /// A decision-record batch written while the purge runs, between its child deletes and its purge record. The purge
    /// already holds the tenant id's lock exclusively, so the batch, which takes it shared before any row, waits (pg_locks, with
    /// pg_stat_activity's wait), having written nothing. Once the purge commits, the batch goes on, its row is dropped by the trigger (the
    /// purge record is there), and nothing of the tenant is left.
    /// </summary>
    [PostgresFact]   // mutations: lock_tenant_id(id, true) dropped from the purge, or taken after the deletes
    public async Task A_decision_batch_written_while_the_purge_runs_waits_for_it_and_is_then_dropped()
    {
        await using var t = await CreateDbAsync();
        var acme = await SeedAsync(t, "acme", Directory1);
        var globex = await SeedAsync(t, "globex", Directory2);
        var pause = new Pause();
        var purge = Task.Run(() => Store(t, new Eligibility("acme"), pause).PurgeAsync(Due("acme"), Record(t, "acme"), Ct));
        try
        {
            await pause.Paused.Task.WaitAsync(Patience);
            await using var probe = await ProbeAsync(t);
            Assert.Equal(1L, await CountAsync(probe, null, "SELECT COUNT(*) FROM pg_catalog.pg_locks l WHERE l.granted AND l.mode = 'ExclusiveLock' AND "
                + Others + " AND " + TenantLock("acme")));   // the purge holds the tenant id's lock, exclusively

            var batch = Task.Run(() => Engine.DecisionRecords(t).UpsertAsync([DecisionRecordOf(t, "acme") with { Version = "9.9.9" }], Ct));

            await WaitUntilAsync(probe, null, "SELECT COUNT(DISTINCT l.pid) FROM pg_catalog.pg_locks l JOIN pg_catalog.pg_stat_activity a ON a.pid = l.pid " +
                "WHERE NOT l.granted AND a.wait_event_type = 'Lock' AND a.wait_event = 'advisory' AND " + Others + " AND " + TenantLock("acme"), 1, batch, purge);
            Assert.False(batch.IsCompleted, "the decision batch finished while the purge held the tenant");
            Assert.False(purge.IsCompleted);
            Assert.Equal(0L, await CountAsync(probe, null, "SELECT COUNT(*) FROM pg_catalog.pg_locks l WHERE l.relation = 'decision_records'::regclass AND l.pid NOT IN "
                + "(SELECT pid FROM pg_catalog.pg_locks WHERE locktype = 'advisory' AND granted AND mode = 'ExclusiveLock') AND " + Others));   // the batch touched no row

            pause.Release.SetResult();
            var counts = await purge.WaitAsync(Patience);
            await batch.WaitAsync(Patience);   // not refused: dropped

            Assert.Equal(1, counts.Single(c => c.Table == "decision_records").Rows);
            Assert.Equal(Purged, await RowsAsync(t, "acme", acme));
            Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM decision_records WHERE tenant_id='acme'"));
            Assert.Equal(Seeded, await RowsAsync(t, "globex", globex));
        }
        finally
        {
            await LetGoAsync(pause, purge);
        }
    }

    /// <summary>
    /// A token issued while the purge runs: the purge holds the tenant's row FOR UPDATE, so the operator's issue (its foreign-key check
    /// takes the row FOR KEY SHARE) and a member's issue (the cap's FOR NO KEY UPDATE) both wait. Once the purge commits the row is gone:
    /// the operator's issue is refused by the foreign key and the member's finds no tenant. No token is left, and none authenticates.
    /// </summary>
    [PostgresFact]   // mutation: FOR UPDATE on the tenant's row dropped from the purge
    public async Task A_token_issued_while_the_purge_runs_waits_for_it_and_is_then_refused()
    {
        await using var t = await CreateDbAsync();
        var acme = await SeedAsync(t, "acme", Directory1);
        var pause = new Pause();
        var purge = Task.Run(() => Store(t, new Eligibility("acme"), pause).PurgeAsync(Due("acme"), Record(t, "acme"), Ct));
        try
        {
            await pause.Paused.Task.WaitAsync(Patience);
            await using var probe = await ProbeAsync(t);

            var tokens = Engine.Tokens(t);
            var byOperator = Task.Run(() => tokens.IssueAsync("acme", "late", Ct));
            var byMember = Task.Run(() => tokens.IssueForMemberAsync("acme", Directory1, Oid, "late", t.Clock.UtcNow.AddDays(30), TokenKind.Developer, 5, Ct));

            await WaitUntilAsync(probe, null, Waiting, 2, byOperator, byMember, purge);
            Assert.False(byOperator.IsCompleted, "the operator's token was issued while the purge held the tenant");
            Assert.False(byMember.IsCompleted, "the member's token was issued while the purge held the tenant");
            Assert.False(purge.IsCompleted);

            pause.Release.SetResult();
            await purge.WaitAsync(Patience);

            var refusal = (PostgresException)await RefusedAsync(() => byOperator.WaitAsync(Patience));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refusal.SqlState);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => byMember.WaitAsync(Patience));
            Assert.Equal(Purged, await RowsAsync(t, "acme", acme));
            Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM tokens"));
        }
        finally
        {
            await LetGoAsync(pause, purge);
        }
    }

    /// <summary>A session of this test's database waiting for a lock while it runs a statement that starts with <paramref name="sql"/>.</summary>
    private static string WaitingIn(string sql) => Waiting + $" AND query LIKE '{sql}%'";

    /// <summary>
    /// A token's activity note written while the purge runs, between its DELETE token_activity and its DELETE
    /// tokens. token_activity's foreign key points at tokens, not tenants, so the tenant's row does not hold the note back; the tenant's
    /// tokens, held FOR UPDATE since before the deletes, do. Another session holds the tenant's decision record, so the purge stops at its
    /// DELETE decision_records, the step between the two. The note waits (its foreign-key check, FOR KEY SHARE on the token). Once the
    /// purge commits the token is gone, so the note is refused by its foreign key (what the activity writer counts as a failed write, as
    /// on SQLite: the contract's test). The purge never fails with the foreign key's 23503.
    /// </summary>
    [PostgresFact]   // mutation: the tokens' FOR UPDATE dropped from the purge
    public async Task A_token_activity_note_written_while_the_purge_runs_waits_for_it_and_is_then_refused()
    {
        await using var t = await CreateDbAsync();
        var acme = await SeedAsync(t, "acme", Directory1);
        var globex = await SeedAsync(t, "globex", Directory2);
        await using var other = await Db(t).OpenAsync();
        var holding = await other.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT 1 FROM decision_records WHERE tenant_id = 'acme' FOR UPDATE", other, holding))
            await hold.ExecuteNonQueryAsync();

        var purge = Task.Run(() => Store(t, new Eligibility("acme")).PurgeAsync(Due("acme"), Record(t, "acme"), Ct));
        Task note = Task.CompletedTask;
        try
        {
            await using var probe = await ProbeAsync(t);
            await WaitUntilAsync(probe, null, WaitingIn("DELETE FROM decision_records"), 1, purge);
            Assert.False(purge.IsCompleted, "the purge did not wait for the decision record held");   // past DELETE token_activity

            var tomorrow = DateOnly.FromDateTime(t.Clock.UtcNow.UtcDateTime).AddDays(1);
            note = Task.Run(async () => await Engine.TokenActivity(t).RecordAsync(acme, tomorrow, Ct));
            await WaitUntilAsync(probe, null, WaitingIn("INSERT INTO token_activity"), 1, note, purge);
            Assert.False(note.IsCompleted, "a token activity note was written while the purge held the tenant's tokens");
            Assert.False(purge.IsCompleted);

            await holding.RollbackAsync();
            var counts = await purge.WaitAsync(Patience);   // not a 23503: no note landed before the tokens' delete

            var refusal = (PostgresException)await RefusedAsync(() => note.WaitAsync(Patience));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refusal.SqlState);
            Assert.Equal("token_activity", refusal.TableName);
            Assert.Equal(1, counts.Single(c => c.Table == "token_activity").Rows);
            Assert.Equal(Purged, await RowsAsync(t, "acme", acme));
            Assert.Equal(Seeded, await RowsAsync(t, "globex", globex));
        }
        finally
        {
            await holding.DisposeAsync();   // rolls back if the test failed while it held the row
            await Task.WhenAny(Task.WhenAll(purge, note), Task.Delay(Patience));
        }
    }

    /// <summary>
    /// A token's activity note already written, not yet committed, when the purge starts. It holds its token (FOR KEY SHARE), so
    /// the purge waits at the tokens' FOR UPDATE, before the re-check and before any delete; once the note commits, the purge goes on and
    /// deletes it with the rest. Were the tokens taken only after the purge's DELETE token_activity (which cannot see an uncommitted
    /// note), the committed note would then fail the tokens' delete with the foreign key's 23503.
    /// </summary>
    [PostgresFact]   // mutations: the tokens' FOR UPDATE dropped from the purge, or taken after its DELETE token_activity
    public async Task A_token_activity_note_written_before_the_purge_holds_the_tokens_is_waited_for_and_deleted()
    {
        await using var t = await CreateDbAsync();
        var acme = await SeedAsync(t, "acme", Directory1);
        var globex = await SeedAsync(t, "globex", Directory2);
        await using var other = await Db(t).OpenAsync();
        var writing = await other.BeginTransactionAsync();
        await using (var write = new NpgsqlCommand("INSERT INTO token_activity(token_id, day) VALUES(@id, @d)", other, writing))
        {
            write.Parameters.AddWithValue("id", acme);
            write.Parameters.AddWithValue("d", DateOnly.FromDateTime(t.Clock.UtcNow.UtcDateTime).AddDays(1));
            await write.ExecuteNonQueryAsync();
        }

        var eligibility = new Eligibility("acme");
        var purge = Task.Run(() => Store(t, eligibility).PurgeAsync(Due("acme"), Record(t, "acme"), Ct));
        try
        {
            await using var probe = await ProbeAsync(t);
            await WaitUntilAsync(probe, null, Waiting, 1, purge);
            Assert.False(purge.IsCompleted, "the purge did not wait for the token held by the uncommitted note");
            Assert.Equal(1L, await CountAsync(probe, null, WaitingIn("SELECT 1 FROM tokens")));   // at the tokens' FOR UPDATE, not at a delete
            Assert.Empty(eligibility.Rechecks);   // before the re-check, so before every delete

            await writing.CommitAsync();
            var counts = await purge.WaitAsync(Patience);

            Assert.Equal(2, counts.Single(c => c.Table == "token_activity").Rows);
            Assert.Equal(Purged, await RowsAsync(t, "acme", acme));
            Assert.Equal(Seeded, await RowsAsync(t, "globex", globex));
        }
        finally
        {
            await writing.DisposeAsync();   // rolls back if the test failed before the commit
            await Task.WhenAny(purge, Task.Delay(Patience));
        }
    }

    /// <summary>
    /// The same note, queued before its token was revoked and written after: the token is revoked (committed) while the note,
    /// not yet committed, holds it. The purge's tokens lock takes every token of the tenant, revoked or not, so it still waits for the
    /// note at that lock, before the re-check and before any delete, and then deletes it with the rest. A lock on live tokens only would
    /// pass the revoked one by, and the committed note would then fail the purge's DELETE tokens with the foreign key's 23503.
    /// </summary>
    [PostgresFact]   // mutation: the tokens' FOR UPDATE limited to live tokens (revoked_at IS NULL)
    public async Task A_token_activity_note_of_a_token_revoked_since_is_still_waited_for_and_deleted()
    {
        await using var t = await CreateDbAsync();
        var acme = await SeedAsync(t, "acme", Directory1);
        var globex = await SeedAsync(t, "globex", Directory2);
        await using var other = await Db(t).OpenAsync();
        var writing = await other.BeginTransactionAsync();
        await using (var write = new NpgsqlCommand("INSERT INTO token_activity(token_id, day) VALUES(@id, @d)", other, writing))
        {
            write.Parameters.AddWithValue("id", acme);
            write.Parameters.AddWithValue("d", DateOnly.FromDateTime(t.Clock.UtcNow.UtcDateTime).AddDays(1));
            await write.ExecuteNonQueryAsync();
        }
        // Revoked since the note was queued: a plain update, which the note's FOR KEY SHARE on the token does not hold back.
        await ExecAsync(t, $"UPDATE tokens SET revoked_at = now(), revoked_by = 'operator' WHERE id = '{acme}'");
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM tokens WHERE tenant_id = 'acme' AND revoked_at IS NOT NULL"));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM tokens WHERE tenant_id = 'acme' AND revoked_at IS NULL"));

        var eligibility = new Eligibility("acme");
        var purge = Task.Run(() => Store(t, eligibility).PurgeAsync(Due("acme"), Record(t, "acme"), Ct));
        try
        {
            await using var probe = await ProbeAsync(t);
            await WaitUntilAsync(probe, null, Waiting, 1, purge);
            Assert.False(purge.IsCompleted, "the purge did not wait for the revoked token held by the uncommitted note");
            Assert.Equal(1L, await CountAsync(probe, null, WaitingIn("SELECT 1 FROM tokens")));   // at the tokens' FOR UPDATE, not at a delete
            Assert.Empty(eligibility.Rechecks);   // before the re-check, so before every delete

            await writing.CommitAsync();
            var counts = await purge.WaitAsync(Patience);

            Assert.Equal(2, counts.Single(c => c.Table == "token_activity").Rows);
            Assert.Equal(Purged, await RowsAsync(t, "acme", acme));
            Assert.Equal(Seeded, await RowsAsync(t, "globex", globex));
        }
        finally
        {
            await writing.DisposeAsync();   // rolls back if the test failed before the commit
            await Task.WhenAny(purge, Task.Delay(Patience));
        }
    }

    /// <summary>
    /// The purge's lock order against the directory unbind, which takes the tenant's row (FOR NO KEY UPDATE) and then
    /// revokes the directory's self-issued tokens (their rows). Another session holds the directory's link, so the unbind stops at its
    /// DELETE: holding the tenant's row, not yet at the tokens. The purge then takes the tenant id's lock and waits for the tenant's row,
    /// holding no token. Once the link is let go, the unbind revokes and commits, and the purge goes on and purges: both succeed. A purge
    /// that took the tokens before the tenant's row would hold the self-issued token the unbind then waits for, while it waits for the
    /// unbind's tenant row: a deadlock (40P01), which ends one of the two.
    /// </summary>
    [PostgresFact]   // mutation: the tokens' FOR UPDATE taken before the tenant's row
    public async Task A_purge_behind_an_unbind_waits_for_it_and_neither_deadlocks()
    {
        await using var t = await CreateDbAsync();
        var acme = await SeedAsync(t, "acme", Directory1);
        var globex = await SeedAsync(t, "globex", Directory2);
        Assert.NotNull(await Engine.Tokens(t).IssueForMemberAsync("acme", Directory1, Oid, "self", t.Clock.UtcNow.AddDays(30), TokenKind.Developer, 5, Ct));
        Assert.Equal(1L, await ScalarAsync(t, $"SELECT COUNT(*) FROM tokens WHERE tenant_id = 'acme' AND issued_via = 'self' AND owner_tid = '{Directory1}'"));

        await using var other = await Db(t).OpenAsync();
        var holding = await other.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand($"SELECT 1 FROM sign_in_directories WHERE azure_tenant_id = '{Directory1}' FOR KEY SHARE", other, holding))
            await hold.ExecuteNonQueryAsync();

        var unbind = Task.Run(() => Engine.SignInDirectory(t).UnbindAsync("acme", Directory1, t.Clock.UtcNow, "operator", Ct));
        Task<IReadOnlyList<PurgedTable>>? purge = null;
        try
        {
            await using var probe = await ProbeAsync(t);
            await WaitUntilAsync(probe, null, WaitingIn("DELETE FROM sign_in_directories"), 1, unbind);
            Assert.False(unbind.IsCompleted, "the unbind did not wait for the link held");   // past its tenant row's lock

            purge = Task.Run(() => Store(t, new Eligibility("acme")).PurgeAsync(Due("acme"), Record(t, "acme"), Ct));
            await WaitUntilAsync(probe, null, WaitingIn("SELECT 1 FROM tenants"), 1, purge, unbind);
            Assert.False(purge.IsCompleted, "the purge did not wait for the unbind's tenant row");
            Assert.False(unbind.IsCompleted);

            await holding.RollbackAsync();
            Assert.Equal(1, await unbind.WaitAsync(Patience));   // the self-issued token revoked: no deadlock ended it
            await purge.WaitAsync(Patience);                      // nor the purge

            Assert.Equal(Purged, await RowsAsync(t, "acme", acme));
            Assert.Equal(Seeded, await RowsAsync(t, "globex", globex));
        }
        finally
        {
            await holding.DisposeAsync();   // rolls back if the test failed while it held the link
            await Task.WhenAny(Task.WhenAll(purge ?? Task.CompletedTask, unbind), Task.Delay(Patience));
        }
    }

    /// <summary>
    /// A deadlock: another session holds the tenant's row (FOR NO KEY UPDATE, as the tenant store's writes take
    /// it). The purge takes the tenant id's lock, then waits for the row: so the purge's two locks come in that order, before anything else
    /// is read or written (the re-check has not run). The other session then asks for the tenant id's lock, and the server ends one of the
    /// two: the purge, whose deadlock check runs first (the other's deadlock_timeout is set long). The purge is refused with "Nothing was
    /// deleted; run the purge again." and is not retried: a retry would wait for the other session's tenant id lock, which it still holds.
    /// </summary>
    [PostgresFact]   // mutations: the purge retried on 40P01 (PostgresRetry); the deadlock let through as it is
    public async Task A_deadlock_ends_the_purge_as_a_refusal_with_nothing_deleted_and_it_is_not_retried()
    {
        await using var t = await CreateDbAsync();
        var acme = await SeedAsync(t, "acme", Directory1);
        var eligibility = new Eligibility("acme");
        // The other session is the test server's own login: only a superuser may set deadlock_timeout, and the stores may sign in as the
        // app role.
        await using var other = await ((PostgresTestDb)t).OpenAsTestServerAsync();
        await using var holding = await other.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SET LOCAL deadlock_timeout = '60s'; SELECT 1 FROM tenants WHERE id = 'acme' FOR NO KEY UPDATE", other, holding))
            await hold.ExecuteNonQueryAsync();

        var purge = Task.Run(() => Store(t, eligibility).PurgeAsync(Due("acme"), Record(t, "acme"), Ct));
        await using var probe = await ProbeAsync(t);
        await WaitUntilAsync(probe, null, Waiting, 1, purge);
        Assert.False(purge.IsCompleted, "the purge did not wait for the tenant's row");
        Assert.Equal(1L, await CountAsync(probe, null, "SELECT COUNT(*) FROM pg_catalog.pg_locks l WHERE l.granted AND l.mode = 'ExclusiveLock' AND "
            + Others + " AND " + TenantLock("acme")));   // the tenant id's lock came first
        Assert.Empty(eligibility.Rechecks);

        await using (var cycle = new NpgsqlCommand("SELECT lock_tenant_id('acme', false)", other, holding))
            await cycle.ExecuteNonQueryAsync().WaitAsync(Patience);   // granted once the server has ended the purge

        var refused = await Assert.ThrowsAsync<PurgeRefusedException>(() => purge.WaitAsync(Patience));
        Assert.Equal(PostgresTenantPurgeStore.DeadlockRefusal("acme"), refused.Message);
        Assert.EndsWith("Nothing was deleted; run the purge again.", refused.Message, StringComparison.Ordinal);
        await holding.RollbackAsync();
        Assert.Equal(Seeded, await RowsAsync(t, "acme", acme));
        Assert.Null(await Store(t, eligibility).FindPurgeAsync("acme", Ct));
        Assert.Empty(eligibility.Rechecks);

        // Nothing is left locked or half done: the same purge then runs.
        await Store(t, new Eligibility("acme")).PurgeAsync(Due("acme"), Record(t, "acme"), Ct);
        Assert.Equal(Purged, await RowsAsync(t, "acme", acme));
    }

    /// <summary>
    /// The first guard over PostgreSQL's own catalog: a table in the schema nobody classified refuses the purge, and so does a
    /// classified table that does not exist (SQLite's sqlite_sequence among them: PostgreSQL's list leaves it out). Nothing is deleted.
    /// </summary>
    [PostgresFact]   // mutation: sqlite_sequence kept in PostgreSQL's list (every purge is then refused, and the contract fails too)
    public async Task The_first_guard_refuses_a_table_nobody_classified_and_a_classified_table_that_does_not_exist()
    {
        await using var t = await CreateDbAsync();
        var acme = await SeedAsync(t, "acme", Directory1);
        Assert.DoesNotContain(CorePurgeTables.Tables, c => c.Table == "sqlite_sequence");
        Assert.Equal(CorePurgeTables.Tables.Select(c => c.Table).Order(StringComparer.Ordinal), await Engine.TablesAsync(t));   // the schema, exactly

        var sqliteSequence = new Classifies(new PurgeTable("sqlite_sequence", PurgeTableFate.NoTenantData, "SQLite's AUTOINCREMENT counters"));
        var refused = await Assert.ThrowsAsync<PurgeRefusedException>(() => Store(t, new Eligibility("acme"), sqliteSequence).PurgeAsync(Due("acme"), Record(t, "acme"), Ct));
        Assert.Equal("Table sqlite_sequence is classified for the purge but does not exist. Nothing was deleted.", refused.Message);

        await ExecAsync(t, "CREATE TABLE stray(tenant_id text)");
        refused = await Assert.ThrowsAsync<PurgeRefusedException>(() => Store(t, new Eligibility("acme")).PurgeAsync(Due("acme"), Record(t, "acme"), Ct));
        Assert.Equal("Table stray is not classified for the purge (deleted, kept or no tenant data). Nothing was deleted.", refused.Message);
        Assert.Equal(Seeded, await RowsAsync(t, "acme", acme));
        Assert.Equal(0L, await MarkersAsync(t, "acme"));

        // Classified by a module, the same table no longer refuses.
        await Store(t, new Eligibility("acme"), new Classifies(new PurgeTable("stray", PurgeTableFate.Deleted, "a module's", "tenant_id"))).PurgeAsync(Due("acme"), Record(t, "acme"), Ct);
        Assert.Equal(Purged, await RowsAsync(t, "acme", acme));
    }

    /// <summary>A module that classifies <paramref name="tables"/> and deletes nothing itself.</summary>
    private sealed class Classifies(params PurgeTable[] tables) : IPurgeContributor
    {
        public IReadOnlyList<PurgeTable> Tables => tables;
        public IReadOnlyList<string> NotReached => [];
        public Task<IReadOnlyList<PurgedTable>> DeleteAsync(System.Data.Common.DbTransaction transaction, string tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PurgedTable>>([]);
    }

    /// <summary>The nudge day (as SQLite's): a day not yet recorded is claimed once; the same day again is not; another day is.</summary>
    [PostgresFact]
    public async Task A_nudge_day_is_claimed_once_and_another_day_is_claimed_again()
    {
        await using var t = await CreateDbAsync();
        var store = Store(t, null);
        var day = new DateOnly(2026, 10, 10);
        Assert.True(await store.TryClaimNudgeDayAsync(day, Ct));
        Assert.False(await store.TryClaimNudgeDayAsync(day, Ct));
        Assert.True(await store.TryClaimNudgeDayAsync(day.AddDays(1), Ct));
        Assert.False(await store.TryClaimNudgeDayAsync(day.AddDays(1), Ct));
        Assert.Equal("323032362D31302D3131", await ScalarAsync(t, "SELECT " + Engine.HexSql("value") + " FROM meta WHERE key='purge_nudge_day'"));
    }

    /// <summary>The operator's summary, as SQLite's lists it with no module: the evidence, then the purge record; what is never reached.</summary>
    [PostgresFact]
    public async Task The_summary_lists_what_is_kept_and_not_reached_as_sqlites_does()
    {
        await using var t = await CreateDbAsync();
        var store = (PostgresTenantPurgeStore)Store(t, null);
        var sqlite = new SqliteTenantPurgeStore(null!, null, []);
        Assert.Equal(sqlite.Kept, store.Kept);
        Assert.Equal(sqlite.NotReached, store.NotReached);
        Assert.Equal(sqlite.Classification.Where(c => c.Table != "sqlite_sequence"), store.Classification);
    }
}

/// <summary>
/// <see cref="TenantPurgeStoreContract"/> on SQLite. The tenant purge's store's hosted tests (the hosted module's parts) stay in
/// the hosted test project. Also the declared move of the core's list into <see cref="CorePurgeTables"/>: SQLite's list is
/// the one it was, entry for entry and in the same order.
/// </summary>
[Collection("sqlite-pools")]
public class TenantPurgeStoreTests : TenantPurgeStoreContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    /// <summary>SqliteTenantPurgeStore.CoreTables before it moved, copied from that source line for line.</summary>
    private static readonly PurgeTable[] BeforeTheMove =
    [
        new("token_activity", PurgeTableFate.Deleted, "which token was used on which day"),
        new("decision_records", PurgeTableFate.Deleted, "what was decided per package version, and why (no token or person)", "tenant_id"),
        new("tokens", PurgeTableFate.Deleted, "the tenant's feed tokens (hashes), owners and labels", "tenant_id"),
        new("shared_app_verifications", PurgeTableFate.Deleted, "the tenant's onboarding check records", "tenant_id"),
        new("feed_checks", PurgeTableFate.Deleted, "the tenant's feed setup checks (may hold admin names)", "tenant_id"),
        new("upstream_identities", PurgeTableFate.Deleted, "the tenant's upstream binding and certificate", "tenant_id"),
        new("sign_in_directories", PurgeTableFate.Deleted, "the directory link: freed, so the organisation may sign up again", "tenant_id"),
        new("members", PurgeTableFate.Deleted, "who signed in, with display names", "tenant_id"),
        new("auto_join_settings", PurgeTableFate.Deleted, "the tenant's auto-join switch", "tenant_id"),
        new("internal_prefixes", PurgeTableFate.Deleted, "the tenant's policy", "tenant_id"),
        new("package_overrides", PurgeTableFate.Deleted, "the tenant's policy", "tenant_id"),
        new("waivers", PurgeTableFate.Deleted, "the tenant's policy", "tenant_id"),
        new("tenants", PurgeTableFate.Deleted, "the tenant itself", "id"),
        new("audit_events", PurgeTableFate.Deleted, "the tenant's package decisions (deleted last, through the purge trigger)", "tenant_id"),
        new("admin_actions", PurgeTableFate.Kept, "the operator's evidence of what was done, including the purge itself (retention: TenantPurgePolicy.AdminActionsRetention, pruned daily by the retention pass)", "tenant_id"),
        new("tenant_purges", PurgeTableFate.Kept, "the purge record: makes a second run a no-op and blocks the id's reuse; no personal fields", "tenant_id"),
        new("meta", PurgeTableFate.NoTenantData, "the master key check and the last purge nudge day"),
        new("schema_migrations", PurgeTableFate.NoTenantData, "schema versions"),
        new("shared_app_credentials", PurgeTableFate.NoTenantData, "Packmoat's own shared app"),
        new("sqlite_sequence", PurgeTableFate.NoTenantData, "SQLite's AUTOINCREMENT counters"),
        new("job_leases", PurgeTableFate.NoTenantData, "which server copy runs each scheduled job, and until when"),
        new("cookie_keys", PurgeTableFate.NoTenantData, "the cookie protection keys, encrypted"),
        // Added after the move, by migration 24.
        new("rate_counters", PurgeTableFate.NoTenantData, "rate-limit windows: pseudonymous (unsalted SHA-256) keys and counts, short-lived: deleted within one window plus about a day"),
        // Added after the move, by migration 25.
        new("form_nonces", PurgeTableFate.NoTenantData, "single-use form nonces: hashes of nonces and of pseudonymous keys, short-lived: deleted when used or within their life plus about a day"),
        // Added after the move, by migration 26.
        new("feed_setup_scratch", PurgeTableFate.Deleted, "what the feed setup wizard keeps for a person between pages (may hold admin names), short-lived: 30 minutes plus about a day", "tenant_id"),
    ];

    [Fact]   // mutation: CorePurgeTables reordered, an entry changed, or sqlite_sequence moved
    public void Sqlites_core_tables_are_the_list_before_the_move_and_the_engine_neutral_list_is_it_without_sqlite_sequence()
    {
        Assert.Equal(BeforeTheMove, SqliteTenantPurgeStore.CoreTables);
        Assert.Equal(BeforeTheMove.Where(c => c.Table != "sqlite_sequence"), CorePurgeTables.Tables);
        Assert.Equal(CorePurgeTables.Tables, CorePurgeTables.WithEngineTables());
        Assert.Equal(SqliteTenantPurgeStore.CoreTables, new SqliteTenantPurgeStore(null!, null, []).Classification);
        Assert.Throws<ArgumentException>(() => CorePurgeTables.WithEngineTables(new PurgeTable("engine_things", PurgeTableFate.Deleted, "x", "tenant_id")));
    }
}
