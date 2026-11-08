using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The PostgreSQL adapters, and how each is built, in one list (#303 P3): the twin of <see cref="SqliteStores"/>, member for member. Only
/// the tests use it: PACKMOAT_DB_ENGINE=postgres is refused until P5, and no project in src/ references this one. A store not built yet
/// throws <see cref="NotSupportedException"/> naming the pull request that brings it; each P3 pull request replaces its stores' throws
/// with the adapter, and the tests' pinned PendingStores list shrinks with it. A member's type is the port until its adapter lands.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresStores
{
    private readonly AesGcmSecretProtector _protector;

    /// <param name="protector">The master key, for the stores that keep secrets encrypted.</param>
    public PostgresStores(PostgresDatabase db, AesGcmSecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(protector);
        Database = db;
        _protector = protector;
    }

    /// <summary>The database every store here is over.</summary>
    public PostgresDatabase Database { get; }

    /// <summary>The schema's migrations and the readiness check's read of them: the database itself.</summary>
    public ISchemaMigrator Migrator => Database;

    /// <summary>The master-key check (#288 P2, PR 5).</summary>
    public IMasterKeyCheck MasterKeyCheck => new PostgresMasterKeyCheck(Database, _protector);

    /// <summary>The audit store. PR 3 makes it one adapter with the batch, retention and query ports too, as SQLite's.</summary>
    public IAuditLog Audit => throw Pending(nameof(Audit), 3);

    public IDecisionRecordLog Decisions => throw Pending(nameof(Decisions), 4);

    public IDecisionRecordQuery DecisionQuery => throw Pending(nameof(DecisionQuery), 4);

    public IAdminActionLog AdminActions => throw Pending(nameof(AdminActions), 2);

    public ISignInDirectoryStore SignInDirectory => throw Pending(nameof(SignInDirectory), 2);

    public IMemberStore Members => throw Pending(nameof(Members), 2);

    public IAutoJoinSettingStore AutoJoin => throw Pending(nameof(AutoJoin), 2);

    public IFeedCheckStore FeedChecks => throw Pending(nameof(FeedChecks), 5);

    public ISelfServiceFeedStore SelfServiceFeeds => throw Pending(nameof(SelfServiceFeeds), 5);

    public ITenantStore Tenants(IClock clock, TenantLifecycle lifecycle) => throw Pending(nameof(Tenants), 1);

    /// <summary>The token service over <paramref name="tenants"/>. PR 1 makes it the authenticator too, as SQLite's.</summary>
    public ITokenService TokenService(ITenantStore tenants, IClock clock, IServingGate gate) => throw Pending(nameof(TokenService), 1);

    /// <summary>The token activity log. PR 3 makes it the active-developer query and the retention too, as SQLite's.</summary>
    public ITokenActivityLog TokenActivity(IClock clock) => throw Pending(nameof(TokenActivity), 3);

    public IJobLease JobLease(IClock clock) => throw Pending(nameof(JobLease), 2);

    public ISharedAppCredentialStore SharedAppCredentials(IClock clock) => throw Pending(nameof(SharedAppCredentials), 5);

    public ITenantPurgeStore PurgeStore(TenantLifecycle lifecycle) => throw Pending(nameof(PurgeStore), 6);

    public ICookieKeyStore CookieKeys(IClock clock) => throw Pending(nameof(CookieKeys), 2);

    public IOrganisationStore Organisations(TenantLifecycle lifecycle) => throw Pending(nameof(Organisations), 6);

    /// <summary>The refusal of a store not built yet, naming the pull request of #303 that brings it.</summary>
    private static NotSupportedException Pending(string store, int pullRequest) =>
        new($"PostgreSQL's {store} store is not built yet: it arrives in #303 P3, PR {pullRequest}.");
}
