using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host;
using Packmoat.Host.Tests;
using Packmoat.Infrastructure.Postgres;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// PostgreSQL switched on, end to end against the test server, as production runs it: the owner role runs
/// <c>packmoat-admin db migrate --grant-to</c> the app role; the command line, signed in as the app role, makes a tenant and issues a token;
/// the server (the core's composition, PACKMOAT_DB_ENGINE=postgres, the database's cookie keys) starts as the app role, its start's checks
/// pass, and it serves a proxy download that writes an audit row, which the command line reads back. Then what the start refuses, and that
/// the command line never migrates a PostgreSQL schema. A module's twin, with a purge, is in that module's tests.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresServerStartTests
{
    private const string Feed = "/v3/u/org/proj/_packaging/feed/nuget/v3/";

    /// <summary>Every version of every package was published long ago, so the 14-day cool-down never hides one.</summary>
    private sealed class OldDates : IPublishDateSource
    {
        public Task<IReadOnlyDictionary<string, DateTimeOffset>?> GetPublishDatesAsync(Ecosystem ecosystem, string packageId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>?>(new Dictionary<string, DateTimeOffset> { ["1.0.0"] = DateTimeOffset.UtcNow.AddDays(-100) });
    }

    private static CliEnvironment Cli(string connectionString, string key) =>
        new(connectionString, () => AesGcmSecretProtector.FromBase64(key), new SystemClock(), "e2e-op") { Engine = DatabaseEngine.Postgres };

    private static async Task<(int Code, string Out, string Err)> RunAsync(CliEnvironment env, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await AdminCli.RunAsync(args, o, e, env, TestCli.Modules);
        return (code, o.ToString(), e.ToString());
    }

    private static void Postgres(PackmoatOptions o, string connectionString, string key)
    {
        o.Database = connectionString;
        o.DatabaseEngine = "postgres";
        o.CookieKeys = "database";
        o.MasterKeyBase64 = key;
        o.AllowedHosts = "feeds.example.test";
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    // The CI end-to-end test. mutations: the server's engine choice left on SQLite (AddSqliteStores over the connection
    // string); RequireAvailable refusing postgres; the CLI's db group not routed to PostgresDbCommands; the audit row never written
    [PostgresFact]
    public async Task The_server_boots_as_the_app_role_and_serves_a_token_a_proxy_download_and_an_audit_row()
    {
        await using var roles = await PostgresTestRoles.CreateAsync();
        await using var t = await PostgresScratchDatabase.CreateAsync(owner: roles.Owner);
        await using var upstream = await FakeUpstream.StartAsync();
        upstream.AddPackage("Acme.Lib", "1.0.0");
        var key = AesGcmSecretProtector.GenerateKeyBase64();
        var owner = Cli(roles.ConnectionString(t, roles.Owner), key);
        var app = Cli(roles.ConnectionString(t, roles.App), key);

        // The owner role migrates and grants; the app role then works the command line.
        var migrate = await RunAsync(owner, "db", "migrate", "--grant-to", roles.App);
        Assert.True(migrate.Code == 0, migrate.Err);
        Assert.Contains($"granted to {roles.App} on schema public", migrate.Out, StringComparison.Ordinal);
        var create = await RunAsync(app, "tenant", "create", "acme", "Acme", "--upstream-base", upstream.BaseUrl, "--path-prefix", "org/", "--feed", upstream.FeedIndexUrl);
        Assert.True(create.Code == 0, create.Err);
        var issue = await RunAsync(app, "token", "issue", "acme", "--label", "ci");
        Assert.True(issue.Code == 0, issue.Err);
        var token = issue.Out.Split('\n').Single(l => l.StartsWith("token: ", StringComparison.Ordinal))["token: ".Length..].Trim();

        await using var server = PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], [], s =>
        {
            s.AddSingleton<IPublishDateSource>(new OldDates());
            s.AddSingleton<IUpstreamCredentialSource>(new StaticBasicCredentialSource(FakeUpstream.User, FakeUpstream.Secret));
        }, o => Postgres(o, roles.ConnectionString(t, roles.App), key));
        Assert.IsType<PostgresDatabase>(server.Services.GetRequiredService<PostgresDatabase>());
        await server.StartAsync();
        try
        {
            var baseUrl = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("localhost", "127.0.0.1").TrimEnd('/');
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/readyz")).StatusCode);
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
            var listing = await http.GetAsync(Feed + "flat2/acme.lib/index.json");
            Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
            Assert.Contains("1.0.0", await listing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            var download = await http.GetAsync(Feed + "flat2/acme.lib/1.0.0/acme.lib.1.0.0.nupkg");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(upstream.NupkgBytes("Acme.Lib", "1.0.0"), await download.Content.ReadAsByteArrayAsync());

            // The download's audit row is written off the request path: the command line, as the app role, reads it back.
            string audit = "";
            for (var i = 0; i < 100 && !audit.Contains("acme.lib", StringComparison.OrdinalIgnoreCase); i++)
            {
                if (i > 0) await Task.Delay(100);
                var tail = await RunAsync(app, "audit", "tail", "--tenant", "acme");
                Assert.True(tail.Code == 0, tail.Err);
                audit = tail.Out;
            }
            Assert.Contains("acme.lib", audit, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1L, await ScalarAsync(roles.ConnectionString(t, roles.Owner), "SELECT COUNT(*) FROM audit_events WHERE tenant_id = 'acme'"));
        }
        finally
        {
            await server.StopAsync();
        }
    }

    /// <summary>
    /// A rolling upgrade: copies of this release run while the next release's owner migrates the database past this build (here, a newer
    /// version recorded and a table added, as an expand-only migration does). A running copy stays ready and keeps serving: it is not
    /// taken out of service before the new copies are up. A new start on that schema is still refused, naming the versions.
    /// </summary>
    [PostgresFact]   // mutations: the PostgreSQL server's readiness not allowed a newer schema (every old copy goes unready at the migration); the start allowing it
    public async Task A_running_server_stays_ready_when_the_next_release_migrates_past_it_and_a_new_start_on_it_is_refused()
    {
        await using var roles = await PostgresTestRoles.CreateAsync();
        await using var t = await PostgresScratchDatabase.CreateAsync(owner: roles.Owner);
        var key = AesGcmSecretProtector.GenerateKeyBase64();
        var ownerConnection = roles.ConnectionString(t, roles.Owner);
        var migrate = await RunAsync(Cli(ownerConnection, key), "db", "migrate", "--grant-to", roles.App);
        Assert.True(migrate.Code == 0, migrate.Err);
        var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };
        await using var server = PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], [], s => s.AddSingleton<IClock>(clock),
            o => Postgres(o, roles.ConnectionString(t, roles.App), key));
        await server.StartAsync();
        try
        {
            var baseUrl = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("localhost", "127.0.0.1").TrimEnd('/');
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/readyz")).StatusCode);

            var next = Packmoat.Infrastructure.CoreSchema.Track.LatestVersion + 1;
            await using (var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ownerConnection) { Pooling = false }.ConnectionString))
            {
                await c.OpenAsync();
                await using var cmd = new NpgsqlCommand($"CREATE TABLE next_release_rows(id integer PRIMARY KEY); INSERT INTO schema_migrations(version, applied_at) VALUES({next}, clock_timestamp())", c);
                await cmd.ExecuteNonQueryAsync();
            }
            clock.Advance(Readiness.CacheFor + TimeSpan.FromSeconds(1));
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/readyz")).StatusCode);
            Assert.Equal("ready", await (await http.GetAsync("/readyz")).Content.ReadAsStringAsync());

            var refused = Assert.Throws<PostgresStartupException>(() =>
            {
                var again = PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], [], null, o => Postgres(o, roles.ConnectionString(t, roles.App), key));
                again.DisposeAsync().AsTask().GetAwaiter().GetResult();
            });
            Assert.Contains($"core: at version {next}, newer than this build's {next - 1}", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    // mutations: the start's checks not called by Build; called but not awaited (the server serving before they finish)
    [PostgresFact]
    public async Task A_refused_check_stops_the_start_before_anything_is_served()
    {
        await using var t = await PostgresScratchDatabase.FromTemplateAsync();
        var superuser = new NpgsqlConnectionStringBuilder(PostgresTestServer.ConnectionString) { Database = t.Name, MaxPoolSize = 10 }.ConnectionString;
        var key = AesGcmSecretProtector.GenerateKeyBase64();
        var ex = Assert.Throws<PostgresStartupException>(() =>
        {
            var app = PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], [], null, o => Postgres(o, superuser, key));
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        });
        Assert.Contains("Refusing to start", ex.Message, StringComparison.Ordinal);
        Assert.Contains("superuser", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new NpgsqlConnectionStringBuilder(superuser).Password!, ex.Message, StringComparison.Ordinal);
        // Nothing was written: the master-key check, which stores its marker on a first start, never ran.
        Assert.Equal(0L, await ScalarAsync(superuser, "SELECT COUNT(*) FROM meta WHERE key = 'key_check'"));
    }

    // PostgresDatabase still implements ISchemaMigrator.MigrateAsync (migrating with no grants), so the command line must
    // check the schema through the stores' own port and never migrate. Signed in as the owner (who could migrate) on an empty database.
    // mutation: AdminCli migrating through the database's own MigrateAsync on PostgreSQL
    [PostgresFact]
    public async Task The_command_line_never_migrates_a_postgres_schema()
    {
        await using var roles = await PostgresTestRoles.CreateAsync();
        await using var t = await PostgresScratchDatabase.CreateAsync(owner: roles.Owner);
        var ownerConnection = roles.ConnectionString(t, roles.Owner);
        var (code, output, error) = await RunAsync(Cli(ownerConnection, AesGcmSecretProtector.GenerateKeyBase64()), "tenant", "list");
        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.StartsWith("error: The PostgreSQL database at ", error, StringComparison.Ordinal);
        Assert.Contains("The database's schema is not this build's", error, StringComparison.Ordinal);
        Assert.Contains("run packmoat-admin db migrate as the owner role first", error, StringComparison.Ordinal);
        Assert.Equal(0L, await ScalarAsync(ownerConnection, "SELECT COUNT(*) FROM pg_catalog.pg_tables WHERE schemaname = 'public'"));
    }

    private static readonly string[] Create = ["tenant", "create", "acme", "Acme", "--upstream-base", "https://pkgs.dev.azure.com/", "--path-prefix", "org/",
        "--feed", "https://pkgs.dev.azure.com/org/p/_packaging/f/nuget/v3/index.json"];

    /// <summary>A database the owner migrated with db migrate --grant-to the app role, as production has it.</summary>
    private static async Task<(PostgresTestRoles Roles, PostgresScratchDatabase Db, string Key)> MigratedAsync()
    {
        var roles = await PostgresTestRoles.CreateAsync();
        try
        {
            var t = await PostgresScratchDatabase.CreateAsync(owner: roles.Owner);
            var key = AesGcmSecretProtector.GenerateKeyBase64();
            var migrate = await RunAsync(Cli(roles.ConnectionString(t, roles.Owner), key), "db", "migrate", "--grant-to", roles.App);
            Assert.True(migrate.Code == 0, migrate.Err);
            return (roles, t, key);
        }
        catch
        {
            await roles.DisposeAsync();
            throw;
        }
    }

    // Signed in as a superuser, the server refuses to start; so does every command line command but
    // the db group, before it writes. mutation: the CLI's start checks not run (only the schema port)
    [PostgresFact]
    public async Task The_command_line_signed_in_as_a_superuser_refuses_and_writes_nothing()
    {
        var (roles, t, key) = await MigratedAsync();
        await using (roles)
        await using (t)
        {
            var superuser = new NpgsqlConnectionStringBuilder(PostgresTestServer.ConnectionString) { Database = t.Name, MaxPoolSize = 10 }.ConnectionString;
            var (code, output, error) = await RunAsync(Cli(superuser, key), Create);
            Assert.Equal(1, code);
            Assert.Equal("", output);
            Assert.StartsWith("error: The PostgreSQL database at ", error, StringComparison.Ordinal);
            // A command's refusal says the command will not run, not that a server will not start.
            Assert.Contains("fails the checks the server makes before it starts. Refusing to run the command:", error, StringComparison.Ordinal);
            Assert.DoesNotContain("Refusing to start", error, StringComparison.Ordinal);
            Assert.Contains("superuser", error, StringComparison.Ordinal);
            Assert.DoesNotContain(new NpgsqlConnectionStringBuilder(superuser).Password!, error, StringComparison.Ordinal);
            Assert.Equal(0L, await ScalarAsync(superuser, "SELECT COUNT(*) FROM tenants"));
            Assert.Equal(0L, await ScalarAsync(superuser, "SELECT COUNT(*) FROM admin_actions"));
        }
    }

    // The app role with session_replication_role = replica set for it on the database (the row
    // triggers off): the server refuses, and so does the command line, before it writes. mutation: the CLI's start checks not run
    [PostgresFact]
    public async Task The_command_line_refuses_a_role_whose_session_replication_role_is_replica_and_writes_nothing()
    {
        var (roles, t, key) = await MigratedAsync();
        await using (roles)
        await using (t)
        {
            await PostgresTestRoles.AdminAsync($"ALTER ROLE \"{roles.App}\" IN DATABASE \"{t.Name}\" SET session_replication_role = replica");
            try
            {
                NpgsqlConnection.ClearAllPools();
                var (code, output, error) = await RunAsync(Cli(roles.ConnectionString(t, roles.App), key), Create);
                Assert.Equal(1, code);
                Assert.Equal("", output);
                Assert.Contains("session_replication_role is replica", error, StringComparison.Ordinal);
                Assert.Equal(0L, await ScalarAsync(roles.ConnectionString(t, roles.Owner), "SELECT COUNT(*) FROM tenants"));
            }
            finally
            {
                await PostgresTestRoles.AdminAsync($"ALTER ROLE \"{roles.App}\" IN DATABASE \"{t.Name}\" RESET session_replication_role");
            }
        }
    }

    // The command line caps its own pool at 5, so a Maximum Pool Size of 1000 (which the server warns about: more than the test
    // server's max_connections) gives the command no pool-size warning. mutations: the cap removed (the command warns); the warning skipped
    // for the server too
    [PostgresFact]
    public async Task The_command_line_caps_its_own_pool_and_skips_the_server_s_pool_size_warning()
    {
        var (roles, t, key) = await MigratedAsync();
        await using (roles)
        await using (t)
        {
            var big = roles.ConnectionString(t, roles.App, maxPoolSize: 1000);
            await using (var asServer = PostgresDatabase.Create(PostgresConnectionString.Parse(big)))
            {
                var tracks = TestCli.Modules.Select(m => m.Migrations).OfType<MigrationTrack>().ToList();
                var facts = await PostgresStartupChecks.GatherAsync(asServer, tracks, new SystemClock());
                Assert.False(facts.PoolCapped);
                Assert.Contains(PostgresStartupChecks.Evaluate(facts, new PostgresStartupOptions()),
                    f => f.Kind == PostgresFindingKind.Warning && f.Message.StartsWith("PACKMOAT_DB's Maximum Pool Size is 1000", StringComparison.Ordinal));
            }
            var (code, output, error) = await RunAsync(Cli(big, key), "tenant", "list");
            Assert.True(code == 0, error);
            Assert.DoesNotContain("Maximum Pool Size", error, StringComparison.Ordinal);
            Assert.Equal("", error);
        }
    }

    // The cap never raises a pool under 2, so the command line still refuses one, in a command's words, and writes nothing.
    // mutations: the refusal skipped with the warning; the pool raised to 2
    [PostgresFact]
    public async Task The_command_line_still_refuses_a_pool_of_one_and_writes_nothing()
    {
        var (roles, t, key) = await MigratedAsync();
        await using (roles)
        await using (t)
        {
            var (code, output, error) = await RunAsync(Cli(roles.ConnectionString(t, roles.App, maxPoolSize: 1), key), Create);
            Assert.Equal(1, code);
            Assert.Equal("", output);
            Assert.StartsWith("error: The PostgreSQL database at ", error, StringComparison.Ordinal);
            Assert.Contains("Refusing to run the command:", error, StringComparison.Ordinal);
            Assert.Contains("PACKMOAT_DB's Maximum Pool Size is 1: Packmoat needs at least 2", error, StringComparison.Ordinal);
            Assert.Equal(0L, await ScalarAsync(roles.ConnectionString(t, roles.Owner), "SELECT COUNT(*) FROM tenants"));
        }
    }

    // A PostgreSQL error (here a role the owner never granted to, so the start checks cannot read the version tables) is
    // the database's own message on one "error:" line and exit 1, never a crash. mutation: the catch narrowed back to SqliteException
    [PostgresFact]
    public async Task A_postgres_error_is_one_error_line_and_exit_1()
    {
        var (roles, t, key) = await MigratedAsync();
        await using (roles)
        await using (t)
        {
            var stranger = $"pm_stranger_{Guid.NewGuid():N}";
            await PostgresTestRoles.AdminAsync($"CREATE ROLE {stranger} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '{roles.Password}'");
            try
            {
                var connection = new NpgsqlConnectionStringBuilder(roles.ConnectionString(t, roles.App)) { Username = stranger }.ConnectionString;
                var (code, output, error) = await RunAsync(Cli(connection, key), "tenant", "list");
                Assert.Equal(1, code);
                Assert.Equal("", output);
                var line = Assert.Single(error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                Assert.StartsWith("error: 42501: permission denied", line, StringComparison.Ordinal);
                Assert.DoesNotContain(roles.Password, error, StringComparison.Ordinal);
            }
            finally
            {
                NpgsqlConnection.ClearAllPools();
                await PostgresTestRoles.AdminAsync($"DROP OWNED BY {stranger}; DROP ROLE IF EXISTS {stranger}");
            }
        }
    }
}
