using System.Net;
using static Packmoat.Host.Tests.ProxyFixture;

namespace Packmoat.Host.Tests;

public class HardeningTests
{
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task Only_GET_is_accepted_even_before_authentication(string method)
    {
        await using var p = await StartAsync();
        using var http = p.Client(null);

        var response = await http.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/v3/index.json"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("GET", response.Content.Headers.Allow.Single());
        Assert.Equal(0, p.Upstream.FeedRequestCount);
    }

    [Fact]
    public async Task A_request_with_a_large_body_is_refused()
    {
        await using var p = await StartAsync();
        using var http = p.Client(AcmeToken);
        var request = new HttpRequestMessage(HttpMethod.Get, "/v3/index.json") { Content = new ByteArrayContent(new byte[16 * 1024]) };

        var response = await http.SendAsync(request);

        Assert.True(response.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest, $"got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task The_server_does_not_announce_what_it_is()
    {
        await using var p = await StartAsync();
        using var http = p.Client(AcmeToken);
        var response = await http.GetAsync("/healthz");
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task A_token_over_its_ceiling_gets_429_with_retry_after_and_other_tokens_are_unaffected()
    {
        await using var p = await StartAsync(tokenRequestsPerMinute: 3);
        using var acme = p.Client(AcmeToken);
        using var globex = p.Client(GlobexToken);

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add((await acme.GetAsync("/healthz")).StatusCode == HttpStatusCode.OK ? (await acme.GetAsync("/v3/index.json")).StatusCode : HttpStatusCode.BadGateway);

        var limited = await acme.GetAsync("/v3/index.json");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await globex.GetAsync("/v3/index.json")).StatusCode);

        p.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.OK, (await acme.GetAsync("/v3/index.json")).StatusCode);
    }
}
