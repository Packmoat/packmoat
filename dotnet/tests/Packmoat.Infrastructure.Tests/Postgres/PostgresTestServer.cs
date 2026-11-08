namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// The PostgreSQL server the [PostgresFact] and [PostgresTheory] tests run against (#288): PACKMOAT_TEST_POSTGRES, a connection string.
/// Unset outside CI, they skip with a reason naming the variable, so a plain dotnet test never needs Docker. Set, they run, and a server
/// that cannot be reached fails them: they never skip then. In CI (CI=true) unset is a failure too: CI must run them. Run them locally with
/// dotnet/tools/postgres-tests.sh, which starts the same image CI uses.
/// </summary>
internal static class PostgresTestServer
{
    public const string Variable = "PACKMOAT_TEST_POSTGRES";

    /// <summary>The trait the Postgres-backed tests carry, so tools/postgres-tests.sh can pick them: --filter Category=Postgres.</summary>
    public const string Category = "Postgres";

    /// <summary>The variable's value, or null when unset or empty.</summary>
    public static string? Value => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } v ? v : null;

    /// <summary>True on a CI runner (GitHub Actions sets CI=true).</summary>
    public static bool InCi => IsCi(Environment.GetEnvironmentVariable("CI"));

    internal static bool IsCi(string? ci) => string.Equals(ci, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why a Postgres test skips, or null when it runs: it skips only when <paramref name="value"/> is unset and <paramref name="ci"/> is
    /// not true. In CI it runs and <see cref="ConnectionString"/> fails it.
    /// </summary>
    internal static string? SkipReason(string? value, string? ci) =>
        !string.IsNullOrEmpty(value) || IsCi(ci) ? null
            : $"PostgreSQL test skipped: {Variable} is not set. Set it to a connection string, or run dotnet/tools/postgres-tests.sh.";

    /// <summary>The connection string to test against. Throws when unset, which in CI fails the test instead of skipping it.</summary>
    public static string ConnectionString => Value ?? throw new InvalidOperationException(
        $"{Variable} is not set, but CI=true: in CI the PostgreSQL tests must run, never skip. Give this job the postgres service and the variable.");
}

/// <summary>A test that needs a PostgreSQL server (see <see cref="PostgresTestServer"/>). Its class carries [Trait("Category", "Postgres")].</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute() => Skip = PostgresTestServer.SkipReason(PostgresTestServer.Value, Environment.GetEnvironmentVariable("CI"));
}

/// <summary>A theory that needs a PostgreSQL server (see <see cref="PostgresTestServer"/>). Its class carries [Trait("Category", "Postgres")].</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute() => Skip = PostgresTestServer.SkipReason(PostgresTestServer.Value, Environment.GetEnvironmentVariable("CI"));
}
