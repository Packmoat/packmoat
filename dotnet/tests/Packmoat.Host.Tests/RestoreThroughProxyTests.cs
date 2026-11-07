using Packmoat.Domain;
using static Packmoat.Host.Tests.ProxyFixture;

namespace Packmoat.Host.Tests;

/// <summary>Real `dotnet restore` through the real proxy against a fake upstream feed. Requires `dotnet` on PATH.</summary>
public class RestoreThroughProxyTests
{
    [Fact]
    public async Task An_old_version_restores_and_the_bytes_arrive_intact()
    {
        await using var p = await StartAsync();
        p.Publish("Acme.Lib", "1.0.0", ageDays: 100);

        var r = await Restore.RunAsync(p.SourceUrl, AcmeToken, "Acme.Lib", "1.0.0");

        Assert.True(r.Succeeded, r.Output);
        Assert.Equal("1.0.0", r.ResolvedVersion);
    }

    [Fact]
    public async Task A_young_exact_pin_fails_with_a_clear_error_and_the_package_is_never_fetched_upstream()
    {
        await using var p = await StartAsync();
        p.Publish("Acme.Lib", "1.0.0", 100);
        p.Publish("Acme.Lib", "2.0.0", 3);

        var r = await Restore.RunAsync(p.SourceUrl, AcmeToken, "Acme.Lib", "2.0.0");

        Assert.False(r.Succeeded);
        Assert.Contains("NU1102", r.Output);
        Assert.DoesNotContain(p.Upstream.Requests, q => q.Path.Contains("acme.lib.2.0.0.nupkg"));
    }

    [Fact]
    public async Task A_floating_request_quietly_resolves_to_the_newest_allowed_version()
    {
        await using var p = await StartAsync();
        p.Publish("Acme.Lib", "1.0.0", 100);
        p.Publish("Acme.Lib", "1.1.0", 40);
        p.Publish("Acme.Lib", "1.2.0", 2);

        var r = await Restore.RunAsync(p.SourceUrl, AcmeToken, "Acme.Lib", "1.*");

        Assert.True(r.Succeeded, r.Output);
        Assert.Equal("1.1.0", r.ResolvedVersion);
    }

    [Fact]
    public async Task A_tenant_without_a_cooldown_sees_the_young_version()
    {
        await using var p = await StartAsync();
        p.Publish("Acme.Lib", "2.0.0", 3);

        var r = await Restore.RunAsync(p.SourceUrl, GlobexToken, "Acme.Lib", "2.0.0");

        Assert.True(r.Succeeded, r.Output);
    }

    [Fact]
    public async Task An_internal_package_name_is_never_served_even_when_it_is_old()
    {
        await using var p = await StartAsync();
        p.Publish("Contoso.Secrets", "1.0.0", 900);

        var r = await Restore.RunAsync(p.SourceUrl, AcmeToken, "Contoso.Secrets", "1.0.0");

        Assert.False(r.Succeeded);
        Assert.DoesNotContain(p.Upstream.Requests, q => q.Path.Contains("contoso.secrets.1.0.0.nupkg"));
    }

    [Fact]
    public async Task A_waiver_lets_a_young_version_through()
    {
        var waiver = new Waiver("w1", "Acme.Lib", "2.0.0", new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), "hotfix", "olivia");
        await using var p = await StartAsync(_ => new TenantPolicy(14, new[] { "Contoso." }, new[] { waiver }));
        p.Publish("Acme.Lib", "2.0.0", 3);

        var r = await Restore.RunAsync(p.SourceUrl, AcmeToken, "Acme.Lib", "2.0.0");

        Assert.True(r.Succeeded, r.Output);
    }

    [Fact]
    public async Task When_the_date_source_is_down_everything_young_or_unknown_fails_closed()
    {
        await using var p = await StartAsync();
        p.Publish("Acme.Lib", "1.0.0", 900);
        p.Dates.Unavailable = true;

        var r = await Restore.RunAsync(p.SourceUrl, AcmeToken, "Acme.Lib", "1.0.0");

        Assert.False(r.Succeeded);
        Assert.Contains("NU1301", r.Output); // 503 from the proxy: a retryable outage, not "no such package"
    }

    [Fact]
    public async Task No_token_or_a_bad_token_is_rejected_and_nothing_reaches_the_upstream_feed()
    {
        await using var p = await StartAsync();
        p.Publish("Acme.Lib", "1.0.0", 100);

        var none = await Restore.RunAsync(p.SourceUrl, null, "Acme.Lib", "1.0.0");
        var bad = await Restore.RunAsync(p.SourceUrl, "pmt_wrong", "Acme.Lib", "1.0.0");

        Assert.False(none.Succeeded);
        Assert.False(bad.Succeeded);
        Assert.Contains("401", none.Output + bad.Output);
        Assert.Equal(0, p.Upstream.FeedRequestCount);
    }

    [Fact]
    public async Task A_large_package_streams_through_unchanged()
    {
        await using var p = await StartAsync();
        p.Publish("Acme.Big", "1.0.0", 100, extraBytes: 40 * 1024 * 1024);

        var r = await Restore.RunAsync(p.SourceUrl, AcmeToken, "Acme.Big", "1.0.0");

        Assert.True(r.Succeeded, r.Output);
    }
}
