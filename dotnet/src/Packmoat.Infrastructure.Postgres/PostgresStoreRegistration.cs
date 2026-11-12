using Microsoft.Extensions.DependencyInjection;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The server's PostgreSQL stores: the twin of the host's <c>AddSqliteStores</c>, port for port, from the one list the
/// tests and the hosted adapter also build from (<see cref="PostgresStores"/>). Each registration keeps its kind: the stateless stores as
/// the instances <see cref="PostgresStores"/> made, the rest as factories that read the clock and the tenant lifecycle from the container.
/// Where one adapter serves several ports, each port resolves the one adapter. What the composition root needs beyond the container comes
/// back as ports (<see cref="RegisteredStores"/>), over the same adapters.
/// <para>The cookie keys are always registered: PostgreSQL needs PACKMOAT_COOKIE_KEYS=database (its PACKMOAT_DB is no file to keep a
/// key folder next to), which the start checks before anything is registered.</para>
/// <para>PackmoatApp.Build calls it when PACKMOAT_DB_ENGINE=postgres.</para>
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public static class PostgresStoreRegistration
{
    public static RegisteredStores AddPostgresStores(this IServiceCollection services, PostgresStores stores)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(stores);
        services.AddSingleton(stores.Database);
        // The tenant lifecycle: a module's creation participants, purge contributors and eligibility, for this database, or
        // the core's none. The tenant, sign-up and purge stores all take theirs from it.
        services.AddSingleton(sp => sp.GetService<TenantLifecycleFactory>()?.For(stores.Database) ?? TenantLifecycle.None);
        services.AddSingleton(sp => stores.Tenants(sp.GetRequiredService<IClock>(), sp.GetRequiredService<TenantLifecycle>()));
        services.AddSingleton<ITenantStore>(sp => sp.GetRequiredService<PostgresTenantStore>());
        services.AddSingleton(sp => stores.TokenService(sp.GetRequiredService<PostgresTenantStore>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<IServingGate>()));
        services.AddSingleton(sp => stores.TokenActivity(sp.GetRequiredService<IClock>()));
        // The audit store. The proxy's IAuditLog (a queue over it) is registered after the overrides, by the composition root.
        services.AddSingleton(stores.Audit);
        services.AddSingleton<IAuditBatchLog>(sp => sp.GetRequiredService<PostgresAuditLog>());
        services.AddSingleton<IAuditRetention>(sp => sp.GetRequiredService<PostgresAuditLog>());
        services.AddSingleton<IAuditQuery>(sp => sp.GetRequiredService<PostgresAuditLog>());
        services.AddSingleton<ITokenActivityRetention>(sp => sp.GetRequiredService<PostgresTokenActivity>());
        services.AddSingleton(stores.Decisions);
        services.AddSingleton<IDecisionRecordLog>(sp => sp.GetRequiredService<PostgresDecisionRecordLog>());
        // The support view (the admin site's Decisions panel) reads the record only through this port.
        services.AddSingleton<IDecisionRecordQuery>(stores.DecisionQuery);
        services.AddSingleton<IAdminActionLog>(stores.AdminActions);
        services.AddSingleton<ISignInDirectoryStore>(stores.SignInDirectory);
        services.AddSingleton<ITokenService>(sp => sp.GetRequiredService<PostgresTokenService>());
        services.AddSingleton<IActiveDeveloperQuery>(sp => sp.GetRequiredService<PostgresTokenActivity>());
        services.AddSingleton<ISharedAppCredentialStore>(sp => stores.SharedAppCredentials(sp.GetRequiredService<IClock>()));
        services.AddSingleton<IMemberStore>(stores.Members);
        services.AddSingleton<IAutoJoinSettingStore>(stores.AutoJoin);
        services.AddSingleton<IFeedCheckStore>(stores.FeedChecks);
        services.AddSingleton<ISelfServiceFeedStore>(stores.SelfServiceFeeds);
        // Read only in the server: the daily nudge counts the tenants due for purge. Only the admin CLI purges.
        services.AddSingleton<ITenantPurgeStore>(sp => stores.PurgeStore(sp.GetRequiredService<TenantLifecycle>()));
        // Always the database's cookie keys (see the class remarks).
        services.AddSingleton<ICookieKeyStore>(sp => stores.CookieKeys(sp.GetRequiredService<IClock>()));
        // The token service and the token activity as registered above, never new ones: the authenticator checks what the token port
        // resolves to and records to what the activity ports resolve to.
        return new RegisteredStores(stores.Database, stores.Migrator, stores.MasterKeyCheck, stores.JobLease, stores.RateCounter, stores.FormNonces, stores.FeedSetupScratch,
            sp => sp.GetRequiredService<PostgresTokenService>(), sp => sp.GetRequiredService<PostgresTokenActivity>());
    }
}
