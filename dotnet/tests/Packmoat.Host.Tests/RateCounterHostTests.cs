using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Admin;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// The shared rate counter in the server: over a database the server counts every signed-in limit there (never in the
/// process), reports each failure, and the daily retention pass deletes the windows that have ended.
/// </summary>
[Collection("sqlite-pools")]
public class RateCounterHostTests : IDisposable
{
    private readonly TempDir _temp = TempDir.Create("packmoat-rates-", clearSqlitePools: true);
    private readonly string _key = AesGcmSecretProtector.GenerateKeyBase64();
    private readonly FakeClock _clock = new();
    private string DbPath => _temp.File("packmoat.db");

    // xunit 2 disposes a test class through IDisposable only (an IAsyncDisposable one is never called).
    public void Dispose() => _temp.Dispose();

    private WebApplication BuildServer() =>
        PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], TestModules.Server, s => s.AddSingleton<IClock>(_clock), o =>
        {
            o.Database = DbPath;
            o.MasterKeyBase64 = _key;
            o.AllowedHosts = "feeds.example.test";
        });

    [Fact]   // mutations: the in-process counter registered over a database (each copy would count alone again); failures not reported
    public async Task Over_a_database_the_server_counts_in_the_database_and_reports_its_failures()
    {
        await using var app = BuildServer();
        var counter = Assert.IsType<ReportingRateCounter>(app.Services.GetRequiredService<IRateCounter>());
        Assert.IsType<SqliteRateCounter>(counter.Inner);
        Assert.Single(app.Services.GetServices<IRateCounter>());
    }

    /// <summary>
    /// the production composition's daily retention pass, as the server runs it (the real service, its lease and
    /// every step), deletes the database's ended windows and keeps a running one.
    /// </summary>
    [Fact]   // mutation: PackmoatApp not handing the retention service the counter (ended windows would pile up)
    public async Task The_production_retention_pass_deletes_the_databases_ended_windows()
    {
        _clock.UtcNow = DateTimeOffset.UtcNow;   // the database's own floors read the real time
        await using var app = BuildServer();
        var counter = app.Services.GetRequiredService<IRateCounter>();
        await counter.HitAsync(RateScopes.AdminWrite, "10.0.0.7", 30, TimeSpan.FromMinutes(1));
        await counter.HitAsync(RateScopes.TokenIssue, "dir/oid", 10, TimeSpan.FromHours(1));
        _clock.Advance(TimeSpan.FromMinutes(5));

        var retention = app.Services.GetServices<IHostedService>().OfType<RetentionService>().Single();
        Assert.True(await retention.PassAsync(default));

        await using var c = await new SqliteDatabase(DbPath).OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT group_concat(scope) FROM rate_counters";
        Assert.Equal(RateScopes.TokenIssue, await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// The retention service's own step, given a counter directly: it deletes ended windows only and logs the count. That the server hands
    /// the service its counter is the test above's.
    /// </summary>
    [Fact]   // mutation: the step pruning a running window, or logging no count
    public async Task The_retention_step_deletes_ended_windows_only_and_logs_the_count()
    {
        var db = new SqliteDatabase(DbPath);
        await db.MigrateAsync(TestTracks.Modules);
        var rates = new SqliteRateCounter(db, _clock);
        await rates.HitAsync(RateScopes.AdminWrite, "10.0.0.7", 30, TimeSpan.FromMinutes(1));
        await rates.HitAsync(RateScopes.TokenIssue, "dir/oid", 10, TimeSpan.FromHours(1));
        _clock.Advance(TimeSpan.FromMinutes(5));
        var log = new JobLeaseHostTests.RecordingLogger();
        var service = new RetentionService(new SqliteFeedCheckStore(db), _clock, log, rates: rates);

        Assert.True(await service.PassAsync(default));

        Assert.Contains((LogLevel.Information, "Retention: 1 ended rate-limit window(s) deleted"), log.Lines);
        await using var c = await db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT scope FROM rate_counters";
        Assert.Equal(RateScopes.TokenIssue, await cmd.ExecuteScalarAsync());
    }

    [Fact]   // the admin site's 429 when the counter cannot be reached: refused, never let through, with the whole window to wait
    public async Task The_admin_write_limiter_fails_closed()
    {
        var limiter = new AdminWriteLimiter(new DownCounter(), max: 30);
        Assert.Equal(60, await limiter.TryAcquireAsync("10.0.0.7", default));
    }

    [Fact]   // mutation: the admin site's default limit changed (30 writes a minute per address, as before)
    public async Task The_admin_write_limiter_defaults_to_30_a_minute_per_address()
    {
        var counter = new RecordingCounter();
        Assert.Null(await new AdminWriteLimiter(counter).TryAcquireAsync("10.0.0.7", default));
        Assert.Equal((RateScopes.AdminWrite, "10.0.0.7", 30, TimeSpan.FromMinutes(1)), Assert.Single(counter.Hits));
    }

    private sealed class DownCounter : IRateCounter
    {
        public Task<RateDecision> HitAsync(string scope, string key, int max, TimeSpan window, CancellationToken ct = default) =>
            throw new IOException("the database is down");
        public Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default) => throw new IOException("the database is down");
    }

    private sealed class RecordingCounter : IRateCounter
    {
        public List<(string Scope, string Key, int Max, TimeSpan Window)> Hits { get; } = new();
        public Task<RateDecision> HitAsync(string scope, string key, int max, TimeSpan window, CancellationToken ct = default)
        {
            Hits.Add((scope, key, max, window));
            return Task.FromResult(RateDecision.Allow);
        }
        public Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default) => Task.FromResult(0);
    }
}
