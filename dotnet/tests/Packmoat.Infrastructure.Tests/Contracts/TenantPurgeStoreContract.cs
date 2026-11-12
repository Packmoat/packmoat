using System.Data.Common;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Lifecycle;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The tenant purge's store as a contract : the core's lifecycle only (no module's tables). A purge happens only
/// when the eligibility still finds the tenant due inside the purge's own transaction; it deletes the tenant's rows, its audit events and
/// its decision records, and nothing of any other tenant; it is recorded once, so a second purge does nothing and the id is never used
/// again; and a failure part way leaves every row where it was. A module's purge (its own tables, its eligibility's rules) is tested with
/// that module. Each test that kills one of the plan's mutations says which.
/// </summary>
public abstract class TenantPurgeStoreContract : StoreContract
{
    protected static readonly CancellationToken Ct = CancellationToken.None;
    protected const string Directory1 = "eeeeeeee-eeee-4eee-8eee-000000000001";
    protected const string Directory2 = "eeeeeeee-eeee-4eee-8eee-000000000002";
    protected const string Oid = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";

    /// <summary>
    /// A purge eligibility for the tests: the tenants named are due, the rest not eligible. <see cref="StillDue"/> is its answer inside the
    /// purge's transaction. Each re-check writes a marker row (see <see cref="MarkersAsync"/>) on the transaction it is given, into a table
    /// the purge keeps: committed with the purge, rolled back with it, and lost (or blocked) if the re-check were handed another
    /// connection's transaction.
    /// </summary>
    protected sealed class Eligibility(params string[] due) : IPurgeRecheck
    {
        public bool StillDue { get; set; } = true;
        public List<(string Tenant, DateTimeOffset At)> Rechecks { get; } = [];

        private PurgeCandidate Assess(string id) => due.Contains(id)
            ? new PurgeCandidate(id, PurgeVerdict.Due, "ended", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), "ended", "detail for " + id)
            : new PurgeCandidate(id, PurgeVerdict.NotEligible, "active", null, "", "");

        public Task<IReadOnlyList<PurgeCandidate>> ListAsync(DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PurgeCandidate>>(due.Select(Assess).ToList());

        public Task<PurgeCandidate?> AssessAsync(string tenantId, DateTimeOffset now, CancellationToken ct) => Task.FromResult<PurgeCandidate?>(Assess(tenantId));

        public Task ConfirmAsync(PurgeCandidate candidate, CancellationToken ct) => Task.CompletedTask;

        public async Task<bool> StillDueAsync(DbTransaction transaction, PurgeCandidate candidate, DateTimeOffset now, CancellationToken ct)
        {
            await using var cmd = transaction.Connection!.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "INSERT INTO admin_actions(time, actor_tid, actor_oid, actor_name, action, tenant_id, detail, site) " +
                $"VALUES('{now.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}+00:00', '', '', 'recheck', '{MarkerAction}', '{candidate.TenantId}', '', 'test')";
            await cmd.ExecuteNonQueryAsync(ct);
            Rechecks.Add((candidate.TenantId, now));
            return StillDue && due.Contains(candidate.TenantId);
        }
    }

    private const string MarkerAction = "test.recheck";

    /// <summary>The re-check's marker rows for the tenant that are in the database now.</summary>
    protected async Task<long> MarkersAsync(IEngineDb t, string tenant) =>
        (long)(await ScalarAsync(t, $"SELECT COUNT(*) FROM admin_actions WHERE action='{MarkerAction}' AND tenant_id='{tenant}'"))!;

    /// <summary>A module's purge contributor with no tables of its own: it runs <paramref name="delete"/> on the purge's transaction.</summary>
    protected sealed class Contributor(Func<DbTransaction, string, Task> delete) : IPurgeContributor
    {
        public IReadOnlyList<PurgeTable> Tables => [];
        public IReadOnlyList<string> NotReached => [];

        public async Task<IReadOnlyList<PurgedTable>> DeleteAsync(DbTransaction transaction, string tenantId, CancellationToken ct)
        {
            await delete(transaction, tenantId);
            return [];
        }
    }

    /// <summary>A module's purge contributor that classifies <paramref name="tables"/> and deletes nothing.</summary>
    protected sealed class Forgetful(params PurgeTable[] tables) : IPurgeContributor
    {
        public IReadOnlyList<PurgeTable> Tables => tables;
        public IReadOnlyList<string> NotReached => [];
        public Task<IReadOnlyList<PurgedTable>> DeleteAsync(DbTransaction transaction, string tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PurgedTable>>([]);
    }

    /// <summary>A module's purge contributor that classifies <paramref name="table"/> as deleted (by its tenant column) and deletes it.</summary>
    protected sealed class Deletes(PurgeTable table) : IPurgeContributor
    {
        public IReadOnlyList<PurgeTable> Tables => [table];
        public IReadOnlyList<string> NotReached => [];
        public async Task<IReadOnlyList<PurgedTable>> DeleteAsync(DbTransaction transaction, string tenantId, CancellationToken ct)
        {
            await using var cmd = transaction.Connection!.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"DELETE FROM {table.Table} WHERE {table.TenantColumn}='{tenantId}'";
            return [new PurgedTable(table.Table, await cmd.ExecuteNonQueryAsync(ct))];
        }
    }

    /// <summary>A tenant authenticator that lets every request in as <paramref name="tokenId"/> of acme, for the activity writer.</summary>
    private sealed class AuthenticatesAs(string tokenId) : ITenantAuthenticator
    {
        private static readonly Tenant Acme = new("acme", "Acme", "https://x/", "o/", "https://x/i.json", new TenantPolicy(14));
        public ValueTask<AuthResult?> AuthenticateAsync(string token, CancellationToken ct) => ValueTask.FromResult<AuthResult?>(new AuthResult(Acme, tokenId));
    }

    /// <summary>The purge store with this eligibility (none: every purge refused) and these contributors, as a lifecycle gives them.</summary>
    protected ITenantPurgeStore Store(IEngineDb t, IPurgeRecheck? eligibility, params IPurgeContributor[] contributors) =>
        Engine.Purge(t, new TenantLifecycle([], contributors, eligibility is null ? null : () => eligibility));

    protected static PurgeCandidate Due(string tenant) => new(tenant, PurgeVerdict.Due, "ended", null, "ended", "detail");

    protected static TenantPurgeRecord Record(IEngineDb t, string tenant) => new(tenant, t.Clock.UtcNow, "ended", "operator");

    /// <summary>
    /// A tenant with a row in every kind of table a purge reaches, written through the stores: a token used on one day, a member, its
    /// directory link, a policy prefix, two audit events, a decision record, and the operator's record of it. Returns the token's id.
    /// </summary>
    protected async Task<string> SeedAsync(IEngineDb t, string tenant, string directory)
    {
        await AddTenantAsync(t, tenant);
        var token = await Engine.Tokens(t).IssueAsync(tenant, "ci", Ct);
        await Engine.TokenActivity(t).RecordAsync(token.TokenId, DateOnly.FromDateTime(t.Clock.UtcNow.UtcDateTime), Ct);
        Assert.True(await Engine.SignInDirectory(t).BindAsync(new DirectoryBinding(directory, tenant, t.Clock.UtcNow, "operator"), Ct));
        await Engine.Members(t).UpsertAsync(directory, Oid, tenant, "Ada", t.Clock.UtcNow, Ct);
        await Engine.Tenants(t).AddInternalPrefixAsync(tenant, "Contoso.", Ct);
        foreach (var request in new[] { tenant + "-1", tenant + "-2" })
            await Engine.Audit(t).AppendAsync(new AuditEvent(t.Clock.UtcNow, request, tenant, token.TokenId, AuditKind.Download, "moq", "4.20.72",
                Decision.Allow, VerdictReason.CooldownElapsed, null), Ct);
        await Engine.DecisionRecords(t).UpsertAsync([DecisionRecordOf(t, tenant)], Ct);
        await Engine.AdminActions(t).AppendAsync(new AdminActionRecord(t.Clock.UtcNow, "", "", "op", "tenant.create", tenant, "", "cli"), Ct);
        return token.TokenId;
    }

    protected static DecisionRecord DecisionRecordOf(IEngineDb t, string tenant) =>
        new(tenant, Ecosystem.NuGet, "moq", "4.21.0", Decision.Hide, VerdictReason.Cooldown, null, 14, null, 1, t.Clock.UtcNow, t.Clock.UtcNow, 1);

    /// <summary>How many rows the tenant has in each table a purge reaches or keeps, as one line to compare.</summary>
    protected async Task<string> RowsAsync(IEngineDb t, string tenant, string tokenId)
    {
        var counts = new List<string>();
        foreach (var (table, where) in new[]
        {
            ("tenants", $"id='{tenant}'"), ("tokens", $"tenant_id='{tenant}'"), ("token_activity", $"token_id='{tokenId}'"),
            ("sign_in_directories", $"tenant_id='{tenant}'"), ("members", $"tenant_id='{tenant}'"), ("internal_prefixes", $"tenant_id='{tenant}'"),
            ("audit_events", $"tenant_id='{tenant}'"), ("decision_records", $"tenant_id='{tenant}'"), ("admin_actions", $"tenant_id='{tenant}' AND action<>'{MarkerAction}'"),
            ("tenant_purges", $"tenant_id='{tenant}'"),
        })
            counts.Add($"{table}={await ScalarAsync(t, $"SELECT COUNT(*) FROM {table} WHERE {where}")}");
        return string.Join(" ", counts);
    }

    protected const string Seeded =
        "tenants=1 tokens=1 token_activity=1 sign_in_directories=1 members=1 internal_prefixes=1 audit_events=2 decision_records=1 admin_actions=1 tenant_purges=0";

    /// <summary>After its purge: nothing but the operator's record and the purge record.</summary>
    protected const string Purged =
        "tenants=0 tokens=0 token_activity=0 sign_in_directories=0 members=0 internal_prefixes=0 audit_events=0 decision_records=0 admin_actions=1 tenant_purges=1";

    // ---------------------------------------------------------------- only when due

    [EngineFact]
    public async Task With_no_eligibility_every_purge_is_refused_and_nothing_is_deleted()
    {
        await using var t = await CreateDbAsync();
        var token = await SeedAsync(t, "acme", Directory1);
        var store = Store(t, null);

        Assert.Null(store.Eligibility);
        var refused = await Assert.ThrowsAsync<PurgeRefusedException>(() => store.PurgeAsync(Due("acme"), Record(t, "acme"), Ct));
        Assert.Equal(TenantPurgeCommands.NoEligibility, refused.Message);
        Assert.Equal(Seeded, await RowsAsync(t, "acme", token));
    }

    [EngineFact]   // mutation: the re-check's answer ignored
    public async Task A_tenant_no_longer_due_inside_the_transaction_is_refused_and_nothing_is_deleted()
    {
        await using var t = await CreateDbAsync();
        var token = await SeedAsync(t, "acme", Directory1);
        var eligibility = new Eligibility("acme") { StillDue = false };

        await Assert.ThrowsAsync<PurgeRefusedException>(() => Store(t, eligibility).PurgeAsync(Due("acme"), Record(t, "acme"), Ct));

        Assert.Equal(Seeded, await RowsAsync(t, "acme", token));
        Assert.Null(await Store(t, eligibility).FindPurgeAsync("acme", Ct));
        // Asked once, at the record's time, on the purge's transaction: its marker went with the refusal.
        Assert.Equal(new[] { ("acme", t.Clock.UtcNow) }, eligibility.Rechecks);
        Assert.Equal(0L, await MarkersAsync(t, "acme"));
    }

    [EngineFact]
    public async Task A_tenant_the_eligibility_does_not_list_is_refused_by_the_command_and_nothing_is_deleted()
    {
        await using var t = await CreateDbAsync();
        var token = await SeedAsync(t, "acme", Directory1);
        var commands = new TenantPurgeCommands(Store(t, new Eligibility("someone-else")), Engine.AdminActions(t), t.Clock);

        await Assert.ThrowsAsync<PurgeRefusedException>(() => commands.PurgeAsync(ActorScope.OperatorAs("operator"), "acme", "acme", Ct));

        Assert.Equal(Seeded, await RowsAsync(t, "acme", token));
    }

    // ---------------------------------------------------------------- what a purge deletes

    [EngineFact]   // mutations: skip purging=1 (on SQLite the audit trigger then refuses the delete); the re-check on another connection
    public async Task A_purge_deletes_the_tenants_rows_audit_and_decisions_and_keeps_every_other_tenants()
    {
        await using var t = await CreateDbAsync();
        var acme = await SeedAsync(t, "acme", Directory1);
        var globex = await SeedAsync(t, "globex", Directory2);
        var store = Store(t, new Eligibility("acme"));

        var counts = await store.PurgeAsync(Due("acme"), Record(t, "acme"), Ct);

        Assert.Equal(Purged, await RowsAsync(t, "acme", acme));
        Assert.Equal(Seeded, await RowsAsync(t, "globex", globex));
        Assert.Equal(new[] { "globex-1", "globex-2" }, await ColumnAsync(t, "SELECT request_id FROM audit_events ORDER BY request_id"));
        Assert.Equal(2, counts.Single(c => c.Table == "audit_events").Rows);
        Assert.Equal(1, counts.Single(c => c.Table == "decision_records").Rows);
        Assert.Equal(1, counts.Single(c => c.Table == "tenants").Rows);
        Assert.Equal(new TenantPurgeRecord("acme", t.Clock.UtcNow, "ended", "operator"), await store.FindPurgeAsync("acme", Ct));
        // The record is closed: no other connection may ever see a purge running.
        // (purging stays an integer 0/1 on every engine, so it reads as a long: the PostgreSQL schema keeps it bigint with the same CHECK.)
        Assert.Equal(0L, await ScalarAsync(t, "SELECT purging FROM tenant_purges WHERE tenant_id='acme'"));
        // The re-check ran on the purge's transaction: its marker was committed with it.
        Assert.Equal(1L, await MarkersAsync(t, "acme"));
    }

    /// <summary>
    /// The feed setup scratch may hold the names of a feed's administrators: a purge deletes the tenant's rows, every person's and kind's,
    /// live or expired, and keeps every other tenant's (the same person in another tenant too).
    /// </summary>
    [EngineFact]   // mutations: the purge skipping the scratch (its second guard then refuses), or the tenant's feed-check lease; the table left out of the classification
    public async Task A_purge_deletes_the_tenants_feed_setup_scratch_and_keeps_every_other_tenants()
    {
        await using var t = await CreateDbAsync();
        var acme = await SeedAsync(t, "acme", Directory1);
        var globex = await SeedAsync(t, "globex", Directory2);
        var scratch = Engine.FeedSetupScratch(t);
        var ada = Guid.Parse(Oid);
        var grace = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
        foreach (var kind in FeedSetupScratchKinds.All)
            await scratch.PutAsync("acme", ada, kind, "{\"NamedAdmins\":[{\"Name\":\"Ada Lovelace\"}]}", TimeSpan.FromMinutes(30), Ct);
        await scratch.PutAsync("acme", grace, FeedSetupScratchKinds.Developer, "{}", TimeSpan.FromMinutes(1), Ct);
        await scratch.PutAsync("globex", ada, FeedSetupScratchKinds.Settings, "globex's", TimeSpan.FromMinutes(30), Ct);
        var lease = Engine.JobLease(t);
        foreach (var job in new[] { AzureSetupState.LeaseJob("acme"), AzureSetupState.LeaseJob("globex"), JobLeases.Retention, AzureSetupState.LeaseJob("acme-2") })
            Assert.True(await lease.TryAcquireAsync(job, "copy-1", TimeSpan.FromMinutes(2), Ct));
        t.Clock.Advance(TimeSpan.FromMinutes(5));   // grace's has expired, but is not yet pruned: the purge deletes it too

        var counts = await Store(t, new Eligibility("acme")).PurgeAsync(Due("acme"), Record(t, "acme"), Ct);

        Assert.Equal(4, counts.Single(c => c.Table == "feed_setup_scratch").Rows);
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM feed_setup_scratch WHERE tenant_id = 'acme'"));
        Assert.Equal("globex's", await scratch.GetAsync("globex", ada, FeedSetupScratchKinds.Settings, Ct));
        // and the tenant's feed-check lease, which names it, while every other job's stays (not counted: job_leases holds no tenant data)
        Assert.Equal(["feed-check:acme-2", "feed-check:globex", "retention"], await ColumnAsync(t, "SELECT job FROM job_leases ORDER BY job"));
        Assert.DoesNotContain(counts, c => c.Table == "job_leases");
        Assert.Equal(Purged, await RowsAsync(t, "acme", acme));
        Assert.Equal(Seeded, await RowsAsync(t, "globex", globex));
    }

    /// <summary>Rests on the engine's triggers (SQLite: migrations 15 and 17's RAISE(IGNORE); Postgres: its triggers).</summary>
    [EngineFact]
    [Trait(Needs, Triggers)]
    public async Task A_late_audit_event_or_decision_record_for_a_purged_tenant_is_dropped_and_another_tenants_is_kept()
    {
        await using var t = await CreateDbAsync();
        var acme = await SeedAsync(t, "acme", Directory1);
        await SeedAsync(t, "globex", Directory2);
        await Store(t, new Eligibility("acme")).PurgeAsync(Due("acme"), Record(t, "acme"), Ct);

        // A request already in flight when the purge ran.
        await Engine.Audit(t).AppendAsync(new AuditEvent(t.Clock.UtcNow, "late", "acme", acme, AuditKind.Download, "moq", "4.20.72",
            Decision.Allow, VerdictReason.CooldownElapsed, null), Ct);
        await Engine.DecisionRecords(t).UpsertAsync([DecisionRecordOf(t, "acme") with { Version = "9.9.9" }, DecisionRecordOf(t, "globex") with { Version = "9.9.9" }], Ct);
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM audit_events WHERE tenant_id='acme'"));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM decision_records WHERE tenant_id='acme'"));
        Assert.Equal(2L, await ScalarAsync(t, "SELECT COUNT(*) FROM decision_records WHERE tenant_id='globex'"));
        Assert.Equal(new[] { "globex-1", "globex-2" }, await ColumnAsync(t, "SELECT request_id FROM audit_events ORDER BY request_id"));
    }

    /// <summary>
    /// What a token's activity note meets once its tenant's purge has committed, on every engine. On
    /// PostgreSQL that is where a note that arrived while the purge ran ends up (the purge holds the tenant's tokens, so the note's
    /// foreign-key check waits for it). The token is gone, so the note is refused by its foreign key: SQLite's INSERT OR IGNORE does not
    /// ignore a foreign key, and PostgreSQL's ON CONFLICT DO NOTHING does not either. The activity writer, which drains the notes off the
    /// request path, counts it as one failed write and logs its warning, the same on both; nothing is recorded and the request was answered.
    /// </summary>
    [EngineFact]
    public async Task A_token_activity_note_for_a_purged_tenants_token_is_refused_and_the_activity_writer_counts_it()
    {
        await using var t = await CreateDbAsync();
        var acme = await SeedAsync(t, "acme", Directory1);
        await Store(t, new Eligibility("acme")).PurgeAsync(Due("acme"), Record(t, "acme"), Ct);
        var tomorrow = DateOnly.FromDateTime(t.Clock.UtcNow.UtcDateTime).AddDays(1);

        await RefusedAsync(() => Engine.TokenActivity(t).RecordAsync(acme, tomorrow, Ct).AsTask());

        var warnings = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using (var writer = new ActivityRecordingAuthenticator(new AuthenticatesAs(acme), Engine.TokenActivity(t), t.Clock, warnings.Enqueue))
        {
            Assert.NotNull(await writer.AuthenticateAsync("a request's token", Ct));   // the request is answered; the note is queued
            await writer.DrainAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, writer.FailedWrites);
            Assert.Equal(0, writer.DroppedWrites);
        }
        var warning = Assert.Single(warnings);
        Assert.StartsWith("Token activity write failed (", warning, StringComparison.Ordinal);
        Assert.EndsWith("); 1 failure(s) so far. The active-developer count may be too low.", warning, StringComparison.Ordinal);
        Assert.Equal(0L, await ScalarAsync(t, $"SELECT COUNT(*) FROM token_activity WHERE token_id='{acme}'"));
    }

    // ---------------------------------------------------------------- the second guard

    /// <summary>
    /// The second guard: a module that declares a table of its own deleted but leaves the tenant's row in it
    /// refuses the purge, once every delete has run and before the commit, so nothing is deleted. The module's table has no foreign key
    /// to tenants, so nothing but the guard stops the purge. Deleted, the same row no longer refuses.
    /// </summary>
    [EngineFact]   // mutation: the second guard (RequireDeletedAsync) skipped, on either engine
    public async Task A_module_table_declared_deleted_but_left_holding_a_row_refuses_the_purge_and_nothing_is_deleted()
    {
        await using var t = await CreateDbAsync();
        var acme = await SeedAsync(t, "acme", Directory1);
        var globex = await SeedAsync(t, "globex", Directory2);
        await ExecAsync(t, "CREATE TABLE module_notes(tenant_id TEXT NOT NULL)");
        await ExecAsync(t, "INSERT INTO module_notes(tenant_id) VALUES('acme')");
        await ExecAsync(t, "INSERT INTO module_notes(tenant_id) VALUES('globex')");
        var notes = new PurgeTable("module_notes", PurgeTableFate.Deleted, "the module's notes", "tenant_id");

        var refused = await Assert.ThrowsAsync<PurgeRefusedException>(() =>
            Store(t, new Eligibility("acme"), new Forgetful(notes)).PurgeAsync(Due("acme"), Record(t, "acme"), Ct));

        Assert.Equal("Table module_notes still holds 1 row(s) for tenant acme after the purge. Nothing was deleted.", refused.Message);
        Assert.Equal(Seeded, await RowsAsync(t, "acme", acme));
        Assert.Equal(Seeded, await RowsAsync(t, "globex", globex));
        Assert.Equal(2L, await ScalarAsync(t, "SELECT COUNT(*) FROM module_notes"));
        Assert.Null(await Store(t, new Eligibility("acme")).FindPurgeAsync("acme", Ct));
        Assert.Equal(0L, await MarkersAsync(t, "acme"));

        // A module that deletes its row: the purge goes, and the other tenant's row stays.
        var counts = await Store(t, new Eligibility("acme"), new Deletes(notes)).PurgeAsync(Due("acme"), Record(t, "acme"), Ct);
        Assert.Equal(1, counts.Single(c => c.Table == "module_notes").Rows);
        Assert.Equal(Purged, await RowsAsync(t, "acme", acme));
        Assert.Equal(new[] { "globex" }, await ColumnAsync(t, "SELECT tenant_id FROM module_notes"));
    }

    // ---------------------------------------------------------------- once only

    [EngineFact]
    public async Task A_second_purge_does_nothing()
    {
        await using var t = await CreateDbAsync();
        await SeedAsync(t, "acme", Directory1);
        var globex = await SeedAsync(t, "globex", Directory2);
        var store = Store(t, new Eligibility("acme"));
        var commands = new TenantPurgeCommands(store, Engine.AdminActions(t), t.Clock);

        var first = await commands.PurgeAsync(ActorScope.OperatorAs("operator"), "acme", "acme", Ct);
        Assert.False(first.AlreadyPurged);
        var adminRows = await ScalarAsync(t, "SELECT COUNT(*) FROM admin_actions");

        t.Clock.Advance(TimeSpan.FromDays(1));
        var second = await commands.PurgeAsync(ActorScope.OperatorAs("operator"), "acme", "acme", Ct);
        Assert.True(second.AlreadyPurged);
        Assert.Equal(first.PurgedAt, second.PurgedAt);
        Assert.Empty(second.Counts);
        Assert.Equal(adminRows, await ScalarAsync(t, "SELECT COUNT(*) FROM admin_actions"));

        // The store itself, asked again, refuses (the tenant is gone) and leaves the first record as it was.
        await Assert.ThrowsAsync<PurgeRefusedException>(() => store.PurgeAsync(Due("acme"), Record(t, "acme"), Ct));
        Assert.Equal(new TenantPurgeRecord("acme", first.PurgedAt, "ended", "operator"), await store.FindPurgeAsync("acme", Ct));
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM tenant_purges"));
        Assert.Equal(Seeded, await RowsAsync(t, "globex", globex));
    }

    /// <summary>Rests on the engine's triggers (SQLite: migration 15's; Postgres: its triggers).</summary>
    [EngineFact]
    [Trait(Needs, Triggers)]
    public async Task A_purged_id_is_never_used_again_and_its_record_cannot_be_written_twice()
    {
        await using var t = await CreateDbAsync();
        await SeedAsync(t, "acme", Directory1);
        await AddTenantAsync(t, "globex");
        await Store(t, new Eligibility("acme")).PurgeAsync(Due("acme"), Record(t, "acme"), Ct);

        var reuse = await RefusedAsync(() => AddTenantAsync(t, "acme"));
        Assert.Contains("never used again", reuse.Message);
        var rename = await RefusedAsync(() => ExecAsync(t, "UPDATE tenants SET id='acme' WHERE id='globex'"));
        Assert.Contains("never used again", rename.Message);
        await RefusedAsync(() => ExecAsync(t, "INSERT INTO tenant_purges(tenant_id, purged_at, reason, actor) VALUES('acme', '2026-10-11T12:00:00.0000000+00:00', 'r', 'a')"));
        await RefusedAsync(() => ExecAsync(t, "DELETE FROM tenant_purges WHERE tenant_id='acme'"));

        Assert.Equal(new[] { "globex" }, await ColumnAsync(t, "SELECT id FROM tenants"));
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM tenant_purges"));
    }

    // ---------------------------------------------------------------- all or nothing

    [EngineFact]
    public async Task A_failure_part_way_leaves_every_row_where_it_was()
    {
        await using var t = await CreateDbAsync();
        var acme = await SeedAsync(t, "acme", Directory1);
        var globex = await SeedAsync(t, "globex", Directory2);
        long tokensSeenByTheFailure = -1;
        var failing = new Contributor(async (tx, tenant) =>
        {
            // By now the core's child tables are deleted for the tenant, on this transaction; then the failure.
            await using var cmd = tx.Connection!.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT COUNT(*) FROM tokens WHERE tenant_id='{tenant}'";
            tokensSeenByTheFailure = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            throw new InvalidOperationException("the module's delete failed");
        });

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => Store(t, new Eligibility("acme"), failing).PurgeAsync(Due("acme"), Record(t, "acme"), Ct));

        Assert.Equal("the module's delete failed", thrown.Message);
        Assert.Equal(0L, tokensSeenByTheFailure);
        Assert.Equal(Seeded, await RowsAsync(t, "acme", acme));
        Assert.Equal(Seeded, await RowsAsync(t, "globex", globex));
        Assert.Equal(0L, await MarkersAsync(t, "acme"));

        // Nothing is left half done or locked: the same purge then runs.
        await Store(t, new Eligibility("acme")).PurgeAsync(Due("acme"), Record(t, "acme"), Ct);
        Assert.Equal(Purged, await RowsAsync(t, "acme", acme));
        Assert.Equal(1L, await MarkersAsync(t, "acme"));
    }
}
