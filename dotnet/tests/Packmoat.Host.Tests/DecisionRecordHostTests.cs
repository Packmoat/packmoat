using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// The per-version decision record through the real proxy: listings and downloads are both recorded, coalesced per version and
/// decision, and a broken record store changes nothing a client sees.
/// </summary>
[Collection("sqlite-pools")]
public class DecisionRecordHostTests
{
    private const string Feed = "/v3/u/org/proj/_packaging/feed/nuget/v3/";

    private sealed class ThrowingDecisionLog : IDecisionRecordLog
    {
        public int Attempts;
        public Task UpsertAsync(IReadOnlyList<DecisionRecord> records, CancellationToken ct) { Interlocked.Increment(ref Attempts); throw new IOException("disk gone"); }
        public Task<int> PruneAsync(DateTimeOffset lastSeenBefore, CancellationToken ct) => throw new IOException("disk gone");
    }

    /// <summary>All of one tenant's rows, through the read port (these tests write far fewer than its maximum).</summary>
    private static async Task<IReadOnlyList<DecisionRecord>> RowsAsync(IDecisionRecordQuery query, string tenant)
    {
        var page = await query.QueryAsync(new DecisionRecordQuery(tenant, Limit: DecisionRecordQuery.MaxLimit), default);
        Assert.False(page.More);
        return page.Rows;
    }

