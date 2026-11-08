using System.Data.Common;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The tenant purge's store as a contract (#288 P2, from #105, #194): the core's lifecycle only (no module's tables). A purge happens only
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

    [Fact]
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

    [Fact]   // mutation: the re-check's answer ignored
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

    [Fact]
    public async Task A_tenant_the_eligibility_does_not_list_is_refused_by_the_command_and_nothing_is_deleted()
    {
        await using var t = await CreateDbAsync();
        var token = await SeedAsync(t, "acme", Directory1);
        var commands = new TenantPurgeCommands(Store(t, new Eligibility("someone-else")), Engine.AdminActions(t), t.Clock);

        await Assert.ThrowsAsync<PurgeRefusedException>(() => commands.PurgeAsync(ActorScope.OperatorAs("operator"), "acme", "acme", Ct));

        Assert.Equal(Seeded, await RowsAsync(t, "acme", token));
    }

    // ---------------------------------------------------------------- what a purge deletes

    [Fact]   // mutations: skip purging=1 (on SQLite the audit trigger then refuses the delete); the re-check on another connection
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
        // (purging stays an integer 0/1 on every engine, so it reads as a long: PR 5's schema keeps it bigint with the same CHECK.)
        Assert.Equal(0L, await ScalarAsync(t, "SELECT purging FROM tenant_purges WHERE tenant_id='acme'"));
        // The re-check ran on the purge's transaction: its marker was committed with it.
        Assert.Equal(1L, await MarkersAsync(t, "acme"));
    }

    /// <summary>Rests on the engine's triggers (SQLite: migrations 15 and 17's RAISE(IGNORE); Postgres: P2 PR 6).</summary>
    [Fact]
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

    // ---------------------------------------------------------------- once only

    [Fact]
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

    /// <summary>Rests on the engine's triggers (SQLite: migration 15's; Postgres: P2 PR 6).</summary>
    [Fact]
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

    [Fact]
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
