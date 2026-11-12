using Npgsql;
using Packmoat.Infrastructure.Postgres;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// Two login roles on the test server, as production has them: an owner (no superuser, no CREATEDB, no CREATEROLE) that owns
/// its databases and migrates them, and an app role that <c>db migrate --grant-to</c> grants to, which the server and the day-to-day
/// command line sign in as. Both are made by the test server's own (superuser) login and dropped when this is disposed: the app role
/// first, then the owner, once no database holds anything of either.
/// </summary>
public sealed class PostgresTestRoles : IAsyncDisposable
{
    public string Owner { get; } = $"pm_owner_{Guid.NewGuid():N}";
    public string App { get; } = $"pm_app_{Guid.NewGuid():N}";

    /// <summary>Both roles' password: a throwaway for this run's roles on a throwaway server.</summary>
    public string Password { get; } = PostgresConnectionStringTests.Sentinel();

    private PostgresTestRoles()
    {
    }

    /// <summary>Makes both roles. A failure drops whichever was made.</summary>
    public static async Task<PostgresTestRoles> CreateAsync()
    {
        var roles = new PostgresTestRoles();
        try
        {
            await AdminAsync($"CREATE ROLE {roles.Owner} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '{roles.Password}'");
            await AdminAsync($"CREATE ROLE {roles.App} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '{roles.Password}'");
            return roles;
        }
        catch
        {
            await roles.DisposeAsync();
            throw;
        }
    }

    /// <summary><paramref name="role"/>'s (one of these two) connection string to <paramref name="t"/>, with a pool the server can take.</summary>
    public string ConnectionString(PostgresScratchDatabase t, string role, int maxPoolSize = 10) =>
        new NpgsqlConnectionStringBuilder(PostgresTestServer.ConnectionString)
        { Database = t.Name, Username = role, Password = Password, MaxPoolSize = maxPoolSize }.ConnectionString;

    /// <summary>A database over <paramref name="t"/>, signed in as <paramref name="role"/>; the caller disposes it.</summary>
    public PostgresDatabase Database(PostgresScratchDatabase t, string role) => PostgresDatabase.Create(PostgresConnectionString.Parse(ConnectionString(t, role)));

    /// <summary>Runs <paramref name="sql"/> as the test server's own login (a superuser), on its own database, unpooled.</summary>
    public static async Task AdminAsync(string sql)
    {
        await using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(PostgresTestServer.ConnectionString) { Pooling = false }.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        try
        {
            await AdminAsync($"DROP ROLE IF EXISTS {App}");
        }
        finally
        {
            await AdminAsync($"DROP ROLE IF EXISTS {Owner}");
        }
    }
}
