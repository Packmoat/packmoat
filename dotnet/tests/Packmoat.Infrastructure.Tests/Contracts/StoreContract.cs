using System.Data.Common;
using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Packmoat.Application;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// One database engine as the store contracts see it (#232 S1): a fresh database at the current schema, each store's adapter as its port,
/// and the few raw operations a contract test needs where a port has no way in (setting up or reading back a row). The SQL a contract passes
/// must mean the same on every engine: plain SELECT, INSERT, UPDATE and DELETE on the shared schema, no engine functions, no catalog tables.
/// </summary>
public interface IStoreEngine
{
    /// <summary>A fresh database at the current schema, with the engine's stores.</summary>
    Task<TestDb> CreateAsync();

    /// <summary>Runs one statement.</summary>
    Task ExecAsync(TestDb t, string sql);

    /// <summary>The first column of the first row, or null for no row (<see cref="DBNull"/> for a NULL value); integers read as long.</summary>
    Task<object?> ScalarAsync(TestDb t, string sql);

    /// <summary>The first column of every row, as invariant text ("NULL" for a NULL value).</summary>
    Task<List<string>> ColumnAsync(TestDb t, string sql);

    /// <summary>The engine's refusal of a write (a constraint, a trigger, a foreign key): asserts <paramref name="write"/> throws it.</summary>
    Task<DbException> RefusedAsync(Func<Task> write);

    /// <summary>The engine's SQL for a blob column as hex text, for comparing rows as text.</summary>
    string HexSql(string column);

    IFeedCheckStore FeedChecks(TestDb t);
    IDecisionRecordLog DecisionRecords(TestDb t);
    IDecisionRecordQuery DecisionQuery(TestDb t);
    ISharedAppCredentialStore SharedApp(TestDb t);
    IAutoJoinSettingStore AutoJoin(TestDb t);
    ISignInDirectoryStore SignInDirectory(TestDb t);
    IMemberStore Members(TestDb t);
    IAdminActionLog AdminActions(TestDb t);
    ISelfServiceFeedStore SelfServiceFeeds(TestDb t);
    IAuditLog Audit(TestDb t);
    IAuditBatchLog AuditBatch(TestDb t);
    IAuditRetention AuditRetention(TestDb t);
    IAuditQuery AuditQuery(TestDb t);
    TokenActivityPorts TokenActivity(TestDb t);
    IJobLease JobLease(TestDb t);
    ICookieKeyStore CookieKeys(TestDb t);
}

/// <summary>The SQLite engine: <see cref="TestDb"/> and the <c>Sqlite*</c> adapters.</summary>
public sealed class SqliteStoreEngine : IStoreEngine
{
    public static SqliteStoreEngine Instance { get; } = new();

    public Task<TestDb> CreateAsync() => TestDb.CreateAsync();

