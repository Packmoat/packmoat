using Packmoat.Admin;
using Packmoat.Application;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The directory -> tenant binding store as a contract (#232 S1). Migration 7, backup and the <c>tenant directory</c> CLI stay in
/// <see cref="CustomerDirectoryTests"/>.
/// </summary>
public abstract class CustomerDirectoryContract : StoreContract
{
    protected const string Dir = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    protected const string Dir2 = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";

    [Fact]
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

        // the schema itself refuses an upper-case id and an unknown tenant
        await RefusedAsync(() => ScalarAsync(t, $"INSERT INTO sign_in_directories VALUES('{Dir.ToUpperInvariant()}','acme','x','y')"));
        await RefusedAsync(() => store.BindAsync(new DirectoryBinding(Dir2, "nobody", t.Clock.UtcNow, "op"), default));
    }
}
