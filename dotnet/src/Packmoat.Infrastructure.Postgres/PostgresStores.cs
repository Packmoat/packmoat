using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The PostgreSQL adapters, and how each is built, in one list: the twin of <see cref="SqliteStores"/>, member for member. The
/// composition roots build from it when PACKMOAT_DB_ENGINE=postgres: the server (AddPostgresStores) and the command line. Every store has its adapter here, and a test holds every port <see cref="SqliteStores"/> gives to be given here too.
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

    /// <summary>
    /// The schema port the server's start and readiness check use: on PostgreSQL it never migrates, only reads, and refuses
    /// a schema not at this build's version (<see cref="PostgresServerSchema"/>). The owner role migrates, with
    /// <see cref="PostgresDatabase.MigrateAndGrantAsync(IReadOnlyList{MigrationTrack}, string, CancellationToken)"/>.
    /// </summary>
    public ISchemaMigrator Migrator => new PostgresServerSchema(Database);

    /// <summary>The master-key check.</summary>
    public IMasterKeyCheck MasterKeyCheck => new PostgresMasterKeyCheck(Database, _protector);

    /// <summary>The audit store: one adapter with the batch, retention and query ports too, as SQLite's.</summary>
    public PostgresAuditLog Audit => new(Database);

    /// <summary>The per-version decision record.</summary>
    public PostgresDecisionRecordLog Decisions => new(Database);

    /// <summary>The decision record's read port, for the support view.</summary>
    public PostgresDecisionRecordQuery DecisionQuery => new(Database);

    /// <summary>The record of admin writes, with its retention prune.</summary>
    public PostgresAdminActionLog AdminActions => new(Database);

    /// <summary>The customer directory to tenant links.</summary>
    public PostgresSignInDirectoryStore SignInDirectory => new(Database);

    /// <summary>The people who signed in on the customer site.</summary>
    public PostgresMemberStore Members => new(Database);

    /// <summary>The auto-join setting.</summary>
    public PostgresAutoJoinSettingStore AutoJoin => new(Database);

    /// <summary>The wizard's feed checks and their 30-day redaction.</summary>
    public PostgresFeedCheckStore FeedChecks => new(Database);

    /// <summary>The self-service feed writes: Connect and Withdraw.</summary>
    public PostgresSelfServiceFeedStore SelfServiceFeeds => new(Database);

    /// <summary>The tenant store, with <paramref name="lifecycle"/>'s creation participants.</summary>
    public PostgresTenantStore Tenants(IClock clock, TenantLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        return new(Database, clock, _protector, lifecycle.Participants);
    }

    /// <summary>The token service over <paramref name="tenants"/>, and the authenticator, as SQLite's.</summary>
    public PostgresTokenService TokenService(PostgresTenantStore tenants, IClock clock, IServingGate gate) => new(Database, tenants, clock, gate);

    /// <summary>The token activity log, the active-developer query and the retention, timed by <paramref name="clock"/>, as SQLite's.</summary>
    public PostgresTokenActivity TokenActivity(IClock clock) => new(Database, clock);

    /// <summary>The scheduled jobs' lease, timed by <paramref name="clock"/>.</summary>
    public PostgresJobLease JobLease(IClock clock) => new(Database, clock);

    /// <summary>The shared rate counter, timed by <paramref name="clock"/>, as SQLite's.</summary>
    public PostgresRateCounter RateCounter(IClock clock) => new(Database, clock);

    /// <summary>The form nonces, timed by <paramref name="clock"/>, as SQLite's.</summary>
    public PostgresFormNonceStore FormNonces(IClock clock) => new(Database, clock);

    /// <summary>The feed setup scratch, timed by <paramref name="clock"/>, as SQLite's.</summary>
    public PostgresFeedSetupScratch FeedSetupScratch(IClock clock) => new(Database, clock);

    /// <summary>The shared app's credential slots and the operator's verification records, timed by <paramref name="clock"/>.</summary>
    public PostgresSharedAppCredentialStore SharedAppCredentials(IClock clock) => new(Database, clock, _protector);

    /// <summary>The purge store, with the lifecycle's eligibility (made now) and purge contributors, as SQLite's.</summary>
    public PostgresTenantPurgeStore PurgeStore(TenantLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        return new(Database, lifecycle.Eligibility?.Invoke(), lifecycle.Contributors);
    }

    /// <summary>The signed-in sites' cookie keys, encrypted with the master key.</summary>
    public PostgresCookieKeyStore CookieKeys(IClock clock) => new(Database, clock, _protector);

    /// <summary>The organisation store, with the lifecycle's creation participants, as SQLite's.</summary>
    public PostgresOrganisationStore Organisations(TenantLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        return new(Database, lifecycle.Participants);
    }
}
