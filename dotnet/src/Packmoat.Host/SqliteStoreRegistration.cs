using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Web;
using Packmoat.Infrastructure;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Host;

/// <summary>
/// The server's SQLite stores (#278 step 0, A3), registered from the one list the command line and the hosted modules also build from
/// (<see cref="SqliteStores"/>). Each registration keeps its kind: the stateless stores as the instances <see cref="SqliteStores"/> made,
/// the rest as factories that read the clock and the tenant lifecycle from the container (a test's or a module's, registered later).
/// Where one adapter serves several ports, each port resolves the one adapter.
/// </summary>
public static class SqliteStoreRegistration
{
    public static void AddSqliteStores(this IServiceCollection services, SqliteStores stores, CookieKeyStorage cookieKeys)
    {
        ArgumentNullException.ThrowIfNull(stores);
        services.AddSingleton(stores.Database);
        // The tenant lifecycle (#194): a module's creation participants, purge contributors and eligibility, for this database, or
        // the core's none. The tenant, sign-up and purge stores all take theirs from it.
        services.AddSingleton(sp => sp.GetService<TenantLifecycleFactory>()?.For(stores.Database) ?? TenantLifecycle.None);
        services.AddSingleton(sp => stores.Tenants(sp.GetRequiredService<IClock>(), sp.GetRequiredService<TenantLifecycle>()));
        services.AddSingleton<ITenantStore>(sp => sp.GetRequiredService<SqliteTenantStore>());
        services.AddSingleton(sp => stores.TokenService(sp.GetRequiredService<SqliteTenantStore>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<IServingGate>()));
        services.AddSingleton(sp => stores.TokenActivity(sp.GetRequiredService<IClock>()));
        // The audit store. The proxy's IAuditLog (a queue over it) is registered after the overrides, in PackmoatApp.
        services.AddSingleton(stores.Audit);
        services.AddSingleton<IAuditBatchLog>(sp => sp.GetRequiredService<SqliteAuditLog>());
        services.AddSingleton<IAuditRetention>(sp => sp.GetRequiredService<SqliteAuditLog>());
        services.AddSingleton<IAuditQuery>(sp => sp.GetRequiredService<SqliteAuditLog>());
        services.AddSingleton<ITokenActivityRetention>(sp => sp.GetRequiredService<SqliteTokenActivity>());
        services.AddSingleton(stores.Decisions);
        services.AddSingleton<IDecisionRecordLog>(sp => sp.GetRequiredService<SqliteDecisionRecordLog>());
        // The support view (the admin site's Decisions panel) reads the record only through this port, by way of AdminQueries.
        services.AddSingleton<IDecisionRecordQuery>(stores.DecisionQuery);
        services.AddSingleton<IAdminActionLog>(stores.AdminActions);
        services.AddSingleton<ISignInDirectoryStore>(stores.SignInDirectory);
        services.AddSingleton<ITokenService>(sp => sp.GetRequiredService<SqliteTokenService>());
        services.AddSingleton<IActiveDeveloperQuery>(sp => sp.GetRequiredService<SqliteTokenActivity>());
        services.AddSingleton<ISharedAppCredentialStore>(sp => stores.SharedAppCredentials(sp.GetRequiredService<IClock>()));
        services.AddSingleton<IMemberStore>(stores.Members);
        services.AddSingleton<IAutoJoinSettingStore>(stores.AutoJoin);
        services.AddSingleton<IFeedCheckStore>(stores.FeedChecks);
        services.AddSingleton<ISelfServiceFeedStore>(stores.SelfServiceFeeds);
        // Read only in the server: the daily nudge counts the tenants due for purge. Only the admin CLI purges.
        services.AddSingleton<ITenantPurgeStore>(sp => stores.PurgeStore(sp.GetRequiredService<TenantLifecycle>()));
        // PACKMOAT_COOKIE_KEYS=database only (#232 B1): the signed-in sites' cookie keys, encrypted with the master key. Folder mode
        // (the default) registers nothing here.
        if (cookieKeys == CookieKeyStorage.Database)
            services.AddSingleton<ICookieKeyStore>(sp => stores.CookieKeys(sp.GetRequiredService<IClock>()));
    }
}
