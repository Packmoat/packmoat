using Packmoat.Application;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The auto-join setting (#80) as a contract (#232 S1): no row means on, one row per tenant, no cache. Migration 11 and a failed read stay
/// in <see cref="AutoJoinStoreTests"/>.
/// </summary>
public abstract class AutoJoinStoreContract : StoreContract
{
    [Fact]
    public async Task No_row_reads_as_on()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        Assert.True(await Engine.AutoJoin(t).IsOnAsync("acme", default));
    }

    [Fact]
    public async Task Off_and_on_round_trip_with_who_and_when()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        var store = Engine.AutoJoin(t);
        await store.SetAsync("acme", false, t.Clock.UtcNow, "Grace (ffffffff-ffff-4fff-8fff-ffffffffffff)", default);
        Assert.False(await store.IsOnAsync("acme", default));
        Assert.False(await Engine.AutoJoin(t).IsOnAsync("acme", default));   // a second instance reads the same row
        Assert.Equal(0L, await ScalarAsync(t, "SELECT enabled FROM auto_join_settings WHERE tenant_id='acme'"));
        Assert.Equal("Grace (ffffffff-ffff-4fff-8fff-ffffffffffff)", await ScalarAsync(t, "SELECT changed_by FROM auto_join_settings WHERE tenant_id='acme'"));
        Assert.Equal(t.Clock.UtcNow.ToString("O"), await ScalarAsync(t, "SELECT changed_at FROM auto_join_settings WHERE tenant_id='acme'"));

        t.Clock.Advance(TimeSpan.FromMinutes(5));
        await store.SetAsync("acme", true, t.Clock.UtcNow, "Ada", default);
        Assert.True(await store.IsOnAsync("acme", default));
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM auto_join_settings"));
        Assert.Equal("Ada", await ScalarAsync(t, "SELECT changed_by FROM auto_join_settings WHERE tenant_id='acme'"));
    }

    [Fact]
    public async Task There_is_no_cache_a_change_made_elsewhere_is_read_at_once()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        var store = Engine.AutoJoin(t);
        Assert.True(await store.IsOnAsync("acme", default));
        await ScalarAsync(t, "INSERT INTO auto_join_settings(tenant_id, enabled, changed_at, changed_by) VALUES('acme', 0, '2026-01-01T00:00:00.0000000+00:00', 'by hand')");
        Assert.False(await store.IsOnAsync("acme", default));
    }

    [Fact]
    public async Task The_schema_refuses_anything_but_zero_or_one_and_an_unknown_tenant()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        // 'on' is SQLite's alone (AutoJoinStoreTests): PostgreSQL cannot read it as a number at all, which is not the CHECK's refusal.
        foreach (var bad in new[] { "2", "-1", "NULL" })
            await RefusedAsync(() => ScalarAsync(t, $"INSERT INTO auto_join_settings(tenant_id, enabled, changed_at, changed_by) VALUES('acme', {bad}, '2026-01-01T00:00:00.0000000+00:00', 'y')"));
        await RefusedAsync(() => Engine.AutoJoin(t).SetAsync("nobody", false, t.Clock.UtcNow, "Ada", default));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM auto_join_settings"));
    }

    [Fact]
    public async Task Each_tenant_has_its_own_setting()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        await AddTenantAsync(t, "globex");
        var store = Engine.AutoJoin(t);
        await store.SetAsync("acme", false, t.Clock.UtcNow, "Grace", default);
        Assert.False(await store.IsOnAsync("acme", default));
        Assert.True(await store.IsOnAsync("globex", default));
        Assert.True(await store.IsOnAsync("ACME", default));   // the tenant id is matched exactly
    }
}
