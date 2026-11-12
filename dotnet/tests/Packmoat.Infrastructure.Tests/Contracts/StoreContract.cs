using System.Data.Common;
using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Packmoat.Application;
using Packmoat.Infrastructure.Lifecycle;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// One database engine as SQL sees it: a fresh database at the current schema, and the few raw operations a contract test
/// needs where a port has no way in (setting up or reading back a row). The SQL a contract passes must mean the same on every engine: plain
/// SELECT, INSERT, UPDATE and DELETE on the shared schema, no engine functions, no catalog tables.
/// </summary>
public interface ISqlEngine
{
    /// <summary>A fresh database at the current schema, with the engine's stores; the handle carries nothing of the engine.</summary>
    Task<IEngineDb> CreateAsync();

    /// <summary>Runs one statement.</summary>
    Task ExecAsync(IEngineDb t, string sql);

    /// <summary>The first column of the first row, or null for no row (<see cref="DBNull"/> for a NULL value); integers read as long.</summary>
    Task<object?> ScalarAsync(IEngineDb t, string sql);

    /// <summary>The first column of every row, as invariant text ("NULL" for a NULL value).</summary>
    Task<List<string>> ColumnAsync(IEngineDb t, string sql);

    /// <summary>The engine's refusal of a write (a constraint, a trigger, a foreign key): asserts <paramref name="write"/> throws it.</summary>
    Task<DbException> RefusedAsync(Func<Task> write);

    /// <summary>The engine's SQL for a blob column as hex text, for comparing rows as text.</summary>
    string HexSql(string column);

    /// <summary>The schema's tables by name, in ordinal order, without the engine's own bookkeeping tables.</summary>
    Task<List<string>> TablesAsync(IEngineDb t);

    /// <summary>
    /// SQL for a time column's value <paramref name="ago"/> before now by the clock the schema's rules read, so a row can
    /// be dated just either side of a retention floor.
    /// </summary>
    string TimeAgoSql(TimeSpan ago);

    /// <summary>SQL for a blob literal holding the bytes <paramref name="hex"/> spells.</summary>
    string BlobSql(string hex);

    /// <summary>
    /// Whether <paramref name="refusal"/> is the CHECK constraint's named <paramref name="name"/> (an engine that names constraints) or
    /// written <paramref name="check"/> (one that reports the text): not a NOT NULL's, a key's, a trigger's or another CHECK's.
    /// </summary>
    bool IsCheckViolation(DbException refusal, string name, string check);
}

/// <summary>
/// One database engine's master-key check: its SQL and its <see cref="IMasterKeyCheck"/>. PostgreSQL has this before it has
/// the stores (<see cref="MasterKeyCheckContract"/>).
/// </summary>
public interface IMasterKeyEngine : ISqlEngine
{
    /// <summary>The engine's master-key check over <paramref name="t"/>'s database, with <paramref name="key"/> as the master key.</summary>
    IMasterKeyCheck MasterKeyCheck(IEngineDb t, AesGcmSecretProtector key);

    /// <summary>
    /// Test only: adds a BEFORE INSERT trigger on meta to <paramref name="t"/>'s database alone, so the next insert of a key_check
    /// row while none exists first inserts <paramref name="rivalMarker"/>: a rival first start, with another key, writing between this
    /// start's read and its write, every time.
    /// </summary>
    Task ArrangeRivalMarkerAsync(IEngineDb t, byte[] rivalMarker);
}

/// <summary>
/// One database engine as the store contracts see it: its SQL (<see cref="ISqlEngine"/>) and each store's adapter as its port.
/// </summary>
public interface IStoreEngine : IMasterKeyEngine
{
    /// <summary>The tenant store: the one the token service checks against, so a tenant made or deactivated here is seen by
    /// <see cref="Tokens"/>.</summary>
    ITenantStore Tenants(IEngineDb t);

    /// <summary>
    /// A tenant store built from <paramref name="lifecycle"/> as production builds it: its creation participants take part in each
    /// creation, in its transaction. For the contract tests of what a participant sees.
    /// </summary>
    ITenantStore TenantsWith(IEngineDb t, TenantLifecycle lifecycle);

    /// <summary>The token service and its authenticator, over the tenant store <see cref="Tenants"/> gives.</summary>
    TokenPorts Tokens(IEngineDb t);

    /// <summary>
    /// The tenant purge, built from <paramref name="lifecycle"/> as production builds it: its eligibility (none: every purge refused) and
    /// its contributors. Its creation participants play no part in a purge.
    /// </summary>
    ITenantPurgeStore Purge(IEngineDb t, TenantLifecycle lifecycle);

    /// <summary>The organisation creation, with the same lifecycle participants as <see cref="Tenants"/> (the core's tests: none).</summary>
    IOrganisationStore Organisations(IEngineDb t);

    IFeedCheckStore FeedChecks(IEngineDb t);
    IDecisionRecordLog DecisionRecords(IEngineDb t);
    IDecisionRecordQuery DecisionQuery(IEngineDb t);
    ISharedAppCredentialStore SharedApp(IEngineDb t);
    IAutoJoinSettingStore AutoJoin(IEngineDb t);
    ISignInDirectoryStore SignInDirectory(IEngineDb t);
    IMemberStore Members(IEngineDb t);
    IAdminActionLog AdminActions(IEngineDb t);
    ISelfServiceFeedStore SelfServiceFeeds(IEngineDb t);
    IAuditLog Audit(IEngineDb t);
    IAuditBatchLog AuditBatch(IEngineDb t);
    IAuditRetention AuditRetention(IEngineDb t);
    IAuditQuery AuditQuery(IEngineDb t);
    TokenActivityPorts TokenActivity(IEngineDb t);
    IJobLease JobLease(IEngineDb t);
    IRateCounter RateCounter(IEngineDb t);
    IFormNonceStore FormNonces(IEngineDb t);
    IFeedSetupScratch FeedSetupScratch(IEngineDb t);
    ICookieKeyStore CookieKeys(IEngineDb t);
}

