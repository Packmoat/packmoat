using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>#117: the daily retention pass prunes admin_actions after 12 months, by itself, and the production host wires it to the database.</summary>
[Collection("sqlite-pools")]
public class AdminActionPruneHostTests
{
    private sealed class PruneActions : IAdminActionLog
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<(DateTimeOffset Before, DateTimeOffset Now)> Calls = new();
        public ValueTask AppendAsync(AdminActionRecord record, CancellationToken ct) => ValueTask.CompletedTask;
        public Task<IReadOnlyList<AdminActionRecord>> LatestAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<AdminActionRecord>>(Array.Empty<AdminActionRecord>());
        public Task<AdminActionRecord?> LatestByAsync(string tenantId, string action, string actorTid, string actorOid, CancellationToken ct) => Task.FromResult<AdminActionRecord?>(null);
        public Task<int> PruneAsync(DateTimeOffset before, DateTimeOffset now, CancellationToken ct) { Calls.Enqueue((before, now)); return Task.FromResult(2); }
    }

    /// <summary>Blanking feed checks fails, so the test also shows the prune does not depend on the steps before it.</summary>
    private sealed class BrokenFeedChecks : IFeedCheckStore
    {
        public Task<int> BlankPersonalDataAsync(DateTimeOffset checkedBefore, CancellationToken ct) => throw new IOException("disk gone");
        public Task<long> RecordAsync(FeedCheck check, CancellationToken ct) => throw new NotSupportedException();
        public Task<FeedCheck?> LatestAsync(string tenantId, FeedCheckKind kind, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class BrokenDecisions : IDecisionRecordLog
    {
        public Task UpsertAsync(IReadOnlyList<DecisionRecord> records, CancellationToken ct) => throw new IOException("disk gone");
        public Task<int> PruneAsync(DateTimeOffset lastSeenBefore, CancellationToken ct) => throw new IOException("disk gone");
    }

    [Fact]   // mutations: the prune not called from the pass; a failing earlier step stops it; the wrong cutoff
    public async Task The_daily_retention_pass_itself_prunes_admin_actions_older_than_12_months()
    {
        var clock = new FakeClock();
        var actions = new PruneActions();
        using var service = new RetentionService(new BrokenFeedChecks(), clock, NullLogger.Instance,
            interval: TimeSpan.FromDays(1), firstPass: TimeSpan.FromMilliseconds(20), decisions: new BrokenDecisions(), actions: actions);
        await service.StartAsync(default);
        for (var i = 0; i < 200 && actions.Calls.IsEmpty; i++) await Task.Delay(25);
        await service.StopAsync(default);

        Assert.Equal((clock.UtcNow - TimeSpan.FromDays(365), clock.UtcNow), Assert.Single(actions.Calls));
    }

    [Fact]
    public async Task With_no_action_log_there_is_nothing_to_prune()
    {
        using var service = new RetentionService(new BrokenFeedChecks(), new FakeClock(), NullLogger.Instance);
        Assert.Equal(0, await service.PruneAdminActionsAsync(default));
    }

    [Fact]   // mutation: the first pass waits a whole interval (FirstPass = Interval)
    public void The_first_pass_comes_minutes_after_start_not_a_day()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-first-pass-").FullName;
        try
        {
            using var app = PackmoatApp.Build(new[] { "--urls", "http://127.0.0.1:0" }, TestModules.Server, null, o =>
            {
                o.DatabasePath = Path.Combine(dir, "packmoat.db");
                o.MasterKeyBase64 = AesGcmSecretProtector.GenerateKeyBase64();
                o.AllowedHosts = "feeds.example.test";
            });
            var retention = app.Services.GetServices<IHostedService>().OfType<RetentionService>().Single();
            Assert.Equal(RetentionService.StartupDelay, retention.FirstPass);
            Assert.True(RetentionService.StartupDelay < RetentionService.Interval);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task The_production_host_prunes_the_databases_admin_actions()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-actions-").FullName;
        try
        {
            var clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };   // the database's floor uses the real time
            await using var app = PackmoatApp.Build(new[] { "--urls", "http://127.0.0.1:0" }, TestModules.Server, s => s.AddSingleton<IClock>(clock), o =>
            {
                o.DatabasePath = Path.Combine(dir, "packmoat.db");
                o.MasterKeyBase64 = AesGcmSecretProtector.GenerateKeyBase64();
                o.AllowedHosts = "feeds.example.test";
            });
            var log = app.Services.GetRequiredService<IAdminActionLog>();
            await log.AppendAsync(new AdminActionRecord(clock.UtcNow.AddDays(-400), "", "", "Ada", "prefix.add", "acme", "old", "admin"), default);
            await log.AppendAsync(new AdminActionRecord(clock.UtcNow.AddDays(-10), "", "", "Ada", "prefix.add", "acme", "recent", "admin"), default);

            var retention = app.Services.GetServices<IHostedService>().OfType<RetentionService>().Single();
            Assert.Equal(1, await retention.PruneAdminActionsAsync(default));

            Assert.Equal(new[] { IAdminActionLog.PruneAction, "prefix.add" }, (await log.LatestAsync(10, default)).Select(r => r.Action));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch { } }
    }
}
