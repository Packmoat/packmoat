using System.Reflection;
using Packmoat.Infrastructure.Postgres;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// The PostgreSQL store engine's pending stores (#303 P3, PR 0), with no server: a store's adapter is only made, never used, so a handle
/// over a database that is never connected to is enough. A store member throws if and only if it is on the pinned list.
/// </summary>
public class PostgresStoreEngineTests
{
    /// <summary>
    /// Each store member of <see cref="IStoreEngine"/> and the <see cref="PostgresStores"/> member it is built from. Pinned with the list:
    /// a store added to the engine is added here too (<see cref="Every_store_member_of_the_engine_is_mapped"/>).
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
        ["Members"] = "Members",
        ["Organisations"] = "Organisations",
        ["Purge"] = "PurgeStore",
        ["SelfServiceFeeds"] = "SelfServiceFeeds",
        ["SharedApp"] = "SharedAppCredentials",
        ["SignInDirectory"] = "SignInDirectory",
        ["Tenants"] = "Tenants",
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

    /// <summary>What calling <paramref name="call"/> throws (unwrapped from reflection), or null when it returns.</summary>
    private static Exception? Thrown(Func<object?> call)
    {
        try
        {
            call();
            return null;
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            return e.InnerException;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private static object?[] Arguments(MethodInfo m, PostgresTestDb t) =>
        m.GetParameters().Select(p => p.ParameterType == typeof(IEngineDb) ? t : p.ParameterType == typeof(Lifecycle.TenantLifecycle) ? (object)TestDb.Lifecycle
            : throw new InvalidOperationException($"{m.Name} takes a {p.ParameterType.Name}: give the test one")).ToArray();

    [Fact]
    public void The_pending_list_is_in_ordinal_order_and_names_only_store_members()
    {
        Assert.Equal(PostgresStoreEngine.PendingStores.Distinct().Order(StringComparer.Ordinal), PostgresStoreEngine.PendingStores);
        Assert.All(PostgresStoreEngine.PendingStores, n => Assert.Contains(n, EngineStoreMembers().Select(m => m.Name)));
    }

    [Fact]
    public void Every_store_member_of_the_engine_is_mapped()
    {
        Assert.Equal(EngineStoreMembers().Select(m => m.Name), BuiltFrom.Keys.Order(StringComparer.Ordinal));
        var storesMembers = typeof(PostgresStores).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m is PropertyInfo || m is MethodInfo { IsSpecialName: false }).Select(m => m.Name).Except(NotStores).Order(StringComparer.Ordinal);
        Assert.Equal(BuiltFrom.Values.Distinct().Order(StringComparer.Ordinal), storesMembers);
    }

    // mutation (#303): a pending store's member stops throwing while it is still on the list, or one throws that is off it
    [Fact]
    public async Task A_store_member_of_the_engine_throws_if_and_only_if_it_is_pending()
    {
        await using var t = Unconnected();
        foreach (var m in EngineStoreMembers())
        {
            var thrown = Thrown(() => m.Invoke(PostgresStoreEngine.Instance, Arguments(m, t)));
            if (PostgresStoreEngine.PendingStores.Contains(m.Name))
            {
                var pending = Assert.IsType<NotSupportedException>(thrown);
                Assert.Contains("arrives in #303 P3, PR ", pending.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.True(thrown is null, $"{m.Name} is off the pending list but throws: {thrown}");
            }
        }
    }

    // mutation (#303): a PostgresStores member stops throwing while the engine member built from it is still pending
    [Fact]
    public async Task A_postgres_stores_member_throws_if_and_only_if_an_engine_member_built_from_it_is_pending()
    {
        await using var t = Unconnected();
        var stores = new PostgresStores(t.Db, t.Protector);
        foreach (var name in BuiltFrom.Values.Distinct())
        {
            var member = typeof(PostgresStores).GetMember(name, BindingFlags.Public | BindingFlags.Instance).Single();
            var thrown = Thrown(() => member switch
            {
                PropertyInfo p => p.GetValue(stores),
                MethodInfo m => m.Invoke(stores, m.GetParameters().Select(p => p.ParameterType == typeof(Lifecycle.TenantLifecycle) ? (object?)TestDb.Lifecycle
                    : p.ParameterType == typeof(Domain.IClock) ? t.Clock : p.ParameterType == typeof(Application.IServingGate) ? Application.ServeAllGate.Instance : null).ToArray()),
                _ => throw new InvalidOperationException(name),
            });
            var pending = BuiltFrom.Where(b => b.Value == name).Any(b => PostgresStoreEngine.PendingStores.Contains(b.Key));
            if (pending) Assert.IsType<NotSupportedException>(thrown);
            else Assert.True(thrown is null, $"PostgresStores.{name} is built, but throws: {thrown}");
        }
    }

    [Fact]
    public async Task The_stores_that_are_there_from_the_start_do_not_throw()
    {
        await using var t = Unconnected();
        var stores = new PostgresStores(t.Db, t.Protector);
        Assert.Same(t.Db, stores.Database);
        Assert.Same(t.Db, stores.Migrator);
        Assert.IsType<PostgresMasterKeyCheck>(stores.MasterKeyCheck);
        Assert.IsType<PostgresMasterKeyCheck>(PostgresStoreEngine.Instance.MasterKeyCheck(t, t.Protector));
    }
}
