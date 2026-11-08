using System.Data.Common;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The core's organisation creation as a contract (#288 P2, from #195): the tenant, its directory link, the first member and the record
/// in one transaction, the core's lifecycle only. A directory already linked writes nothing; a participant's veto, or a record that cannot
/// be written, leaves nothing; the first member's older row is moved to the new tenant; and of two creations racing for one directory,
/// exactly one wins. The hosted module's participants and the command line stay in the hosted OrganisationCreationTests. Each test that
/// kills one of the plan's mutations says which.
/// </summary>
public abstract class OrganisationStoreContract : StoreContract
{
    protected static readonly CancellationToken Ct = CancellationToken.None;
    protected const string Directory1 = "eeeeeeee-eeee-4eee-8eee-000000000001";
    protected const string Oid = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";

    /// <summary>Every table's row count (the schema's tables, as the engine lists them), so "nothing written" means nothing anywhere.</summary>
    protected async Task<string> AllCountsAsync(IEngineDb t)
    {
        var tables = await Engine.TablesAsync(t);
        Assert.Contains("sign_in_directories", tables);
        var counts = new List<string>();
        foreach (var table in tables) counts.Add($"{table}={await ScalarAsync(t, $"SELECT COUNT(*) FROM {table}")}");
        return string.Join(" ", counts);
    }

    protected static NewOrganisation Organisation(IEngineDb t, string id = "acme", string directory = Directory1, bool member = true, bool record = true) =>
        new(TestTenants.Operator(id), directory, t.Clock.UtcNow, "operator op",
            member ? new OrganisationMember(Oid, "Ada") : null,
            record ? new AdminActionRecord(t.Clock.UtcNow, "", "", "op", "tenant.create", id, "directory=" + directory, "cli") : null);

    /// <summary>A caller's creation participant: runs <paramref name="on"/> on the creation's transaction.</summary>
    protected sealed class Participant(Func<DbTransaction, NewTenant, Task> on) : ITenantCreationParticipant
    {
        public Task OnCreatingAsync(DbTransaction transaction, NewTenant tenant, DateTimeOffset createdAt, CancellationToken ct) => on(transaction, tenant);
    }

    protected static async Task OnTransactionAsync(DbTransaction tx, string sql)
    {
        await using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task The_tenant_link_member_and_record_are_written_together_with_the_ids_lower_case()
    {
        await using var t = await CreateDbAsync();
        var o = Organisation(t) with { AzureTenantId = Directory1.ToUpperInvariant(), FirstMember = new OrganisationMember(Oid.ToUpperInvariant(), "Ada") };

        Assert.Equal(OrganisationCreateResult.Created, await Engine.Organisations(t).CreateAsync(o, [], Ct));

        Assert.Equal(new[] { "acme" }, await ColumnAsync(t, "SELECT id FROM tenants"));
        Assert.Equal(new DirectoryBinding(Directory1, "acme", t.Clock.UtcNow, "operator op"), await Engine.SignInDirectory(t).GetAsync(Directory1, Ct));
        Assert.Equal(new Member(Directory1, Oid, "acme", "Ada", t.Clock.UtcNow, t.Clock.UtcNow), await Engine.Members(t).GetAsync(Directory1, Oid, Ct));
        var record = Assert.Single(await Engine.AdminActions(t).LatestAsync(10, Ct));
        Assert.Equal(("tenant.create", "acme", "directory=" + Directory1, "cli"), (record.Action, record.TenantId, record.Detail, record.Site));
    }

    [Fact]
    public async Task With_no_member_and_no_record_only_the_tenant_and_its_link_are_written()
    {
        await using var t = await CreateDbAsync();
        var before = await AllCountsAsync(t);
        Assert.Equal(OrganisationCreateResult.Created, await Engine.Organisations(t).CreateAsync(Organisation(t, member: false, record: false), [], Ct));
        Assert.Equal(before.Replace("sign_in_directories=0", "sign_in_directories=1").Replace("tenants=0", "tenants=1"), await AllCountsAsync(t));
    }

    [Fact]   // mutation: remove the already-linked check
    public async Task A_directory_already_linked_is_taken_writes_nothing_and_leaves_the_link_alone()
    {
        await using var t = await CreateDbAsync();
        Assert.Equal(OrganisationCreateResult.Created, await Engine.Organisations(t).CreateAsync(Organisation(t, "first"), [], Ct));
        var before = await AllCountsAsync(t);
        var ran = false;
        t.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(OrganisationCreateResult.DirectoryTaken,
            await Engine.Organisations(t).CreateAsync(Organisation(t, "second"), [new Participant((_, _) => { ran = true; return Task.CompletedTask; })], Ct));

        Assert.False(ran);   // decided before the tenant row or any participant
        Assert.Equal(before, await AllCountsAsync(t));
        Assert.Equal(new[] { "first" }, await ColumnAsync(t, $"SELECT tenant_id FROM sign_in_directories WHERE azure_tenant_id='{Directory1}'"));
        Assert.Equal(new[] { "first" }, await ColumnAsync(t, "SELECT id FROM tenants"));
    }

    [Fact]
    public async Task A_participants_veto_rolls_everything_back_and_keeps_its_code()
    {
        await using var t = await CreateDbAsync();
        var before = await AllCountsAsync(t);
        var order = new List<string>();

        var refused = await Assert.ThrowsAsync<TenantCreationRefusedException>(() => Engine.Organisations(t).CreateAsync(Organisation(t),
            [new Participant(async (tx, tenant) => { order.Add("writes"); await OnTransactionAsync(tx, $"INSERT INTO internal_prefixes(tenant_id, prefix) VALUES('{tenant.Id}', 'Acme.')"); }),
             new Participant((_, _) => { order.Add("vetoes"); throw new TenantCreationRefusedException("No.", "some-code"); }),
             new Participant((_, _) => { order.Add("never"); return Task.CompletedTask; })], Ct));

        Assert.Equal("some-code", refused.Code);
        Assert.Equal(new[] { "writes", "vetoes" }, order);
        Assert.Equal(before, await AllCountsAsync(t));
    }

    [Fact]
    public async Task The_first_members_older_row_in_another_tenant_is_moved_to_the_new_one()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "old");
        var firstSeen = t.Clock.UtcNow;
        await Engine.Members(t).UpsertAsync(Directory1, Oid, "old", "Ada (old)", firstSeen, Ct);
        t.Clock.Advance(TimeSpan.FromDays(3));

