using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

[Collection("sqlite-pools")]
public class SqliteHostTests
{
    [Fact]
    public async Task A_token_issued_through_the_service_restores_and_the_decisions_are_audited_without_secrets()
    {
        await using var f = await SqliteHostFixture.StartAsync();
        f.Publish("Acme.Lib", "1.0.0", 100);
        f.Publish("Acme.Lib", "2.0.0", 3);
        var token = await f.Tokens.IssueAsync("acme", "ci", default);

        var ok = await Restore.RunAsync(f.SourceUrl, token.Plaintext, "Acme.Lib", "1.0.0");
        var refused = await Restore.RunAsync(f.SourceUrl, token.Plaintext, "Acme.Lib", "2.0.0");

        Assert.True(ok.Succeeded, ok.Output);
        Assert.False(refused.Succeeded);
        // The refused restore stops at the listing, which is not audited (#136): the hidden download is fetched directly.
        using (var http = new HttpClient { BaseAddress = new Uri(f.BaseUrl) })
        {
            http.DefaultRequestHeaders.Authorization = new("Bearer", token.Plaintext);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/v3/u/org/proj/_packaging/feed/nuget/v3/flat2/acme.lib/2.0.0/acme.lib.2.0.0.nupkg")).StatusCode);
        }
        var rows = await f.Audit.QueryAsync("acme", 100, default);
        Assert.Contains(rows, r => r.Kind == AuditKind.Download && r.Decision == Decision.Allow && r.Version == "1.0.0" && r.TokenId == token.TokenId);
        Assert.Contains(rows, r => r.Kind == AuditKind.Download && r.Decision == Decision.Hide && r.Reason == VerdictReason.Cooldown && r.Version == "2.0.0");
        Assert.All(rows, r => Assert.Equal(AuditKind.Download, r.Kind));
        Assert.All(rows, r => { Assert.Equal("acme", r.TenantId); Assert.False(string.IsNullOrEmpty(r.RequestId)); });
        Assert.DoesNotContain(rows, r => r.ToString().Contains(token.Plaintext));
    }

    [Fact]
    public async Task A_revoked_token_is_refused_within_the_cache_window()
    {
        await using var f = await SqliteHostFixture.StartAsync();
        f.Publish("Acme.Lib", "1.0.0", 100);
        var token = await f.Tokens.IssueAsync("acme", "ci", default);
        Assert.True((await Restore.RunAsync(f.SourceUrl, token.Plaintext, "Acme.Lib", "1.0.0")).Succeeded);

        await f.Tokens.RevokeAsync(token.TokenId, default);
        f.Clock.Advance(TimeSpan.FromSeconds(6));
        var after = await Restore.RunAsync(f.SourceUrl, token.Plaintext, "Acme.Lib", "1.0.0");

        Assert.False(after.Succeeded);
        Assert.Contains("401", after.Output);
    }

    [Fact]
    public async Task A_waiver_added_through_the_store_takes_effect_within_seconds_and_is_audited()
    {
        await using var f = await SqliteHostFixture.StartAsync();
        f.Publish("Acme.Lib", "2.0.0", 3);
        var token = await f.Tokens.IssueAsync("acme", "ci", default);
        Assert.False((await Restore.RunAsync(f.SourceUrl, token.Plaintext, "Acme.Lib", "2.0.0")).Succeeded);

        var waiver = await f.Tenants.AddWaiverAsync("acme", "Acme.Lib", "2.0.0", f.Clock.UtcNow.AddDays(1), "needed for hotfix", "olivia", default);
        f.Clock.Advance(TimeSpan.FromSeconds(6));
        var after = await Restore.RunAsync(f.SourceUrl, token.Plaintext, "Acme.Lib", "2.0.0");

        Assert.True(after.Succeeded, after.Output);
        var rows = await f.Audit.QueryAsync("acme", 100, default);
        Assert.Contains(rows, r => r.Reason == VerdictReason.Waiver && r.WaiverId == waiver.Id && r.Decision == Decision.Allow);
    }

    [Fact]
    public async Task A_suspended_tenant_is_cut_off()
    {
        await using var f = await SqliteHostFixture.StartAsync();
        f.Publish("Acme.Lib", "1.0.0", 100);
        var token = await f.Tokens.IssueAsync("acme", "ci", default);
        await f.Tenants.SetActiveAsync("acme", false, default);
        f.Clock.Advance(TimeSpan.FromSeconds(6));

        Assert.False((await Restore.RunAsync(f.SourceUrl, token.Plaintext, "Acme.Lib", "1.0.0")).Succeeded);
        Assert.Equal(0, f.Upstream.FeedRequestCount);
    }

    [Fact]
    public async Task Token_guessing_is_slowed_with_a_429_and_recovers_after_the_window()
    {
        await using var f = await SqliteHostFixture.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(f.BaseUrl) };

        // Distinct wrong tokens: the same wrong token repeated is a stale secret, not guessing, and is not blocked so soon (#124).
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 22; i++)
        {
            http.DefaultRequestHeaders.Authorization = new("Bearer", "pmt_" + i.ToString("D2") + new string('a', 46));
            codes.Add((await http.GetAsync("/v3/index.json")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, codes[0]);
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[^1]);
        Assert.Equal(20, codes.Count(c => c == HttpStatusCode.Unauthorized));

        f.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/v3/index.json")).StatusCode);
        Assert.Equal(0, f.Upstream.FeedRequestCount);
    }

    [Fact]
    public async Task When_the_token_store_fails_the_client_gets_503_not_a_bogus_401()
    {
        await using var f = await SqliteHostFixture.StartAsync();
        var token = await f.Tokens.IssueAsync("acme", "ci", default);
        using var http = new HttpClient { BaseAddress = new Uri(f.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", token.Plaintext);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/v3/index.json")).StatusCode);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(f.DbDir)) File.Delete(file);
        f.Clock.Advance(TimeSpan.FromSeconds(6)); // let the cache expire so the store is consulted
        var response = await http.GetAsync("/v3/index.json");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
