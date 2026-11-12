using System.Reflection;
using Packmoat.Application;
using Packmoat.Infrastructure.Postgres;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// The PostgreSQL stores as a whole, with no server: a store's adapter is only made, never used, so a handle over a database that
/// is never connected to is enough. P3 is closed: no store is pending any more, every engine member and every
/// <see cref="PostgresStores"/> member builds its adapter, and every port <see cref="SqliteStores"/> gives, <see cref="PostgresStores"/>
/// gives too, member for member.
/// </summary>
public class PostgresStoreEngineTests
{
    /// <summary>
    /// Each store member of <see cref="IStoreEngine"/> and the <see cref="PostgresStores"/> member it is built from. Pinned: a store added
    /// to the engine is added here too (<see cref="Every_store_member_of_the_engine_is_mapped"/>).
    /// </summary>
    internal static readonly Dictionary<string, string> BuiltFrom = new(StringComparer.Ordinal)
    {
        ["AdminActions"] = "AdminActions",
        ["Audit"] = "Audit",
        ["AuditBatch"] = "Audit",
        ["AuditQuery"] = "Audit",
        ["AuditRetention"] = "Audit",
        ["AutoJoin"] = "AutoJoin",
        ["CookieKeys"] = "CookieKeys",
        ["DecisionQuery"] = "DecisionQuery",
        ["DecisionRecords"] = "Decisions",
        ["FeedChecks"] = "FeedChecks",
        ["JobLease"] = "JobLease",
        ["RateCounter"] = "RateCounter",
        ["FormNonces"] = "FormNonces",
        ["FeedSetupScratch"] = "FeedSetupScratch",
        ["Members"] = "Members",
        ["Organisations"] = "Organisations",
        ["Purge"] = "PurgeStore",
        ["SelfServiceFeeds"] = "SelfServiceFeeds",
        ["SharedApp"] = "SharedAppCredentials",
        ["SignInDirectory"] = "SignInDirectory",
        ["Tenants"] = "Tenants",
        ["TenantsWith"] = "Tenants",
        ["TokenActivity"] = "TokenActivity",
        ["Tokens"] = "TokenService",
    };

    /// <summary>The members of <see cref="PostgresStores"/> that are not stores, and are there from the start.</summary>
    private static readonly string[] NotStores = ["Database", "Migrator", "MasterKeyCheck"];

    /// <summary>The store members <see cref="IStoreEngine"/> declares itself (not its SQL's, nor the master-key check's).</summary>
    private static List<MethodInfo> EngineStoreMembers() =>
        typeof(IStoreEngine).GetMethods().OrderBy(m => m.Name, StringComparer.Ordinal).ToList();

    /// <summary>A handle over a PostgreSQL database that is never connected to: port 1, which nothing serves.</summary>
    private static PostgresTestDb Unconnected() =>
        new(PostgresDatabase.Create(PostgresConnectionString.Parse("Host=127.0.0.1;Port=1;Database=packmoat;Username=packmoat;Password=x")));   // gitleaks:allow

    /// <summary>What calling <paramref name="call"/> returns, unwrapped from reflection.</summary>
    private static object? Called(Func<object?> call)
    {
        try
        {
            return call();
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private static object?[] Arguments(MethodInfo m, PostgresTestDb t) =>
        m.GetParameters().Select(p => p.ParameterType == typeof(IEngineDb) ? t : p.ParameterType == typeof(Lifecycle.TenantLifecycle) ? (object)TestDb.Lifecycle
            : throw new InvalidOperationException($"{m.Name} takes a {p.ParameterType.Name}: give the test one")).ToArray();

    [Fact]
    public void Every_store_member_of_the_engine_is_mapped()
    {
        Assert.Equal(EngineStoreMembers().Select(m => m.Name), BuiltFrom.Keys.Order(StringComparer.Ordinal));
        var storesMembers = typeof(PostgresStores).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m is PropertyInfo || m is MethodInfo { IsSpecialName: false }).Select(m => m.Name).Except(NotStores).Order(StringComparer.Ordinal);
        Assert.Equal(BuiltFrom.Values.Distinct().Order(StringComparer.Ordinal), storesMembers);
    }

    // mutation: a store member throws again (the pending stores' NotSupportedException put back)
    [Fact]
    public async Task Every_store_member_of_the_engine_builds_its_adapter()
    {
        await using var t = Unconnected();
        foreach (var m in EngineStoreMembers())
            Assert.True(Called(() => m.Invoke(PostgresStoreEngine.Instance, Arguments(m, t))) is not null, $"{m.Name} built nothing");
    }

