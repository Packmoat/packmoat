using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host;

namespace Packmoat.Host.Tests;

/// <summary>Developer counting through the real host: the activity write happens on use, apart from the audit log, and never fails a request.</summary>
public class DeveloperCountingHostTests
{
    private sealed class ThrowingActivityLog : ITokenActivityLog
    {
        public int Attempts;
        public ValueTask RecordAsync(string tokenId, DateOnly day, CancellationToken ct) { Interlocked.Increment(ref Attempts); throw new IOException("disk gone"); }
    }

    private static async Task<HttpStatusCode> GetIndexAsync(SqliteHostFixture f, string token)
    {
        using var http = new HttpClient { BaseAddress = new Uri(f.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return (await http.GetAsync("/v3/index.json")).StatusCode;
    }

    [Fact]
    public async Task Use_is_counted_even_when_nothing_is_audited()
    {
        await using var f = await SqliteHostFixture.StartAsync();
        f.Publish("Acme.Lib", "2.0.0", 3);   // too young: both restores stop at the listing, which is not audited
        var ann = await f.Tokens.IssueAsync("acme", "ann", default);
        var bob = await f.Tokens.IssueAsync("acme", "bob", default);

        Assert.False((await Restore.RunAsync(f.SourceUrl, ann.Plaintext, "Acme.Lib", "2.0.0")).Succeeded);
        Assert.False((await Restore.RunAsync(f.SourceUrl, bob.Plaintext, "Acme.Lib", "2.0.0")).Succeeded);

        Assert.Empty(await f.Audit.QueryAsync("acme", 100, default));   // no audit row at all...
        await f.Recorder.DrainAsync(TimeSpan.FromSeconds(5));
        var active = await f.Activity.GetAsync("acme", 30, default);
        Assert.Equal(2, active.Count);   // ...and both developers still count
        Assert.Equal(new[] { "ann", "bob" }, active.Tokens.Select(t => t.Label).OrderBy(x => x));
    }

    [Fact]
    public async Task A_cached_hit_still_records_the_new_day_and_each_day_is_written_once()
    {
        await using var f = await SqliteHostFixture.StartAsync();
        var token = await f.Tokens.IssueAsync("acme", "ann", default);

        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, await GetIndexAsync(f, token.Plaintext));
        f.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(HttpStatusCode.OK, await GetIndexAsync(f, token.Plaintext));

        await f.Recorder.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, await CountRowsAsync(f));
        var one = await f.Activity.GetAsync("acme", 1, default);
        Assert.Single(one.Tokens);
        Assert.Equal(DateOnly.FromDateTime(f.Clock.UtcNow.UtcDateTime), one.Tokens[0].LastActiveDay);
    }

    [Fact]
    public async Task A_failing_activity_write_never_fails_the_request()
    {
        var broken = new ThrowingActivityLog();
        await using var f = await SqliteHostFixture.StartAsync(activityLog: broken);
        var token = await f.Tokens.IssueAsync("acme", "ann", default);

        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, await GetIndexAsync(f, token.Plaintext));

        await f.Recorder.DrainAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, await GetIndexAsync(f, token.Plaintext));
        await f.Recorder.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, broken.Attempts);                  // it tried, then backed off instead of hammering a broken store on every request
        Assert.Equal(1, f.Recorder.FailedWrites);          // and the failure is counted, so an undercount is visible
        Assert.Equal(0, await CountRowsAsync(f));
    }

    private static async Task<long> CountRowsAsync(SqliteHostFixture f)
    {
        await using var c = await f.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM token_activity";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task The_default_production_registrations_record_activity_after_an_authenticated_request()
    {
        // No fixture wrapper: PackmoatApp's own wiring. Deleting the activity registration there makes this fail.
        var dir = Directory.CreateTempSubdirectory("packmoat-activity-").FullName;
        try
        {
            await using var app = PackmoatApp.Build(new[] { "--urls", "http://127.0.0.1:0" }, TestModules.Server, null, o =>
            {
                o.Database = Path.Combine(dir, "packmoat.db");
                o.MasterKeyBase64 = Packmoat.Infrastructure.AesGcmSecretProtector.GenerateKeyBase64();
                o.AllowedHosts = "feeds.example.test";
            });
            await app.StartAsync();
            var tenants = app.Services.GetRequiredService<Packmoat.Infrastructure.SqliteTenantStore>();
            var tokens = app.Services.GetRequiredService<Packmoat.Infrastructure.SqliteTokenService>();
            await tenants.CreateAsync(new NewTenant("acme", "Acme", "http://127.0.0.1:1/", "org/", "http://127.0.0.1:1/index.json", 14, TenantOrigin.Operator), default);
            var token = await tokens.IssueAsync("acme", "ann", default);

            var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First().Replace("localhost", "127.0.0.1");
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            http.DefaultRequestHeaders.Authorization = new("Bearer", token.Plaintext);
            Assert.NotEqual(HttpStatusCode.Unauthorized, (await http.GetAsync("/v3/index.json")).StatusCode);   // authenticated (the upstream is unreachable on purpose)

            var recorder = Assert.IsType<ActivityRecordingAuthenticator>(app.Services.GetRequiredService<ITenantAuthenticator>());
            await recorder.DrainAsync(TimeSpan.FromSeconds(10));
            var active = await app.Services.GetRequiredService<Packmoat.Infrastructure.SqliteTokenActivity>().GetAsync("acme", 2, default);
            Assert.Equal(new[] { token.TokenId }, active.Tokens.Select(x => x.TokenId));
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }
}