/// <summary>
/// SQLite as SQL sees it: the raw operations of <see cref="ISqlEngine"/> over a SQLite database, whichever test database
/// holds it. The core's <see cref="SqliteStoreEngine"/> and the hosted tests' SQLite engine both take theirs from here, as the PostgreSQL
/// engines take theirs from <see cref="PostgresSqlEngine"/>.
/// </summary>
public abstract class SqliteSqlEngine : ISqlEngine
{
    public abstract Task<IEngineDb> CreateAsync();

    /// <summary>The SQLite database <paramref name="t"/> is: the one place an engine takes its handle back. A handle from another engine is refused.</summary>
    protected abstract SqliteDatabase Database(IEngineDb t);

    public async Task ExecAsync(IEngineDb t, string sql)
    {
        await using var c = await Database(t).OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<object?> ScalarAsync(IEngineDb t, string sql)
    {
        await using var c = await Database(t).OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    public async Task<List<string>> ColumnAsync(IEngineDb t, string sql)
    {
        await using var c = await Database(t).OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var rows = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add(r.IsDBNull(0) ? "NULL" : Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture)!);
        return rows;
    }

    public async Task<DbException> RefusedAsync(Func<Task> write) => await Assert.ThrowsAsync<SqliteException>(write);

    public string HexSql(string column) => $"hex({column})";

    public async Task<List<string>> TablesAsync(IEngineDb t) =>
        (await ColumnAsync(t, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite%'")).Order(StringComparer.Ordinal).ToList();

    /// <summary>The time as the writers store it ("O" format, UTC): the triggers read julianday('now'), this machine's clock.</summary>
    public string TimeAgoSql(TimeSpan ago) => $"'{(DateTimeOffset.UtcNow - ago).ToString("O", CultureInfo.InvariantCulture)}'";

    public string BlobSql(string hex) => $"X'{hex}'";

    /// <summary>
    /// SQLITE_CONSTRAINT_CHECK, the extended result code, with the constraint's text in the message ("CHECK constraint failed: …"; SQLite
    /// names an unnamed CHECK by its text), spaces and line breaks compared as one space.
    /// </summary>
    public bool IsCheckViolation(DbException refusal, string name, string check) =>
        refusal is SqliteException { SqliteExtendedErrorCode: 275 }
        && OneSpace(refusal.Message).EndsWith($"CHECK constraint failed: {OneSpace(check)}'.", StringComparison.Ordinal);

    private static string OneSpace(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
}

/// <summary>The SQLite engine: <see cref="TestDb"/> and the <c>Sqlite*</c> adapters.</summary>
public sealed class SqliteStoreEngine : SqliteSqlEngine, IStoreEngine
{
    public static SqliteStoreEngine Instance { get; } = new();

    public override async Task<IEngineDb> CreateAsync() => await TestDb.CreateAsync();

    /// <summary>
    /// The one place a contract's handle is taken back to the SQLite <see cref="TestDb"/> it is: this engine's own adapters need its
    /// connection and its stores. A handle from another engine is refused.
    /// </summary>
    private static TestDb Sql(IEngineDb t) => t as TestDb ?? throw new ArgumentException($"Not a SQLite test database: {t.GetType().Name}.", nameof(t));

    protected override SqliteDatabase Database(IEngineDb t) => Sql(t).Db;

    public ITenantStore Tenants(IEngineDb t) => Sql(t).Tenants;
    public ITenantStore TenantsWith(IEngineDb t, TenantLifecycle lifecycle) => Sql(t).Stores.Tenants(t.Clock, lifecycle);
    public TokenPorts Tokens(IEngineDb t) => new(Sql(t).Tokens, Sql(t).Tokens);
    public ITenantPurgeStore Purge(IEngineDb t, TenantLifecycle lifecycle) => Sql(t).Stores.PurgeStore(lifecycle);
    public IOrganisationStore Organisations(IEngineDb t) => Sql(t).Stores.Organisations(TestDb.Lifecycle);
    public IFeedCheckStore FeedChecks(IEngineDb t) => new SqliteFeedCheckStore(Sql(t).Db);
    public IDecisionRecordLog DecisionRecords(IEngineDb t) => new SqliteDecisionRecordLog(Sql(t).Db);
    public IDecisionRecordQuery DecisionQuery(IEngineDb t) => new SqliteDecisionRecordQuery(Sql(t).Db);
    public ISharedAppCredentialStore SharedApp(IEngineDb t) => new SqliteSharedAppCredentialStore(Sql(t).Db, t.Clock, t.Protector);
    public IAutoJoinSettingStore AutoJoin(IEngineDb t) => new SqliteAutoJoinSettingStore(Sql(t).Db);
    public ISignInDirectoryStore SignInDirectory(IEngineDb t) => new SqliteSignInDirectoryStore(Sql(t).Db);
    public IMemberStore Members(IEngineDb t) => new SqliteMemberStore(Sql(t).Db);
    public IAdminActionLog AdminActions(IEngineDb t) => new SqliteAdminActionLog(Sql(t).Db);
    public ISelfServiceFeedStore SelfServiceFeeds(IEngineDb t) => new SqliteSelfServiceFeedStore(Sql(t).Db);
    public IAuditLog Audit(IEngineDb t) => new SqliteAuditLog(Sql(t).Db);
    public IAuditBatchLog AuditBatch(IEngineDb t) => new SqliteAuditLog(Sql(t).Db);
    public IAuditRetention AuditRetention(IEngineDb t) => new SqliteAuditLog(Sql(t).Db);
    public IAuditQuery AuditQuery(IEngineDb t) => new SqliteAuditLog(Sql(t).Db);
    public IJobLease JobLease(IEngineDb t) => new SqliteJobLease(Sql(t).Db, t.Clock);
    public IRateCounter RateCounter(IEngineDb t) => new SqliteRateCounter(Sql(t).Db, t.Clock);
    public IFormNonceStore FormNonces(IEngineDb t) => new SqliteFormNonceStore(Sql(t).Db, t.Clock);
    public IFeedSetupScratch FeedSetupScratch(IEngineDb t) => new SqliteFeedSetupScratch(Sql(t).Db, t.Clock);
    public ICookieKeyStore CookieKeys(IEngineDb t) => new SqliteCookieKeyStore(Sql(t).Db, t.Clock, t.Protector);
    public IMasterKeyCheck MasterKeyCheck(IEngineDb t, AesGcmSecretProtector key) => new SqliteMasterKeyCheck(Sql(t).Db, key);

    /// <summary>A plain trigger. Its own insert does not fire it again (recursive triggers are off, and the value test stops it anyway).</summary>
    public async Task ArrangeRivalMarkerAsync(IEngineDb t, byte[] rivalMarker)
    {
        var rival = BlobSql(Convert.ToHexString(rivalMarker));
        await ExecAsync(t, $"""
            CREATE TRIGGER test_rival_marker BEFORE INSERT ON meta
            WHEN NEW.key = 'key_check' AND NEW.value <> {rival} AND NOT EXISTS (SELECT 1 FROM meta WHERE key = 'key_check')
            BEGIN INSERT INTO meta(key, value) VALUES('key_check', {rival}); END
            """);
    }
    public TokenActivityPorts TokenActivity(IEngineDb t)
    {
        var activity = new SqliteTokenActivity(Sql(t).Db, t.Clock);
        return new(activity, activity, activity);
    }
}

/// <summary>The token-activity ports as one object, the way the tests use them (the SQLite adapter is one class with all three).</summary>
public sealed class TokenActivityPorts(ITokenActivityLog log, IActiveDeveloperQuery query, ITokenActivityRetention retention)
    : ITokenActivityLog, IActiveDeveloperQuery, ITokenActivityRetention
{
    public ValueTask RecordAsync(string tokenId, DateOnly day, CancellationToken cancellationToken) => log.RecordAsync(tokenId, day, cancellationToken);
    public Task<ActiveDevelopers> GetAsync(string tenantId, int days, CancellationToken cancellationToken) => query.GetAsync(tenantId, days, cancellationToken);
    public Task<int> PruneAsync(DateOnly keepFrom, CancellationToken cancellationToken) => retention.PruneAsync(keepFrom, cancellationToken);
}

/// <summary>The token service and its authenticator as one object, the way the tests use them (the SQLite adapter is one class with both).</summary>
public sealed class TokenPorts(ITokenService service, ITenantAuthenticator authenticator) : ITokenService, ITenantAuthenticator
{
    public Task<IssuedToken> IssueAsync(string tenantId, string label, CancellationToken cancellationToken, TokenKind kind = TokenKind.Developer) =>
        service.IssueAsync(tenantId, label, cancellationToken, kind);
    public Task<bool> RevokeAsync(string tokenId, CancellationToken cancellationToken) => service.RevokeAsync(tokenId, cancellationToken);
    public Task<bool> RevokeAsync(string tenantId, string tokenId, CancellationToken cancellationToken) => service.RevokeAsync(tenantId, tokenId, cancellationToken);
    public Task<IReadOnlyList<TokenInfo>> ListAsync(string tenantId, CancellationToken cancellationToken) => service.ListAsync(tenantId, cancellationToken);
    public Task<IssuedToken?> IssueForMemberAsync(string tenantId, string ownerTid, string ownerOid, string label, DateTimeOffset expiresAt, TokenKind kind, int maxActive,
        CancellationToken cancellationToken) => service.IssueForMemberAsync(tenantId, ownerTid, ownerOid, label, expiresAt, kind, maxActive, cancellationToken);
    public Task<IReadOnlyList<TokenInfo>> ListForMemberAsync(string tenantId, string ownerTid, string ownerOid, CancellationToken cancellationToken) =>
        service.ListForMemberAsync(tenantId, ownerTid, ownerOid, cancellationToken);
    public Task<bool> RevokeForMemberAsync(string tenantId, string ownerTid, string ownerOid, string tokenId, string revokedBy, CancellationToken cancellationToken) =>
        service.RevokeForMemberAsync(tenantId, ownerTid, ownerOid, tokenId, revokedBy, cancellationToken);
    public Task<bool> RevokeInTenantAsync(string tenantId, string tokenId, string revokedBy, CancellationToken cancellationToken) =>
        service.RevokeInTenantAsync(tenantId, tokenId, revokedBy, cancellationToken);
    public ValueTask<AuthResult?> AuthenticateAsync(string token, CancellationToken cancellationToken) => authenticator.AuthenticateAsync(token, cancellationToken);
}

/// <summary>
/// The base of every store contract. A contract is an abstract class holding the tests every adapter of a store's port must pass.
/// It reaches the database only through <see cref="SqlContract{TEngine}.Engine"/>: never through an engine's own types (<c>t.Db</c>, a <c>Sqlite*</c> constructor,
/// <c>SqliteException</c>, the handle's SQLite-typed <c>t.Tenants</c> and <c>t.Tokens</c>), the database file, or SQL only one engine understands. Each contract has one concrete subclass per engine: today
/// the SQLite one (<c>TenantStoreTests</c> for <c>TenantStoreContract</c>, and so on), which also keeps the tests that only make sense on
/// SQLite (migrations from older versions, triggers, catalog tables, bytes on disk). A Postgres adapter adds an engine and a second subclass
/// of each contract, and runs the same tests. A contract whose port an engine has before it has the stores (the master-key check on
/// PostgreSQL) derives from <see cref="SqlContract{TEngine}"/> over a smaller engine instead.
/// </summary>
public abstract class StoreContract : SqlContract<IStoreEngine>
{

    /// <summary>
    /// The trait on a contract test that rests on the schema's own rules, its triggers (the append-only logs, the retention floors, the
    /// purge and dropped-row rules) rather than the adapter's code or a constraint. Both engines have the triggers (PostgreSQL too
    /// a later step, where SchemaRulesContract checks each one), so nothing skips them; the mark says which store tests a change to a trigger can
    /// break, on either engine. The list is pinned in <see cref="StoreContractCoverageTests.TriggerTests"/>.
    /// </summary>
    public const string Needs = "Needs";
    public const string Triggers = "Triggers";

    /// <summary>An operator-made tenant (<see cref="TestTenants.Operator"/>), through the engine's tenant store.</summary>
    protected Task AddTenantAsync(IEngineDb t, string id = "acme", int cooldown = 14) => Engine.Tenants(t).CreateAsync(TestTenants.Operator(id, cooldown), default);
}

/// <summary>
/// The root of every contract: the engine it runs on and the raw SQL helpers, all through <see cref="Engine"/>. A contract
/// is an abstract subclass (through <see cref="StoreContract"/> for the stores), held to the same rules.
/// </summary>
public abstract class SqlContract<TEngine> where TEngine : ISqlEngine
{
    protected abstract TEngine Engine { get; }

    protected Task<IEngineDb> CreateDbAsync() => Engine.CreateAsync();
    protected Task ExecAsync(IEngineDb t, string sql) => Engine.ExecAsync(t, sql);
    protected Task<object?> ScalarAsync(IEngineDb t, string sql) => Engine.ScalarAsync(t, sql);
    protected Task<List<string>> ColumnAsync(IEngineDb t, string sql) => Engine.ColumnAsync(t, sql);
    protected Task<DbException> RefusedAsync(Func<Task> write) => Engine.RefusedAsync(write);
}

/// <summary>
/// The contracts' own guards: every contract runs on SQLite (a contract with no concrete subclass would hold tests that never run), the
/// list of contracts is the one a second engine must cover, and no contract reaches past <see cref="IStoreEngine"/>.
/// </summary>
[Collection("sqlite-pools")]
public class StoreContractCoverageTests
{
    internal static readonly Type[] Types = typeof(StoreContract).Assembly.GetTypes();

    /// <summary>An abstract class under <see cref="SqlContract{TEngine}"/>, other than the bases themselves: one contract.</summary>
    internal static bool IsContract(Type t)
    {
        if (!t.IsAbstract || t == typeof(StoreContract) || t.IsGenericTypeDefinition) return false;
        for (var b = t.BaseType; b is not null; b = b.BaseType)
            if (b.IsGenericType && b.GetGenericTypeDefinition() == typeof(SqlContract<>)) return true;
        return false;
    }

    private static List<string> ContractNames() => Types.Where(IsContract).Select(t => t.Name).Order(StringComparer.Ordinal).ToList();

    public static TheoryData<string> Contracts() => new(ContractNames());

    /// <summary>
    /// A fixture's engine: its Engine override, the nearest one up its classes. (Asked of the fixture alone, the name is ambiguous: the
    /// generic base's abstract property and the override are both found.)
    /// </summary>
    internal static object? EngineOf(Type fixture)
    {
        for (var type = fixture; type is not null; type = type.BaseType)
            if (type.GetProperty("Engine", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } property)
                return property.GetValue(Activator.CreateInstance(fixture));
        return null;
    }

    /// <summary>Whether <paramref name="fixture"/> carries [Trait("Category", "Postgres")], the trait tools/postgres-tests.sh picks.</summary>
    internal static bool IsPostgresFixture(Type fixture) =>
        fixture.GetCustomAttributesData().Any(a => a.AttributeType == typeof(TraitAttribute)
            && (string?)a.ConstructorArguments[0].Value == "Category" && (string?)a.ConstructorArguments[1].Value == PostgresTestServer.Category);

    [Theory]
    [MemberData(nameof(Contracts))]
    public void Every_contract_has_a_sqlite_fixture_that_runs_its_tests(string contract)
    {
        var type = Types.Single(t => t.Name == contract);
        Assert.EndsWith("Contract", contract);
        Assert.Contains(type.GetMethods(BindingFlags.Instance | BindingFlags.Public), m => m.GetCustomAttributes<FactAttribute>().Any());
        var fixtures = Types.Where(t => !t.IsAbstract && type.IsAssignableFrom(t)).ToList();
        Assert.Contains(fixtures, f => f.Name == contract[..^"Contract".Length] + "Tests");
        foreach (var fixture in fixtures)
        {
            var collection = fixture.GetCustomAttributesData().Single(a => a.AttributeType == typeof(CollectionAttribute)).ConstructorArguments[0].Value;
            var engine = EngineOf(fixture);
            if (engine is PostgresSqlEngine)
            {
                // A PostgreSQL fixture: in the Postgres collection with its trait, named after the SQLite one, and every test an
                // [EngineFact], so it skips as [PostgresFact] does when there is no server.
                Assert.Equal(PostgresCollection.Name, collection);
                Assert.True(IsPostgresFixture(fixture), $"{fixture.Name} has no [Trait(\"Category\", \"Postgres\")]");
                if (((PostgresSqlEngine)engine).AsAppRole)
                {
                    // The app role's twin of a PostgreSQL fixture: PostgresAppRole<the rest of its name>, derived from it.
                    Assert.True(fixture.Name.StartsWith(AppRolePrefix, StringComparison.Ordinal) && fixture.BaseType?.Name == "Postgres" + fixture.Name[AppRolePrefix.Length..]
                        && EngineOf(fixture.BaseType) is PostgresSqlEngine { AsAppRole: false }, $"{fixture.Name} is not named after, and derived from, the PostgreSQL fixture it repeats");
                    continue;
                }
                // Named after the SQLite one; a contract run for a second shape of its data (a flagged operator tenant) may put the
                // shape's name between, as its SQLite fixture does: PostgresFlaggedOperatorSelfServiceFeedStoreTests.
                Assert.True(fixture.Name == "Postgres" + contract[..^"Contract".Length] + "Tests"
                    || fixture.Name.StartsWith("Postgres", StringComparison.Ordinal) && fixture.Name.EndsWith(contract[..^"Contract".Length] + "Tests", StringComparison.Ordinal)
                    && Types.Any(t => !t.IsAbstract && t.Name == fixture.Name["Postgres".Length..] && type.IsAssignableFrom(t) && !IsPostgresFixture(t)),
                    $"{fixture.Name} is not named after a SQLite fixture of {contract}");
                Assert.All(type.GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(m => m.GetCustomAttributes<FactAttribute>().Any()),
                    m => Assert.NotNull(m.GetCustomAttribute<EngineFactAttribute>()));
                continue;
            }
            // Every SQLite fixture builds databases, so it runs alone (see SqlitePoolsCollection), on the SQLite engine.
            Assert.Equal("sqlite-pools", collection);
            Assert.IsType<SqliteStoreEngine>(engine);
            Assert.False(IsPostgresFixture(fixture));
        }
    }

    /// <summary>
    /// Every contract runs on PostgreSQL as well as SQLite, through a fixture named after its SQLite one
    /// (Postgres&lt;stem&gt;Tests) on the PostgreSQL engine, with the Postgres trait; the rest of its shape is checked above. This replaces
    /// the list of contracts allowed a PostgreSQL fixture: a contract added without one fails here.
    /// </summary>
    [Theory]   // mutation: a contract's PostgreSQL fixture removed
    [MemberData(nameof(Contracts))]
    public void Every_contract_has_a_postgres_fixture(string contract)
    {
        var type = Types.Single(t => t.Name == contract);
        var name = "Postgres" + contract[..^"Contract".Length] + "Tests";
        var fixture = Types.SingleOrDefault(t => !t.IsAbstract && t.Name == name && type.IsAssignableFrom(t));
        Assert.True(fixture is not null, $"{contract} has no PostgreSQL fixture {name}");
        Assert.True(IsPostgresFixture(fixture!), $"{name} has no [Trait(\"Category\", \"Postgres\")]");
        Assert.IsAssignableFrom<PostgresSqlEngine>(EngineOf(fixture!));
    }

    /// <summary>The name an app-role twin of a PostgreSQL fixture starts with, in place of "Postgres".</summary>
    internal const string AppRolePrefix = "PostgresAppRole";

    /// <summary>
    /// The whole PostgreSQL contract suite runs again as the app role: every PostgreSQL fixture of every contract (the
    /// owner's, on the superuser's databases) has a twin PostgresAppRole&lt;the rest of its name&gt;, derived from it, on the app-role
    /// engine, so its every test, the contract's and its own, runs signed in as the role <c>db migrate --grant-to</c> granted to.
    /// </summary>
    [Theory]   // mutation: a twin removed, or its engine the owner's
    [MemberData(nameof(Contracts))]
    public void Every_postgres_fixture_has_an_app_role_twin(string contract)
    {
        var type = Types.Single(t => t.Name == contract);
        var postgres = Types.Where(t => !t.IsAbstract && type.IsAssignableFrom(t) && EngineOf(t) is PostgresSqlEngine { AsAppRole: false }).ToList();
        Assert.NotEmpty(postgres);
        foreach (var fixture in postgres)
        {
            var twin = Types.SingleOrDefault(t => t.Name == AppRolePrefix + fixture.Name["Postgres".Length..]);
            Assert.True(twin is not null, $"{fixture.Name} has no app-role twin {AppRolePrefix}{fixture.Name["Postgres".Length..]}");
            Assert.Equal(fixture, twin!.BaseType);
            Assert.True(EngineOf(twin) is PostgresSqlEngine { AsAppRole: true }, $"{twin.Name} is not on the app-role engine");
            Assert.True(IsPostgresFixture(twin), $"{twin.Name} has no [Trait(\"Category\", \"Postgres\")]");
        }
    }

    [Fact]
    public void There_is_a_contract_for_every_store_suite()
    {
        string[] expected =
        [
            "AdminActionLogContract", "AuditContract", "AuditRetentionStoreContract", "AutoJoinStoreContract", "CookieKeyStoreContract", "CustomerDirectoryContract",
            "DecisionRecordQueryContract", "DecisionRecordStoreContract", "DeveloperCountingContract", "FeedCheckStoreContract", "FeedSetupScratchContract", "FormNonceStoreContract", "JobLeaseContract",
            "MasterKeyCheckContract", "MemberTokenContract", "OrganisationStoreContract", "RateCounterContract", "SchemaRulesContract", "SelfServiceFeedStoreContract", "SharedAppIdentityContract",
            "TenantPurgeStoreContract",
            "TenantStoreContract", "TokenContract", "WaiverStoreContract",
        ];
        Assert.Equal(expected, ContractNames());
    }

    /// <summary>
    /// A contract names no SQLite type and sends no SQL only SQLite understands, so a second engine can run it unchanged. The words are
    /// matched in the code (comments, which may name the SQLite adapter a mutation was made in, are left out): a contract that needs one of
    /// them belongs in the SQLite fixture.
    /// </summary>
    [Fact]
    public void No_contract_reaches_past_the_engine()
    {
        // Read from the source tree: a run with no contract files found would check nothing, so it fails instead.
        var files = Directory.Exists(ContractsDir())
            ? Directory.GetFiles(ContractsDir(), "*Contract.cs").Where(f => System.IO.Path.GetFileName(f) != "StoreContract.cs").ToList()
            : [];
        Assert.NotEmpty(files);
        Assert.Equal(ContractNames().Count, files.Count);
        foreach (var file in files)
        {
            var offences = Offences(Code(File.ReadAllLines(file)));
            Assert.True(offences.Count == 0, $"{System.IO.Path.GetFileName(file)} uses {string.Join(", ", offences.Select(o => $"\"{o}\""))}: move the test to the SQLite fixture");
        }

        // The base classes' helpers (AddTenantAsync and the rest) run in every contract, so they are held to the same rule. The rest of
        // StoreContract.cs is the SQLite engine and this check, which name SQLite by design.
        var lines = File.ReadAllLines(System.IO.Path.Combine(ContractsDir(), "StoreContract.cs"));
        foreach (var declaration in new[] { "public abstract class StoreContract : SqlContract<IStoreEngine>", "public abstract class SqlContract<TEngine> where TEngine : ISqlEngine" })
        {
            var start = Array.IndexOf(lines, declaration);
            var end = Array.IndexOf(lines, "}", start);
            Assert.True(start >= 0 && end > start, $"The class body of \"{declaration}\" was not found");
            var helpers = Offences(Code(lines[start..end]));
            Assert.True(helpers.Count == 0, $"\"{declaration}\"'s helpers use {string.Join(", ", helpers.Select(o => $"\"{o}\""))}: reach the database through Engine");
        }
    }

