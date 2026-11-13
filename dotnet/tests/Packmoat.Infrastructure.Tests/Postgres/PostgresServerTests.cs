using Npgsql;
using Packmoat.Infrastructure.Postgres;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// The PostgreSQL tests in one collection, so they never run alongside each other (each later one makes its own database). At its end the
/// template the scratch databases are copied from is dropped (<see cref="PostgresTemplateFixture"/>).
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresTemplateFixture>
{
    public const string Name = "Postgres";
}

/// <summary>
/// Against the server PACKMOAT_TEST_POSTGRES names (see <see cref="PostgresTestServer"/>): the CI guard, a connection and its version
/// check, and a wrong password that never shows itself.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresServerTests
{
    private static PostgresConnectionString TestServer() => PostgresConnectionString.Parse(PostgresTestServer.ConnectionString);

    // A plain [Fact], never skipped. Mutation: the variable unset in a CI job (this fails, and so does every [PostgresFact]).
    [Fact]
    public async Task In_CI_the_postgres_tests_have_a_server_that_answers()
    {
        if (!PostgresTestServer.InCi) return;   // outside CI an unset variable only skips the Postgres tests
        Assert.True(PostgresTestServer.Value is not null,
            $"CI=true but {PostgresTestServer.Variable} is not set: this job runs the tests without the postgres service, so the PostgreSQL tests cannot run.");
        await using var db = PostgresDatabase.Create(TestServer());
        await using var connection = await db.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        Assert.Equal(1, await command.ExecuteScalarAsync());

        // In CI the PostgreSQL tests are set to run, the contract ones too (through the discoverer itself), not to skip.
        Assert.Null(new PostgresFactAttribute().Skip);
        Assert.Null(new PostgresTheoryAttribute().Skip);
        Assert.All(EngineFactTests.ContractTests(), m => Assert.All(EngineFactTests.Discover(typeof(PostgresMasterKeyCheckTests), m), c => Assert.Null(c.SkipReason)));
    }

    [PostgresFact]
    public async Task A_connection_opens_on_the_supported_major_version()
    {
        await using var db = PostgresDatabase.Create(TestServer());
        await using var connection = await db.OpenAsync();
        Assert.Equal(PostgresDatabase.SupportedMajorVersion, connection.PostgreSqlVersion.Major);
        await using var command = new NpgsqlCommand("SHOW server_version_num", connection);
        Assert.StartsWith("17", (string?)await command.ExecuteScalarAsync());
    }

    [PostgresTheory]
    [InlineData("plain")]
    [InlineData("it's quoted")]
    public async Task A_parameter_round_trips(string value)
    {
        await using var db = PostgresDatabase.Create(TestServer());
        await using var connection = await db.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT @v", connection) { Parameters = { new("v", value) } };
        Assert.Equal(value, await command.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task A_wrong_password_fails_without_showing_the_password_or_the_string()
    {
        var sentinel = PostgresConnectionStringTests.Sentinel();
        var whole = new NpgsqlConnectionStringBuilder(PostgresTestServer.ConnectionString) { Password = sentinel, Pooling = false }.ConnectionString;
        await using var db = PostgresDatabase.Create(PostgresConnectionString.Parse(whole));
        var e = await Assert.ThrowsAnyAsync<NpgsqlException>(() => db.OpenAsync());
        Assert.Equal("28P01", Assert.IsType<PostgresException>(e).SqlState);   // the server refused the password, not something else
        PostgresConnectionStringTests.AssertRedacted(e, sentinel, whole);
    }
}
