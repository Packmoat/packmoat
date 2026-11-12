using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// through the real host: only downloads are audited, the audit write is off the request path (a broken or stuck store changes
/// nothing a client sees), and the daily retention pass prunes audit_events and token_activity. Each test that kills one of the plan's
/// mutations says which.
/// </summary>
[Collection("sqlite-pools")]
public class AuditDownloadsHostTests
{
    private const string Feed = "/v3/u/org/proj/_packaging/feed/nuget/v3/";

    private sealed class ThrowingStore : IAuditBatchLog
    {
        public int Attempts;
        public Task AppendBatchAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct) { Interlocked.Increment(ref Attempts); throw new IOException("disk gone"); }
    }

    private sealed class HangingStore : IAuditBatchLog
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attempts;
        public async Task AppendBatchAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct) { Interlocked.Increment(ref Attempts); await Release.Task; }
    }

    private static HttpClient Client(SqliteHostFixture f, string token)
    {
        var http = new HttpClient { BaseAddress = new Uri(f.BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return http;
    }

    private static Task<HttpResponseMessage> DownloadAsync(HttpClient http, string version) =>
        http.GetAsync($"{Feed}flat2/acme.lib/{version}/acme.lib.{version}.nupkg");

    private static async Task AssertDownloadsAnsweredAsync(SqliteHostFixture f)
    {
        f.Publish("Acme.Lib", "1.0.0", 100);
        f.Publish("Acme.Lib", "2.0.0", 3);
        var token = await f.Tokens.IssueAsync("acme", "ci", default);
        using var http = Client(f, token.Plaintext);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await DownloadAsync(http, "1.0.0")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await DownloadAsync(http, "2.0.0")).StatusCode);
        }
    }

    [Fact]   // mutation: the audit write awaited on the request path again (the store's exception becomes a 502)
    public async Task A_throwing_audit_store_still_serves_an_allowed_download_and_hides_a_hidden_one()
    {
        var store = new ThrowingStore();
        await using var f = await SqliteHostFixture.StartAsync(auditStore: store);

        await AssertDownloadsAnsweredAsync(f);

        Assert.True(await f.AuditQueue.DrainAsync(TimeSpan.FromSeconds(5)));
        Assert.True(store.Attempts >= 1);
        Assert.Equal(6, f.AuditQueue.LostEvents);   // the loss is counted, so the gap is visible
    }

    [Fact]   // mutation: the audit write awaited on the request path again (the request waits for the stuck store)
    public async Task A_hanging_audit_store_still_serves_an_allowed_download_and_hides_a_hidden_one()
    {
        var store = new HangingStore();
        await using var f = await SqliteHostFixture.StartAsync(auditStore: store);
        try
        {
            await AssertDownloadsAnsweredAsync(f);
            Assert.True(store.Attempts >= 1);   // the writer really is stuck in the store
        }
        finally { store.Release.TrySetResult(); }
    }

    [Fact]   // mutation: Listing or Metadata rows written again
    public async Task Only_downloads_are_audited_allowed_and_hidden_never_listings_or_metadata()
    {
        await using var f = await SqliteHostFixture.StartAsync(queuedAudit: true);
        f.Publish("Acme.Lib", "1.0.0", 100);
        f.Publish("Acme.Lib", "2.0.0", 3);
        var token = await f.Tokens.IssueAsync("acme", "ci", default);
        using var http = Client(f, token.Plaintext);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(Feed + "flat2/acme.lib/index.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(Feed + "registrations2/acme.lib/index.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(Feed + "registrations2/acme.lib/1.0.0.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(Feed + "registrations2/acme.lib/2.0.0.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DownloadAsync(http, "1.0.0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DownloadAsync(http, "2.0.0")).StatusCode);

        Assert.True(await f.AuditQueue.DrainAsync(TimeSpan.FromSeconds(5)));
        var rows = (await f.Audit.QueryAsync("acme", 100, default)).OrderBy(r => r.Version).ToList();
        Assert.Equal(new[] { ("1.0.0", Decision.Allow), ("2.0.0", Decision.Hide) }, rows.Select(r => (r.Version, r.Decision)));
        Assert.All(rows, r => Assert.Equal((AuditKind.Download, token.TokenId), (r.Kind, r.TokenId)));
    }

    // ---------------------------------------------------------------- retention

    private sealed class RecordingAuditRetention : IAuditRetention
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<DateTimeOffset> Calls = new();
        public Task<int> PruneAsync(DateTimeOffset before, CancellationToken ct) { Calls.Enqueue(before); return Task.FromResult(3); }
    }

    private sealed class RecordingActivityRetention : ITokenActivityRetention
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<DateOnly> Calls = new();
        public Task<int> PruneAsync(DateOnly keepFrom, CancellationToken ct) { Calls.Enqueue(keepFrom); return Task.FromResult(4); }
    }

    private sealed class BrokenAuditRetention : IAuditRetention
    {
        public Task<int> PruneAsync(DateTimeOffset before, CancellationToken ct) => throw new IOException("disk gone");
    }

    /// <summary>Blanking feed checks fails, so the test also shows the prunes do not depend on the steps before them.</summary>
    private sealed class BrokenFeedChecks : IFeedCheckStore
    {
        public Task<int> BlankPersonalDataAsync(DateTimeOffset checkedBefore, CancellationToken ct) => throw new IOException("disk gone");
        public Task<long> RecordAsync(FeedCheck check, CancellationToken ct) => throw new NotSupportedException();
        public Task<FeedCheck?> LatestAsync(string tenantId, FeedCheckKind kind, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]   // mutations: either prune skipped by the pass; a failing earlier step stops them; the wrong cutoffs
    public async Task The_daily_retention_pass_itself_prunes_audit_events_after_90_days_and_token_activity_after_365()
    {
        var clock = new FakeClock();
        var audit = new RecordingAuditRetention();
        var activity = new RecordingActivityRetention();
        using var service = new RetentionService(new BrokenFeedChecks(), clock, NullLogger.Instance,
            interval: TimeSpan.FromDays(1), firstPass: TimeSpan.FromMilliseconds(20), audit: audit, tokenActivity: activity);
        await service.StartAsync(default);
        for (var i = 0; i < 200 && (audit.Calls.IsEmpty || activity.Calls.IsEmpty); i++) await Task.Delay(25);
        await service.StopAsync(default);

        Assert.Equal(clock.UtcNow - TimeSpan.FromDays(90) - TimeSpan.FromMinutes(1), Assert.Single(audit.Calls));   // a minute's margin
        // 365 days kept, today included: the longest window the active-developer count can ask for.
        Assert.Equal(DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(-364), Assert.Single(activity.Calls));
    }

    [Fact]   // mutation: a failing audit prune stops the token_activity prune
    public async Task A_failing_audit_prune_does_not_stop_the_token_activity_prune()
    {
        var clock = new FakeClock();
        var activity = new RecordingActivityRetention();
        using var service = new RetentionService(new BrokenFeedChecks(), clock, NullLogger.Instance,
            interval: TimeSpan.FromDays(1), firstPass: TimeSpan.FromMilliseconds(20), audit: new BrokenAuditRetention(), tokenActivity: activity);
        await service.StartAsync(default);
        for (var i = 0; i < 200 && activity.Calls.IsEmpty; i++) await Task.Delay(25);
        await service.StopAsync(default);
        Assert.Single(activity.Calls);
    }

    [Fact]
    public async Task With_no_stores_there_is_nothing_to_prune()
    {
        using var service = new RetentionService(new BrokenFeedChecks(), new FakeClock(), NullLogger.Instance);
        Assert.Equal(0, await service.PruneAuditAsync(default));
        Assert.Equal(0, await service.PruneTokenActivityAsync(default));
    }

    private static WebApplication BuildProductionHost(string dir, IClock clock, Action<IServiceCollection>? more = null) =>
        PackmoatApp.Build(new[] { "--urls", "http://127.0.0.1:0" }, TestModules.Server, s => { s.AddSingleton(clock); more?.Invoke(s); }, o =>
        {
            o.Database = Path.Combine(dir, "packmoat.db");
            o.MasterKeyBase64 = AesGcmSecretProtector.GenerateKeyBase64();
            o.AllowedHosts = "feeds.example.test";
        });

    [Fact]   // mutation: the prune margin dropped (the server's clock a little ahead of the database's straddles a row, and the chunk is refused)
    public async Task A_server_clock_slightly_ahead_of_the_database_does_not_block_the_prune()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-audit-margin-").FullName;
        try
        {
            var real = DateTimeOffset.UtcNow;
            var clock = new FakeClock { UtcNow = real.AddSeconds(30) };   // this server's clock runs 30 s ahead of the database's
            await using var app = BuildProductionHost(dir, clock);
            var audit = app.Services.GetRequiredService<SqliteAuditLog>();
            await audit.AppendAsync(new AuditEvent(real.AddDays(-120), "old", "acme", "tok", AuditKind.Download, "moq", "1.0.0", Decision.Allow, VerdictReason.CooldownElapsed, null), default);
            // 10 s younger than the database's floor: the trigger refuses it, so the prune must not select it.
            await audit.AppendAsync(new AuditEvent(real - AuditPolicy.Retention + TimeSpan.FromSeconds(10), "edge", "acme", "tok", AuditKind.Download, "moq", "1.0.0", Decision.Allow, VerdictReason.CooldownElapsed, null), default);

            var retention = app.Services.GetServices<IHostedService>().OfType<RetentionService>().Single();
            Assert.Equal(1, await retention.PruneAuditAsync(default));

            Assert.Equal(new[] { "edge" }, (await audit.QueryAsync("acme", 10, default)).Select(e => e.RequestId));
            Assert.Equal(TimeSpan.FromMinutes(1), RetentionService.AuditPruneMargin);
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }

    [Fact]   // mutation: the queue registered with AddSingleton (it would replace an audit log the caller registered)
    public async Task An_audit_log_registered_by_the_caller_is_not_replaced_by_the_queue()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-audit-override-").FullName;
        try
        {
            var mine = new RecordingAuditLog();
            await using var app = BuildProductionHost(dir, new FakeClock(), s => s.AddSingleton<IAuditLog>(mine));
            Assert.Same(mine, app.Services.GetRequiredService<IAuditLog>());
            Assert.Single(app.Services.GetServices<IAuditLog>());
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }

    [Fact]   // mutation: the production host does not hand the database to the retention pass
    public async Task The_production_host_prunes_the_databases_audit_events_and_token_activity()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-audit-prune-").FullName;
        try
        {
            var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };   // the database's floor uses the real time
            var dbPath = Path.Combine(dir, "packmoat.db");
            await using var app = PackmoatApp.Build(new[] { "--urls", "http://127.0.0.1:0" }, TestModules.Server, s => s.AddSingleton<IClock>(clock), o =>
            {
                o.Database = dbPath;
                o.MasterKeyBase64 = AesGcmSecretProtector.GenerateKeyBase64();
                o.AllowedHosts = "feeds.example.test";
            });
            Assert.IsType<QueuedAuditLog>(app.Services.GetRequiredService<IAuditLog>());   // the proxy's audit log is the queue

            var db = new SqliteDatabase(dbPath);
            var audit = new SqliteAuditLog(db);
            await audit.AppendAsync(new AuditEvent(clock.UtcNow.AddDays(-120), "old", "acme", "tok", AuditKind.Download, "moq", "1.0.0", Decision.Allow, VerdictReason.CooldownElapsed, null), default);
            await audit.AppendAsync(new AuditEvent(clock.UtcNow.AddDays(-10), "recent", "acme", "tok", AuditKind.Download, "moq", "1.0.0", Decision.Allow, VerdictReason.CooldownElapsed, null), default);
            var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
            await using (var c = await db.OpenAsync())
            await using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = $"""
                    INSERT INTO tenants(id, name, upstream_base, allowed_path_prefix, feed_index_url, cooldown_days, created_at) VALUES('acme', 'Acme', 'https://x/', 'o/', 'https://x/i', 14, '2026-01-01');
                    INSERT INTO tokens(id, tenant_id, token_hash, display_prefix, label, created_at) VALUES('tok', 'acme', 'h', 'pm', 'ci', '2026-01-01');
                    INSERT INTO token_activity(token_id, day) VALUES('tok', '{today.AddDays(-400):yyyy-MM-dd}'), ('tok', '{today.AddDays(-364):yyyy-MM-dd}'), ('tok', '{today:yyyy-MM-dd}');
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            var retention = app.Services.GetServices<IHostedService>().OfType<RetentionService>().Single();
            Assert.Equal(1, await retention.PruneAuditAsync(default));
            Assert.Equal(1, await retention.PruneTokenActivityAsync(default));

            Assert.Equal(new[] { "recent" }, (await audit.QueryAsync("acme", 10, default)).Select(e => e.RequestId));
            await using (var c = await db.OpenAsync())
            await using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM token_activity";
                Assert.Equal(2L, await cmd.ExecuteScalarAsync());
            }
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }
}
