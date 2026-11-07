using System.Globalization;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// #157 PR 1 as a contract (#232 S1): the cap-checked customer insert in one transaction, the duplicate refusal, revoked_by and the waiver
/// list. Each test that kills one of the planned mutations says which. Migration 20 stays in <see cref="WaiverStoreTests"/>.
/// </summary>
public abstract class WaiverStoreContract : StoreContract
{
    protected static readonly CancellationToken Ct = CancellationToken.None;
    protected const string Reason = "urgent security fix";
    protected const int Cap = WaiverRules.MaxActiveCustomerWaivers;

    protected static Task<CustomerWaiverInsert> Customer(TestDb t, string package, string version = "1.0.0", string tenant = "acme", int days = 7) =>
        t.Tenants.AddCustomerWaiverAsync(tenant, package, version, t.Clock.UtcNow.AddDays(days), Reason, "Alice (oid)", Cap, Ct);

    protected Task<long> CountAsync(TestDb t, string where) =>
        ScalarAsync(t, "SELECT COUNT(*) FROM waivers WHERE " + where).ContinueWith(x => (long)x.Result!);

    // ---------------------------------------------------------------- the cap

    [Fact]   // mutations: drop the cap check; count with <= instead of <; a deferred transaction (the count and insert see different rows)
    public async Task Twenty_six_concurrent_customer_inserts_leave_exactly_twenty_five()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, Cap + 1).Select(i => Task.Run(() => Customer(t, "pkg" + i))));

        Assert.Equal(Cap, results.Count(r => r.Outcome == WaiverInsertOutcome.Added));
        Assert.Equal(1, results.Count(r => r.Outcome == WaiverInsertOutcome.CapReached));
        Assert.All(results.Where(r => r.Outcome == WaiverInsertOutcome.CapReached), r => Assert.Null(r.Waiver));
        Assert.Equal(Cap, await CountAsync(t, "tenant_id='acme' AND source='customer'"));
        // Every count reported after an insert is distinct, 1 to 25: each insert saw the ones before it.
        Assert.Equal(Enumerable.Range(1, Cap), results.Where(r => r.Outcome == WaiverInsertOutcome.Added).Select(r => r.ActiveCustomerWaivers).Order());
    }

    [Fact]   // mutations: count operator rows; count revoked rows; count expired rows; count another tenant's rows
    public async Task Expired_revoked_operator_and_other_tenants_waivers_do_not_count()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        await t.AddTenantAsync("other");
        for (var i = 0; i < 5; i++) await t.Tenants.AddWaiverAsync("acme", "op" + i, "1.0.0", t.Clock.UtcNow.AddDays(30), "r", "olivia", Ct);
        for (var i = 0; i < 5; i++) Assert.Equal(WaiverInsertOutcome.Added, (await Customer(t, "theirs" + i, tenant: "other")).Outcome);
        for (var i = 0; i < 5; i++)
        {
            var r = await Customer(t, "revoked" + i);
            Assert.True(await t.Tenants.RevokeWaiverAsync("acme", r.Waiver!.Id, "Alice (oid)", Ct));
        }
        for (var i = 0; i < 5; i++) Assert.Equal(WaiverInsertOutcome.Added, (await Customer(t, "short" + i, days: 1)).Outcome);
        t.Clock.Advance(TimeSpan.FromDays(1));   // the five one-day waivers expire exactly now: an expiry equal to now is no longer active

        for (var i = 1; i <= Cap; i++)
        {
            var r = await Customer(t, "live" + i);
            Assert.Equal(WaiverInsertOutcome.Added, r.Outcome);
            Assert.Equal(i, r.ActiveCustomerWaivers);
        }
        Assert.Equal(WaiverInsertOutcome.CapReached, (await Customer(t, "one-more")).Outcome);
        Assert.Equal(0L, await CountAsync(t, "package_id='one-more'"));
    }

    [Fact]
    public async Task The_operator_insert_is_never_capped_and_is_stored_as_operator()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();
        for (var i = 0; i < Cap; i++) await Customer(t, "c" + i);
        var w = await t.Tenants.AddWaiverAsync("acme", "Moq", "*", t.Clock.UtcNow.AddDays(3650), "r", "olivia", Ct);
        Assert.Equal(WaiverSource.Operator, w.Source);
        Assert.Equal("operator", await ScalarAsync(t, $"SELECT source FROM waivers WHERE id='{w.Id}'"));
        Assert.Equal((long)Cap, await CountAsync(t, "source='customer'"));
    }

    [Fact]
    public async Task A_customer_insert_reports_the_tenants_origin_for_the_alert()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        await t.Tenants.CreateAsync(new NewTenant("org-k3j7x2m6q4w7z4ab", "Self", "https://pkgs.dev.azure.com/", "o/", "https://pkgs.dev.azure.com/o/p/index.json", 14, TenantOrigin.Self), Ct);
        Assert.Equal(TenantOrigin.Operator, (await Customer(t, "a")).Origin);
        Assert.Equal(TenantOrigin.Self, (await Customer(t, "a", tenant: "org-k3j7x2m6q4w7z4ab")).Origin);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Customer(t, "a", tenant: "nobody"));
    }

    [Fact]
    public async Task A_customer_waiver_is_stored_as_customer_and_reads_back_with_its_source()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();
        var r = await Customer(t, "Moq", "4.21.0");
        Assert.Equal(WaiverSource.Customer, r.Waiver!.Source);
        Assert.Matches("^wvr_[0-9a-f]{12}$", r.Waiver.Id);
        var w = Assert.Single((await t.Tenants.GetAsync("acme", Ct))!.Policy.Waivers);
        Assert.Equal(r.Waiver, w);
        Assert.Equal("customer", await ScalarAsync(t, $"SELECT source FROM waivers WHERE id='{w.Id}'"));
    }

    [Fact]
    public async Task A_customer_insert_bumps_the_policy_revision_and_a_refused_one_does_not()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();
        async Task<long> Revision() => (await t.Tenants.GetAsync("acme", Ct))!.Policy.Revision;
        var r0 = await Revision();
        await Customer(t, "a");
        var r1 = await Revision();
        Assert.True(r1 > r0);
        Assert.Equal(WaiverInsertOutcome.Duplicate, (await Customer(t, "a")).Outcome);
        Assert.Equal(r1, await Revision());
    }

    // ---------------------------------------------------------------- duplicates

    [Fact]   // mutation: remove the duplicate check
    public async Task A_duplicate_active_waiver_is_refused_for_the_same_package_and_version_however_written()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();
        await t.AddTenantAsync("other");
        var first = await Customer(t, "Moq", "4.21.0");
        Assert.Equal(WaiverInsertOutcome.Added, first.Outcome);

        var again = await Customer(t, "moq", "4.21.0");
        Assert.Equal(WaiverInsertOutcome.Duplicate, again.Outcome);
        Assert.Null(again.Waiver);
        Assert.Equal(WaiverInsertOutcome.Duplicate, (await Customer(t, "MOQ", "4.21")).Outcome);   // 4.21 is 4.21.0
        var op = await Assert.ThrowsAsync<WaiverRefusedException>(() => t.Tenants.AddWaiverAsync("acme", "Moq", "4.21.0.0", t.Clock.UtcNow.AddDays(1), "r", "olivia", Ct));
        Assert.Equal(WaiverRefusal.Duplicate, op.Reason);
        Assert.Equal(1L, await CountAsync(t, "package_id='moq'"));

        Assert.Equal(WaiverInsertOutcome.Added, (await Customer(t, "Moq", "4.21.1")).Outcome);                 // another version
        Assert.Equal(WaiverInsertOutcome.Added, (await Customer(t, "Moq", "4.21.0", tenant: "other")).Outcome); // another tenant
    }

    [Fact]
    public async Task A_revoked_or_expired_waiver_is_no_duplicate()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();
        var first = await Customer(t, "Moq", "4.21.0", days: 1);
        Assert.True(await t.Tenants.RevokeWaiverAsync("acme", first.Waiver!.Id, "Alice (oid)", Ct));
        var second = await Customer(t, "Moq", "4.21.0", days: 1);
        Assert.Equal(WaiverInsertOutcome.Added, second.Outcome);
        t.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(WaiverInsertOutcome.Added, (await Customer(t, "Moq", "4.21.0")).Outcome);
    }

    [Fact]
    public async Task A_wildcard_duplicates_only_a_wildcard()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();
        await t.Tenants.AddWaiverAsync("acme", "Moq", "*", t.Clock.UtcNow.AddDays(10), "r", "olivia", Ct);
        Assert.Equal(WaiverInsertOutcome.Added, (await Customer(t, "Moq", "4.21.0")).Outcome);   // narrower: harmless, and revoking the wildcard keeps it
        await Assert.ThrowsAsync<WaiverRefusedException>(() => t.Tenants.AddWaiverAsync("acme", "moq", " * ", t.Clock.UtcNow.AddDays(1), "r", "olivia", Ct));
    }

    // ---------------------------------------------------------------- revoked_by

    [Fact]   // mutation: revoked_by not written
    public async Task Revoke_records_who_and_a_cross_tenant_revoke_changes_nothing()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        await t.AddTenantAsync("other");
        var mine = (await Customer(t, "a")).Waiver!;
        var theirs = (await Customer(t, "b", tenant: "other")).Waiver!;
        var byOperator = await t.Tenants.AddWaiverAsync("acme", "c", "1.0.0", t.Clock.UtcNow.AddDays(1), "r", "olivia", Ct);

        Assert.False(await t.Tenants.RevokeWaiverAsync("acme", theirs.Id, "Mallory (oid)", Ct));
        Assert.Equal("NULL", (await ColumnAsync(t, $"SELECT revoked_by FROM waivers WHERE id='{theirs.Id}'")).Single());
        Assert.Equal("NULL", (await ColumnAsync(t, $"SELECT revoked_at FROM waivers WHERE id='{theirs.Id}'")).Single());

        Assert.True(await t.Tenants.RevokeWaiverAsync("acme", mine.Id, "Alice (oid)", Ct));
        Assert.True(await t.Tenants.RevokeWaiverAsync(null, byOperator.Id, "olivia", Ct));
        Assert.Equal("Alice (oid)", await ScalarAsync(t, $"SELECT revoked_by FROM waivers WHERE id='{mine.Id}'"));
        Assert.Equal("olivia", await ScalarAsync(t, $"SELECT revoked_by FROM waivers WHERE id='{byOperator.Id}'"));

        Assert.False(await t.Tenants.RevokeWaiverAsync("acme", mine.Id, "Bob (oid)", Ct));   // already revoked: the first revoker stays
        Assert.Equal("Alice (oid)", await ScalarAsync(t, $"SELECT revoked_by FROM waivers WHERE id='{mine.Id}'"));
    }

    // ---------------------------------------------------------------- the list

    [Fact]
    public async Task The_list_is_active_first_then_newest_first_holds_history_and_only_the_tenants_own_rows()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        await t.AddTenantAsync("other");
        var op = await t.Tenants.AddWaiverAsync("acme", "Op.Pkg", "*", t.Clock.UtcNow.AddDays(10), "operator reason", "olivia", Ct);
        t.Clock.Advance(TimeSpan.FromMinutes(1));
        var revoked = (await Customer(t, "revoked", days: 3)).Waiver!;
        t.Clock.Advance(TimeSpan.FromMinutes(1));
        await t.Tenants.RevokeWaiverAsync("acme", revoked.Id, "Bob (oid)", Ct);
        var live = (await Customer(t, "live")).Waiver!;
        await Customer(t, "theirs", tenant: "other");

        var list = await t.Tenants.ListWaiversAsync("acme", 100, Ct);

        Assert.Equal(new[] { live.Id, op.Id, revoked.Id }, list.Select(w => w.Id));   // the two active ones first, newest first; then the history
        Assert.Equal(WaiverSource.Customer, list[0].Source);
        Assert.Null(list[0].RevokedAt);
        Assert.Null(list[0].RevokedBy);
        Assert.Equal("Alice (oid)", list[0].CreatedBy);
        Assert.Equal(t.Clock.UtcNow, list[0].CreatedAt);
        Assert.Equal(Reason, list[0].Reason);
        Assert.Equal("Bob (oid)", list[2].RevokedBy);
        Assert.Equal(t.Clock.UtcNow, list[2].RevokedAt);
        Assert.Equal(WaiverSource.Operator, list[1].Source);
        Assert.Equal("*", list[1].Version);
        Assert.Equal("op.pkg", list[1].PackageId);
        Assert.True(list[0].IsActiveAt(t.Clock.UtcNow));
        Assert.True(list[1].IsActiveAt(t.Clock.UtcNow));
        Assert.False(list[2].IsActiveAt(t.Clock.UtcNow));
        Assert.False(list[0].IsActiveAt(list[0].ExpiresAt));

        Assert.Equal(new[] { live.Id }, (await t.Tenants.ListWaiversAsync("acme", 1, Ct)).Select(w => w.Id));
        Assert.Empty(await t.Tenants.ListWaiversAsync("nobody", 100, Ct));
    }

    [Fact]   // #161 review: mutation: order the list by created_at alone (the old active waiver falls off the end)
    public async Task An_old_active_waiver_is_listed_even_behind_more_than_the_limit_of_newer_ended_ones()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();
        var old = await t.Tenants.AddWaiverAsync("acme", "Old.Pkg", "1.0.0", t.Clock.UtcNow.AddDays(30), Reason, "olivia", Ct);
        const int limit = 200;
        for (var i = 0; i < limit + 5; i++)
        {
            t.Clock.Advance(TimeSpan.FromSeconds(1));
            var w = await t.Tenants.AddWaiverAsync("acme", "pkg" + i, "1.0.0", t.Clock.UtcNow.AddDays(1), Reason, "olivia", Ct);
            if (i % 2 == 0) await t.Tenants.RevokeWaiverAsync("acme", w.Id, "Bob (oid)", Ct);
        }
        t.Clock.Advance(TimeSpan.FromDays(2));   // every newer one has now ended: revoked or expired

        var list = await t.Tenants.ListWaiversAsync("acme", limit, Ct);

        Assert.Equal(limit, list.Count);
        Assert.Equal(old.Id, list[0].Id);
        Assert.True(list[0].IsActiveAt(t.Clock.UtcNow));
        Assert.All(list.Skip(1), w => Assert.False(w.IsActiveAt(t.Clock.UtcNow)));
        Assert.Equal(list.Skip(1).Select(w => w.CreatedAt).OrderDescending(), list.Skip(1).Select(w => w.CreatedAt));   // the history newest first
    }

    [Fact]   // #161 review: mutation: count the cap before the duplicate check
    public async Task A_duplicate_at_the_cap_is_reported_as_a_duplicate_not_as_the_cap()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();
        for (var i = 0; i < Cap; i++) Assert.Equal(WaiverInsertOutcome.Added, (await Customer(t, "pkg" + i)).Outcome);

        Assert.Equal(WaiverInsertOutcome.Duplicate, (await Customer(t, "PKG0", "1.0")).Outcome);
        Assert.Equal(WaiverInsertOutcome.CapReached, (await Customer(t, "another")).Outcome);
        Assert.Equal(Cap, await CountAsync(t, "tenant_id='acme'"));
    }

    // ---------------------------------------------------------------- through the commands, end to end

    [Fact]   // mutation: alert at >= 24, or never
    public async Task Through_the_commands_the_25th_customer_waiver_alerts_once_and_the_26th_is_refused()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();
        var alerts = new ListAlerts();
        var commands = new AdminCommands(t.Tenants, t.Tokens, t.Clock, alerts: alerts);
        var actor = ActorScope.ForTenant("acme", "Alice (oid)");

        for (var i = 1; i < Cap; i++) await commands.AddWaiverAsync(actor, "acme", "pkg" + i, "1.0.0", 7, Reason, Ct);
        Assert.Empty(alerts.Alerts);
        await commands.AddWaiverAsync(actor, "acme", "pkg" + Cap, "1.0.0", 7, Reason, Ct);
        var alert = Assert.IsType<OpsAlert.WaiverCapReached>(Assert.Single(alerts.Alerts));
        Assert.Equal(TenantRef.For("acme", TenantOrigin.Operator), alert.Tenant);

        var ex = await Assert.ThrowsAsync<WaiverRefusedException>(() => commands.AddWaiverAsync(actor, "acme", "pkg-extra", "1.0.0", 7, Reason, Ct));
        Assert.Equal(WaiverRefusal.TooManyWaivers, ex.Reason);
        Assert.Single(alerts.Alerts);
        await commands.AddWaiverAsync(ActorScope.OperatorAs("olivia"), "acme", "pkg-extra", "*", 7, "r", Ct);   // the operator is not capped
    }

    protected sealed class ListAlerts : IOpsAlerts
    {
        public List<OpsAlert> Alerts { get; } = new();
        public void Notify(OpsAlert alert) => Alerts.Add(alert);
    }
}
