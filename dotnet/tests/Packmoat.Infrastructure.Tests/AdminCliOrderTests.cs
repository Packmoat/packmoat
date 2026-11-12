using System.Reflection;
using Microsoft.Data.Sqlite;
using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The command line opens the database in one order: migrate, then the module's lifecycle, then the tenant store
/// (which takes the lifecycle's participants), then the master-key check, then the module's serving gate. A module asked for its
/// lifecycle sees a migrated database with no key marker yet; one asked for its gate sees the marker the key check recorded. The master
/// key is read only after the migration and the backup (which needs none), and the token service asks the module's gate.
/// </summary>
[Collection("sqlite-pools")]
public class AdminCliOrderTests
{
    /// <summary>Notes, when the tool asks it for each part, what the database holds then.</summary>
    private sealed class Probe(string path) : IAdminCliModule
    {
        public readonly List<string> Seen = [];
        public string Group => "probe";
        public string Usage => "";
        public string EnvironmentUsage => "";
        public Task<int> RunAsync(AdminCliContext context) => Task.FromResult(0);
        public MigrationTrack? Migrations => null;

        public TenantLifecycle? Lifecycle(IRelationalDatabase db, CliEnvironment env, Action<string> note)
        {
            Seen.Add($"lifecycle migrated={Migrated()} key={KeyRecorded()}");
            return null;
        }

        public IServingGate? Gate(IRelationalDatabase db, CliEnvironment env, Action<string> warn)
        {
            Seen.Add($"gate migrated={Migrated()} key={KeyRecorded()}");
            return null;
        }

        private bool Migrated() => Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='tenants'") is 1L;
        private bool KeyRecorded() => Migrated() && Scalar("SELECT COUNT(*) FROM meta WHERE key='key_check'") is 1L;

        private object? Scalar(string sql)
        {
            if (!File.Exists(path)) return null;
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar();
        }
    }

    // mutations: the lifecycle asked for after the key check; the gate asked for before it; the database opened unmigrated
    [Fact]
    public async Task Migrate_then_lifecycle_then_tenants_then_key_check_then_gate()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-cli-order-").FullName;
        try
        {
            var path = Path.Combine(dir, "packmoat.db");
            var key = AesGcmSecretProtector.GenerateKeyBase64();
            var probe = new Probe(path);
            var error = new StringWriter();
            var code = await AdminCli.RunAsync(["tenant", "list"], new StringWriter(), error,
                () => new CliEnvironment(path, () => AesGcmSecretProtector.FromBase64(key), new FakeClock()), [probe]);
            Assert.True(code == 0, error.ToString());
            Assert.Equal(["lifecycle migrated=True key=False", "gate migrated=True key=True"], probe.Seen);
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }

    [Fact]   // the key check stops the tool before the gate is asked for: a wrong key reaches no module's gate
    public async Task A_wrong_master_key_stops_the_tool_after_the_lifecycle_and_before_the_gate()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-cli-order-").FullName;
        try
        {
            var path = Path.Combine(dir, "packmoat.db");
            var first = AesGcmSecretProtector.GenerateKeyBase64();
            Assert.Equal(0, await AdminCli.RunAsync(["tenant", "list"], new StringWriter(), new StringWriter(),
                () => new CliEnvironment(path, () => AesGcmSecretProtector.FromBase64(first), new FakeClock()), []));
            var probe = new Probe(path);
            var other = AesGcmSecretProtector.GenerateKeyBase64();
            // The refusal is an error exit or the key check's own exception; either way the tool stops there.
            int code;
            try
            {
                code = await AdminCli.RunAsync(["tenant", "list"], new StringWriter(), new StringWriter(),
                    () => new CliEnvironment(path, () => AesGcmSecretProtector.FromBase64(other), new FakeClock()), [probe]);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("master key", StringComparison.Ordinal)) { code = -1; }
            Assert.NotEqual(0, code);
            Assert.Equal(["lifecycle migrated=True key=True"], probe.Seen);
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }

    /// <summary>A module that gives only a serving gate.</summary>
    private sealed class GateOnly(IServingGate? gate) : IAdminCliModule
    {
        public string Group => "gateonly";
        public string Usage => "";
        public string EnvironmentUsage => "";
        public Task<int> RunAsync(AdminCliContext context) => Task.FromResult(0);
        public MigrationTrack? Migrations => null;
        public TenantLifecycle? Lifecycle(IRelationalDatabase db, CliEnvironment env, Action<string> note) => null;
        public IServingGate? Gate(IRelationalDatabase db, CliEnvironment env, Action<string> warn) => gate;
    }

    /// <summary>A gate of the test's own, told apart from the core's by reference. Never asked here.</summary>
    private sealed class TestGate : IServingGate
    {
        public Task<ServingAnswer> AskAsync(string tenantId, CancellationToken cancellationToken) => throw new InvalidOperationException("not asked here");
    }

