using Packmoat.Application;
using Packmoat.Infrastructure.Lifecycle;
using Packmoat.Infrastructure.Postgres;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// PostgreSQL as the store contracts see it: <see cref="PostgresSqlEngine"/>'s SQL, and each store's adapter from
/// <see cref="PostgresStores"/>, as <see cref="SqliteStoreEngine"/> takes SQLite's from its stores. Every store is built,
/// so every contract has a PostgreSQL fixture (StoreContractCoverageTests).
/// </summary>
public sealed class PostgresStoreEngine : PostgresSqlEngine, IStoreEngine
{
    public static new PostgresStoreEngine Instance { get; } = new(asAppRole: false);

    /// <summary>The stores signed in as the app role, on databases the owner role migrated with --grant-to.</summary>
    public static new PostgresStoreEngine AppRoleInstance { get; } = new(asAppRole: true);

    private PostgresStoreEngine(bool asAppRole) : base(asAppRole)
    {
    }

    private static PostgresStores Stores(IEngineDb t) => new(Pg(t).Db, t.Protector);

    public ITenantStore Tenants(IEngineDb t) => Stores(t).Tenants(t.Clock, TestDb.Lifecycle);

    public ITenantStore TenantsWith(IEngineDb t, TenantLifecycle lifecycle) => Stores(t).Tenants(t.Clock, lifecycle);

    public TokenPorts Tokens(IEngineDb t)
    {
        var stores = Stores(t);
        var service = stores.TokenService(stores.Tenants(t.Clock, TestDb.Lifecycle), t.Clock, ServeAllGate.Instance);
        return new(service, service);
    }

    public ITenantPurgeStore Purge(IEngineDb t, TenantLifecycle lifecycle) => Stores(t).PurgeStore(lifecycle);
    public IOrganisationStore Organisations(IEngineDb t) => Stores(t).Organisations(TestDb.Lifecycle);
    public IFeedCheckStore FeedChecks(IEngineDb t) => Stores(t).FeedChecks;
    public IDecisionRecordLog DecisionRecords(IEngineDb t) => Stores(t).Decisions;
    public IDecisionRecordQuery DecisionQuery(IEngineDb t) => Stores(t).DecisionQuery;
    public ISharedAppCredentialStore SharedApp(IEngineDb t) => Stores(t).SharedAppCredentials(t.Clock);
    public IAutoJoinSettingStore AutoJoin(IEngineDb t) => Stores(t).AutoJoin;
    public ISignInDirectoryStore SignInDirectory(IEngineDb t) => Stores(t).SignInDirectory;
    public IMemberStore Members(IEngineDb t) => Stores(t).Members;
    public IAdminActionLog AdminActions(IEngineDb t) => Stores(t).AdminActions;
    public ISelfServiceFeedStore SelfServiceFeeds(IEngineDb t) => Stores(t).SelfServiceFeeds;
    public IAuditLog Audit(IEngineDb t) => Stores(t).Audit;
    public IAuditBatchLog AuditBatch(IEngineDb t) => Stores(t).Audit;
    public IAuditRetention AuditRetention(IEngineDb t) => Stores(t).Audit;
    public IAuditQuery AuditQuery(IEngineDb t) => Stores(t).Audit;
    public IJobLease JobLease(IEngineDb t) => Stores(t).JobLease(t.Clock);
    public IRateCounter RateCounter(IEngineDb t) => Stores(t).RateCounter(t.Clock);
    public IFormNonceStore FormNonces(IEngineDb t) => Stores(t).FormNonces(t.Clock);
    public IFeedSetupScratch FeedSetupScratch(IEngineDb t) => Stores(t).FeedSetupScratch(t.Clock);
    public ICookieKeyStore CookieKeys(IEngineDb t) => Stores(t).CookieKeys(t.Clock);

    public TokenActivityPorts TokenActivity(IEngineDb t)
    {
        var activity = Stores(t).TokenActivity(t.Clock);
        return new(activity, activity, activity);
    }
}
