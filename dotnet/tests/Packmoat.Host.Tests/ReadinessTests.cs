using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packmoat.Domain;
using Packmoat.Host.Modules;
using Packmoat.Infrastructure;
using static Packmoat.Host.Tests.ProxyFixture;

namespace Packmoat.Host.Tests;

/// <summary>
/// /readyz (#233): through the whole production server (a database, PACKMOAT_HOSTS, a module with its own migration track), it answers
/// exactly "ready" or "not ready", says nothing of why, logs only the kind and only when the answer changes, keeps an answer for 5 seconds,
/// fails closed, and writes nothing. It is reached without a token and is otherwise filtered as /healthz is.
/// </summary>
[Collection("sqlite-pools")]
public class ReadinessTests
{
    private const string Host = "feeds.example.test";

    /// <summary>A module with one migration track, so a missing module track can be shown.</summary>
    private sealed class TrackModule : IPackmoatModule
    {
        public void ConfigureServices(PackmoatModuleContext context) { }
        public ModuleSite? ConfigureSite(PackmoatModuleContext context, IReadOnlyCollection<string> takenHosts) => null;
        public MigrationTrack? Migrations { get; } = new("probe", [["CREATE TABLE probe_things(id INTEGER PRIMARY KEY)"]]);
    }

    private sealed class Server : IAsyncDisposable
    {
        private readonly TempDir _temp = TempDir.Create("packmoat-readyz-", clearSqlitePools: true);
        public string Dir => _temp.Path;
        public string DbPath => Path.Combine(Dir, "packmoat.db");
        public string Key { get; } = AesGcmSecretProtector.GenerateKeyBase64();
        public FakeClock Clock { get; } = new();
        public CapturingLoggerProvider Logs { get; } = new();
        public WebApplication App { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;

        public static async Task<Server> StartAsync()
        {
            var s = new Server();
            try
            {
                await s.BuildAndStartAsync();
                return s;
            }
            catch
            {
                await s.DisposeAsync();   // the folder, and a server that started (#292)
                throw;
            }
        }

        private async Task BuildAndStartAsync()
        {
            var s = this;
            s.App = PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], [new TrackModule()], services =>
            {
                services.AddSingleton<IClock>(s.Clock);
                services.AddSingleton<ILoggerProvider>(s.Logs);
            }, o =>
            {
                o.DatabasePath = s.DbPath;
                o.MasterKeyBase64 = s.Key;
                o.AllowedHosts = Host;
            });
            await s.App.StartAsync();
            var address = s.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("localhost", "127.0.0.1");
            s.Http = new HttpClient { BaseAddress = new Uri(address) };
        }

        public async Task<(HttpStatusCode Code, string Body)> Get(string path = Readiness.Route, string host = Host, string? token = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Host = host;
            if (token is not null) request.Headers.Authorization = new("Bearer", token);
            using var response = await Http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        /// <summary>A statement on the database, from outside the server (as an operator's mistake would be).</summary>
        public async Task Sql(string sql, Action<SqliteCommand>? bind = null)
        {
            await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            bind?.Invoke(cmd);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<long> Count(string sql)
        {
            await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return (long)(await cmd.ExecuteScalarAsync())!;
        }

        public List<string> ReadinessLines() { lock (Logs.Lines) return Logs.Lines.Where(l => l.StartsWith(Readiness.LogCategory + ":", StringComparison.Ordinal)).ToList(); }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            try
            {
                if (App is not null)
                {
                    try { await App.StopAsync(); }
                    finally { await App.DisposeAsync(); }
                }
            }
            finally { _temp.Dispose(); }
        }
    }

    /// <summary>Past the cache, so the next probe reads the database again.</summary>
    private static void PastTheCache(Server s) => s.Clock.Advance(Readiness.CacheFor + TimeSpan.FromSeconds(1));

