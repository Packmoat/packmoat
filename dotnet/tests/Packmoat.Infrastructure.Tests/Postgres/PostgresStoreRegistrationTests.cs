using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host;
using Packmoat.Host.Web;
using Packmoat.Infrastructure.Lifecycle;
using Packmoat.Infrastructure.Postgres;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// The server's PostgreSQL stores (<see cref="PostgresStoreRegistration.AddPostgresStores"/>), called directly: the server calls it
/// when PACKMOAT_DB_ENGINE=postgres. It registers what SQLite's registration does, port for port and kind for
/// kind, and shares one adapter where SQLite's does (the twin of the host's StoreIdentityTests). Nothing here connects: building a store
/// opens no connection.
/// </summary>
public class PostgresStoreRegistrationTests
{
    /// <summary>A database over a server that is never reached: the registration and the stores' constructors connect nothing.</summary>
    private static PostgresStores Stores() => new(
        PostgresDatabase.Create(PostgresConnectionString.Parse("Host=db.invalid;Database=packmoat;Username=packmoat_app")),
        AesGcmSecretProtector.FromBase64(AesGcmSecretProtector.GenerateKeyBase64()));

    private static (ServiceProvider Services, RegisteredStores Registered, PostgresStores Stores) Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FakeClock());
        services.AddSingleton<IServingGate>(ServeAllGate.Instance);
        var stores = Stores();
        var registered = services.AddPostgresStores(stores);
        return (services.BuildServiceProvider(), registered, stores);
    }

    private static T Field<T>(object owner) => (T)owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(f => f.FieldType == typeof(T)).GetValue(owner)!;

    /// <summary>A registration as "service type: kind", with the engine's name taken out of the adapter types, so the two engines compare.</summary>
    private static string[] Shape(IServiceCollection services, string engine) => services
        .Select(d => $"{d.ServiceType.Name.Replace(engine, "Engine", StringComparison.Ordinal)}: {d.Lifetime} " +
            (d.ImplementationInstance is not null ? "instance" : d.ImplementationFactory is not null ? "factory" : "type"))
        .ToArray();

    // mutations: a port left out of AddPostgresStores; a stateless store registered as a factory (or a factory as one instance); the
    // cookie keys left out
    [Fact]
    public void It_registers_every_port_SQLite_s_registration_does_in_the_same_kind_and_order()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-registration-").FullName;
        try
        {
            var sqlite = new ServiceCollection();
            sqlite.AddSqliteStores(new SqliteStores(new SqliteDatabase(Path.Combine(dir, "packmoat.db")),
                AesGcmSecretProtector.FromBase64(AesGcmSecretProtector.GenerateKeyBase64())), CookieKeyStorage.Database);
            var postgres = new ServiceCollection();
            postgres.AddPostgresStores(Stores());
            Assert.Equal(Shape(sqlite, "Sqlite"), Shape(postgres, "Postgres"));
            Assert.Contains(postgres, d => d.ServiceType == typeof(ICookieKeyStore));
            Assert.True(postgres.Count >= 25, "too few registrations to mean anything");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]   // mutation: a second tenant store instance (for the port, or for the token service)
    public async Task The_tenant_port_and_the_token_service_share_one_tenant_store()
    {
        await using var sp = Build().Services;
        var tenants = sp.GetRequiredService<PostgresTenantStore>();
        Assert.Same(tenants, sp.GetRequiredService<ITenantStore>());
        Assert.Same(tenants, Field<PostgresTenantStore>(sp.GetRequiredService<PostgresTokenService>()));
    }

    [Fact]   // mutation: an audit port given its own PostgresAuditLog
    public async Task The_audit_ports_are_one_audit_log()
    {
        await using var sp = Build().Services;
        var audit = sp.GetRequiredService<PostgresAuditLog>();
        Assert.Same(audit, sp.GetRequiredService<IAuditBatchLog>());
        Assert.Same(audit, sp.GetRequiredService<IAuditRetention>());
        Assert.Same(audit, sp.GetRequiredService<IAuditQuery>());
    }

    [Fact]   // mutations: the token port, or an activity port, given its own adapter
    public async Task The_token_and_activity_ports_are_one_adapter_each()
    {
        await using var sp = Build().Services;
        Assert.Same(sp.GetRequiredService<PostgresTokenService>(), sp.GetRequiredService<ITokenService>());
        var activity = sp.GetRequiredService<PostgresTokenActivity>();
        Assert.Same(activity, sp.GetRequiredService<ITokenActivityRetention>());
        Assert.Same(activity, sp.GetRequiredService<IActiveDeveloperQuery>());
        Assert.Same(sp.GetRequiredService<PostgresDecisionRecordLog>(), sp.GetRequiredService<IDecisionRecordLog>());
    }

    // mutations: the returned factories made new adapters rather than resolving the registered ones; the returned database, migrator or
    // job lease another database's
    [Fact]
    public async Task The_returned_ports_are_the_registered_adapters_over_the_one_database()
    {
        var (sp, registered, stores) = Build();
        await using (sp)
        {
            Assert.Same(sp.GetRequiredService<PostgresTokenService>(), registered.TokenAuthenticator(sp));
            Assert.Same(sp.GetRequiredService<PostgresTokenActivity>(), registered.TokenActivityLog(sp));
            Assert.Same(stores.Database, registered.Database);
            Assert.Same(stores.Database, sp.GetRequiredService<PostgresDatabase>());
            // On PostgreSQL the server never migrates: its schema port is the read-only one, over the same database.
            Assert.Same(stores.Database, Field<PostgresDatabase>(Assert.IsType<PostgresServerSchema>(registered.Migrator)));
            Assert.Equal(DatabaseEngine.Postgres, registered.Database.Engine);
            Assert.IsType<PostgresMasterKeyCheck>(registered.MasterKeyCheck);
            Assert.IsType<PostgresJobLease>(registered.JobLease(new FakeClock()));
        }
    }

    [Fact]   // mutation: the lifecycle's factory not asked, so a module's purge rules are never the server's
    public async Task The_tenant_lifecycle_is_the_module_s_for_this_database()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FakeClock());
        services.AddSingleton<IServingGate>(ServeAllGate.Instance);
        var asked = new List<IRelationalDatabase>();
        var lifecycle = new TenantLifecycle([], [], null);
        services.AddSingleton(new TenantLifecycleFactory(db => { asked.Add(db); return lifecycle; }));
        var stores = Stores();
        services.AddPostgresStores(stores);
        await using var sp = services.BuildServiceProvider();
        Assert.Same(lifecycle, sp.GetRequiredService<TenantLifecycle>());
        Assert.Same(stores.Database, Assert.Single(asked));
        Assert.IsType<PostgresTenantPurgeStore>(sp.GetRequiredService<ITenantPurgeStore>());
    }

    [Fact]
    public async Task With_no_module_the_lifecycle_is_the_core_s_none()
    {
        await using var sp = Build().Services;
        Assert.Same(TenantLifecycle.None, sp.GetRequiredService<TenantLifecycle>());
    }
}