    private static HttpClient Client(SqliteHostFixture f, string token)
    {
        var http = new HttpClient { BaseAddress = new Uri(f.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return http;
    }

    private static async Task<string[]> ListAsync(HttpClient http)
    {
        var response = await http.GetAsync(Feed + "flat2/acme.lib/index.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("versions").EnumerateArray().Select(v => v.GetString()!).ToArray();
    }

    private static Task<HttpResponseMessage> DownloadAsync(HttpClient http, string version) =>
        http.GetAsync($"{Feed}flat2/acme.lib/{version}/acme.lib.{version}.nupkg");

    [Fact]
    public async Task Listings_and_downloads_are_recorded_per_version_with_the_publish_date_and_cooldown_used()
    {
        await using var f = await SqliteHostFixture.StartAsync();
        f.Publish("Acme.Lib", "1.0.0", 100);
        f.Publish("Acme.Lib", "2.0.0", 3);
        var token = await f.Tokens.IssueAsync("acme", "ci", default);
        using var http = Client(f, token.Plaintext);

        Assert.Equal(new[] { "1.0.0" }, await ListAsync(http));
        Assert.Equal(HttpStatusCode.OK, (await DownloadAsync(http, "1.0.0")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DownloadAsync(http, "1.0.0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DownloadAsync(http, "2.0.0")).StatusCode);

        Assert.Equal(2, await f.DecisionRecorder.FlushAsync());
        var rows = (await RowsAsync(new SqliteDecisionRecordQuery(f.Db), "acme")).ToDictionary(r => r.Version);
        Assert.Equal(2, rows.Count);
        var allowed = rows["1.0.0"];
        Assert.Equal(("acme.lib", Decision.Allow, VerdictReason.CooldownElapsed, (int?)14, (DateTimeOffset?)(f.Clock.UtcNow - TimeSpan.FromDays(100)), 3L),
            (allowed.PackageId, allowed.Decision, allowed.Reason, allowed.CooldownDays, allowed.PublishedAt, allowed.SeenCount));   // the listing and two downloads
        var hidden = rows["2.0.0"];
        Assert.Equal((Decision.Hide, VerdictReason.Cooldown, (DateTimeOffset?)(f.Clock.UtcNow - TimeSpan.FromDays(3)), 2L),
            (hidden.Decision, hidden.Reason, hidden.PublishedAt, hidden.SeenCount));                                           // the listing and one download
        Assert.All(rows.Values, r => Assert.Equal((f.Clock.UtcNow, f.Clock.UtcNow), (r.FirstSeenAt, r.LastSeenAt)));

        // The audit log is unchanged by this: it still never holds the allowed version from the listing.
        Assert.DoesNotContain(await f.Audit.QueryAsync("acme", 100, default), e => e.Kind == AuditKind.Listing && e.Decision == Decision.Allow);

        // Once the version is old enough, the new decision is a second row: the history is kept.
        f.Clock.Advance(TimeSpan.FromDays(12));
        Assert.Equal(new[] { "1.0.0", "2.0.0" }, await ListAsync(http));
        await f.DecisionRecorder.FlushAsync();
        var history = (await RowsAsync(new SqliteDecisionRecordQuery(f.Db), "acme")).Where(r => r.Version == "2.0.0").ToList();
        Assert.Equal(new[] { Decision.Allow, Decision.Hide }, history.Select(r => r.Decision).Order());
    }

    [Fact]
    public async Task A_broken_record_store_changes_no_answer_and_the_failure_is_counted()
    {
        var broken = new ThrowingDecisionLog();
        await using var f = await SqliteHostFixture.StartAsync(decisionLog: broken);
        f.Publish("Acme.Lib", "1.0.0", 100);
        f.Publish("Acme.Lib", "2.0.0", 3);
        var token = await f.Tokens.IssueAsync("acme", "ci", default);
        using var http = Client(f, token.Plaintext);

        Assert.Equal(new[] { "1.0.0" }, await ListAsync(http));
        Assert.Equal(HttpStatusCode.OK, (await DownloadAsync(http, "1.0.0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DownloadAsync(http, "2.0.0")).StatusCode);
        await f.DecisionRecorder.FlushAsync();
        Assert.Equal(new[] { "1.0.0" }, await ListAsync(http));
        Assert.Equal(HttpStatusCode.OK, (await DownloadAsync(http, "1.0.0")).StatusCode);

        Assert.Equal(1, broken.Attempts);
        Assert.Equal(1, f.DecisionRecorder.FailedWrites);
    }

    private sealed class PruneLog : IDecisionRecordLog
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<DateTimeOffset> Cutoffs = new();
        public Task UpsertAsync(IReadOnlyList<DecisionRecord> records, CancellationToken ct) => Task.CompletedTask;
        public Task<int> PruneAsync(DateTimeOffset lastSeenBefore, CancellationToken ct) { Cutoffs.Enqueue(lastSeenBefore); return Task.FromResult(3); }
    }

    /// <summary>Blanking feed checks fails, so the test also shows the prune does not depend on the steps before it.</summary>
    private sealed class BrokenFeedChecks : IFeedCheckStore
    {
        public Task<int> BlankPersonalDataAsync(DateTimeOffset checkedBefore, CancellationToken ct) => throw new IOException("disk gone");
        public Task<long> RecordAsync(FeedCheck check, CancellationToken ct) => throw new NotSupportedException();
        public Task<FeedCheck?> LatestAsync(string tenantId, FeedCheckKind kind, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task The_daily_retention_pass_itself_prunes_decision_records_not_seen_for_365_days()
    {
        var clock = new FakeClock();
        var log = new PruneLog();
        using var service = new RetentionService(new BrokenFeedChecks(), clock, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            interval: TimeSpan.FromDays(1), firstPass: TimeSpan.FromMilliseconds(20), decisions: log);
        await service.StartAsync(default);
        for (var i = 0; i < 200 && log.Cutoffs.IsEmpty; i++) await Task.Delay(25);
        await service.StopAsync(default);

        Assert.Equal(clock.UtcNow - TimeSpan.FromDays(365), Assert.Single(log.Cutoffs));   // one pass, by itself, with the 365-day cutoff
    }

    [Fact]
    public async Task The_production_host_records_decisions_in_the_database_and_the_retention_pass_prunes_after_365_days()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-decisions-").FullName;
        try
        {
            var clock = new FakeClock();
            await using var app = PackmoatApp.Build(new[] { "--urls", "http://127.0.0.1:0" }, TestModules.Server, s => s.AddSingleton<IClock>(clock), o =>
            {
                o.Database = Path.Combine(dir, "packmoat.db");
                o.MasterKeyBase64 = AesGcmSecretProtector.GenerateKeyBase64();
                o.AllowedHosts = "feeds.example.test";
            });
            var recorder = Assert.IsType<RecordingPackageGate>(app.Services.GetRequiredService<IPackageGate>());
            Assert.Same(recorder, app.Services.GetRequiredService<IPackageGate>());
            await app.Services.GetRequiredService<SqliteTenantStore>().CreateAsync(
                new NewTenant("acme", "Acme", "http://127.0.0.1:1/", "org/", "http://127.0.0.1:1/index.json", 0, TenantOrigin.Operator), default);
            var tenant = (await app.Services.GetRequiredService<ITenantStore>().GetAsync("acme", default))!;

            await recorder.DecideAsync(tenant, "Old.Lib", "1.0.0", default);       // zero cooldown: allowed with no date lookup
            Assert.Equal(1, await recorder.FlushAsync());
            clock.Advance(TimeSpan.FromDays(DecisionRecordPolicy.RetentionDays) + TimeSpan.FromSeconds(1));
            await recorder.DecideAsync(tenant, "New.Lib", "1.0.0", default);
            await recorder.FlushAsync();

            var retention = app.Services.GetServices<IHostedService>().OfType<RetentionService>().Single();
            Assert.Equal(1, await retention.PruneDecisionsAsync(default));
            var left = await RowsAsync(app.Services.GetRequiredService<IDecisionRecordQuery>(), "acme");
            Assert.Equal("new.lib", Assert.Single(left).PackageId);
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }
}