    public async Task ExecAsync(TestDb t, string sql)
    {
        await using var c = await t.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<object?> ScalarAsync(TestDb t, string sql)
    {
        await using var c = await t.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    public async Task<List<string>> ColumnAsync(TestDb t, string sql)
    {
        await using var c = await t.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var rows = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add(r.IsDBNull(0) ? "NULL" : Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture)!);
        return rows;
    }

    public async Task<DbException> RefusedAsync(Func<Task> write) => await Assert.ThrowsAsync<SqliteException>(write);

    public string HexSql(string column) => $"hex({column})";

    public IFeedCheckStore FeedChecks(TestDb t) => new SqliteFeedCheckStore(t.Db);
    public IDecisionRecordLog DecisionRecords(TestDb t) => new SqliteDecisionRecordLog(t.Db);
    public IDecisionRecordQuery DecisionQuery(TestDb t) => new SqliteDecisionRecordQuery(t.Db);
    public ISharedAppCredentialStore SharedApp(TestDb t) => new SqliteSharedAppCredentialStore(t.Db, t.Clock, t.Protector);
    public IAutoJoinSettingStore AutoJoin(TestDb t) => new SqliteAutoJoinSettingStore(t.Db);
    public ISignInDirectoryStore SignInDirectory(TestDb t) => new SqliteSignInDirectoryStore(t.Db);
    public IMemberStore Members(TestDb t) => new SqliteMemberStore(t.Db);
    public IAdminActionLog AdminActions(TestDb t) => new SqliteAdminActionLog(t.Db);
    public ISelfServiceFeedStore SelfServiceFeeds(TestDb t) => new SqliteSelfServiceFeedStore(t.Db);
    public IAuditLog Audit(TestDb t) => new SqliteAuditLog(t.Db);
    public IAuditBatchLog AuditBatch(TestDb t) => new SqliteAuditLog(t.Db);
    public IAuditRetention AuditRetention(TestDb t) => new SqliteAuditLog(t.Db);
    public IAuditQuery AuditQuery(TestDb t) => new SqliteAuditLog(t.Db);
    public IJobLease JobLease(TestDb t) => new SqliteJobLease(t.Db, t.Clock);
    public ICookieKeyStore CookieKeys(TestDb t) => new SqliteCookieKeyStore(t.Db, t.Clock, t.Protector);
    public TokenActivityPorts TokenActivity(TestDb t)
    {
        var activity = new SqliteTokenActivity(t.Db, t.Clock);
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

/// <summary>
/// The base of every store contract (#232 S1). A contract is an abstract class holding the tests every adapter of a store's port must pass.
/// It reaches the database only through <see cref="Engine"/>: never through an engine's own types (<c>t.Db</c>, a <c>Sqlite*</c> constructor,
/// <c>SqliteException</c>), the database file, or SQL only one engine understands. Each contract has one concrete subclass per engine: today
/// the SQLite one (<c>TenantStoreTests</c> for <c>TenantStoreContract</c>, and so on), which also keeps the tests that only make sense on
/// SQLite (migrations from older versions, triggers, catalog tables, bytes on disk). A Postgres adapter adds an engine and a second subclass
/// of each contract, and runs the same tests.
/// </summary>
public abstract class StoreContract
{
    protected abstract IStoreEngine Engine { get; }

    protected Task<TestDb> CreateDbAsync() => Engine.CreateAsync();
    protected Task ExecAsync(TestDb t, string sql) => Engine.ExecAsync(t, sql);
    protected Task<object?> ScalarAsync(TestDb t, string sql) => Engine.ScalarAsync(t, sql);
    protected Task<List<string>> ColumnAsync(TestDb t, string sql) => Engine.ColumnAsync(t, sql);
    protected Task<DbException> RefusedAsync(Func<Task> write) => Engine.RefusedAsync(write);
}

/// <summary>
/// The contracts' own guards: every contract runs on SQLite (a contract with no concrete subclass would hold tests that never run), the
/// list of contracts is the one a second engine must cover, and no contract reaches past <see cref="IStoreEngine"/>.
/// </summary>
[Collection("sqlite-pools")]
public class StoreContractCoverageTests
{
    private static readonly Type[] Types = typeof(StoreContract).Assembly.GetTypes();

    private static List<string> ContractNames() =>
        Types.Where(t => t.IsAbstract && t != typeof(StoreContract) && typeof(StoreContract).IsAssignableFrom(t)).Select(t => t.Name).Order(StringComparer.Ordinal).ToList();

    public static TheoryData<string> Contracts() => new(ContractNames());

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
            // Every fixture builds databases, so it runs alone (see SqlitePoolsCollection), on the SQLite engine.
            Assert.Equal("sqlite-pools", fixture.GetCustomAttributesData().Single(a => a.AttributeType == typeof(CollectionAttribute)).ConstructorArguments[0].Value);
            var engine = fixture.GetProperty("Engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Activator.CreateInstance(fixture));
            Assert.IsType<SqliteStoreEngine>(engine);
        }
    }

    [Fact]
    public void There_is_a_contract_for_every_store_suite()
    {
        string[] expected =
        [
            "AdminActionLogContract", "AuditContract", "AuditRetentionStoreContract", "AutoJoinStoreContract", "CookieKeyStoreContract", "CustomerDirectoryContract",
            "DecisionRecordQueryContract", "DecisionRecordStoreContract", "DeveloperCountingContract", "FeedCheckStoreContract", "JobLeaseContract",
            "MemberTokenContract", "SelfServiceFeedStoreContract", "SharedAppIdentityContract", "TenantStoreContract", "TokenContract",
            "WaiverStoreContract",
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
            var code = File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)).Select(l => TrailingComment.Replace(l, ""));
            var offences = Offences(string.Join('\n', code));
            Assert.True(offences.Count == 0, $"{System.IO.Path.GetFileName(file)} uses {string.Join(", ", offences.Select(o => $"\"{o}\""))}: move the test to the SQLite fixture");
        }
    }

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
    public void The_engine_check_catches(string line) => Assert.NotEmpty(Offences(line));

    /// <summary>And these are ordinary contract code, which it must let through.</summary>
    [Theory]
    [InlineData("await using var t = await CreateDbAsync();")]
    [InlineData("var store = Engine.FeedChecks(t);")]
    [InlineData("await t.Tenants.GetAsync(\"acme\", default);")]
    [InlineData("var p = System.IO.Path.Combine(a, b);")]
    [InlineData("var d = t.Dbx + t.Directory;")]
    [InlineData("await ExecAsync(t, \"DELETE FROM audit_events WHERE request_id='old'\");")]
    public void The_engine_check_lets_through(string line) => Assert.Empty(Offences(line));

    private static readonly string[] SqliteOnlyWords =
    [
        "Sqlite", "sqlite_", "PRAGMA", "pragma_", "group_concat", "GROUP_CONCAT", "julianday", "datetime(", "DATETIME(", "strftime", "VACUUM",
        "rowid", "ROWID", "AUTOINCREMENT", "RawBytesAsync", "MigrateAsync",
    ];

    /// <summary>TestDb's SQLite members (the connection, the file, its folder) through any variable; System.IO.Path is not one.</summary>
    private static readonly System.Text.RegularExpressions.Regex SqliteMember = new(@"\.(?:Db|Dir)\b|(?<!\bIO)\.Path\b");

    private static List<string> Offences(string code) =>
        SqliteOnlyWords.Where(w => code.Contains(w, StringComparison.Ordinal))
            .Concat(SqliteMember.Matches(code).Select(m => m.Value))
            .Distinct().ToList();

    /// <summary>A comment at the end of a line: "//" after a space (a URL's "//" follows a colon).</summary>
    private static readonly System.Text.RegularExpressions.Regex TrailingComment = new(@"\s//\s.*$");

    private static string ContractsDir([System.Runtime.CompilerServices.CallerFilePath] string self = "") => System.IO.Path.GetDirectoryName(self)!;

    /// <summary>
    /// Every contract's test methods, pinned in Contracts/contract-tests.txt: deleting or renaming one fails here unless the list changes in
    /// the same pull request, so a contract cannot lose a test (and a second engine the check it brings) without anyone seeing it.
    /// </summary>
    [Fact]
    public void The_contract_tests_are_the_pinned_list()
    {
        var actual = Types.Where(t => t.IsAbstract && t != typeof(StoreContract) && typeof(StoreContract).IsAssignableFrom(t))
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
    /// <see cref="IStoreEngine.RefusedAsync"/> is what every contract's refusal checks rest on, so it is checked itself, on every fixture's
    /// engine: a write that succeeds must fail it, and a write that is refused must give back the very exception the engine threw.
    /// </summary>
    [Theory]
    [MemberData(nameof(Contracts))]
    public async Task The_engine_refusal_check_fails_on_a_write_that_succeeds_and_returns_the_real_refusal(string contract)
    {
        var type = Types.Single(t => t.Name == contract);
        foreach (var fixture in Types.Where(t => !t.IsAbstract && type.IsAssignableFrom(t)))
        {
            var engine = (IStoreEngine)fixture.GetProperty("Engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Activator.CreateInstance(fixture))!;
            await using var t = await engine.CreateAsync();

            await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => engine.RefusedAsync(() => Task.CompletedTask));
            await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => engine.RefusedAsync(() => engine.ExecAsync(t, "UPDATE schema_migrations SET applied_at = applied_at")));

            Exception? thrown = null;
            var refusal = await engine.RefusedAsync(async () =>
            {
                try { await engine.ExecAsync(t, "INSERT INTO schema_migrations(version, applied_at) VALUES(1, 'again')"); }   // version 1 is there
                catch (Exception e) { thrown = e; throw; }
            });
            Assert.NotNull(thrown);
            Assert.Same(thrown, refusal);
        }
    }
}
