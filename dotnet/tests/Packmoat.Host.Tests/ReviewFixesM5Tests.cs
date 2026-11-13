using System.Text.Json.Nodes;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>Regression tests for onboarding verification.</summary>
public class ReviewFixesM5Tests
{
    private const string Packmoat = "AadServicePrincipal;tenant\\11111111-spid";

    private static OnboardingChecks Build(FakeDevOps d, OnboardingOptions o, string principal = "11111111-spid")
    {
        o.Organization = "org"; o.Project = "proj"; o.Feed = "pub";
        o.FeedsApiBase = d.Base; o.PackagesBase = d.Base; o.PackmoatPrincipal = principal;
        o.RetryWaitCap = TimeSpan.Zero;
        return new OnboardingChecks(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, new StaticBasicCredentialSource("packmoat", "SP-SECRET-TOKEN"), OnboardingChecksTests.TenantFor(d), o);
    }

    private static CheckResult Get(IReadOnlyList<CheckResult> r, string id) => r.Single(x => x.Id == id);
    private static OnboardingOptions WithDeveloper() => new() { DeveloperToken = FakeDevOps.DeveloperSecret };
    private static OnboardingOptions WithAdmin() => new() { AdminToken = FakeDevOps.AdminSecret };

    // ---- 18: positive controls

    [Fact]
    public async Task A_refusal_proves_nothing_when_Packmoat_itself_cannot_read_the_feed()
    {
        await using var d = await FakeDevOps.StartAsync();
        d.PackmoatIndexStatus = 404;   // wrong feed name: everything would be "refused" for the wrong reason
        var o = WithDeveloper();
        o.PrivateFeedProbeUrls.Add(d.Base + "/org/private/_packaging/hr/nuget/v3/index.json");

        var r = await Build(d, o).RunAsync(default);

        Assert.Equal(CheckStatus.Skip, Get(r, "developer").Status);
        Assert.Contains("control failed", Get(r, "developer").Detail);
        Assert.Equal(CheckStatus.Skip, Get(r, "private").Status);
        Assert.Contains("NOT VERIFIED", Get(r, "private").Detail);
    }