    /// <summary>The gate a token service asks (its private field, read as StoreIdentityTests reads the server's).</summary>
    private static IServingGate GateOf(ITokenService tokens) => FieldOf<IServingGate>(Assert.IsType<SqliteTokenService>(tokens));

    private static async Task WithMigrated(Func<SqliteDatabase, CliEnvironment, Task> body)
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-cli-order-").FullName;
        try
        {
            var path = Path.Combine(dir, "packmoat.db");
            var db = new SqliteDatabase(path);
            await db.MigrateAsync([]);
            var key = AesGcmSecretProtector.GenerateKeyBase64();
            await body(db, new CliEnvironment(path, () => AesGcmSecretProtector.FromBase64(key), new FakeClock()));
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }

    [Fact]   // mutation: TokenServiceFor gives ServeAllGate whatever it is handed
    public Task The_token_service_asks_the_module_s_gate_or_the_core_s_when_there_is_none() => WithMigrated((db, env) =>
    {
        var stores = new SqliteStores(db, env.Protector());
        var store = stores.Tenants(env.Clock, TenantLifecycle.None);
        var tenants = new AdminCli.CliTenantPorts(store, g => stores.TokenService(store, env.Clock, g));
        var gate = new TestGate();
        Assert.Same(gate, GateOf(AdminCli.TokenServiceFor(tenants, [gate])));
        Assert.Same(ServeAllGate.Instance, GateOf(AdminCli.TokenServiceFor(tenants, [])));
        return Task.CompletedTask;
    });

    // mutations (review): the module's gate dropped on the way to the token service (gates.Clear(), or ServeAllGate passed straight in)
    [Fact]
    public Task The_command_line_s_stores_give_the_token_service_the_module_s_gate() => WithMigrated(async (db, env) =>
    {
        var gate = new TestGate();
        var opened = await AdminCli.OpenStoresAsync(db, env, [new GateOnly(gate)], new StringWriter());
        Assert.NotNull(opened);
        Assert.Same(gate, GateOf(opened.Tokens));
        var none = await AdminCli.OpenStoresAsync(db, env, [new GateOnly(null)], new StringWriter());
        Assert.NotNull(none);
        Assert.Same(ServeAllGate.Instance, GateOf(none.Tokens));
    });

    [Fact]   // mutation: a second tenant store made for the command line's token service
    public Task The_command_line_s_token_service_checks_the_tenant_store_its_commands_use() => WithMigrated(async (db, env) =>
    {
        var opened = await AdminCli.OpenStoresAsync(db, env, [], new StringWriter());
        Assert.NotNull(opened);
        Assert.Same(opened.Tenants, FieldOf<SqliteTenantStore>(opened.Tokens));
    });

    // mutations: the audit query, or a face of the token activity, given its own adapter
    [Fact]
    public Task The_command_line_s_ports_share_one_audit_log_and_one_token_activity() => WithMigrated(async (db, env) =>
    {
        var opened = await AdminCli.OpenStoresAsync(db, env, [], new StringWriter());
        Assert.NotNull(opened);
        Assert.IsType<SqliteAuditLog>(opened.Stores.AuditQuery);
        Assert.Same(opened.Stores.AuditQuery, opened.Stores.AuditRetention);
        var (query, retention) = opened.Stores.TokenActivity(env.Clock);
        Assert.IsType<SqliteTokenActivity>(query);
        Assert.Same(query, retention);
        Assert.Same(db, opened.Stores.Database);
    });

    /// <summary>The one private field of type <typeparamref name="T"/> that <paramref name="owner"/> holds.</summary>
    private static T FieldOf<T>(object owner) => (T)owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(f => f.FieldType == typeof(T)).GetValue(owner)!;

    [Fact]   // two gates still stop the tool, with the same line
    public Task Two_modules_gates_open_nothing() => WithMigrated(async (db, env) =>
    {
        var error = new StringWriter();
        Assert.Null(await AdminCli.OpenStoresAsync(db, env, [new GateOnly(new TestGate()), new GateOnly(new TestGate())], error));
        Assert.Equal("2 modules give a serving gate. Refusing to run: at most one may decide who is served.", error.ToString().Trim());
    });

    // mutation (review): the stores, and with them the master key, made before the migration or the backup
    [Fact]
    public async Task Backup_needs_no_master_key()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-cli-order-").FullName;
        try
        {
            var file = Path.Combine(dir, "backup.db");
            var error = new StringWriter();
            var unset = new CliEnvironment(Path.Combine(dir, "packmoat.db"),
                () => AesGcmSecretProtector.FromEnvironment("PACKMOAT_TEST_278_MASTER_KEY_NEVER_SET"), new FakeClock(), "op");
            var code = await AdminCli.RunAsync(["backup", file], new StringWriter(), error, () => unset, []);
            Assert.True(code == 0, error.ToString());
            Assert.True(File.Exists(file));
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }
}
