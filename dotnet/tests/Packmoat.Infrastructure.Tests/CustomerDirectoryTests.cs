using Microsoft.Data.Sqlite;
using Npgsql;
using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// <see cref="CustomerDirectoryContract"/> on PostgreSQL, each test on a database of its own, and the lock only PostgreSQL
/// takes: the tenant row the unbind reads FOR NO KEY UPDATE.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresCustomerDirectoryTests : CustomerDirectoryContract
{
    protected override IStoreEngine Engine => PostgresStoreEngine.Instance;

    /// <summary>
    /// The unbind takes the tenant's row FOR NO KEY UPDATE first, as the member token cap does, so it waits for
    /// whoever holds that row (a member issue, the purge) and only then removes the link and revokes the directory's tokens. Here a
    /// session holds the row; the unbind must wait for it, and finishes once it is let go. Without the lock it would write at once: the
    /// link and the tokens it changes need no lock on the tenant row of their own.
    /// </summary>
    [PostgresFact]   // mutations: FOR NO KEY UPDATE dropped from the unbind; weakened to FOR KEY SHARE
    public async Task The_unbind_waits_for_whoever_holds_the_tenant_row()
    {
        await using var t = await Engine.CreateAsync();
        var db = ((PostgresTestDb)t).Db;
        await AddTenantAsync(t, "acme");
        var store = Engine.SignInDirectory(t);
        Assert.True(await store.BindAsync(new DirectoryBinding(Dir, "acme", t.Clock.UtcNow, "op"), default));
        var token = (await Engine.Tokens(t).IssueForMemberAsync("acme", Dir, "cccccccc-cccc-4ccc-8ccc-cccccccccccc", "laptop", t.Clock.UtcNow.AddDays(9),
            TokenKind.Developer, 5, default))!;

        await using var holder = await db.OpenAsync();
        await using var hold = await holder.BeginTransactionAsync();
        // FOR NO KEY UPDATE, as a member issue holds it: the weakest lock the unbind must still wait for. A FOR UPDATE
        // holder would also block an unbind weakened to FOR KEY SHARE, which a member issue does not, and the race would be back.
        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM tenants WHERE id='acme' FOR NO KEY UPDATE", holder, hold)) await lockRow.ExecuteScalarAsync();

        var running = Task.Run(() => store.UnbindAsync("acme", Dir, t.Clock.UtcNow, "op", default));
        await PostgresTenantStoreTests.WaitForAsync(holder, hold, running);
        Assert.False(running.IsCompleted, "the unbind wrote while another session held the tenant row");
        Assert.NotNull(await store.GetAsync(Dir, default));
        Assert.NotNull(await Engine.Tokens(t).AuthenticateAsync(token.Plaintext, default));

        await hold.CommitAsync();
        Assert.Equal(1, await running.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Null(await store.GetAsync(Dir, default));
        Assert.Null(await Engine.Tokens(t).AuthenticateAsync(token.Plaintext, default));
    }
}

/// <summary><see cref="CustomerDirectoryContract"/> on SQLite, and the tests only SQLite has.</summary>
[Collection("sqlite-pools")]
public class CustomerDirectoryTests : CustomerDirectoryContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    [Fact]
    public async Task Migration_7_adds_the_binding_table_and_marks_existing_actions_as_admin_site()
    {
        await using var t = new TestDb();
        await t.Db.MigrateAsync(6);
        await ScalarAsync(t, "INSERT INTO admin_actions(time, actor_tid, actor_oid, actor_name, action, tenant_id, detail) VALUES('2026-10-10T00:00:00Z','tid','oid','Founder','tenant.cooldown','acme','14')");
        await t.Db.MigrateAsync(TestTracks.Modules);
        Assert.True(SqliteDatabase.LatestVersion >= 7);
        Assert.Equal("admin", await ScalarAsync(t, "SELECT site FROM admin_actions"));
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='sign_in_directories'"));
        // the append-only triggers still hold after the column was added
        await Assert.ThrowsAsync<SqliteException>(() => ScalarAsync(t, "UPDATE admin_actions SET site='cli'"));
    }

    [Fact]
    public async Task A_binding_survives_backup_and_restore()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync("acme");
        await new SqliteSignInDirectoryStore(t.Db).BindAsync(new DirectoryBinding(Dir, "acme", t.Clock.UtcNow, "op"), default);
        var backup = Path.Combine(t.Dir, "copy.db");
        var v = await SqliteBackup.BackupAsync(t.Db, backup, false, default, t.Protector);
        Assert.True(v.Ok);
        Assert.Equal(SqliteDatabase.LatestVersion, v.SchemaVersion);
        var target = Path.Combine(t.Dir, "restored.db");
        await SqliteBackup.RestoreAsync(backup, target, false, t.Clock.UtcNow);
        var restored = new SqliteDatabase(target);
        await restored.MigrateAsync(TestTracks.Modules);
        Assert.Equal("acme", (await new SqliteSignInDirectoryStore(restored).GetAsync(Dir, default))!.TenantId);
    }

    private sealed class Cli : IAsyncDisposable
    {
        private readonly TempDir _temp = TempDir.Create("packmoat-cli-dir-", clearSqlitePools: true);
        private readonly CliEnvironment _env;
        public FakeClock Clock { get; } = new();
        public string DbPath => Path.Combine(_temp.Path, "packmoat.db");

        public Cli()
        {
            var key = AesGcmSecretProtector.GenerateKeyBase64();
            _env = new CliEnvironment(DbPath, () => AesGcmSecretProtector.FromBase64(key), Clock, "founder");
        }

        public async Task<(int Code, string Out, string Err)> Run(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var code = await AdminCli.RunAsync(args, o, e, _env, TestCli.Modules);
            return (code, o.ToString(), e.ToString());
        }

        public async Task<IReadOnlyList<AdminActionRecord>> Actions()
        {
            var db = new SqliteDatabase(DbPath);
            return await new SqliteAdminActionLog(db).LatestAsync(100, default);
        }

        public async Task Exec(string sql)
        {
            await using var c = await new SqliteDatabase(DbPath).OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync() => _temp.DisposeAsync();
    }

    private static async Task<Cli> WithTenantsAsync()
    {
        var cli = new Cli();
        Assert.Equal(0, (await cli.Run("tenant", "create", "acme", "Acme", "--upstream-base", "https://pkgs.dev.azure.com/", "--path-prefix", "acme-org/", "--feed", "https://pkgs.dev.azure.com/acme-org/p/_packaging/f/nuget/v3/index.json")).Code);
        Assert.Equal(0, (await cli.Run("tenant", "create", "globex", "Globex", "--upstream-base", "https://pkgs.dev.azure.com/", "--path-prefix", "globex-org/", "--feed", "https://pkgs.dev.azure.com/globex-org/p/_packaging/f/nuget/v3/index.json")).Code);
        return cli;
    }

    [Fact]
    public async Task The_operator_binds_shows_and_unbinds_a_directory_and_each_change_is_recorded()
    {
        await using var cli = await WithTenantsAsync();
        var bind = await cli.Run("tenant", "directory", "bind", "acme", "--azure-tenant", Dir.ToUpperInvariant());
        Assert.Equal(0, bind.Code);
        Assert.Contains(Dir, bind.Out);

        var show = await cli.Run("tenant", "directory", "show", "acme");
        Assert.Equal(0, show.Code);
        Assert.Contains("1 linked directory", show.Out);
        Assert.Contains(Dir + "\tbound", show.Out);
        Assert.Contains("by founder", show.Out);

        Assert.Equal(0, (await cli.Run("tenant", "directory", "unbind", "acme", "--azure-tenant", Dir)).Code);
        Assert.Contains("0 linked directories", (await cli.Run("tenant", "directory", "show", "acme")).Out);

        var actions = await cli.Actions();
        Assert.Equal(new[] { "directory.unbind.tokens-revoked", "directory.unbind", "directory.bind" }, actions.Select(a => a.Action));
        Assert.All(actions, a => { Assert.Equal("cli", a.Site); Assert.Equal("founder", a.ActorName); Assert.Equal("acme", a.TenantId); });
        Assert.Equal($"directory {Dir}; revoked=0", actions[0].Detail);
        Assert.All(actions.Skip(1), a => Assert.Equal("directory " + Dir, a.Detail));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("9188040d-6c67-4c5b-b112-36a304b66dad")]
    public async Task A_bad_or_consumer_directory_is_refused(string directory)
    {
        await using var cli = await WithTenantsAsync();
        var r = await cli.Run("tenant", "directory", "bind", "acme", "--azure-tenant", directory);
        Assert.Equal(1, r.Code);
        Assert.Contains("0 linked directories", (await cli.Run("tenant", "directory", "show", "acme")).Out);
    }

    [Fact]
    public async Task A_directory_bound_to_one_tenant_cannot_be_bound_to_another_and_the_attempt_is_recorded_as_failed()
    {
        await using var cli = await WithTenantsAsync();
        Assert.Equal(0, (await cli.Run("tenant", "directory", "bind", "acme", "--azure-tenant", Dir)).Code);
        var again = await cli.Run("tenant", "directory", "bind", "globex", "--azure-tenant", Dir);
        Assert.Equal(1, again.Code);
        Assert.Contains("already linked to another tenant", again.Err);
        Assert.DoesNotContain("acme", again.Err);   // never says which tenant has it
        Assert.Equal(1, (await cli.Run("tenant", "directory", "unbind", "globex", "--azure-tenant", Dir)).Code);   // not globex's to remove
        Assert.Equal(1, (await cli.Run("tenant", "directory", "bind", "nobody", "--azure-tenant", Dir2)).Code);
        var actions = (await cli.Actions()).Select(a => a.Action + " " + a.TenantId).ToList();
        Assert.Contains("directory.bind.failed globex", actions);
        Assert.Contains("directory.unbind.failed globex", actions);
        Assert.Contains("directory.bind.failed nobody", actions);
        Assert.Contains("1 linked directory", (await cli.Run("tenant", "directory", "show", "acme")).Out);
    }

    [Fact]
    public async Task Show_suggests_the_verified_shared_directory_but_never_binds_it()
    {
        await using var cli = await WithTenantsAsync();
        Assert.Equal(0, (await cli.Run("identity", "bind", "acme", "--azure-tenant", Dir2)).Code);
        Assert.DoesNotContain("suggestion", (await cli.Run("tenant", "directory", "show", "acme")).Out);   // no passing check yet
        await cli.Exec($"INSERT INTO shared_app_verifications(tenant_id, azure_tenant_id, feed_index_url, client_id, verified_at, verified_by) VALUES('acme','{Dir2.ToUpperInvariant()}','https://x/','client','2026-10-10T00:00:00Z','founder')");

        var show = (await cli.Run("tenant", "directory", "show", "acme")).Out;
        Assert.Contains("suggestion", show);
        Assert.Contains($"tenant directory bind acme --azure-tenant {Dir2}", show);
        Assert.Contains("0 linked directories", show);
        Assert.Empty(await cli.Actions());
    }
}
