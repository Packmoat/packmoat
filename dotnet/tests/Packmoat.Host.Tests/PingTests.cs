using System.Net;
using Packmoat.Application;
using static Packmoat.Host.Tests.ProxyFixture;

namespace Packmoat.Host.Tests;

/// <summary>The deploy health check: an authenticated request that touches the token store and nothing upstream.</summary>
[Collection("sqlite-pools")]
public class PingTests
{
    [Fact]
    public async Task A_valid_token_gets_204_and_nothing_upstream_is_touched()
    {
        await using var p = await StartAsync();
        using var http = p.Client(AcmeToken);

        var response = await http.GetAsync("/v3/ping");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, p.Upstream.FeedRequestCount);
        Assert.Equal(0, p.Dates.Calls);
    }

    /// <summary>
    /// The deploy canary as docs/runbooks/cicd.md creates it: no path prefix, a feed that does not exist and no upstream binding, so its
    /// upstream is not set up. Its operator-issued token must keep pinging: the proxy's token path does not ask about the upstream (#63).
    /// </summary>
    [Fact]
    public async Task A_canary_tenant_with_no_upstream_still_gets_204()
    {
        await using var f = await SqliteHostFixture.StartAsync();
        await f.Tenants.CreateAsync(new NewTenant("canary", "Deploy canary", "https://pkgs.dev.azure.com/", "", "https://pkgs.dev.azure.com/none/_packaging/none/nuget/v3/index.json", 14, TenantOrigin.Operator), default);
        Assert.Null(await f.Tenants.GetUpstreamBindingAsync("canary", default));
        var token = await f.Tokens.IssueAsync("canary", "deploy canary", default);
        using var http = new HttpClient { BaseAddress = new Uri(f.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", token.Plaintext);

        Assert.Equal(HttpStatusCode.NoContent, (await http.GetAsync("/v3/ping")).StatusCode);
        Assert.Equal(0, f.Upstream.FeedRequestCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pmt_wrong")]
    public async Task No_token_or_a_bad_token_is_a_401(string? token)
    {
        await using var p = await StartAsync();
        using var http = p.Client(token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/v3/ping")).StatusCode);
    }

    [Fact]
    public async Task A_revoked_token_stops_pinging_once_the_cache_expires()
    {
        await using var f = await SqliteHostFixture.StartAsync();
        var token = await f.Tokens.IssueAsync("acme", "canary", default);
        using var http = new HttpClient { BaseAddress = new Uri(f.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", token.Plaintext);
        Assert.Equal(HttpStatusCode.NoContent, (await http.GetAsync("/v3/ping")).StatusCode);

        await f.Tokens.RevokeAsync(token.TokenId, default);
        f.Clock.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/v3/ping")).StatusCode);
    }

    [Fact]
    public async Task Only_GET_is_accepted_on_the_ping_route_too()
    {
        await using var p = await StartAsync();
        using var http = p.Client(AcmeToken);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.PostAsync("/v3/ping", null)).StatusCode);
    }
}
