using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// <see cref="EngineFactDiscoverer"/> (#288 P2, review F3): a contract test on the SQLite fixture always runs, and on a PostgreSQL fixture
/// it skips exactly when a [PostgresFact] would. A discoverer that skipped everything would otherwise let every SQLite contract test
/// skip unseen. No server needed.
/// </summary>
public class EngineFactTests
{
    private const string Set = "Host=127.0.0.1;Port=5432;Database=x;Username=x;Password=x";

    // mutations: SkipFor skips SQLite fixtures too; SkipFor ignores CI; SkipFor ignores the variable
    [Theory]
    [InlineData(typeof(MasterKeyCheckTests), null, null, false)]
    [InlineData(typeof(MasterKeyCheckTests), null, "true", false)]
    [InlineData(typeof(MasterKeyCheckTests), Set, null, false)]
    [InlineData(typeof(MasterKeyCheckTests), "", "false", false)]
    [InlineData(typeof(PostgresMasterKeyCheckTests), null, null, true)]
    [InlineData(typeof(PostgresMasterKeyCheckTests), "", "false", true)]
    [InlineData(typeof(PostgresMasterKeyCheckTests), null, "true", false)]
    [InlineData(typeof(PostgresMasterKeyCheckTests), null, "TRUE", false)]
    [InlineData(typeof(PostgresMasterKeyCheckTests), Set, null, false)]
    [InlineData(typeof(PostgresMasterKeyCheckTests), Set, "true", false)]
    public void A_contract_test_skips_only_on_a_postgres_fixture_with_no_server_outside_CI(Type fixture, string? value, string? ci, bool skips)
    {
        var reason = EngineFactDiscoverer.SkipFor(fixture, value, ci);
        Assert.Equal(skips, reason is not null);
        if (skips) Assert.Equal(PostgresTestServer.SkipReason(value, ci), reason);
    }

    // mutation: Discover returns a skipped case whatever SkipFor says (or never asks it)
    [Fact]
    public void The_discoverer_runs_every_sqlite_contract_test_and_skips_a_postgres_one_as_SkipFor_says()
    {
        foreach (var method in ContractTests())
        {
            Assert.All(Discover(typeof(MasterKeyCheckTests), method), c => Assert.Null(c.SkipReason));
            var expected = EngineFactDiscoverer.SkipFor(typeof(PostgresMasterKeyCheckTests), PostgresTestServer.Value, Environment.GetEnvironmentVariable("CI"));
            Assert.All(Discover(typeof(PostgresMasterKeyCheckTests), method), c => Assert.Equal(expected, c.SkipReason));
        }
    }

    internal static IEnumerable<string> ContractTests() =>
        typeof(MasterKeyCheckContract).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<EngineFactAttribute>() is not null).Select(m => m.Name);

    /// <summary>The test cases xUnit would make of <paramref name="method"/> in <paramref name="fixture"/>, through the discoverer itself.</summary>
    internal static List<IXunitTestCase> Discover(Type fixture, string method)
    {
        var assembly = new TestAssembly(Reflector.Wrap(fixture.Assembly));
        var testClass = new TestClass(new TestCollection(assembly, null, "probe"), Reflector.Wrap(fixture));
        var testMethod = new TestMethod(testClass, Reflector.Wrap(fixture.GetMethod(method)!));
        var fact = testMethod.Method.GetCustomAttributes(typeof(FactAttribute)).Single();
        var found = new EngineFactDiscoverer(new NullMessageSink()).Discover(new NoOptions(), testMethod, fact).ToList();
        Assert.Single(found);
        return found;
    }

    private sealed class NoOptions : ITestFrameworkDiscoveryOptions
    {
        public TValue GetValue<TValue>(string name) => default!;
        public void SetValue<TValue>(string name, TValue value) { }
    }
}
