using Xunit.Abstractions;
using Xunit.Sdk;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// A contract test: a [Fact] on the SQLite fixture, and on a PostgreSQL fixture ([Trait("Category", "Postgres")]) a
/// [PostgresFact]: skipped, with the same reason, when PACKMOAT_TEST_POSTGRES is unset outside CI. A contract's test is written once, so
/// which it is depends on the fixture class it runs in; <see cref="EngineFactDiscoverer"/> decides per class.
/// </summary>
[XunitTestCaseDiscoverer("Packmoat.Infrastructure.Tests.Postgres.EngineFactDiscoverer", "Packmoat.Infrastructure.Tests")]
[AttributeUsage(AttributeTargets.Method)]
public sealed class EngineFactAttribute : FactAttribute
{
    /// <summary>
    /// Why the test cannot run signed in as the app role, or null when it can. Only for a test whose own arrangement needs
    /// the owner inside the store's own transaction (DDL there): it then skips, with this reason, on the app-role fixtures alone, and still
    /// runs on every other fixture.
    /// </summary>
    public string? NotAsAppRole { get; set; }
}

/// <summary>Finds an <see cref="EngineFactAttribute"/> test as a fact, or as a skipped one on a PostgreSQL fixture with no server.</summary>
public sealed class EngineFactDiscoverer(IMessageSink diagnosticMessageSink) : FactDiscoverer(diagnosticMessageSink)
{
    /// <summary>
    /// Why a contract test in <paramref name="fixture"/> skips, or null when it runs: a SQLite fixture never skips; a PostgreSQL fixture
    /// skips exactly when a [PostgresFact] would (<paramref name="value"/>, PACKMOAT_TEST_POSTGRES, unset and <paramref name="ci"/> not true).
    /// </summary>
    internal static string? SkipFor(Type fixture, string? value, string? ci) =>
        StoreContractCoverageTests.IsPostgresFixture(fixture) ? PostgresTestServer.SkipReason(value, ci) : null;

    public override IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute)
    {
        var fixture = testMethod.TestClass.Class.ToRuntimeType();
        var reason = fixture is null ? null
            : AppRoleSkip(fixture, factAttribute.GetNamedArgument<string?>(nameof(EngineFactAttribute.NotAsAppRole)))
              ?? SkipFor(fixture, PostgresTestServer.Value, Environment.GetEnvironmentVariable("CI"));
        if (reason is not null)
            return [new XunitSkippedDataRowTestCase(DiagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod, reason)];
        return base.Discover(discoveryOptions, testMethod, factAttribute);
    }

    /// <summary>
    /// The skip reason of a test marked <see cref="EngineFactAttribute.NotAsAppRole"/> (<paramref name="why"/>) on an app-role fixture; null on any other fixture, or for any other test.
    /// </summary>
    internal static string? AppRoleSkip(Type fixture, string? why) =>
        why is not null && StoreContractCoverageTests.EngineOf(fixture) is PostgresSqlEngine { AsAppRole: true } ? "Not run as the app role: " + why : null;
}
