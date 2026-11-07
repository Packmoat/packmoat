using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Application.Tests;

/// <summary>
/// #157 PR 1: the customer limits on a waiver, enforced in <see cref="AdminCommands.AddWaiverAsync"/> for tenant actors only. The operator's
/// limits are unchanged. The store's cap and duplicate checks run in one transaction (see WaiverStoreTests); here the store is faked.
/// </summary>
public class CustomerWaiverTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string GoodReason = "urgent security fix";
    private readonly FakeStore _store = new();
    private readonly RecordingAlerts _alerts = new();
    private readonly AdminCommands _commands;
    private static readonly ActorScope Acme = ActorScope.ForTenant("acme", "Alice (oid)");

    public CustomerWaiverTests()
    {
        _commands = new AdminCommands(_store, _store, new FixedClock(), alerts: _alerts);
        _store.Tenants.Add(new Tenant("acme", "Acme", "https://pkgs.dev.azure.com/", "o/", "https://x/index.json", new TenantPolicy(14, new[] { "Contoso.*" })));
        _store.Tenants.Add(new Tenant("other", "Other", "https://pkgs.dev.azure.com/", "o/", "https://x/index.json", new TenantPolicy(14)));
    }

    private async Task<WaiverRefusal> Refused(string package, string version, int days, string reason)
    {
        var ex = await Assert.ThrowsAsync<WaiverRefusedException>(() => _commands.AddWaiverAsync(Acme, "acme", package, version, days, reason, Ct));
        Assert.DoesNotContain(_store.Calls, c => c.StartsWith("waiver", StringComparison.Ordinal) || c.StartsWith("customer-waiver", StringComparison.Ordinal));   // nothing written
        return ex.Reason;
    }

    // ---------------------------------------------------------------- the tenant actor's limits

    [Theory]   // mutation: drop the '*' refusal in WaiverRules.CleanExactVersion
    [InlineData("*")]
    [InlineData("1.*")]
    [InlineData("1.2.*")]
    [InlineData("*-beta")]
    public async Task A_tenant_actor_cannot_waive_a_wildcard(string version) =>
        Assert.Equal(WaiverRefusal.BadVersion, await Refused("Moq", version, 7, GoodReason));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("latest")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1..2")]
    [InlineData("1.2.3-")]
    [InlineData("1.2\u00003")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3-be ta")]
    public async Task A_tenant_actor_needs_an_exact_version(string version) =>
        Assert.Equal(WaiverRefusal.BadVersion, await Refused("Moq", version, 7, GoodReason));

    [Theory]   // mutation: MaxCustomerDays 31; MinCustomerDays 0
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(-1)]
    [InlineData(3650)]
    public async Task A_tenant_actor_waives_for_1_to_30_days(int days) =>
        Assert.Equal(WaiverRefusal.BadExpiry, await Refused("Moq", "4.21.0", days, GoodReason));

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(30)]
    public async Task One_to_thirty_days_are_accepted_and_the_expiry_is_the_clock_plus_the_days(int days)
    {
        var w = await _commands.AddWaiverAsync(Acme, "acme", "Moq", "4.21.0", days, GoodReason, Ct);
        Assert.Equal(new FixedClock().UtcNow.AddDays(days), w.ExpiresAt);
    }

    [Theory]   // mutation: MinReason 1; drop the control-character check
    [InlineData("short")]
    [InlineData("123456789")]
    [InlineData("          123456789          ")]   // nine characters once trimmed
    [InlineData("fix for\nCVE-2026-1")]
    [InlineData("fix for‮CVE-2026-1")]   // a direction override
    [InlineData("fix for​CVE-2026-1")]   // zero-width space
    [InlineData("fix for\tCVE-2026-1")]
    public async Task A_tenant_actor_needs_a_reason_of_10_to_200_plain_characters(string reason) =>
        Assert.Equal(WaiverRefusal.BadReason, await Refused("Moq", "4.21.0", 7, reason));

    [Fact]
    public async Task A_reason_longer_than_the_maximum_is_refused_and_one_at_it_is_accepted()
    {
        Assert.Equal(WaiverRefusal.BadReason, await Refused("Moq", "4.21.0", 7, new string('a', WaiverRules.MaxReason + 1)));
        var w = await _commands.AddWaiverAsync(Acme, "acme", "Moq", "4.21.0", 7, new string('a', WaiverRules.MaxReason), Ct);
        Assert.Equal(WaiverRules.MaxReason, w.Reason.Length);
        Assert.Equal(10, WaiverRules.MinReason);
        Assert.Equal(200, WaiverRules.MaxReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-moq")]
    [InlineData("moq pkg")]
    [InlineData("moq/../x")]
    [InlineData("moq*")]
    [InlineData("moq\u0000")]
    public async Task A_tenant_actor_needs_a_valid_package_id(string package) =>
        Assert.Equal(WaiverRefusal.BadPackage, await Refused(package, "4.21.0", 7, GoodReason));

    [Theory]   // mutation: remove the internal-prefix check
    [InlineData("Contoso.Core")]
    [InlineData("contoso.core")]
    [InlineData("CONTOSO.")]
    public async Task A_tenant_actor_cannot_waive_a_package_under_an_internal_prefix(string package) =>
        Assert.Equal(WaiverRefusal.InternalPrefix, await Refused(package, "1.0.0", 7, GoodReason));

    [Fact]
    public async Task The_stored_package_and_version_are_normalised_and_created_by_is_the_actors_name()
    {
        var w = await _commands.AddWaiverAsync(Acme, "acme", "  Newtonsoft.Json ", "13.0", 7, "  " + GoodReason + "  ", Ct);
        Assert.Equal("newtonsoft.json", w.PackageId);
        Assert.Equal("13.0.0", w.Version);
        Assert.Equal(GoodReason, w.Reason);
        Assert.Equal("Alice (oid)", w.CreatedBy);
        Assert.Equal(WaiverSource.Customer, w.Source);
        Assert.Equal(new[] { $"customer-waiver acme newtonsoft.json 13.0.0 max={WaiverRules.MaxActiveCustomerWaivers}" }, _store.Calls);
    }

    [Fact]
    public async Task A_tenant_actor_still_cannot_waive_for_another_tenant_and_nothing_is_read()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => _commands.AddWaiverAsync(Acme, "other", "Moq", "4.21.0", 7, GoodReason, Ct));
        Assert.Empty(_store.Calls);
        Assert.Empty(_store.Reads);
    }

    [Fact]   // mutation: map Duplicate to success
    public async Task A_duplicate_active_waiver_is_refused()
    {
        _store.NextCustomerOutcome = WaiverInsertOutcome.Duplicate;
        var ex = await Assert.ThrowsAsync<WaiverRefusedException>(() => _commands.AddWaiverAsync(Acme, "acme", "Moq", "4.21.0", 7, GoodReason, Ct));
        Assert.Equal(WaiverRefusal.Duplicate, ex.Reason);
        Assert.Empty(_alerts.Alerts);
    }

    [Fact]   // mutation: ignore CapReached
    public async Task The_cap_refuses_and_says_so()
    {
        _store.NextCustomerOutcome = WaiverInsertOutcome.CapReached;
        _store.ActiveCustomerAfter = WaiverRules.MaxActiveCustomerWaivers;
        var ex = await Assert.ThrowsAsync<WaiverRefusedException>(() => _commands.AddWaiverAsync(Acme, "acme", "Moq", "4.21.0", 7, GoodReason, Ct));
        Assert.Equal(WaiverRefusal.TooManyWaivers, ex.Reason);
        Assert.Empty(_alerts.Alerts);   // a refusal at the cap does not alert again: the alert was sent when the cap was reached
    }

    [Fact]
    public void Refusal_messages_never_echo_input()
    {
        foreach (var reason in Enum.GetValues<WaiverRefusal>())
        {
            var ex = new WaiverRefusedException(reason);
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
            Assert.IsAssignableFrom<ArgumentException>(ex);
        }
    }

    // ---------------------------------------------------------------- the alert at exactly 25

    [Theory]   // mutation: alert at >= 24, or at > 25, or never
    [InlineData(1, false)]
    [InlineData(24, false)]
    [InlineData(25, true)]
    public async Task The_alert_fires_when_the_insert_makes_the_count_exactly_the_cap(int activeAfter, bool alerts)
    {
        _store.ActiveCustomerAfter = activeAfter;
        await _commands.AddWaiverAsync(Acme, "acme", "Moq", "4.21.0", 7, GoodReason, Ct);
        if (!alerts) { Assert.Empty(_alerts.Alerts); return; }
        var alert = Assert.IsType<OpsAlert.WaiverCapReached>(Assert.Single(_alerts.Alerts));
        Assert.Equal(TenantRef.For("acme", TenantOrigin.Operator), alert.Tenant);
        Assert.Equal(25, alert.Cap);
    }

    [Fact]
    public async Task A_broken_alert_sink_never_fails_the_waiver()
    {
        _alerts.Throw = true;
        _store.ActiveCustomerAfter = WaiverRules.MaxActiveCustomerWaivers;
        var w = await _commands.AddWaiverAsync(Acme, "acme", "Moq", "4.21.0", 7, GoodReason, Ct);
        Assert.Equal("moq", w.PackageId);
        Assert.Single(_alerts.Alerts);
    }

    [Fact]
    public void The_cap_is_25() => Assert.Equal(25, WaiverRules.MaxActiveCustomerWaivers);

    [Fact]   // #161 review: mutation: drop the per-tenant alert throttle
    public async Task Revoke_then_add_at_the_cap_alerts_at_most_once_per_tenant_per_hour()
    {
        var clock = new MovableClock();
        var commands = new AdminCommands(_store, _store, clock, alerts: _alerts);
        _store.ActiveCustomerAfter = WaiverRules.MaxActiveCustomerWaivers;
        await commands.AddWaiverAsync(Acme, "acme", "Moq", "4.21.0", 7, GoodReason, Ct);
        clock.UtcNow = clock.UtcNow.AddMinutes(59);
        await commands.AddWaiverAsync(Acme, "acme", "Moq", "4.21.1", 7, GoodReason, Ct);   // a revoke, then an add back to 25
        Assert.Single(_alerts.Alerts);

        // another tenant reaching its cap is still reported
        await commands.AddWaiverAsync(ActorScope.ForTenant("other", "Olga (oid)"), "other", "Moq", "4.21.0", 7, GoodReason, Ct);
        Assert.Equal(2, _alerts.Alerts.Count);

        clock.UtcNow = clock.UtcNow.AddMinutes(2);   // an hour after the first alert
        await commands.AddWaiverAsync(Acme, "acme", "Moq", "4.21.2", 7, GoodReason, Ct);
        Assert.Equal(3, _alerts.Alerts.Count);
        Assert.All(_alerts.Alerts, a => Assert.IsType<OpsAlert.WaiverCapReached>(a));
    }

    // ---------------------------------------------------------------- the operator is unchanged

    [Fact]   // mutation: apply the customer limits to the operator too
    public async Task The_operator_keeps_its_limits_wildcards_long_waivers_short_reasons_and_internal_packages()
    {
        var op = ActorScope.OperatorAs("olivia");
        await _commands.AddWaiverAsync(op, "acme", "Moq", "*", 3650, "r", Ct);
        await _commands.AddWaiverAsync(op, "acme", "Contoso.Core", "1.*", 1, "r", Ct);
        Assert.Equal(new[] { "waiver acme Moq", "waiver acme Contoso.Core" }, _store.Calls);   // the operator's store call: no cap, no customer source
        Assert.Empty(_store.Reads);   // and no tenant read for the prefix check
        await Assert.ThrowsAsync<ArgumentException>(() => _commands.AddWaiverAsync(op, "acme", "Moq", "*", 3651, "r", Ct));
        Assert.Empty(_alerts.Alerts);
    }

    // ---------------------------------------------------------------- revoke records who

    [Fact]   // mutation: revoked_by not passed
    public async Task Revoke_records_the_actors_name()
    {
        _store.Tenants[0] = _store.Tenants[0] with { Policy = new TenantPolicy(14, null, new[] { new Waiver("wvr_000000000001", "moq", "4.21.0", DateTimeOffset.MaxValue, GoodReason, "x") }) };
        Assert.True(await _commands.RevokeWaiverAsync(Acme, "wvr_000000000001", Ct));
        Assert.True(await _commands.RevokeWaiverAsync(ActorScope.OperatorAs("olivia"), "wvr_000000000002", Ct));
        Assert.Equal(new[] { "unwaive acme wvr_000000000001 by=Alice (oid)", "unwaive * wvr_000000000002 by=olivia" }, _store.Calls);
    }

    // ---------------------------------------------------------------- the waiver list

    [Fact]
    public async Task The_waiver_list_is_scoped_like_every_other_query()
    {
        var queries = new AdminQueries(_store, _store, _store, _store);
        Assert.Empty(await queries.ListWaiversAsync(Acme, "acme", Ct));
        Assert.Equal(new[] { $"waivers acme {AdminQueries.WaiverListLimit}" }, _store.Reads);
        await Assert.ThrowsAsync<AccessDeniedException>(() => queries.ListWaiversAsync(Acme, "other", Ct));
        Assert.Single(_store.Reads);
    }
}

