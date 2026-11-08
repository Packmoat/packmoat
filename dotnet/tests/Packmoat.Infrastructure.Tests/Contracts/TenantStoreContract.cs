using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

/// <summary>The tenant store's contract (#232 S1): policy, waivers and their validation through the store.</summary>
public abstract class TenantStoreContract : StoreContract
{
    [Fact]
    public async Task Policy_round_trips_through_the_database()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        await Engine.Tenants(t).AddInternalPrefixAsync("acme", "Contoso.", default);
        await Engine.Tenants(t).SetOverrideAsync("acme", "Moq", 3, default);
        var waiver = await Engine.Tenants(t).AddWaiverAsync("acme", "Newtonsoft.Json", "13.0.4", t.Clock.UtcNow.AddDays(2), "hotfix", "olivia", default);

        var tenant = (await Engine.Tenants(t).GetAsync("acme", default))!;

        Assert.Equal(14, tenant.Policy.CooldownDays);
        Assert.Equal(new[] { "Contoso." }, tenant.Policy.InternalPrefixes);
        Assert.Equal(new PackageOverride("moq", 3), Assert.Single(tenant.Policy.PackageOverrides));
        var w = Assert.Single(tenant.Policy.Waivers);
        Assert.Equal(waiver.Id, w.Id);
        Assert.Equal("newtonsoft.json", w.PackageId);
        Assert.Equal("hotfix", w.Reason);
        Assert.Equal("https://pkgs.dev.azure.com/", tenant.UpstreamBase);
    }

    [Fact]
    public async Task Every_policy_change_bumps_the_revision()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        var seen = new List<long> { (await Engine.Tenants(t).GetAsync("acme", default))!.Policy.Revision };

        async Task Record() => seen.Add((await Engine.Tenants(t).GetAsync("acme", default))!.Policy.Revision);
        await Engine.Tenants(t).SetCooldownAsync("acme", 7, default); await Record();
        await Engine.Tenants(t).AddInternalPrefixAsync("acme", "X.", default); await Record();
        await Engine.Tenants(t).SetOverrideAsync("acme", "a", 1, default); await Record();
        var w = await Engine.Tenants(t).AddWaiverAsync("acme", "a", "*", t.Clock.UtcNow.AddDays(1), "r", "d", default); await Record();
        await Engine.Tenants(t).RevokeWaiverAsync(null, w.Id, "olivia", default); await Record();
        await Engine.Tenants(t).RemoveOverrideAsync("acme", "a", default); await Record();

        Assert.Equal(seen.Order().ToArray(), seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task A_revoked_waiver_no_longer_appears_in_the_policy()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        var w = await Engine.Tenants(t).AddWaiverAsync("acme", "a", "1.0.0", t.Clock.UtcNow.AddDays(1), "r", "d", default);
        Assert.True(await Engine.Tenants(t).RevokeWaiverAsync(null, w.Id, "olivia", default));
        Assert.False(await Engine.Tenants(t).RevokeWaiverAsync(null, w.Id, "olivia", default));
        Assert.Empty((await Engine.Tenants(t).GetAsync("acme", default))!.Policy.Waivers);
    }

    [Fact]
    public async Task A_tenant_scoped_waiver_revoke_cannot_touch_another_tenants_waiver()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        await AddTenantAsync(t, "other");
        var theirs = await Engine.Tenants(t).AddWaiverAsync("other", "a", "1.0.0", t.Clock.UtcNow.AddDays(1), "r", "d", default);
        var revisionBefore = (await Engine.Tenants(t).GetAsync("other", default))!.Policy.Revision;

        Assert.False(await Engine.Tenants(t).RevokeWaiverAsync("acme", theirs.Id, "mallory", default));   // a foreign tenant id revokes nothing
        var after = (await Engine.Tenants(t).GetAsync("other", default))!;
        Assert.Single(after.Policy.Waivers);
        Assert.Equal(revisionBefore, after.Policy.Revision);

        Assert.True(await Engine.Tenants(t).RevokeWaiverAsync("other", theirs.Id, "olivia", default));
        Assert.Empty((await Engine.Tenants(t).GetAsync("other", default))!.Policy.Waivers);
    }

    [Fact]
    public async Task Bad_input_is_refused()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Engine.Tenants(t).SetCooldownAsync("acme", -1, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Engine.Tenants(t).AddInternalPrefixAsync("acme", "  *", default)); // would hide everything
        await Assert.ThrowsAsync<ArgumentException>(() => Engine.Tenants(t).AddWaiverAsync("acme", "a", "*", t.Clock.UtcNow.AddSeconds(-1), "r", "d", default));
        await Assert.ThrowsAsync<ArgumentException>(() => Engine.Tenants(t).AddWaiverAsync("acme", "a", "*", t.Clock.UtcNow.AddDays(1), " ", "d", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Engine.Tenants(t).SetCooldownAsync("nobody", 3, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Engine.Tenants(t).AddInternalPrefixAsync("nobody", "X.", default));
    }

    [Fact]
    public async Task The_engine_sees_database_policy_the_same_way_as_in_memory_policy()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        await Engine.Tenants(t).AddInternalPrefixAsync("acme", "Contoso.", default);
        var tenant = (await Engine.Tenants(t).GetAsync("acme", default))!;
        var engine = Packmoat.Policy.PolicyEngine.CreateDefault(t.Clock);

        var verdict = engine.Decide(new PackageVersionFacts(Ecosystem.NuGet, "contoso.core", "1.0.0", t.Clock.UtcNow.AddDays(-900)), tenant.Policy);

        Assert.Equal(VerdictReason.InternalNamespace, verdict.Reason);
    }
}
