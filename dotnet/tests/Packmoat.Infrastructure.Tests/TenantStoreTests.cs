using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

/// <summary><see cref="TenantStoreContract"/> on SQLite, and the tests only SQLite has (#232 S1).</summary>
[Collection("sqlite-pools")]
public class TenantStoreTests : TenantStoreContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    [Fact]
    public async Task Migrations_can_run_again_without_harm()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync();
        await t.Db.MigrateAsync(TestTracks.Modules);
        Assert.NotNull(await t.Tenants.GetAsync("acme", default));
    }
}