    [Fact]
    public async Task Every_postgres_stores_member_builds_its_adapter()
    {
        await using var t = Unconnected();
        var stores = new PostgresStores(t.Db, t.Protector);
        foreach (var name in BuiltFrom.Values.Distinct())
        {
            var member = typeof(PostgresStores).GetMember(name, BindingFlags.Public | BindingFlags.Instance).Single();
            var built = Called(() => member switch
            {
                PropertyInfo p => p.GetValue(stores),
                MethodInfo m => m.Invoke(stores, m.GetParameters().Select(p => p.ParameterType == typeof(Lifecycle.TenantLifecycle) ? (object?)TestDb.Lifecycle
                    : p.ParameterType == typeof(Domain.IClock) ? t.Clock : p.ParameterType == typeof(IServingGate) ? ServeAllGate.Instance
                    : p.ParameterType == typeof(PostgresTenantStore) ? stores.Tenants(t.Clock, TestDb.Lifecycle)   // the token service's
                    : throw new InvalidOperationException($"{name} takes a {p.ParameterType.Name}: give the test one")).ToArray()),
                _ => throw new InvalidOperationException(name),
            });
            Assert.True(built is not null, $"PostgresStores.{name} built nothing");
        }
    }

    [Fact]
    public async Task The_stores_that_are_there_from_the_start_do_not_throw()
    {
        await using var t = Unconnected();
        var stores = new PostgresStores(t.Db, t.Protector);
        Assert.Same(t.Db, stores.Database);
        Assert.IsType<PostgresServerSchema>(stores.Migrator);   // The server never migrates on PostgreSQL
        Assert.IsType<PostgresMasterKeyCheck>(stores.MasterKeyCheck);
        Assert.IsType<PostgresMasterKeyCheck>(PostgresStoreEngine.Instance.MasterKeyCheck(t, t.Protector));
    }

    [Fact]
    public async Task The_purge_and_organisation_stores_are_built_from_the_lifecycle_as_sqlites_are()
    {
        await using var t = Unconnected();
        var stores = new PostgresStores(t.Db, t.Protector);
        Assert.Null(stores.PurgeStore(Lifecycle.TenantLifecycle.None).Eligibility);
        var recheck = new NeverDue();
        var contributor = new NoTables();
        var purge = stores.PurgeStore(new Lifecycle.TenantLifecycle([], [contributor], () => recheck));
        Assert.Same(recheck, purge.Eligibility);
        Assert.Equal(Lifecycle.CorePurgeTables.Tables.Concat(contributor.Tables), purge.Classification);
        Assert.IsType<PostgresOrganisationStore>(stores.Organisations(Lifecycle.TenantLifecycle.None));
    }

