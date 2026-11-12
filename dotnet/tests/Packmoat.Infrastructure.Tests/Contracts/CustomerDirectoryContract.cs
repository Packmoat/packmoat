using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The directory -> tenant binding store as a contract. Migration 7, backup and the <c>tenant directory</c> CLI stay in
/// <see cref="CustomerDirectoryTests"/>.
/// </summary>
public abstract class CustomerDirectoryContract : StoreContract
{
    protected const string Dir = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    protected const string Dir2 = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";

    [EngineFact]
    public async Task One_directory_maps_to_at_most_one_tenant_and_ids_are_stored_lower_case()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        await AddTenantAsync(t, "globex");
        var store = Engine.SignInDirectory(t);
        Assert.True(await store.BindAsync(new DirectoryBinding(Dir.ToUpperInvariant(), "acme", t.Clock.UtcNow, "op"), default));
        Assert.False(await store.BindAsync(new DirectoryBinding(Dir, "globex", t.Clock.UtcNow, "op"), default));
        Assert.False(await store.BindAsync(new DirectoryBinding(Dir, "acme", t.Clock.UtcNow, "op"), default));
        Assert.Equal("acme", (await store.GetAsync(Dir.ToUpperInvariant(), default))!.TenantId);
        Assert.Equal(Dir, await ScalarAsync(t, "SELECT azure_tenant_id FROM sign_in_directories"));
        Assert.Null(await store.GetAsync("not-a-guid", default));

        // unbind only from the tenant it is bound to
        Assert.Null(await store.UnbindAsync("globex", Dir, t.Clock.UtcNow, "op", default));
        Assert.Equal(0, await store.UnbindAsync("acme", Dir, t.Clock.UtcNow, "op", default));
        Assert.Null(await store.GetAsync(Dir, default));

        // the schema itself refuses an upper-case id and an unknown tenant (bound_at is a real time, which PostgreSQL can read,
        // so the refusal is the CHECK's and not a malformed value's)
        await RefusedAsync(() => ScalarAsync(t, $"INSERT INTO sign_in_directories VALUES('{Dir.ToUpperInvariant()}','acme','2026-01-01T00:00:00.0000000+00:00','y')"));
        await RefusedAsync(() => store.BindAsync(new DirectoryBinding(Dir2, "nobody", t.Clock.UtcNow, "op"), default));
    }

    [EngineFact]   // mutation: the list without its tenant_id scope; the list unsorted
    public async Task A_tenants_list_holds_only_its_own_directories_sorted_by_id_with_who_and_when()
    {
        const string Dir3 = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        await AddTenantAsync(t, "globex");
        var store = Engine.SignInDirectory(t);
        var at = t.Clock.UtcNow;
        Assert.True(await store.BindAsync(new DirectoryBinding(Dir2, "acme", at, "op-1"), default));
        Assert.True(await store.BindAsync(new DirectoryBinding(Dir3, "globex", at.AddMinutes(1), "op-2"), default));
        Assert.True(await store.BindAsync(new DirectoryBinding(Dir.ToUpperInvariant(), "acme", at.AddMinutes(2), "op-3"), default));

        Assert.Equal([new DirectoryBinding(Dir, "acme", at.AddMinutes(2), "op-3"), new DirectoryBinding(Dir2, "acme", at, "op-1")],
            await store.ListForTenantAsync("acme", default));
        Assert.Equal([new DirectoryBinding(Dir3, "globex", at.AddMinutes(1), "op-2")], await store.ListForTenantAsync("globex", default));
        Assert.Empty(await store.ListForTenantAsync("ACME", default));   // the tenant id is matched exactly
        Assert.Equal(new DirectoryBinding(Dir3, "globex", at.AddMinutes(1), "op-2"), await store.GetAsync(Dir3.ToUpperInvariant(), default));
    }
}