    /// <summary>Source lines as code: whole-line and trailing comments left out.</summary>
    private static string Code(IEnumerable<string> lines) =>
        string.Join('\n', lines.Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)).Select(l => TrailingComment.Replace(l, "")));

    /// <summary>The check above on single lines: each of these reaches past the engine and must be caught.</summary>
    [Theory]
    [InlineData("var db = t.Db;")]
    [InlineData("var x = new Thing { Db = t.Db };")]
    [InlineData("await using var c = await s.T.Db.OpenAsync();")]
    [InlineData("Helper(t.Db, sql);")]
    [InlineData("var file = t.Path;")]
    [InlineData("Directory.GetFiles(t.Dir);")]
    [InlineData("await Assert.ThrowsAsync<SqliteException>(() => write());")]
    [InlineData("new SqliteFeedCheckStore(store)")]
    [InlineData("SELECT name FROM sqlite_master")]
    [InlineData("SELECT group_concat(id, ',') FROM tenants")]
    [InlineData("SELECT julianday('now')")]
    [InlineData("PRAGMA integrity_check")]
    [InlineData("var bytes = await t.RawBytesAsync();")]
    [InlineData("await t.Db.MigrateAsync([]);")]
    [InlineData("await t.Tenants.GetAsync(\"acme\", default);")]
    [InlineData("await s.T.Tenants.SetActiveAsync(\"selfco\", false, default);")]
    [InlineData("var issued = await t.Tokens.IssueAsync(\"acme\", \"ci\", default);")]
    [InlineData("var cached = new CachedTenantAuthenticator(t.Tokens, t.Clock);")]
    [InlineData("var commands = new AdminCommands(t.Tenants, t.Tokens, t.Clock);")]
    [InlineData("await t.AddTenantAsync(\"globex\");")]
    [InlineData("await s.T.AddTenantAsync();")]
    [InlineData("await t.AddCoreTenantAsync();")]
    [InlineData("await t.AddLegacyTenantAsync();")]
    [InlineData("var stores = t.Stores;")]
    [InlineData("await t.Stores.Tenants(t.Clock, TestDb.Lifecycle).GetAsync(\"acme\", default);")]
    [InlineData("await t.Audit.AppendAsync(e, default);")]
    [InlineData("var log = s.T.Audit;")]
    [InlineData("var tokens = t.Tokens;")]
    [InlineData("var s = (TestDb)t;")]
    [InlineData("var s = t as TestDb;")]
    [InlineData("if (t is TestDb s) { }")]
    [InlineData("await using var t = await TestDb.CreateAsync();")]
    [InlineData("await ExecAsync(t, \"INSERT OR IGNORE INTO waivers(tenant_id, package) VALUES('acme', 'x')\");")]
    public void The_engine_check_catches(string line) => Assert.NotEmpty(Offences(line));

    /// <summary>And these are ordinary contract code, which it must let through.</summary>
    [Theory]
    [InlineData("await using var t = await CreateDbAsync();")]
    [InlineData("var store = Engine.FeedChecks(t);")]
    [InlineData("await Engine.Tenants(t).GetAsync(\"acme\", default);")]
    [InlineData("var cached = new CachedTenantAuthenticator(Engine.Tokens(t), t.Clock);")]
    [InlineData("await AddTenantAsync(t, \"globex\");")]
    [InlineData("Assert.Equal(\"ann\", r.Tokens.Single().Label);")]
    [InlineData("Assert.Empty(r.Tokens);")]
    [InlineData("foreach (var x in r.Tokens)")]
    [InlineData("await Engine.Audit(t).AppendAsync(e, default);")]
    [InlineData("var p = System.IO.Path.Combine(a, b);")]
    [InlineData("var d = t.Dbx + t.Directory;")]
    [InlineData("await ExecAsync(t, \"DELETE FROM audit_events WHERE request_id='old'\");")]
    public void The_engine_check_lets_through(string line) => Assert.Empty(Offences(line));

    /// <summary>
    /// The words only SQLite (or the SQLite handle) has. Public: the hosted contracts' check reads this list and adds its own
    /// words, so the two lists cannot drift apart.
    /// </summary>
    public static readonly string[] SqliteOnlyWords =
    [
        "Sqlite", "sqlite_", "PRAGMA", "pragma_", "group_concat", "GROUP_CONCAT", "julianday", "datetime(", "DATETIME(", "strftime", "VACUUM",
        "rowid", "ROWID", "AUTOINCREMENT", "RawBytesAsync", "MigrateAsync",
        "INSERT OR",   // SQLite's conflict clause (INSERT OR IGNORE, OR REPLACE): PostgreSQL has ON CONFLICT
        "TestDb",   // the SQLite handle by name: a cast, an "as" or "is", or a database made around the engine
    ];

    /// <summary>
    /// TestDb's SQLite members through any variable, a second line behind the handle's type (<see cref="IEngineDb"/> has none of them):
    /// the connection, the file, its folder (System.IO.Path is not one), its SQLite stores, the SQLite tenant store, audit log and token
    /// service, and the helpers that make a tenant through them. <c>.Tenants</c> and <c>.Audit</c> are properties there, so never called
    /// (<c>Engine.Tenants(t)</c> is). <c>.Tokens</c> is also a result's list (<c>ActiveDevelopers.Tokens</c>), so it counts only where the
    /// token service is used: an "Async" call on it, or handed on with a comma, assigned or ended with a semicolon.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex SqliteMember = new(
        @"\.(?:Db|Dir|Stores)\b|(?<!\bIO)\.Path\b|\.(?:Tenants|Audit)\b(?!\s*\()|\.Tokens\b(?=\s*(?:\.\s*\w*Async\b|[,;]))|\.Add(?:Core|Legacy)?TenantAsync\b");

    private static List<string> Offences(string code) =>
        SqliteOnlyWords.Where(w => code.Contains(w, StringComparison.Ordinal))
            .Concat(SqliteMember.Matches(code).Select(m => m.Value))
            .Distinct().ToList();

    /// <summary>A comment at the end of a line: "//" after a space (a URL's "//" follows a colon).</summary>
    private static readonly System.Text.RegularExpressions.Regex TrailingComment = new(@"\s//\s.*$");

    private static string ContractsDir([System.Runtime.CompilerServices.CallerFilePath] string self = "") => System.IO.Path.GetDirectoryName(self)!;

    /// <summary>
    /// The contract tests marked as resting on triggers (<see cref="StoreContract.Triggers"/>), by Contract.Method, in ordinal order: the
    /// purge's, the audit log's and its retention's, the decision records' late write, and the admin-action prune's
    /// refusal by the 365-day floor (the floor itself is SchemaRulesContract's). No test of the auto-join, customer
    /// directory, member token or feed-check contracts rests on a trigger (their refusals are constraints' and foreign keys'), so none of
    /// theirs is here. A test marked or unmarked changes this list in the same pull request.
    /// </summary>
    public static readonly string[] TriggerTests =
    [
        "AdminActionLogContract.A_prune_the_floor_refuses_throws_and_deletes_nothing",
        "AuditContract.The_audit_table_refuses_updates_and_deletes",
        "AuditRetentionStoreContract.A_batch_is_stored_in_order_and_a_purged_tenants_events_are_dropped",
        "AuditRetentionStoreContract.A_row_89_days_old_cannot_be_deleted_and_one_91_days_old_can",
        "AuditRetentionStoreContract.A_running_purge_still_deletes_the_tenants_young_rows_and_only_those",
        "AuditRetentionStoreContract.No_row_can_be_changed_however_old",
        "AuditRetentionStoreContract.The_floor_is_90_days_to_the_minute_by_the_databases_utc_clock",
        "AuditRetentionStoreContract.With_the_clock_ahead_the_database_refuses_and_keeps_the_young_rows",
        "DecisionRecordStoreContract.A_late_write_for_a_purged_tenant_is_dropped_and_a_live_tenants_is_kept",
        "TenantPurgeStoreContract.A_late_audit_event_or_decision_record_for_a_purged_tenant_is_dropped_and_another_tenants_is_kept",
        "TenantPurgeStoreContract.A_purged_id_is_never_used_again_and_its_record_cannot_be_written_twice",
    ];

    [Fact]
    public void The_tests_that_need_triggers_are_the_pinned_list()
    {
        var marked = Types.Where(IsContract)
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributesData().Any(a => a.AttributeType == typeof(TraitAttribute)
                    && (string?)a.ConstructorArguments[0].Value == StoreContract.Needs && (string?)a.ConstructorArguments[1].Value == StoreContract.Triggers))
                .Select(m => t.Name + "." + m.Name))
            .Order(StringComparer.Ordinal).ToList();
        Assert.Equal(TriggerTests, marked);
    }

    /// <summary>
    /// Every contract's test methods, pinned in Contracts/contract-tests.txt: deleting or renaming one fails here unless the list changes in
    /// the same pull request, so a contract cannot lose a test (and a second engine the check it brings) without anyone seeing it.
    /// </summary>
    [Fact]
    public void The_contract_tests_are_the_pinned_list()
    {
        var actual = Types.Where(IsContract)
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<FactAttribute>().Any())
                .Select(m => t.Name + "." + m.Name))
            .Order(StringComparer.Ordinal).ToList();
        var pinned = File.ReadAllLines(System.IO.Path.Combine(AppContext.BaseDirectory, "Contracts", "contract-tests.txt"))
            .Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        Assert.NotEmpty(pinned);
        Assert.Equal(pinned, actual);
    }

    /// <summary>
    /// <see cref="ISqlEngine.RefusedAsync"/> is what every contract's refusal checks rest on, so it is checked itself, on every fixture's
    /// engine: a write that succeeds must fail it, and a write that is refused must give back the very exception the engine threw. This
    /// theory takes every fixture but the PostgreSQL ones ([Trait("Category", "Postgres")]): theirs need the test server, so they are
    /// checked in <see cref="PostgresStoreContractCoverageTests"/>, which carries the trait. cd.yml runs Category!=Postgres with no server,
    /// so a test here that reached one would fail every deploy.
    /// </summary>
    [Theory]
    [MemberData(nameof(Contracts))]
    public async Task The_engine_refusal_check_fails_on_a_write_that_succeeds_and_returns_the_real_refusal(string contract)
    {
        var type = Types.Single(t => t.Name == contract);
        foreach (var fixture in Types.Where(t => !t.IsAbstract && type.IsAssignableFrom(t)))
        {
            if (IsPostgresFixture(fixture)) continue;
            await AssertRefusalCheckAsync((ISqlEngine)EngineOf(fixture)!);
        }
    }

    /// <summary>The refusal check on one engine: a write that succeeds fails it, and a refused write gives back the engine's own exception.</summary>
    internal static async Task AssertRefusalCheckAsync(ISqlEngine engine)
    {
        await using var t = await engine.CreateAsync();

        // A row of a table every role that runs the stores may write (the app role may only read the version tables), then
        // the same row again, which the primary key refuses.
        var insert = $"INSERT INTO meta(key, value) VALUES('refusal_check', {engine.BlobSql("00")})";
        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => engine.RefusedAsync(() => Task.CompletedTask));
        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => engine.RefusedAsync(() => engine.ExecAsync(t, insert)));
        Assert.Equal(1L, await engine.ScalarAsync(t, "SELECT COUNT(*) FROM meta WHERE key = 'refusal_check'"));

        Exception? thrown = null;
        var refusal = await engine.RefusedAsync(async () =>
        {
            try { await engine.ExecAsync(t, insert); }
            catch (Exception e) { thrown = e; throw; }
        });
        Assert.NotNull(thrown);
        Assert.Same(thrown, refusal);
    }
}

/// <summary>
/// The refusal check (<see cref="StoreContractCoverageTests.AssertRefusalCheckAsync"/>) on every PostgreSQL contract fixture's engine. It needs the test server, so it carries the Postgres trait and collection: tools/postgres-tests.sh and ci.yml run it, and
/// cd.yml's Category!=Postgres leaves it out.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresStoreContractCoverageTests
{
    [PostgresFact]
    public async Task Every_postgres_fixtures_engine_passes_the_refusal_check()
    {
        var types = StoreContractCoverageTests.Types;
        var fixtures = types.Where(t => !t.IsAbstract && StoreContractCoverageTests.IsPostgresFixture(t)
            && types.Any(c => StoreContractCoverageTests.IsContract(c) && c.IsAssignableFrom(t))).ToList();
        Assert.NotEmpty(fixtures);
        foreach (var fixture in fixtures)
            await StoreContractCoverageTests.AssertRefusalCheckAsync((ISqlEngine)StoreContractCoverageTests.EngineOf(fixture)!);
    }
}
