using Npgsql;
using Packmoat.Application;
using Packmoat.Infrastructure.Lifecycle;
using Packmoat.Infrastructure.Postgres;
using Packmoat.Infrastructure.Tests.Postgres;
using static Packmoat.Infrastructure.Tests.Postgres.PostgresLockProbe;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// <see cref="OrganisationStoreContract"/> on PostgreSQL, each test on a database of its own, and what only PostgreSQL
/// has: the creation lock taken before the directory is read.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresOrganisationStoreTests : OrganisationStoreContract
{
    protected override IStoreEngine Engine => PostgresStoreEngine.Instance;

    private static PostgresDatabase Db(IEngineDb t) => ((PostgresTestDb)t).Db;

    /// <summary>
    /// Another session holds <see cref="PostgresLock.TenantCreation"/> (as a creation in flight does) and links the directory to a tenant,
    /// uncommitted. The organisation creation waits for the lock before it reads the directory (pg_locks), so once the holder commits it
    /// sees the link and says DirectoryTaken, having written nothing and run no participant. Reading the directory before the lock, it
    /// would find it free, write the tenant and run the participants after the wait (the tenant insert takes the lock too), and only then
    /// meet the link at the primary key (DirectoryTaken as well, but with the participants run: what this test sees).
    /// </summary>
    [PostgresFact]   // mutation: the organisation creation without its own TenantCreation lock (the tenant insert's is too late)
    public async Task A_creation_waits_for_whoever_holds_the_creation_lock_and_then_sees_their_link()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "first");
        var before = await AllCountsAsync(t);
        await using var holder = await Db(t).OpenAsync();
        await using var holding = await holder.BeginTransactionAsync();
        await PostgresLocks.TakeAsync(holder, holding, PostgresLock.TenantCreation, Ct);
        await using (var link = new NpgsqlCommand(
            $"INSERT INTO sign_in_directories(azure_tenant_id, tenant_id, bound_at, bound_by) VALUES('{Directory1}', 'first', '2026-10-10T12:00:00Z', 'holder')", holder, holding))
            await link.ExecuteNonQueryAsync();

        var ran = false;
        var create = Task.Run(() => Engine.Organisations(t).CreateAsync(Organisation(t, "second"), [new Participant((_, _) => { ran = true; return Task.CompletedTask; })], Ct));

        // Probed from a session of its own, outside any transaction: a transaction reads pg_stat_activity (in Others) once and keeps it.
        await using var probe = await Db(t).OpenAsync();
        await WaitUntilAsync(probe, null, "SELECT COUNT(DISTINCT l.pid) FROM pg_catalog.pg_locks l WHERE NOT l.granted AND l.locktype = 'advisory' AND l.objsubid = 2 " +
            $"AND l.classid::bigint = {PostgresLocks.NamedLockKey} AND l.objid = {(int)PostgresLock.TenantCreation} AND " + Others, 1, create);
        Assert.False(create.IsCompleted, "the creation finished while another held the creation lock");

        await holding.CommitAsync();
        Assert.Equal(OrganisationCreateResult.DirectoryTaken, await create.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(before.Replace("sign_in_directories=0", "sign_in_directories=1"), await AllCountsAsync(t));
        Assert.Equal(new[] { "first" }, await ColumnAsync(t, "SELECT id FROM tenants"));
        Assert.False(ran, "a participant ran: the directory was read before the creation lock");
    }

    /// <summary>A session's link of <paramref name="directory"/> to <paramref name="tenant"/>, as the operator's bind makes it: no creation lock.</summary>
    private static async Task LinkAsync(NpgsqlConnection c, NpgsqlTransaction tx, string directory, string tenant)
    {
        await using var link = new NpgsqlCommand(
            $"INSERT INTO sign_in_directories(azure_tenant_id, tenant_id, bound_at, bound_by) VALUES('{directory}', '{tenant}', '2026-10-10T12:00:00Z', 'operator')", c, tx);
        await link.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The race: the operator's bind takes no creation lock, so its link of the directory, not yet
    /// committed, is invisible to the creation's check, which passes; the creation writes its tenant and runs its participants, then waits
    /// at its own link's insert on the bind's uncommitted primary key (pg_locks). Once the bind commits, the creation meets the primary key
    /// (23505 on sign_in_directories_pkey) and is answered DirectoryTaken, as its check would have said, with nothing of it written. Once
    /// the bind rolls back, the creation goes on and is Created.
    /// </summary>
    // mutation: the 23505 not mapped (the creation then throws a PostgresException, a 500 to the browser)
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_creation_that_loses_the_directory_to_an_operator_bind_at_its_link_is_DirectoryTaken(bool bindCommits)
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "first");
        var before = await AllCountsAsync(t);
        await using var bind = await Db(t).OpenAsync();
        await using var binding = await bind.BeginTransactionAsync();
        await LinkAsync(bind, binding, Directory1, "first");

        var ran = false;
        var create = Task.Run(() => Engine.Organisations(t).CreateAsync(Organisation(t, "second"), [new Participant((_, _) => { ran = true; return Task.CompletedTask; })], Ct));
        try
        {
            await using (var probe = await Db(t).OpenAsync())
                await WaitUntilAsync(probe, null, "SELECT COUNT(DISTINCT l.pid) FROM pg_catalog.pg_locks l WHERE NOT l.granted AND l.locktype = 'transactionid' AND " + Others, 1, create);
            Assert.False(create.IsCompleted, "the creation finished while the bind's link was uncommitted");
            Assert.True(ran, "the creation waited before its participants: not at its link");

            if (bindCommits)
            {
                await binding.CommitAsync();
                Assert.Equal(OrganisationCreateResult.DirectoryTaken, await create.WaitAsync(TimeSpan.FromSeconds(30)));
                Assert.Equal(before.Replace("sign_in_directories=0", "sign_in_directories=1"), await AllCountsAsync(t));
                Assert.Equal(new[] { "first" }, await ColumnAsync(t, "SELECT id FROM tenants"));
                Assert.Equal(new[] { "first" }, await ColumnAsync(t, $"SELECT tenant_id FROM sign_in_directories WHERE azure_tenant_id='{Directory1}'"));
            }
            else
            {
                await binding.RollbackAsync();
                Assert.Equal(OrganisationCreateResult.Created, await create.WaitAsync(TimeSpan.FromSeconds(30)));
                Assert.Equal(new[] { "second" }, await ColumnAsync(t, $"SELECT tenant_id FROM sign_in_directories WHERE azure_tenant_id='{Directory1}'"));
            }
        }
        finally
        {
            await binding.DisposeAsync();   // rolls back if the test failed while it held the link, so the creation can end
            await Task.WhenAny(create, Task.Delay(TimeSpan.FromSeconds(30)));
        }
    }

    /// <summary>
    /// Only the directory's own index is DirectoryTaken: a unique violation on any other constraint in the creation (here a
    /// participant's duplicate tenant row, tenants_pkey) is thrown as it is, and nothing is written.
    /// </summary>
    // mutation: 23505 mapped for any constraint
    [PostgresFact]
    public async Task A_unique_violation_on_another_constraint_is_thrown_not_DirectoryTaken()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "first");
        var before = await AllCountsAsync(t);
        var duplicate = new Participant((tx, _) => OnTransactionAsync(tx,
            "INSERT INTO tenants(id, name, upstream_base, allowed_path_prefix, feed_index_url, cooldown_days, created_at) " +
            "VALUES('first', 'First', 'https://x/', '', 'https://x/i.json', 14, now())"));

        var refused = Assert.IsType<PostgresException>(await RefusedAsync(() => Engine.Organisations(t).CreateAsync(Organisation(t, "second"), [duplicate], Ct)));
        Assert.Equal((PostgresErrorCodes.UniqueViolation, "tenants_pkey"), (refused.SqlState, refused.ConstraintName));
        Assert.Equal(before, await AllCountsAsync(t));
    }
}

/// <summary><see cref="OrganisationStoreContract"/> on SQLite. The organisation creation's hosted tests (the hosted module's parts) stay in the hosted test project.</summary>
[Collection("sqlite-pools")]
public class OrganisationStoreTests : OrganisationStoreContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;
}
