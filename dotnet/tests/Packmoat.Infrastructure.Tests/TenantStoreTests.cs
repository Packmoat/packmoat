using Npgsql;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Postgres;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary><see cref="TenantStoreContract"/> on SQLite, and the tests only SQLite has.</summary>
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

/// <summary>
/// <see cref="TenantStoreContract"/> on PostgreSQL, each test on a database of its own, and the lock only PostgreSQL
/// takes by name: the tenant row the identity binding reads FOR NO KEY UPDATE.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresTenantStoreTests : TenantStoreContract
{
    protected override IStoreEngine Engine => PostgresStoreEngine.Instance;

    /// <summary>
    /// The identity binding takes the tenant's row FOR NO KEY UPDATE, so it waits for whoever holds that row (the purge,
    /// the directory claim) and writes only once they are done. Here a session holds the row; each binding, own and
    /// shared, must wait for it, and finishes once it is let go. Without the lock each would write at once: the row the binding changes
    /// (an existing identity, a feed already the operator's) needs no lock on the tenant row of its own.
    /// </summary>
    [PostgresFact]   // mutation: FOR NO KEY UPDATE dropped from the identity binding (LockTenantRowAsync)
    public async Task The_identity_binding_waits_for_whoever_holds_the_tenant_row()
    {
        await using var t = await Engine.CreateAsync();
        var db = ((PostgresTestDb)t).Db;
        await AddTenantAsync(t);
        await Engine.Tenants(t).SetUpstreamIdentityAsync("acme", new UpstreamIdentity("az-0", "client-0", "PEM"), default);

        Func<Task>[] bindings =
        [
            () => Engine.Tenants(t).SetUpstreamIdentityAsync("acme", new UpstreamIdentity("az-1", "client-1", "PEM"), default),
            () => Engine.Tenants(t).BindSharedIdentityAsync("acme", "11111111-2222-4333-8444-555555555555", default),
        ];
        foreach (var bind in bindings)
        {
            var before = await Engine.Tenants(t).GetUpstreamBindingAsync("acme", default);
            await using var holder = await db.OpenAsync();
            await using var hold = await holder.BeginTransactionAsync();
            // FOR UPDATE, as the purge will hold it; it conflicts with the binding's FOR NO KEY UPDATE.
            await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM tenants WHERE id='acme' FOR UPDATE", holder, hold)) await lockRow.ExecuteScalarAsync();

            var running = Task.Run(bind);
            await WaitForAsync(holder, hold, running);
            Assert.False(running.IsCompleted, "the binding wrote while another session held the tenant row");
            Assert.Equal(before, await Engine.Tenants(t).GetUpstreamBindingAsync("acme", default));

            await hold.CommitAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotEqual(before, await Engine.Tenants(t).GetUpstreamBindingAsync("acme", default));
        }
    }

    /// <summary>
    /// Waits (up to 30 s) until a session of this database waits for a lock, or <paramref name="running"/> ends (which the caller then
    /// reports as the binding not waiting). The directory unbind's test waits the same way.
    /// </summary>
    internal static async Task WaitForAsync(NpgsqlConnection holder, NpgsqlTransaction hold, Task running)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!running.IsCompleted)
        {
            await using var waiting = new NpgsqlCommand(
                "SELECT COUNT(*) FROM pg_catalog.pg_locks l WHERE NOT l.granted AND l.pid <> pg_catalog.pg_backend_pid() " +
                "AND l.pid IN (SELECT pid FROM pg_catalog.pg_stat_activity WHERE datname = current_database())", holder, hold);
            if ((long)(await waiting.ExecuteScalarAsync())! >= 1) return;
            Assert.True(DateTime.UtcNow < deadline, "the binding neither waited for the tenant row nor finished");
            await Task.Delay(10);
        }
    }
}
