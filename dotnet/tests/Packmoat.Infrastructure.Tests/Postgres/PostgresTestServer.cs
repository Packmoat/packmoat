namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// The PostgreSQL server the [PostgresFact] and [PostgresTheory] tests run against: PACKMOAT_TEST_POSTGRES, a connection string.
/// Unset outside CI, they skip with a reason naming the variable, so a plain dotnet test never needs Docker. Set, they run, and a server
/// that cannot be reached fails them: they never skip then. In CI (CI=true) unset is a failure too: CI must run them. Run them locally with
/// dotnet/tools/postgres-tests.sh, which starts the same image CI uses.
/// </summary>
public static class PostgresTestServer
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

    /// <summary>
    /// The time zone every test session runs in: Pacific/Chatham, +12:45 or +13:45, never UTC, so a
    /// time that depended on the session's zone would read back wrong in every test, not only in one that set the zone itself.
    /// </summary>
    public const string SessionTimeZone = "Pacific/Chatham";

    /// <summary>
    /// The connection string to test against: the variable's, with <c>-c TimeZone=</c><see cref="SessionTimeZone"/> added to its Options, so
    /// every session the tests open (the stores', the engine's, the migrator's) starts in that zone, and a pooled one is reset to it.
    /// Throws when unset, which in CI fails the test instead of skipping it.
    /// </summary>
    public static string ConnectionString => InSessionTimeZone(Value ?? throw new InvalidOperationException(
        $"{Variable} is not set, but CI=true: in CI the PostgreSQL tests must run, never skip. Give this job the postgres service and the variable."));

    /// <summary><paramref name="connectionString"/> with <c>-c TimeZone=</c><see cref="SessionTimeZone"/> after any Options it has (the last one wins).</summary>
    internal static string InSessionTimeZone(string connectionString)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        builder.Options = $"{builder.Options} -c TimeZone={SessionTimeZone}".TrimStart();
        return builder.ConnectionString;
    }
}

/// <summary>A test that needs a PostgreSQL server (see <see cref="PostgresTestServer"/>). Its class carries [Trait("Category", "Postgres")].</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute() => Skip = PostgresTestServer.SkipReason(PostgresTestServer.Value, Environment.GetEnvironmentVariable("CI"));
}

/// <summary>
/// A test against a real PgBouncer in transaction mode in front of the test server: PACKMOAT_TEST_PGBOUNCER, a connection string
/// that signs in through it as the test server's own login. Outside CI it skips while that, or <see cref="PostgresTestServer.Variable"/>,
/// is unset (run it with dotnet/tools/pgbouncer-tests.sh). In CI (CI=true) it always runs (the test jobs have a pgbouncer
/// service), and unset fails it, as for <see cref="PostgresFactAttribute"/>. Its class carries [Trait("Category", "Postgres")].
/// </summary>
public sealed class PgBouncerFactAttribute : FactAttribute
{
    public const string Variable = "PACKMOAT_TEST_PGBOUNCER";

    /// <summary>The variable's value; in CI, unset throws, failing the test instead of skipping it.</summary>
    public static string Value => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } v ? v : throw new InvalidOperationException(
        $"{Variable} is not set, but CI=true: in CI the PgBouncer test must run, never skip. Give this job the pgbouncer service and the variable.");

    internal static string? SkipReason(string? value, string? postgres, string? ci) =>
        PostgresTestServer.IsCi(ci) || (!string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(postgres)) ? null
            : $"PgBouncer test skipped: set {Variable} (and {PostgresTestServer.Variable}), or run dotnet/tools/pgbouncer-tests.sh.";

    public PgBouncerFactAttribute() =>
        Skip = SkipReason(Environment.GetEnvironmentVariable(Variable), PostgresTestServer.Value, Environment.GetEnvironmentVariable("CI"));
}

/// <summary>A theory that needs a PostgreSQL server (see <see cref="PostgresTestServer"/>). Its class carries [Trait("Category", "Postgres")].</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute() => Skip = PostgresTestServer.SkipReason(PostgresTestServer.Value, Environment.GetEnvironmentVariable("CI"));
}