/// <summary>WaiverRules on their own: pure.</summary>
public class WaiverRulesTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0", "1.0.0")]
    [InlineData("1.0.0.0", "1.0.0")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData(" 13.0.4 ", "13.0.4")]
    [InlineData("1.2.3-Beta.1", "1.2.3-beta.1")]
    [InlineData("1.2.3+build.5", "1.2.3")]
    [InlineData("1.2.3-rc.1+sha.abc", "1.2.3-rc.1")]
    [InlineData("01.2.3", "1.2.3")]
    public void An_exact_version_is_normalised(string input, string expected) => Assert.Equal(expected, WaiverRules.CleanExactVersion(input));

    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    [InlineData("1.*")]
    [InlineData("1.2.3-*")]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.2.x")]
    [InlineData("[1.0,2.0)")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3-a..b")]
    public void Anything_else_is_not_an_exact_version(string? input) => Assert.Null(WaiverRules.CleanExactVersion(input));

    [Fact]
    public void A_version_longer_than_the_maximum_is_refused() =>
        Assert.Null(WaiverRules.CleanExactVersion("1.2.3-" + new string('a', WaiverRules.MaxVersion)));

    [Theory]
    [InlineData("Newtonsoft.Json", "newtonsoft.json")]
    [InlineData(" Moq ", "moq")]
    [InlineData("a", "a")]
    [InlineData("My_Pkg-1.2", "my_pkg-1.2")]
    public void A_package_id_is_trimmed_and_lower_cased(string input, string expected) => Assert.Equal(expected, WaiverRules.CleanPackageId(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".moq")]
    [InlineData("moq*")]
    [InlineData("mo q")]
    [InlineData("moq/x")]
    public void A_bad_package_id_is_refused(string? input) => Assert.Null(WaiverRules.CleanPackageId(input));

    [Fact]
    public void A_package_id_longer_than_the_maximum_is_refused()
    {
        Assert.NotNull(WaiverRules.CleanPackageId(new string('a', WaiverRules.MaxPackageId)));
        Assert.Null(WaiverRules.CleanPackageId(new string('a', WaiverRules.MaxPackageId + 1)));
    }
}
