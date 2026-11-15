using Microsoft.AspNetCore.Http;

namespace Packmoat.Host.Tests;

/// <summary>Which Host values count as an IP address, and which requests may skip the host-name filter.</summary>
public class HealthHostTests
{
    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("10.1.2.3:8080")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255:65535")]
    [InlineData("1.2.3.4:1")]
    [InlineData("[::1]")]
    [InlineData("[::1]:8080")]
    [InlineData("[fd00::5]:8080")]
    [InlineData("[::ffff:10.0.0.1]")]
    public void An_ipv4_dotted_quad_or_a_bracketed_ipv6_address_is_an_ip_literal(string host) => Assert.True(HealthHost.IsIpLiteral(host));

    [Theory]
    [InlineData("127.0.0.1.evil.com")]
    [InlineData("127.0.0.1.")]
    [InlineData("[::1]x")]
    [InlineData("[::1")]
    [InlineData("::1")]
    [InlineData("[fe80::1%eth0]")]
    [InlineData("[fe80::1%25eth0]")]
    [InlineData("[fe80::1%1]")]
    [InlineData("0177.0.0.1")]
    [InlineData("2130706433")]
    [InlineData("127.1")]
    [InlineData("0x7f000001")]
    [InlineData("256.1.1.1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("01.2.3.4")]
    [InlineData("1.2.3.4:")]
    [InlineData("1.2.3.4:0")]
    [InlineData("1.2.3.4:65536")]
    [InlineData("1.2.3.4:080")]
    [InlineData("1.2.3.4:99999999999")]
    [InlineData("[::1]:")]
    [InlineData("[::1]:0")]
    [InlineData("[]")]
    [InlineData("[1.2.3.4]")]
    [InlineData("1.2.3.4\n")]
    [InlineData("1.2.3.4 ")]
    [InlineData(" 1.2.3.4")]
    [InlineData("feeds.example.test")]
    [InlineData("localhost")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_else_is_not_an_ip_literal(string? host) => Assert.False(HealthHost.IsIpLiteral(host));

    private static bool Allows(string method, string path, string host, string? pathBase = null, string? query = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (pathBase is not null) context.Request.PathBase = pathBase;
        if (query is not null) context.Request.QueryString = new QueryString(query);
        context.Request.Host = new HostString(host);
        return HealthHost.Allows(context);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/readyz")]
    public void A_get_of_a_health_check_with_an_ip_host_skips_the_filter(string path)
    {
        Assert.True(Allows("GET", path, "10.1.2.3"));
        Assert.True(Allows("GET", path, "10.1.2.3:8080"));
        Assert.True(Allows("GET", path, "[::1]:8080"));
        Assert.True(Allows("GET", path, "10.1.2.3", query: "?x=1"));   // a query string is ignored
    }

    [Theory]
    [InlineData("POST", "/healthz", "10.1.2.3")]
    [InlineData("HEAD", "/healthz", "10.1.2.3")]
    [InlineData("HEAD", "/readyz", "10.1.2.3")]
    [InlineData("GET", "/HEALTHZ", "10.1.2.3")]
    [InlineData("GET", "/Readyz", "10.1.2.3")]
    [InlineData("GET", "/healthz/", "10.1.2.3")]
    [InlineData("GET", "/healthzx", "10.1.2.3")]
    [InlineData("GET", "/healthz/x", "10.1.2.3")]
    [InlineData("GET", "/readyz/", "10.1.2.3")]
    [InlineData("GET", "/v3/index.json", "10.1.2.3")]
    [InlineData("GET", "/", "10.1.2.3")]
    [InlineData("GET", "/healthz", "evil.example")]
    [InlineData("GET", "/healthz", "127.0.0.1.evil.com")]
    [InlineData("GET", "/healthz", "2130706433")]
    [InlineData("GET", "/healthz", "localhost")]
    public void Nothing_else_skips_the_filter(string method, string path, string host) => Assert.False(Allows(method, path, host));

    [Fact]
    public void A_path_base_never_skips_the_filter() => Assert.False(Allows("GET", "/healthz", "10.1.2.3", pathBase: "/x"));
}
