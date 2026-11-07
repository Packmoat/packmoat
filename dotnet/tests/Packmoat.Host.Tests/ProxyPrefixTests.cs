using Packmoat.Domain;
using Packmoat.Host.Proxy;

namespace Packmoat.Host.Tests;

/// <summary>The proxy only builds upstream URLs under the tenant's prefix, matched as whole path segments.</summary>
public class ProxyPrefixTests
{
    private static Tenant With(string prefix) => new("acme", "Acme", "https://pkgs.dev.azure.com/", prefix, "https://pkgs.dev.azure.com/acme/p/index.json", new TenantPolicy(14));

    [Theory]
    [InlineData("acme/")]
    [InlineData("acme")]   // an older row stored without its trailing '/': still a whole segment
    public void A_sibling_organisation_that_shares_the_prefix_text_is_refused(string prefix)
    {
        Assert.False(NuGetProxy.TryBuildUpstreamUrl(With(prefix), "acme-secret/_packaging/f/nuget/v3/index.json", out _));
        Assert.False(NuGetProxy.TryBuildUpstreamUrl(With(prefix), "acmex/_packaging/f/nuget/v3/index.json", out _));
        Assert.False(NuGetProxy.TryBuildUpstreamUrl(With(prefix), "acme", out _));
    }

    /// <summary>A self-made organisation has no upstream until setup: an empty base and an empty prefix must admit nothing, not everything.</summary>
    [Theory]
    [InlineData("acme/p/_packaging/f/nuget/v3/index.json")]
    [InlineData("https://evil.example/steal")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("//evil.example/x")]
    [InlineData("v3/index.json")]
    public void A_tenant_with_no_upstream_builds_no_url_at_all(string rest)
    {
        var none = new Tenant("org-x", "Contoso", "", "", "", new TenantPolicy(14));
        Assert.False(NuGetProxy.TryBuildUpstreamUrl(none, rest, out _));
        Assert.False(NuGetProxy.TryBuildUpstreamUrl(none with { AllowedPathPrefix = "acme/" }, rest, out _));
    }

    /// <summary>The feed-index request (no rest): a tenant with no upstream has no feed index to fetch, whatever its feed URL says.</summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("", "https://evil.example/v3/index.json")]       // an upstream base is required as well as a feed URL
    [InlineData("https://pkgs.dev.azure.com/", "")]
    [InlineData("https://pkgs.dev.azure.com/", "not-a-url")]
    public void A_tenant_with_no_upstream_has_no_feed_index(string upstreamBase, string feed)
    {
        Assert.False(NuGetProxy.TryFeedIndexUrl(new Tenant("org-x", "Contoso", upstreamBase, "", feed, new TenantPolicy(14)), out _));
        Assert.True(NuGetProxy.TryFeedIndexUrl(With("acme/"), out var url));
        Assert.Equal("https://pkgs.dev.azure.com/acme/p/index.json", url.AbsoluteUri);
    }

    [Theory]
    [InlineData("acme/")]
    [InlineData("acme")]
    public void Paths_inside_the_organisation_are_allowed(string prefix)
    {
        Assert.True(NuGetProxy.TryBuildUpstreamUrl(With(prefix), "acme/p/_packaging/f/nuget/v3/index.json", out var url));
        Assert.Equal("https://pkgs.dev.azure.com/acme/p/_packaging/f/nuget/v3/index.json", url.AbsoluteUri);
    }
}
