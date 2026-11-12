using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;

namespace Packmoat.Host.Tests;

/// <summary>
/// PACKMOAT_TRUSTED_PROXIES: loopback is always trusted; the setting adds addresses or networks. Run through the real
/// forwarded-headers middleware with the options the server builds, so what is tested is what is served.
/// </summary>
public class TrustedProxiesTests
{
    private const string Client = "198.51.100.7";

    /// <summary>The address the rest of the pipeline sees for a request from <paramref name="remote"/> that claims to forward <see cref="Client"/>.</summary>
    private static async Task<string?> SeenAddress(string? setting, string remote)
    {
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(TrustedProxies.Options(setting)));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Headers["X-Forwarded-For"] = Client;
        await middleware.Invoke(context);
        var seen = context.Connection.RemoteIpAddress!;
        return (seen.IsIPv4MappedToIPv6 ? seen.MapToIPv4() : seen).ToString();
    }

    [Theory]   // unset means loopback only, as before
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Unset_trusts_loopback_only(string? setting)
    {
        Assert.Equal(Client, await SeenAddress(setting, "127.0.0.1"));
        Assert.Equal(Client, await SeenAddress(setting, "::1"));
        Assert.Equal("203.0.113.9", await SeenAddress(setting, "203.0.113.9"));
        Assert.Equal("10.0.0.5", await SeenAddress(setting, "10.0.0.5"));
    }

    [Theory]   // the framework's 127.0.0.0/8 default stays: a proxy on 127.0.0.2 is as trusted as one on 127.0.0.1
    [InlineData(null, "127.0.0.2")]
    [InlineData(null, "127.255.0.9")]
    [InlineData("10.0.0.0/8", "127.0.0.2")]
    [InlineData("10.0.0.0/8", "127.255.0.9")]
    [InlineData(null, "::ffff:127.0.0.2")]
    public async Task All_of_127_slash_8_is_trusted_set_or_unset(string? setting, string remote) =>
        Assert.Equal(Client, await SeenAddress(setting, remote));

    /// <summary>The Host the rest of the pipeline sees for a request from <paramref name="remote"/> with Host feeds.example and the given X-Forwarded-Host.</summary>
    private static async Task<string> SeenHost(string? setting, string remote, string forwardedHost, params string[] hosts)
    {
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(TrustedProxies.Options(setting, hosts)));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Host = new HostString("feeds.example");
        context.Request.Headers["X-Forwarded-For"] = Client;
        context.Request.Headers["X-Forwarded-Host"] = forwardedHost;
        await middleware.Invoke(context);
        return context.Request.Host.Value!;
    }

    [Fact]   // mutation: leave AllowedHosts empty when the setting is set
    public async Task With_the_setting_a_forwarded_host_outside_the_server_s_host_names_is_not_applied()
    {
        Assert.Equal("feeds.example", await SeenHost("10.0.0.0/8", "10.0.0.5", "evil.example", "feeds.example", "app.example"));
        Assert.Equal("feeds.example", await SeenHost("10.0.0.0/8", "127.0.0.1", "evil.example", "feeds.example", "app.example"));
        Assert.Equal("app.example", await SeenHost("10.0.0.0/8", "10.0.0.5", "app.example", "feeds.example", "app.example"));
        Assert.Equal("APP.example:443", await SeenHost("10.0.0.0/8", "10.0.0.5", "APP.example:443", "feeds.example", "app.example"));
        Assert.Equal("feeds.example", await SeenHost("10.0.0.0/8", "10.0.0.5", "evil.example"));   // no host names: none is applied
    }

    [Fact]   // unset: exactly as before (loopback's X-Forwarded-Host is believed; a stranger's is not)
    public async Task Unset_a_forwarded_host_from_loopback_is_applied_as_before()
    {
        Assert.Equal("evil.example", await SeenHost(null, "127.0.0.1", "evil.example", "feeds.example"));
        Assert.Equal("feeds.example", await SeenHost(null, "203.0.113.9", "evil.example", "feeds.example"));
        Assert.Empty(TrustedProxies.Options(null, ["feeds.example"]).AllowedHosts);
    }

    [Fact]
    public async Task An_untrusted_address_keeps_its_own_address()
    {
        Assert.Equal("203.0.113.9", await SeenAddress("10.0.0.0/8", "203.0.113.9"));
        Assert.Equal("203.0.113.9", await SeenAddress("10.0.0.0/8 192.168.1.4", "203.0.113.9"));
    }

    [Theory]   // mutation: add the networks' base addresses to KnownProxies instead of KnownIPNetworks
    [InlineData("10.0.0.0/8", "10.0.0.5")]
    [InlineData("192.168.1.4", "192.168.1.4")]
    [InlineData("192.168.1.4, 10.0.0.0/8", "10.200.1.1")]
    [InlineData("fd00::/8", "fd12::5")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    public async Task A_trusted_address_is_rewritten(string setting, string remote) =>
        Assert.Equal(Client, await SeenAddress(setting, remote));

    [Fact]   // a dual-stack socket reports an IPv4 client as ::ffff:a.b.c.d
    public async Task An_ipv4_mapped_ipv6_address_behaves_as_its_ipv4_address()
    {
        Assert.Equal(Client, await SeenAddress("10.0.0.0/8", "::ffff:10.0.0.5"));
        Assert.Equal("203.0.113.9", await SeenAddress("10.0.0.0/8", "::ffff:203.0.113.9"));
        Assert.Equal(Client, await SeenAddress(null, "::ffff:127.0.0.1"));
    }

    [Fact]
    public void Loopback_stays_trusted_and_only_the_nearest_proxy_is_believed()
    {
        var options = TrustedProxies.Options("10.0.0.0/8", ["feeds.example"]);
        Assert.Contains(IPAddress.Loopback, options.KnownProxies);
        Assert.Contains(IPAddress.IPv6Loopback, options.KnownProxies);
        Assert.Equal(1, options.ForwardLimit);
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost, options.ForwardedHeaders);
    }

    [Fact]
    public void Entries_may_be_separated_by_spaces_commas_or_semicolons()
    {
        var networks = TrustedProxies.Parse(" 10.0.0.0/8,192.168.1.4;  fd00::/8 ");
        Assert.Equal(["10.0.0.0/8", "192.168.1.4/32", "fd00::/8"], networks.Select(n => n.ToString()));
    }

    [Theory]   // mutation: drop the refusal of a network of everything
    [InlineData("0.0.0.0/0", "trusts every address")]
    [InlineData("::/0", "trusts every address")]
    [InlineData("10.0.0.0/8 ::/0", "trusts every address")]
    [InlineData("10.0.0.5/8", "bits set past its prefix")]
    [InlineData("fd00::1/8", "bits set past its prefix")]
    [InlineData("10.0.0.0/33", "not an IP address")]
    [InlineData("::/129", "not an IP address")]
    [InlineData("10.0.0.0/", "not an IP address")]
    [InlineData("10.0.0.0/x", "not an IP address")]
    [InlineData("10.0.0.0/-1", "not an IP address")]
    [InlineData("10", "not an IP address")]
    [InlineData("10.1", "not an IP address")]
    [InlineData("300.0.0.1", "not an IP address")]
    [InlineData("proxy.example.com", "not an IP address")]
    [InlineData("fe80::1%eth0", "not an IP address")]
    [InlineData("10.0.0.0/8/8", "not an IP address")]
    public void A_bad_entry_stops_the_start_without_echoing_the_value(string setting, string reason)
    {
        var e = Assert.Throws<InvalidOperationException>(() => TrustedProxies.Options(setting));
        Assert.Contains(reason, e.Message);
        Assert.Contains(TrustedProxies.Variable, e.Message);
        foreach (var entry in setting.Split(' ')) Assert.DoesNotContain(entry, e.Message);
    }

    [Fact]
    public void The_error_names_the_position_of_the_bad_entry()
    {
        var e = Assert.Throws<InvalidOperationException>(() => TrustedProxies.Parse("10.0.0.0/8, 192.168.1.4, nope"));
        Assert.Contains("entry 3", e.Message);
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("not-an-address")]
    [InlineData("10.0.0.5/8")]
    public void A_bad_value_stops_the_server_from_building(string setting)
    {
        var e = Assert.Throws<InvalidOperationException>(() =>
            PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], TestModules.Server, configureOptions: o => o.TrustedProxies = setting));
        Assert.Contains(TrustedProxies.Variable, e.Message);
    }
}