        Assert.Equal(OrganisationCreateResult.Created,
            await Engine.Organisations(t).CreateAsync(Organisation(t) with { FirstMember = new OrganisationMember(Oid, "Ada Lovelace") }, [], Ct));

        Assert.Equal(new Member(Directory1, Oid, "acme", "Ada Lovelace", firstSeen, t.Clock.UtcNow), await Engine.Members(t).GetAsync(Directory1, Oid, Ct));
        Assert.Empty(await Engine.Members(t).ListForTenantAsync("old", Ct));
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM members"));
    }

    [Fact]   // mutation: commit before the record
    public async Task No_record_means_no_organisation()
    {
        await using var t = await CreateDbAsync();
        // An older record for this tenant id, and (inside the creation, on its transaction) a rule that there may be only one: the new
        // record is then refused by the database, after the tenant, the link and the member are written.
        await Engine.AdminActions(t).AppendAsync(new AdminActionRecord(t.Clock.UtcNow, "", "", "op", "tenant.create", "acme", "earlier", "cli"), Ct);
        var before = await AllCountsAsync(t);
        var oneRecord = new Participant((tx, _) => OnTransactionAsync(tx, "CREATE UNIQUE INDEX test_one_create_record ON admin_actions(action, tenant_id)"));

        await RefusedAsync(() => Engine.Organisations(t).CreateAsync(Organisation(t), [oneRecord], Ct));

        Assert.Equal(before, await AllCountsAsync(t));
        Assert.Empty(await ColumnAsync(t, "SELECT id FROM tenants"));

        // Everything went with it, the rule included: the same creation then succeeds.
        Assert.Equal(OrganisationCreateResult.Created, await Engine.Organisations(t).CreateAsync(Organisation(t), [], Ct));
        Assert.Equal(2L, await ScalarAsync(t, "SELECT COUNT(*) FROM admin_actions WHERE action='tenant.create' AND tenant_id='acme'"));
    }

    /// <summary>
    /// The loser must get <see cref="OrganisationCreateResult.DirectoryTaken"/>, never an engine's error: self sign-up and the command line
    /// turn that answer into "this directory is already linked", and neither catches a database exception.
    /// </summary>
    [Fact]   // mutation: remove the already-linked check (the loser's link then collides on the primary key)
    public async Task Of_two_creations_racing_for_one_directory_exactly_one_wins()
    {
        await using var t = await CreateDbAsync();
        const int rounds = 8;
        var overlapped = 0;
        for (var round = 0; round < rounds; round++)
        {
            var directory = $"eeeeeeee-eeee-4eee-8eee-{round + 100:D12}";
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = 0;
            var bothInFlight = false;
            // The winner holds its transaction open until the loser's call has started (a slow runner may start the second task late),
            // then a little longer, so the loser arrives while the first creation is still open.
            var hold = new Participant(async (_, _) =>
            {
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (Volatile.Read(ref inFlight) < 2 && waited.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(1);
                bothInFlight = Volatile.Read(ref inFlight) == 2;
                await Task.Delay(20);
            });

            async Task<OrganisationCreateResult> Create(string id)
            {
                await gate.Task;
                Interlocked.Increment(ref inFlight);
                return await Engine.Organisations(t).CreateAsync(Organisation(t, id, directory, member: false), [hold], Ct);
            }

            var a = Task.Run(() => Create($"race-{round}-a"));
            var b = Task.Run(() => Create($"race-{round}-b"));
            gate.SetResult();
            var results = await Task.WhenAll(a, b);

            Assert.Equal(1, results.Count(r => r == OrganisationCreateResult.Created));
            Assert.Equal(1, results.Count(r => r == OrganisationCreateResult.DirectoryTaken));
            var winner = results[0] == OrganisationCreateResult.Created ? $"race-{round}-a" : $"race-{round}-b";
            Assert.Equal(new[] { winner }, await ColumnAsync(t, $"SELECT tenant_id FROM sign_in_directories WHERE azure_tenant_id='{directory}'"));
            Assert.Equal(new[] { winner }, await ColumnAsync(t, $"SELECT id FROM tenants WHERE id LIKE 'race-{round}-%'"));
            Assert.Equal(new[] { winner }, await ColumnAsync(t, $"SELECT tenant_id FROM admin_actions WHERE tenant_id LIKE 'race-{round}-%'"));
            if (bothInFlight) overlapped++;
        }
        Assert.True(overlapped == rounds, $"the two creations overlapped in {overlapped} of {rounds} rounds: the race was not run");
    }
}
