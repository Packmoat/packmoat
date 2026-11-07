using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Application.Tests;

/// <summary>Whether a tenant's upstream is ready: strict feed URLs and a binding; a shared binding also needs a matching check.</summary>
public class UpstreamReadinessTests
{
    private const string Dir = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string Client = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";
    private const string Feed = "https://pkgs.dev.azure.com/acme/_packaging/f/nuget/v3/index.json";
    private static readonly DateTimeOffset At = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly Tenant Acme = new("acme", "Acme", "https://pkgs.dev.azure.com/", "acme/", Feed, new TenantPolicy(14));
    private static readonly UpstreamBinding Own = new(IdentityKind.Own, Dir, "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly UpstreamBinding Shared = new(IdentityKind.Shared, Dir, null);
    private static readonly SharedAppVerification Check = new("acme", Dir, Feed, Client, At, "op");

    [Fact]
    public void An_own_app_tenant_with_strict_urls_is_ready() =>
        Assert.Equal(UpstreamReadiness.Ready, UpstreamReadiness.Of(Acme, Own, null, null));

    [Fact]
    public void A_tenant_with_no_binding_is_not_set_up_even_with_good_urls() =>
        Assert.Equal(UpstreamReadiness.NotSetUp, UpstreamReadiness.Of(Acme, null, Check, Client));

    [Fact]
    public void A_self_made_tenant_with_empty_urls_is_not_set_up()
    {
        var empty = Acme with { UpstreamBase = "", AllowedPathPrefix = "", UpstreamFeedIndexUrl = "" };
        Assert.Equal(UpstreamReadiness.NotSetUp, UpstreamReadiness.Of(empty, null, null, null));
        // a binding does not make up for missing URLs, in either mode
        Assert.Equal(UpstreamReadiness.NotSetUp, UpstreamReadiness.Of(empty, Own, null, null));
        Assert.Equal(UpstreamReadiness.NotSetUp, UpstreamReadiness.Of(empty, Shared, Check with { FeedIndexUrl = "" }, Client));
    }

    [Theory]
    [InlineData("http://pkgs.dev.azure.com/", "acme/", "http://pkgs.dev.azure.com/acme/_packaging/f/nuget/v3/index.json")]   // not https
    [InlineData("https://pkgs.dev.azure.com/", "", Feed)]                                                                      // no prefix (the canary's shape)
    [InlineData("http://127.0.0.1:5555/", "", "http://127.0.0.1:5555/feed/index.json")]                                       // both: a test host
    public void An_own_app_tenant_with_create_time_urls_is_ready_but_a_shared_one_is_not_set_up(string upstreamBase, string prefix, string feed)
    {
        // The CLI and admin site have always let an own-app tenant use http or no prefix, and a tenant's URLs never change.
        var t = Acme with { UpstreamBase = upstreamBase, AllowedPathPrefix = prefix, UpstreamFeedIndexUrl = feed };
        Assert.Equal(UpstreamReadiness.Ready, UpstreamReadiness.Of(t, Own, null, null));
        // The shared app keeps the strict rule, even with a matching check record.
        Assert.Equal(UpstreamReadiness.NotSetUp, UpstreamReadiness.Of(t, Shared, Check with { FeedIndexUrl = feed }, Client));
    }

    [Theory]   // mutation: only the shared kind gets the strict rule (an unknown kind treated as an own app)
    [InlineData("http://pkgs.dev.azure.com/", "acme/", "http://pkgs.dev.azure.com/acme/_packaging/f/nuget/v3/index.json")]
    [InlineData("https://pkgs.dev.azure.com/", "", Feed)]
    public void A_binding_kind_this_build_does_not_know_gets_the_strict_rule_and_is_never_ready_with_create_time_urls(string upstreamBase, string prefix, string feed)
    {
        var t = Acme with { UpstreamBase = upstreamBase, AllowedPathPrefix = prefix, UpstreamFeedIndexUrl = feed };
        var unknown = new UpstreamBinding((IdentityKind)99, Dir, null);
        Assert.Equal(UpstreamReadiness.NotSetUp, UpstreamReadiness.Of(t, unknown, Check with { FeedIndexUrl = feed }, Client));
    }

    [Theory]
    [InlineData("https://pkgs.dev.azure.com/", "acme/", "https://pkgs.dev.azure.com/other/_packaging/f/nuget/v3/index.json")] // feed outside the prefix
    [InlineData("https://pkgs.dev.azure.com/", "acme/", "https://pkgs.dev.azure.com/acme/../other/index.json")]                // dot segment
    [InlineData("https://pkgs.dev.azure.com", "acme/", Feed)]                                                                  // base without '/'
    [InlineData("https://pkgs.dev.azure.com/", "acme/", "")]                                                                   // no feed
    public void Invalid_urls_are_not_set_up_in_either_mode(string upstreamBase, string prefix, string feed)
    {
        var t = Acme with { UpstreamBase = upstreamBase, AllowedPathPrefix = prefix, UpstreamFeedIndexUrl = feed };
        Assert.Equal(UpstreamReadiness.NotSetUp, UpstreamReadiness.Of(t, Own, null, null));
        Assert.Equal(UpstreamReadiness.NotSetUp, UpstreamReadiness.Of(t, Shared, Check with { FeedIndexUrl = feed }, Client));
    }

    [Fact]
    public void A_shared_tenant_is_ready_only_with_a_check_for_this_directory_feed_and_client()
    {
        Assert.Equal(UpstreamReadiness.Ready, UpstreamReadiness.Of(Acme, Shared, Check, Client));
        Assert.Equal(UpstreamReadiness.Ready, UpstreamReadiness.Of(Acme, Shared, Check, Client.ToUpperInvariant()));   // same GUID
    }

    [Fact]
    public void A_shared_tenant_without_a_matching_check_is_not_verified()
    {
        Assert.Equal(UpstreamReadiness.NotVerified, UpstreamReadiness.Of(Acme, Shared, null, Client));
        Assert.Equal(UpstreamReadiness.NotVerified, UpstreamReadiness.Of(Acme, Shared, Check, null));   // no active shared credential
        Assert.Equal(UpstreamReadiness.NotVerified, UpstreamReadiness.Of(Acme, Shared, Check, "ffffffff-ffff-4fff-8fff-ffffffffffff"));   // credential changed
        Assert.Equal(UpstreamReadiness.NotVerified, UpstreamReadiness.Of(Acme, Shared, Check with { FeedIndexUrl = Feed + "x" }, Client));
        Assert.Equal(UpstreamReadiness.NotVerified, UpstreamReadiness.Of(Acme, Shared, Check with { AzureTenantId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc" }, Client));
        Assert.Equal(UpstreamReadiness.NotVerified, UpstreamReadiness.Of(Acme, Shared with { AzureTenantId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc" }, Check, Client));
    }

    [Fact]
    public void A_check_for_another_tenant_does_not_count() =>
        Assert.Equal(UpstreamReadiness.NotVerified, UpstreamReadiness.Of(Acme, Shared, Check with { TenantId = "globex" }, Client));

    [Fact]
    public void An_own_app_tenant_needs_no_check() =>
        Assert.Equal(UpstreamReadiness.Ready, UpstreamReadiness.Of(Acme, Own, null, "ffffffff-ffff-4fff-8fff-ffffffffffff"));
}
