using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Infrastructure;

/// <summary>
/// The SQLite adapters, and how each is built, in one list: the server (through <c>AddSqliteStores</c>), the command
/// line and the hosted modules all take theirs from here, so no composition root builds one its own way. The stateless stores are made
/// once, with the database, and every read gives the same one. The rest need what a composition root has only later (the clock, the
/// tenant lifecycle, the serving gate), so each is a method that makes a new one: a composition root calls it once and shares the result.
/// </summary>
public sealed class SqliteStores
{
    private readonly AesGcmSecretProtector _protector;

    /// <param name="protector">The master key, for the stores that keep secrets encrypted (tenant identities, shared-app credentials,
    /// cookie keys).</param>
    public SqliteStores(SqliteDatabase db, AesGcmSecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(protector);
        Database = db;
        _protector = protector;
        Audit = new SqliteAuditLog(db);
        Decisions = new SqliteDecisionRecordLog(db);
        DecisionQuery = new SqliteDecisionRecordQuery(db);
        AdminActions = new SqliteAdminActionLog(db);
        SignInDirectory = new SqliteSignInDirectoryStore(db);
        Members = new SqliteMemberStore(db);
        AutoJoin = new SqliteAutoJoinSettingStore(db);
        FeedChecks = new SqliteFeedCheckStore(db);
        SelfServiceFeeds = new SqliteSelfServiceFeedStore(db);
        MasterKeyCheck = new SqliteMasterKeyCheck(db, protector);
    }

    /// <summary>The database every store here is over.</summary>
    public SqliteDatabase Database { get; }

    /// <summary>The schema's migrations and the readiness check's read of them: the database itself.</summary>
    public ISchemaMigrator Migrator => Database;

    /// <summary>The master-key check: the start's, which records the marker, and the readiness check's read-only one.</summary>
    public SqliteMasterKeyCheck MasterKeyCheck { get; }

    /// <summary>The audit store: the downloads (written in batches by the server's queue) and their retention and reads.</summary>
    public SqliteAuditLog Audit { get; }

    /// <summary>The per-version decision record's writes and prune.</summary>
    public SqliteDecisionRecordLog Decisions { get; }

    /// <summary>The per-version decision record's reads (the support view).</summary>
    public SqliteDecisionRecordQuery DecisionQuery { get; }

    /// <summary>The record of admin writes (the admin site's, the command line's and the modules').</summary>
    public SqliteAdminActionLog AdminActions { get; }

    public SqliteSignInDirectoryStore SignInDirectory { get; }

    public SqliteMemberStore Members { get; }

    public SqliteAutoJoinSettingStore AutoJoin { get; }

    public SqliteFeedCheckStore FeedChecks { get; }

    public SqliteSelfServiceFeedStore SelfServiceFeeds { get; }

    /// <summary>The tenant store, with the lifecycle's creation participants. Make one and share it: the token service checks against it.</summary>
    public SqliteTenantStore Tenants(IClock clock, TenantLifecycle lifecycle) => new(Database, clock, _protector, lifecycle.Participants);

    /// <summary>The token service over <paramref name="tenants"/> (the one tenant store), asking <paramref name="gate"/> who is served.</summary>
    public SqliteTokenService TokenService(SqliteTenantStore tenants, IClock clock, IServingGate gate) => new(Database, tenants, clock, gate);

    public SqliteTokenActivity TokenActivity(IClock clock) => new(Database, clock);

    /// <summary>The scheduled jobs' lease, timed by <paramref name="clock"/>.</summary>
    public SqliteJobLease JobLease(IClock clock) => new(Database, clock);

    /// <summary>The shared rate counter, timed by <paramref name="clock"/>.</summary>
    public SqliteRateCounter RateCounter(IClock clock) => new(Database, clock);

    /// <summary>The form nonces, timed by <paramref name="clock"/>.</summary>
    public SqliteFormNonceStore FormNonces(IClock clock) => new(Database, clock);

    /// <summary>The feed setup scratch, timed by <paramref name="clock"/>.</summary>
    public SqliteFeedSetupScratch FeedSetupScratch(IClock clock) => new(Database, clock);

    public SqliteSharedAppCredentialStore SharedAppCredentials(IClock clock) => new(Database, clock, _protector);

    /// <summary>The purge store, with the lifecycle's eligibility (made now) and purge contributors.</summary>
    public SqliteTenantPurgeStore PurgeStore(TenantLifecycle lifecycle) => new(Database, lifecycle.Eligibility?.Invoke(), lifecycle.Contributors);

    /// <summary>The signed-in sites' cookie keys, encrypted with the master key (PACKMOAT_COOKIE_KEYS=database only).</summary>
    public SqliteCookieKeyStore CookieKeys(IClock clock) => new(Database, clock, _protector);

    /// <summary>The organisation store, with the lifecycle's creation participants.</summary>
    public SqliteOrganisationStore Organisations(TenantLifecycle lifecycle) => new(Database, lifecycle.Participants);
}