    private sealed class NeverDue : Lifecycle.IPurgeRecheck
    {
        public Task<IReadOnlyList<PurgeCandidate>> ListAsync(DateTimeOffset now, CancellationToken ct) => Task.FromResult<IReadOnlyList<PurgeCandidate>>([]);
        public Task<PurgeCandidate?> AssessAsync(string tenantId, DateTimeOffset now, CancellationToken ct) => Task.FromResult<PurgeCandidate?>(null);
        public Task ConfirmAsync(PurgeCandidate candidate, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> StillDueAsync(System.Data.Common.DbTransaction transaction, PurgeCandidate candidate, DateTimeOffset now, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class NoTables : Lifecycle.IPurgeContributor
    {
        public IReadOnlyList<Lifecycle.PurgeTable> Tables { get; } = [new("module_things", Lifecycle.PurgeTableFate.NoTenantData, "a module's")];
        public IReadOnlyList<string> NotReached => [];
        public Task<IReadOnlyList<PurgedTable>> DeleteAsync(System.Data.Common.DbTransaction transaction, string tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PurgedTable>>([]);
    }

    // ---------------------------------------------------------------- parity with SqliteStores

    /// <summary>A store list's public members: its properties and its ordinary methods, declared on it.</summary>
    private static Dictionary<string, MemberInfo> Members(Type stores) =>
        stores.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m is PropertyInfo || m is MethodInfo { IsSpecialName: false })
            .ToDictionary(m => m.Name, StringComparer.Ordinal);

    /// <summary>The ports a member's type gives: Packmoat's interfaces it is or implements.</summary>
    private static IEnumerable<Type> Ports(Type type) =>
        (type.IsInterface ? [type] : Array.Empty<Type>()).Concat(type.GetInterfaces())
            .Where(i => i.Namespace?.StartsWith("Packmoat.", StringComparison.Ordinal) == true).Distinct();

    /// <summary>A SQLite adapter's PostgreSQL twin by name (SqliteTenantStore: PostgresTenantStore); any other type as it is.</summary>
    private static Type Twin(Type type) =>
        type.Namespace == typeof(SqliteStores).Namespace && type.Name.StartsWith("Sqlite", StringComparison.Ordinal)
            && typeof(PostgresStores).Assembly.GetType("Packmoat.Infrastructure.Postgres.Postgres" + type.Name["Sqlite".Length..]) is { } twin ? twin : type;

    /// <summary>
    /// Where <paramref name="postgres"/> falls short of <paramref name="sqlite"/>: a member <paramref name="sqlite"/> has that it has not,
    /// or has as another kind (a property for a method); a port the SQLite member's type gives that the PostgreSQL member's does not; or a
    /// method that takes other arguments (a SQLite adapter counting as its PostgreSQL twin). Empty when every port is given.
    /// </summary>
    internal static List<string> PortGaps(Type sqlite, Type postgres)
    {
        var gaps = new List<string>();
        var theirs = Members(postgres);
        foreach (var (name, member) in Members(sqlite).OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (!theirs.TryGetValue(name, out var twin))
            {
                gaps.Add($"{name}: missing");
                continue;
            }
            if (member.MemberType != twin.MemberType)
            {
                gaps.Add($"{name}: a {member.MemberType} on SQLite, a {twin.MemberType} here");
                continue;
            }
            var (given, giving) = member is PropertyInfo p ? (p.PropertyType, ((PropertyInfo)twin).PropertyType) : (((MethodInfo)member).ReturnType, ((MethodInfo)twin).ReturnType);
            foreach (var port in Ports(given).Except(Ports(giving)).OrderBy(i => i.Name, StringComparer.Ordinal))
                gaps.Add($"{name}: does not give {port.Name}");
            if (member is MethodInfo method)
            {
                var expected = method.GetParameters().Select(a => Twin(a.ParameterType).Name).ToList();
                var actual = ((MethodInfo)twin).GetParameters().Select(a => a.ParameterType.Name).ToList();
                if (!expected.SequenceEqual(actual))
                    gaps.Add($"{name}: takes ({string.Join(", ", actual)}), not ({string.Join(", ", expected)})");
            }
        }
        return gaps;
    }

    // mutation: a port removed from PostgresStores (a member deleted, or one that gives fewer ports)
    [Fact]
    public void Every_port_sqlite_stores_gives_postgres_stores_gives_too()
    {
        Assert.NotEmpty(Members(typeof(SqliteStores)));
        Assert.Empty(PortGaps(typeof(SqliteStores), typeof(PostgresStores)));
    }

    private sealed class SqliteShape
    {
        public SqliteAuditLog Audit => null!;
        public IAdminActionLog AdminActions => null!;
        public SqliteTokenService TokenService(SqliteTenantStore tenants, Domain.IClock clock) => null!;
        public SqliteJobLease JobLease(Domain.IClock clock) => null!;
        public SqliteCookieKeyStore CookieKeys(Domain.IClock clock) => null!;
        public SqliteMemberStore Members => null!;
    }

    private sealed class PostgresShape
    {
        public IAuditLog Audit => null!;   // gives one of the four audit ports
        public PostgresAdminActionLog AdminActions => null!;   // gives the port, as an adapter: fine
        public PostgresTokenService TokenService(PostgresTenantStore tenants, Domain.IClock clock) => null!;   // the twin: fine
        public PostgresJobLease JobLease() => null!;   // takes no clock
        public PostgresCookieKeyStore CookieKeys => null!;   // a property for a method
    }

    [Fact]
    public void The_parity_check_sees_a_missing_member_a_missing_port_another_kind_and_other_arguments()
    {
        Assert.Equal(
            [
                "Audit: does not give IAuditBatchLog",
                "Audit: does not give IAuditQuery",
                "Audit: does not give IAuditRetention",
                "CookieKeys: a Method on SQLite, a Property here",
                "JobLease: takes (), not (IClock)",
                "Members: missing",
            ],
            PortGaps(typeof(SqliteShape), typeof(PostgresShape)));
    }
}
