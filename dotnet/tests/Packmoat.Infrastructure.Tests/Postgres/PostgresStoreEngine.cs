using Packmoat.Application;
using Packmoat.Infrastructure.Lifecycle;
using Packmoat.Infrastructure.Postgres;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// PostgreSQL as the store contracts see it (#303 P3): <see cref="PostgresSqlEngine"/>'s SQL, and each store's adapter from
/// <see cref="PostgresStores"/>, as <see cref="SqliteStoreEngine"/> takes SQLite's from its stores. A store not built yet throws
/// (<see cref="PendingStores"/>), so no contract has a PostgreSQL fixture for it yet.
/// </summary>
public sealed class PostgresStoreEngine : PostgresSqlEngine, IStoreEngine
{
    public static new PostgresStoreEngine Instance { get; } = new();

    private PostgresStoreEngine()
    {
    }

    /// <summary>
    /// The store members of <see cref="IStoreEngine"/> that throw on PostgreSQL because their adapter is not built yet, in ordinal order.
    /// Pinned: each P3 pull request takes its stores off this list as it builds them, and <see cref="PostgresStoreEngineTests"/> checks
    /// that a member throws if and only if it is here. PR 6 leaves it empty.
    /// </summary>
    public static readonly string[] PendingStores =
    [
        "AdminActions", "Audit", "AuditBatch", "AuditQuery", "AuditRetention", "AutoJoin", "CookieKeys", "DecisionQuery", "DecisionRecords",
        "FeedChecks", "JobLease", "Members", "Organisations", "Purge", "SelfServiceFeeds", "SharedApp", "SignInDirectory", "Tenants",
        "TokenActivity", "Tokens",
    ];

    private static PostgresStores Stores(IEngineDb t) => new(Pg(t).Db, t.Protector);

    public ITenantStore Tenants(IEngineDb t) => Stores(t).Tenants(t.Clock, TestDb.Lifecycle);

    public TokenPorts Tokens(IEngineDb t)
    {
        var service = Stores(t).TokenService(Tenants(t), t.Clock, ServeAllGate.Instance);
        return new(service, (ITenantAuthenticator)service);
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
    public IAuditBatchLog AuditBatch(IEngineDb t) => (IAuditBatchLog)Stores(t).Audit;
    public IAuditRetention AuditRetention(IEngineDb t) => (IAuditRetention)Stores(t).Audit;
    public IAuditQuery AuditQuery(IEngineDb t) => (IAuditQuery)Stores(t).Audit;
    public IJobLease JobLease(IEngineDb t) => Stores(t).JobLease(t.Clock);
    public ICookieKeyStore CookieKeys(IEngineDb t) => Stores(t).CookieKeys(t.Clock);

    public TokenActivityPorts TokenActivity(IEngineDb t)
    {
        var log = Stores(t).TokenActivity(t.Clock);
        return new(log, (IActiveDeveloperQuery)log, (ITokenActivityRetention)log);
    }
}