    [Fact]
    public async Task An_expired_developer_credential_is_not_verified_rather_than_refused()
    {
        await using var d = await FakeDevOps.StartAsync();
        d.DeveloperTokenValid = false;
        d.DeveloperIndexStatus = 401;   // an expired PAT is refused everywhere, which says nothing about the feed's permissions

        var dev = Get(await Build(d, WithDeveloper()).RunAsync(default), "developer");

        Assert.Equal(CheckStatus.Skip, dev.Status);
        Assert.Contains("expired or wrong", dev.Detail);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    public async Task A_developer_answer_that_is_not_a_refusal_is_not_a_pass(int status)
    {
        await using var d = await FakeDevOps.StartAsync();
        d.DeveloperIndexStatus = status;
        var dev = Get(await Build(d, WithDeveloper()).RunAsync(default), "developer");
        Assert.Equal(CheckStatus.Skip, dev.Status);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    public async Task A_private_probe_that_answers_404_is_not_verified_not_passed(int status)
    {
        await using var d = await FakeDevOps.StartAsync();
        d.PrivateFeedStatus = status;
        var o = new OnboardingOptions();
        o.PrivateFeedProbeUrls.Add(d.Base + "/org/private/_packaging/hr/nuget/v3/index.json");

        var r = Get(await Build(d, o).RunAsync(default), "private");

        Assert.Equal(CheckStatus.Skip, r.Status);
        Assert.Contains("NOT VERIFIED", r.Detail);
    }

    [Fact]
    public async Task A_private_feed_refused_with_a_working_control_passes()
    {
        await using var d = await FakeDevOps.StartAsync();
        var o = new OnboardingOptions();
        o.PrivateFeedProbeUrls.Add(d.Base + "/org/private/_packaging/hr/nuget/v3/index.json");
        Assert.Equal(CheckStatus.Pass, Get(await Build(d, o).RunAsync(default), "private").Status);
    }

    // ---- 19: summary and exit code

    private static CheckResult R(string id, CheckStatus s) => new(id, id, s, "");

    private static IReadOnlyList<CheckResult> All(CheckStatus s) => OnboardingSummary.Required.Select(id => R(id, s)).ToArray();

    [Fact]
    public void Only_a_clean_pass_of_every_required_check_is_exit_zero_and_may_be_called_verified()
    {
        var ok = OnboardingSummary.Evaluate(All(CheckStatus.Pass));
        Assert.Equal(0, ok.ExitCode);
        Assert.DoesNotContain("NOT VERIFIED", ok.Text);
    }

    [Theory]
    [InlineData(CheckStatus.Skip)]
    [InlineData(CheckStatus.Warn)]
    public void A_skipped_or_warned_required_check_is_never_called_isolated_and_is_not_exit_zero(CheckStatus status)
    {
        var results = All(CheckStatus.Pass).Select(r => r.Id == "developer" ? R("developer", status) : r).ToArray();
        var v = OnboardingSummary.Evaluate(results);
        Assert.Equal(2, v.ExitCode);
        Assert.Contains("NOT VERIFIED", v.Text);
        Assert.Contains("developer", v.Text);
        Assert.DoesNotContain("looks isolated", v.Text);
    }

    [Fact]
    public void A_missing_required_check_counts_as_not_verified()
    {
        var v = OnboardingSummary.Evaluate(All(CheckStatus.Pass).Where(r => r.Id != "views").ToArray());
        Assert.Equal(2, v.ExitCode);
        Assert.Contains("views", v.Text);
    }

    [Fact]
    public void A_failure_is_exit_one_even_when_other_checks_were_skipped()
    {
        var results = All(CheckStatus.Skip).Select(r => r.Id == "views" ? R("views", CheckStatus.Fail) : r).ToArray();
        Assert.Equal(1, OnboardingSummary.Evaluate(results).ExitCode);
    }

    [Theory]
    [InlineData("[org]\\Project Valid Users")]
    [InlineData("[org]\\Contributors")]
    [InlineData("[org]\\Readers")]
    [InlineData("org Build Service (org)")]
    public async Task An_administrator_role_held_by_a_broad_group_fails(string group)
    {
        await using var d = await FakeDevOps.StartAsync();
        d.Permissions = new JsonArray(FakeDevOps.Perm("collaborator", "", Packmoat), FakeDevOps.Perm("administrator", group, "S-1-9-x"));
        var perm = Get(await Build(d, WithAdmin()).RunAsync(default), "permissions");
        Assert.Equal(CheckStatus.Fail, perm.Status);
        Assert.Contains(group, perm.Detail);
    }

    // ---- 20: egress is gone
    [Fact]
    public async Task There_is_no_egress_check_because_the_operators_network_says_nothing_about_the_customers()
    {
        await using var d = await FakeDevOps.StartAsync();
        Assert.DoesNotContain(await Build(d, new OnboardingOptions()).RunAsync(default), r => r.Id == "egress");
    }

    // ---- 21: upstreams

    [Theory]
    [InlineData("https://api.nuget.org.evil.example/v3/index.json")]
    [InlineData("https://evil.example/api.nuget.org/v3/index.json")]
    [InlineData("https://evil.example/?x=registry.npmjs.org")]
    [InlineData("http://api.nuget.org/v3/index.json")]
    [InlineData("not a url at all")]
    public async Task An_upstream_that_only_mentions_an_allowed_host_fails(string location)
    {
        await using var d = await FakeDevOps.StartAsync();
        d.Upstreams = new JsonArray(FakeDevOps.U("https://api.nuget.org/v3/index.json"), FakeDevOps.U(location));
        var r = Get(await Build(d, WithAdmin()).RunAsync(default), "upstreams");
        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Contains("1 extra", r.Detail);
    }

    [Fact]
    public async Task No_upstream_sources_at_all_is_a_failure_not_a_pass()
    {
        await using var d = await FakeDevOps.StartAsync();
        d.Upstreams = new JsonArray();
        Assert.Equal(CheckStatus.Fail, Get(await Build(d, WithAdmin()).RunAsync(default), "upstreams").Status);
    }

    [Theory]
    [InlineData("https://API.NuGet.org/v3/index.json")]
    [InlineData("https://registry.npmjs.org/")]
    public void The_two_allowed_hosts_match_in_any_case(string location) => Assert.True(OnboardingChecks.IsAllowedUpstream(location));

    // ---- 24: permissions identity, pagination, throttling

    [Fact]
    public async Task A_principal_whose_id_merely_contains_ours_is_not_Packmoat()
    {
        await using var d = await FakeDevOps.StartAsync();
        d.Permissions = new JsonArray(
            FakeDevOps.Perm("collaborator", "", "AadServicePrincipal;tenant\\11111111-spid-IMPOSTOR"),
            FakeDevOps.Perm("collaborator", "11111111-spid (look-alike)", "S-1-9-lookalike"));

        var perm = Get(await Build(d, WithAdmin()).RunAsync(default), "permissions");

        Assert.Equal(CheckStatus.Fail, perm.Status);
        Assert.Contains("Packmoat's principal has 'none'", perm.Detail);   // we are not in the list at all
        Assert.Contains("IMPOSTOR", perm.Detail);                          // and the look-alikes are reported as extra holders
    }

    [Fact]
    public async Task Without_a_principal_the_permissions_check_is_not_verified()
    {
        await using var d = await FakeDevOps.StartAsync();
        var perm = Get(await Build(d, WithAdmin(), principal: "").RunAsync(default), "permissions");
        Assert.Equal(CheckStatus.Skip, perm.Status);
        Assert.Contains("--principal", perm.Detail);
    }

    [Fact]
    public async Task Someone_hiding_on_the_second_page_of_permissions_is_found()
    {
        await using var d = await FakeDevOps.StartAsync();
        d.Permissions = new JsonArray(FakeDevOps.Perm("collaborator", "", Packmoat));
        d.PermissionsPage2 = new JsonArray(FakeDevOps.Perm("collaborator", "[org]\\Contributors", "S-1-9-contrib"));

        var perm = Get(await Build(d, WithAdmin()).RunAsync(default), "permissions");

        Assert.Equal(CheckStatus.Fail, perm.Status);
        Assert.Contains("Contributors has collaborator", perm.Detail);
    }

    [Fact]
    public async Task A_throttled_permissions_request_is_retried_and_then_answered()
    {
        await using var d = await FakeDevOps.StartAsync();
        d.Permissions = new JsonArray(FakeDevOps.Perm("collaborator", "", Packmoat));
        d.ThrottleFirstPermissionRequests = 2;

        Assert.Equal(CheckStatus.Pass, Get(await Build(d, WithAdmin()).RunAsync(default), "permissions").Status);
    }

    [Fact]
    public async Task Endless_throttling_ends_as_a_failure_not_a_pass()
    {
        await using var d = await FakeDevOps.StartAsync();
        d.Permissions = new JsonArray(FakeDevOps.Perm("collaborator", "", Packmoat));
        d.ThrottleFirstPermissionRequests = 100;

        var perm = Get(await Build(d, WithAdmin()).RunAsync(default), "permissions");

        Assert.Equal(CheckStatus.Fail, perm.Status);
        Assert.Contains("HTTP 429", perm.Detail);
    }
}