    private static void AssertNotReady((HttpStatusCode Code, string Body) answer)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, answer.Code);
        Assert.Equal("not ready", answer.Body);   // exactly: never a reason
    }

    [Fact]
    public async Task A_fresh_database_is_ready_and_says_exactly_ready()
    {
        await using var s = await Server.StartAsync();
        var (code, body) = await s.Get();
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("ready", body);
    }

    [Fact]   // mutation: Readiness skips the key check
    public async Task A_database_under_another_master_key_is_not_ready_and_the_log_names_only_the_kind()
    {
        await using var s = await Server.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await s.Get()).Code);

        var other = AesGcmSecretProtector.FromBase64(AesGcmSecretProtector.GenerateKeyBase64());
        await s.Sql("UPDATE meta SET value=@v WHERE key='key_check'", c => c.Parameters.AddWithValue("@v", other.Protect("packmoat-master-key-check-v1", "key_check")));
        PastTheCache(s);

        AssertNotReady(await s.Get());
        var lines = string.Join("\n", s.ReadinessLines());
        Assert.Contains("not ready (key)", lines);
        Assert.DoesNotContain(s.Key, lines);
        Assert.DoesNotContain(s.Dir, string.Join("\n", s.Logs.Lines.Where(l => l.Contains("eadiness", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task A_removed_migration_row_is_not_ready()
    {
        await using var s = await Server.StartAsync();
        await s.Sql("DELETE FROM schema_migrations WHERE version=3");
        AssertNotReady(await s.Get());
        Assert.Contains("not ready (schema)", string.Join("\n", s.ReadinessLines()));
    }

    [Fact]
    public async Task A_missing_module_track_is_not_ready()
    {
        await using var s = await Server.StartAsync();
        await s.Sql("DROP TABLE probe_schema_migrations");
        AssertNotReady(await s.Get());
        Assert.Contains("not ready (schema)", string.Join("\n", s.ReadinessLines()));
    }

    [Fact]   // mutations: MatchesAsync records a marker as EnsureAsync does (or readiness calls EnsureAsync); the check opens the database read-write
    public async Task The_check_writes_nothing_not_even_a_missing_key_marker()
    {
        await using var s = await Server.StartAsync();
        await s.Sql("DELETE FROM meta WHERE key='key_check'");

        AssertNotReady(await s.Get());
        Assert.Equal(0, await s.Count("SELECT COUNT(*) FROM meta WHERE key='key_check'"));
        Assert.Contains("not ready (key)", string.Join("\n", s.ReadinessLines()));
    }

    [Fact]   // mutation: Readiness puts the exception's message in the answer
    public async Task A_database_that_is_gone_is_not_ready_says_nothing_of_why_and_is_not_made_again()
    {
        await using var s = await Server.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await s.Get()).Code);
        SqliteConnection.ClearAllPools();
        File.Move(s.DbPath, s.DbPath + ".moved");
        PastTheCache(s);

        AssertNotReady(await s.Get());
        Assert.False(File.Exists(s.DbPath));   // read only: the check never creates the file
        var lines = s.ReadinessLines();
        Assert.Contains(lines, l => l.EndsWith("not ready (database)", StringComparison.Ordinal));
        Assert.All(lines, l => Assert.DoesNotContain(s.Dir, l));
        File.Move(s.DbPath + ".moved", s.DbPath);
    }

    [Fact]   // mutations: no cache; a log line on every probe
    public async Task An_answer_is_kept_for_five_seconds_and_logged_only_when_it_changes()
    {
        await using var s = await Server.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await s.Get()).Code);
        await s.Sql("DELETE FROM schema_migrations WHERE version=3");

        s.Clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(HttpStatusCode.OK, (await s.Get()).Code);   // still the kept answer
        s.Clock.Advance(TimeSpan.FromSeconds(2));
        AssertNotReady(await s.Get());
        for (var i = 0; i < 3; i++) { PastTheCache(s); AssertNotReady(await s.Get()); }

        await s.Sql("INSERT INTO schema_migrations(version, applied_at) VALUES(3, '2026-01-01T00:00:00Z')");
        PastTheCache(s);
        Assert.Equal(HttpStatusCode.OK, (await s.Get()).Code);

        Assert.Equal(new[] { "Packmoat.Readiness: Readiness: ready", "Packmoat.Readiness: Readiness: not ready (schema)", "Packmoat.Readiness: Readiness: ready" }, s.ReadinessLines());
    }

    [Fact]   // mutation: TokenAuthMiddleware lets only /healthz through without a token
    public async Task It_needs_no_token_and_is_filtered_by_host_as_healthz_is()
    {
        await using var s = await Server.StartAsync();
        var loopback = s.Http.BaseAddress!.Authority;
        foreach (var path in new[] { "/healthz", Readiness.Route })
        {
            Assert.Equal(HttpStatusCode.OK, (await s.Get(path)).Code);                         // a listed host, no token
            Assert.Equal(HttpStatusCode.OK, (await s.Get(path, loopback)).Code);               // the loopback address
            Assert.Equal(HttpStatusCode.OK, (await s.Get(path, "localhost")).Code);            // what a Kubernetes probe should send
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Get(path, "evil.example")).Code); // AllowedHosts, before anything else
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Get(path, "10.1.2.3:8080")).Code); // a pod's own address, as Kubernetes sends it
            Assert.Equal(HttpStatusCode.OK, (await s.Get(path, token: "pmt_wrong_but_ignored")).Code);
            using var post = new HttpRequestMessage(HttpMethod.Post, path);
            post.Headers.Host = Host;
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await s.Http.SendAsync(post)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get("/v3/index.json")).Code);
    }

    [Fact]   // the limiters are inside TokenAuthMiddleware, which both checks skip
    public async Task A_blocked_address_and_a_token_over_its_ceiling_still_reach_it_as_they_reach_healthz()
    {
        await using var p = await StartAsync(tokenRequestsPerMinute: 1);
        // A token over its ceiling.
        using var acme = p.Client(AcmeToken);
        Assert.Equal(HttpStatusCode.NoContent, (await acme.GetAsync("/v3/ping")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await acme.GetAsync("/v3/ping")).StatusCode);
        // An address blocked for wrong tokens.
        for (var i = 0; i < 21; i++)
        {
            using var wrong = p.Client("pmt_wrong_" + i.ToString("D4") + new string('x', 40));
            await wrong.GetAsync("/v3/index.json");
        }
        using var blocked = p.Client("pmt_wrong_9999" + new string('x', 40));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await blocked.GetAsync("/v3/index.json")).StatusCode);
        using var anonymous = p.Client(null);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await anonymous.GetAsync("/v3/index.json")).StatusCode);

        foreach (var http in new[] { acme, blocked, anonymous })
        {
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/healthz")).StatusCode);
            // This fixture has no database, so the check answers (fail closed) rather than the limiter.
            var ready = await http.GetAsync(Readiness.Route);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal("not ready", await ready.Content.ReadAsStringAsync());
        }
    }

    private static async Task<(HttpStatusCode Code, string[] Headers)> HeaderNames(Server s)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Readiness.Route);
        request.Headers.Host = Host;
        using var response = await s.Http.SendAsync(request);
        return (response.StatusCode, response.Headers.Select(h => h.Key).Concat(response.Content.Headers.Select(h => h.Key))
            .Select(n => n.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]   // mutation: a not-ready answer carries an X-Reason header
    public async Task A_not_ready_answer_has_no_header_a_ready_one_lacks()
    {
        await using var s = await Server.StartAsync();
        var ready = await HeaderNames(s);
        Assert.Equal(HttpStatusCode.OK, ready.Code);
        await s.Sql("DELETE FROM schema_migrations WHERE version=3");
        PastTheCache(s);
        var notReady = await HeaderNames(s);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.Code);
        Assert.Equal(ready.Headers, notReady.Headers);
    }

    [Theory]   // mutation: TokenAuthMiddleware matches /readyz as a plain string prefix
    [InlineData("/readyzx")]
    [InlineData("/readyz-details")]
    [InlineData("/readyzz/x")]
    public async Task Only_the_readyz_path_itself_skips_the_token(string path)
    {
        await using var s = await Server.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get(path)).Code);
    }

    // ---- the check alone

    private static Readiness Check(Func<CancellationToken, Task<IReadOnlyList<TrackVersion>>>? tracks, Func<CancellationToken, Task<bool>>? key, IClock? clock = null) =>
        new(tracks, key, clock ?? new FakeClock(), NullLogger.Instance);

    private static Task<IReadOnlyList<TrackVersion>> Current(CancellationToken _) => Task.FromResult<IReadOnlyList<TrackVersion>>([new("core", 3, 3, 3)]);

    [Fact]
    public async Task It_fails_closed()
    {
        Assert.Equal(Readiness.Failure.None, await Check(Current, _ => Task.FromResult(true)).CheckAsync(default));
        Assert.Equal(Readiness.Failure.Database, await Check(null, _ => Task.FromResult(true)).CheckAsync(default));   // no database
        Assert.Equal(Readiness.Failure.Database, await Check(Current, null).CheckAsync(default));
        Assert.Equal(Readiness.Failure.Database, await Check(_ => throw new InvalidOperationException("boom"), _ => Task.FromResult(true)).CheckAsync(default));
        Assert.Equal(Readiness.Failure.Database, await Check(Current, _ => throw new TimeoutException()).CheckAsync(default));
        Assert.Equal(Readiness.Failure.Schema, await Check(_ => Task.FromResult<IReadOnlyList<TrackVersion>>([]), _ => Task.FromResult(true)).CheckAsync(default));
        Assert.Equal(Readiness.Failure.Key, await Check(Current, _ => Task.FromResult(false)).CheckAsync(default));
    }

    [Theory]
    [InlineData(3, 3, 3, true)]
    [InlineData(3, 3, 2, false)]      // a row in the middle removed
    [InlineData(3, 2, 2, false)]      // behind
    [InlineData(3, 4, 3, false)]      // newer than this build knows
    [InlineData(3, null, 0, false)]   // no table
    public void A_track_is_current_only_with_every_known_version_and_none_newer(int latest, int? newest, int recorded, bool current) =>
        Assert.Equal(current, new TrackVersion("t", latest, newest, recorded).IsCurrent);
}
