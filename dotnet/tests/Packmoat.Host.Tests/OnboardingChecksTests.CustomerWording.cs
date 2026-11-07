using System.Text.RegularExpressions;
using Packmoat.Application;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// #119 PR A, item 5: the customer site's own wording for the engine's operator text (<see cref="OnboardingOptions.CustomerWording"/>,
/// set only by the setup wizard's delegated check). The CLI's text is pinned here byte for byte as it was before #119.
/// </summary>
public partial class OnboardingChecksTests
{
    private const string Expected = " Expected organisation admin groups: [org]\\Project Collection Administrators (the organisation's built-in Project Collection Administrators, confirmed by the identities API); [proj]\\Project Administrators (the project's built-in Project Administrators, confirmed by the identities API).";
    private const string CliIdentityNote = " (not recognised as a built-in admin group: the identities lookup failed (HTTP 403 from the Azure DevOps API, not 200 (203 or a redirect means a sign-in page: is the admin credential valid and an owner of the feed?)); the admin PAT needs Identity (read) for automatic recognition)";
    private const string SiteBuiltIn = " Built-in administrator groups that Azure DevOps adds to every feed: [org]\\Project Collection Administrators; [proj]\\Project Administrators.";
    private const string CouldNotReadGroup = " (Packmoat couldn't read what this group is)";

    private static async Task<CheckResult> Permissions(FakeDevOps d, bool customer, params string[] confirm)
    {
        var o = new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret };
        if (customer) { o.CustomerWording = true; o.Support = TestOperator.Support; }   // the CLI runs with the default
        o.ConfirmedAdmins.AddRange(confirm);
        return Get(await Build(d, o).RunAsync(default), "permissions");
    }

    [Fact]
    public void Customer_wording_is_off_unless_asked_for() => Assert.False(new OnboardingOptions().CustomerWording);

    [Fact]
    public async Task The_cli_permissions_text_is_unchanged()
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);
        Assert.Equal("Packmoat is collaborator. administrators not confirmed (ask the customer who they are, then rerun with --confirm-admin \"<name>\" for each): Casey Creator." + Expected,
            (await Permissions(d, false)).Detail);
        Assert.Equal("Packmoat is collaborator; every other administrator is expected or confirmed." + Expected + " Confirmed by the operator (--confirm-admin): Casey Creator.",
            (await Permissions(d, false, "Casey Creator")).Detail);
        Assert.Equal("Packmoat is collaborator. confirmed but not a named administrator on the feed (drop that --confirm-admin, or ask the customer what changed): Alice Admin." + Expected + " Confirmed by the operator (--confirm-admin): Casey Creator.",
            (await Permissions(d, false, "Casey Creator", "Alice Admin")).Detail);
        d.IdentitiesStatus = 403;
        Assert.Equal("Packmoat is collaborator. administrators not confirmed (ask the customer who they are, then rerun with --confirm-admin \"<name>\" for each): [org]\\Project Collection Administrators" + CliIdentityNote + ", [proj]\\Project Administrators" + CliIdentityNote + ". Confirmed by the operator (--confirm-admin): Casey Creator.",
            (await Permissions(d, false, "Casey Creator")).Detail);
        DefaultAdmins(d);
        d.IdentitiesStatus = 200;
        d.Permissions.Add(FakeDevOps.Perm("administrator", "Casey Creator", "Microsoft.IdentityModel.Claims.ClaimsIdentity;other\\casey@elsewhere.example"));
        Assert.Equal("Packmoat is collaborator. a confirmation matches more than one administrator (confirm each by its descriptor instead): Casey Creator." + Expected + " Confirmed by the operator (--confirm-admin): Casey Creator.",
            (await Permissions(d, false, "Casey Creator")).Detail);
    }

    [Fact]
    public async Task The_cli_text_without_an_admin_credential_is_unchanged()
    {
        await using var d = await FakeDevOps.StartAsync();
        var o = new OnboardingOptions { SharedAppClientId = "99999999-8888-4777-8666-555555555555", CheckProject = true };
        var r = await Build(d, o).RunWithoutProxyAsync(default);
        Assert.Equal("needs an admin credential that can read the organisation's identities (Graph read)", Get(r, "identity").Detail);
        foreach (var id in new[] { "upstreams", "permissions", "views" })
            Assert.Equal("needs an admin credential for the feed (a short-lived PAT with Packaging read & manage)", Get(r, id).Detail);
        Assert.Equal("needs an admin credential that can read the project (Project read)", Get(r, "project").Detail);
        Assert.Equal("A normal developer account is refused on the public-only feed (checklist 3b)", Get(r, "developer").Title);
        Assert.Equal("NOT VERIFIED: no developer credential given. Ask one developer for a short-lived Packaging (read) PAT and rerun.", Get(r, "developer").Detail);
    }

    [Fact]
    public async Task The_site_says_who_confirmed_and_what_is_left_without_operator_words()
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);
        var waiting = await Permissions(d, true);
        Assert.Equal(CheckStatus.Warn, waiting.Status);
        Assert.Equal("Packmoat feed access has the Collaborator role. Administrators not confirmed yet: Casey Creator." + SiteBuiltIn, waiting.Detail);
        var confirmed = await Permissions(d, true, "Casey Creator");
        Assert.Equal(CheckStatus.Pass, confirmed.Status);
        Assert.Equal("Packmoat feed access has the Collaborator role. Every other administrator is a built-in group or one you confirmed." + SiteBuiltIn + " Confirmed by you: Casey Creator.", confirmed.Detail);
        var gone = await Permissions(d, true, "Casey Creator", "Alice Admin");
        Assert.Equal(CheckStatus.Warn, gone.Status);
        Assert.Equal("Packmoat feed access has the Collaborator role. No longer an administrator of the feed: Alice Admin. Check again." + SiteBuiltIn + " Confirmed by you: Casey Creator.", gone.Detail);
        foreach (var r in new[] { waiting, confirmed, gone }) AssertNoOperatorWords(r.Detail);

        d.Permissions.Add(FakeDevOps.Perm("administrator", "Casey Creator", "Microsoft.IdentityModel.Claims.ClaimsIdentity;other\\casey@elsewhere.example"));
        var twice = await Permissions(d, true, "Casey Creator");
        Assert.Equal("Packmoat feed access has the Collaborator role. More than one administrator has the name Casey Creator. Email support@contoso.example." + SiteBuiltIn + " Confirmed by you: Casey Creator.", twice.Detail);
        AssertNoOperatorWords(twice.Detail);
    }

    [Fact]
    public async Task The_site_says_packmoat_feed_access_has_no_other_role_when_alone()
    {
        await using var d = await FakeDevOps.StartAsync();
        GoodFeed(d);
        var r = await Permissions(d, true);
        Assert.Equal("Packmoat feed access has the Collaborator role. No one else has a role on the feed. Built-in administrator groups that Azure DevOps adds to every feed: [org]\\Project Collection Administrators.", r.Detail);
        Assert.Equal("Packmoat is collaborator and no other identity holds a role. Expected organisation admin groups: [org]\\Project Collection Administrators (the organisation's built-in Project Collection Administrators, confirmed by the identities API).",
            (await Permissions(d, false)).Detail);   // the CLI's, unchanged
    }

    [Fact]
    public async Task On_the_site_a_failed_identities_lookup_never_shows_the_error()
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);
        d.IdentitiesStatus = 403;
        var r = await Permissions(d, true, "Casey Creator");
        Assert.Equal(CheckStatus.Warn, r.Status);
        Assert.Equal("Packmoat feed access has the Collaborator role. Administrators not confirmed yet: [org]\\Project Collection Administrators" + CouldNotReadGroup + ", [proj]\\Project Administrators" + CouldNotReadGroup + ". Confirmed by you: Casey Creator.", r.Detail);
        AssertNoOperatorWords(r.Detail);
    }

    [Fact]
    public async Task On_the_site_a_group_that_is_not_built_in_or_not_described_gets_a_plain_note()
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);
        d.Permissions.Add(FakeDevOps.Perm("administrator", "[other]\\Project Administrators", OtherProjectAdminsDescriptor));
        d.Identities[OtherProjectAdminsDescriptor] = ProjectAdminsIdentity(OtherProjectAdminsDescriptor, scope: "other", projectId: OtherProjectId);
        var notBuiltIn = await Permissions(d, true, "Casey Creator");
        Assert.Contains("Administrators not confirmed yet: [other]\\Project Administrators (not one of Azure DevOps' built-in administrator groups).", notBuiltIn.Detail);
        AssertNoOperatorWords(notBuiltIn.Detail);

        d.Identities.Remove(OtherProjectAdminsDescriptor);
        var undescribed = await Permissions(d, true, "Casey Creator");
        Assert.Contains("Administrators not confirmed yet: [other]\\Project Administrators (Azure DevOps didn't describe this group).", undescribed.Detail);
        Assert.Contains("(not recognised as a built-in admin group: the identities API did not return it)", (await Permissions(d, false, "Casey Creator")).Detail);   // the CLI's
    }

    [Fact]
    public async Task On_the_site_wrong_roles_are_listed_to_change_in_the_feeds_permissions()
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);
        d.Permissions[0] = FakeDevOps.Perm("reader", "", Packmoat);
        d.Permissions.Add(FakeDevOps.Perm("contributor", "[proj]\\Contributors", "Microsoft.TeamFoundation.Identity;S-1-9-1551374245-1-2-3-4-1-5-6-7-8"));
        var r = await Permissions(d, true, "Casey Creator");
        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Equal("Packmoat feed access has the Reader role on the feed; it needs Collaborator. Change these roles in the feed's Permissions: [proj]\\Contributors has Contributor.", r.Detail);
        AssertNoOperatorWords(r.Detail);
        Assert.Equal("fix: Packmoat's principal has 'reader', it needs exactly 'collaborator'; [proj]\\Contributors has contributor", (await Permissions(d, false, "Casey Creator")).Detail);   // the CLI's

        d.Permissions.RemoveAt(0);   // Packmoat feed access has no role at all
        d.Permissions.RemoveAt(d.Permissions.Count - 1);
        Assert.Equal("Packmoat feed access has no role on the feed; it needs Collaborator.", (await Permissions(d, true, "Casey Creator")).Detail);
        Assert.Equal("fix: Packmoat's principal has 'none', it needs exactly 'collaborator'", (await Permissions(d, false, "Casey Creator")).Detail);
    }

    [Theory]
    [InlineData("administrator", "Administrator")]
    [InlineData("contributor", "Contributor")]
    [InlineData("reader", "Reader")]
    public void Roles_are_named_as_azure_devops_shows_them(string role, string shown) => Assert.Equal(shown, OnboardingChecks.RoleName(role));

    [Fact]
    public async Task On_the_site_open_views_are_named_in_plain_words_and_the_cli_keeps_its_text()
    {
        await using var d = await FakeDevOps.StartAsync();
        d.Views = new System.Text.Json.Nodes.JsonArray(FakeDevOps.V("Local", "private"), FakeDevOps.V("Release", "organization"), FakeDevOps.V("Prerelease", "aadTenant"));
        var site = Get(await Build(d, new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret, CustomerWording = true, Support = TestOperator.Support }).RunAsync(default), "views");
        Assert.Equal((CheckStatus.Fail, "These views can be read by more people than the feed: Release (your organisation), Prerelease (your Microsoft directory). In the feed's settings under Views, set each to Specific people."),
            (site.Status, site.Detail));
        AssertNoOperatorWords(site.Detail);
        Assert.Equal("set to specific people: Release is 'organization'; Prerelease is 'aadTenant'",
            Get(await Build(d, new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret }).RunAsync(default), "views").Detail);
    }

    [Fact]
    public async Task On_the_site_upstream_sources_are_listed_plainly_and_the_cli_keeps_its_text()
    {
        await using var d = await FakeDevOps.StartAsync();
        var pass = Get(await Build(d, new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret, CustomerWording = true, Support = TestOperator.Support }).RunAsync(default), "upstreams");
        Assert.Equal("Upstream sources: https://api.nuget.org/v3/index.json, https://registry.npmjs.org/.", pass.Detail);
        Assert.Equal("2 upstream source(s): https://api.nuget.org/v3/index.json, https://registry.npmjs.org/",
            Get(await Build(d, new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret }).RunAsync(default), "upstreams").Detail);

        d.Upstreams.Add(FakeDevOps.U("https://evil.example/v3/index.json"));
        var fail = Get(await Build(d, new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret, CustomerWording = true, Support = TestOperator.Support }).RunAsync(default), "upstreams");
        Assert.Equal((CheckStatus.Fail, "Remove these upstream sources from the feed's settings, keeping only nuget.org and npmjs: https://evil.example/v3/index.json."), (fail.Status, fail.Detail));
        Assert.Equal("remove 1 extra upstream source(s): https://evil.example/v3/index.json",
            Get(await Build(d, new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret }).RunAsync(default), "upstreams").Detail);
    }

    [Fact]
    public async Task The_site_titles_have_no_operator_words_and_the_clis_are_unchanged()
    {
        await using var d = await FakeDevOps.StartAsync();
        GoodFeed(d);
        var site = new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret, CustomerWording = true, Support = TestOperator.Support, SharedAppClientId = "99999999-8888-4777-8666-555555555555" };
        var r = await Build(d, site).RunWithoutProxyAsync(default);
        Assert.Equal("Packmoat feed access is in your organisation exactly once", Get(r, "identity").Title);
        Assert.Equal("Only Packmoat feed access and the administrators you confirm have a role on Packmoat's feed", Get(r, "permissions").Title);
        Assert.StartsWith("This is the feed Packmoat serves: http", Get(r, "feed").Detail);
        var cli = await Build(d, new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret, SharedAppClientId = "99999999-8888-4777-8666-555555555555" }).RunWithoutProxyAsync(default);
        Assert.Equal("Packmoat's shared app is in the organisation exactly once", Get(cli, "identity").Title);
        Assert.Equal("Only Packmoat (and chosen admins) hold a role on the public-only feed", Get(cli, "permissions").Title);
        Assert.StartsWith("the options match the tenant's feed: http", Get(cli, "feed").Detail);
    }

    [Fact]
    public async Task On_the_site_no_upstreams_says_check_again()
    {
        await using var d = await FakeDevOps.StartAsync();
        d.Upstreams = new System.Text.Json.Nodes.JsonArray();
        var site = new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret, CustomerWording = true, Support = TestOperator.Support };
        Assert.EndsWith("Add nuget.org as the only NuGet upstream and check again.", Get(await Build(d, site).RunAsync(default), "upstreams").Detail);
        Assert.EndsWith("Add nuget.org as the only NuGet upstream and rerun.", Get(await Build(d, new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret }).RunAsync(default), "upstreams").Detail);
    }
}

/// <summary>#119 PR A, item 5: a failed read on the site is one fixed sentence, never the exception's message; the CLI's text is unchanged.</summary>
public partial class OnboardingBadResponseTests
{
    private const string ReadFailed = "Packmoat couldn't read this from Azure DevOps. Check again; if it keeps happening, email support@contoso.example.";
    private const string Http203 = "HTTP 203 from the Azure DevOps API, not 200 (203 or a redirect means a sign-in page: is the admin credential valid and an owner of the feed?)";

    [Fact]
    public async Task The_cli_text_for_a_failed_read_is_unchanged()
    {
        var r = await Run(new FakeAzureDevOps { Bad = () => Make("203-html") });
        Assert.Equal("NOT VERIFIED: could not list the organisation's service principals (" + Http203 + "). The admin credential needs Graph (read) as well as Packaging.", Get(r, "identity").Detail);
        Assert.Equal("could not complete: InvalidOperationException: " + Http203, Get(r, "upstreams").Detail);
        Assert.Equal("could not complete: InvalidOperationException: " + Http203, Get(r, "views").Detail);
    }

    [Theory]
    [MemberData(nameof(BadAnswers))]
    public async Task On_the_site_the_service_principal_list_failing_is_one_fixed_sentence_with_no_raw_error(string kind)
    {
        var r = await Run(new FakeAzureDevOps { Bad = () => Make(kind) }, customer: true);
        Assert.Equal((CheckStatus.Skip, ReadFailed), (Get(r, "identity").Status, Get(r, "identity").Detail));
        Assert.Equal(1, OnboardingSummary.Evaluate(r).ExitCode);
    }

    /// <summary>
    /// A check that errored keeps its "could not complete" text on the site too: the withdraw rule reads it, so an error is never a
    /// definite FAIL that would stop serving a connected feed (rewording it is a follow-up, with a flag instead of the text).
    /// </summary>
    [Fact]
    public async Task On_the_site_an_errored_check_is_still_not_a_definite_fail()
    {
        var r = await Run(new FakeAzureDevOps { Bad = () => Make("203-html") }, customer: true);
        foreach (var id in new[] { "upstreams", "views" })
        {
            Assert.Equal(CheckStatus.Fail, Get(r, id).Status);
            Assert.False(ConnectRules.IsDefiniteFail(Get(r, id)));
        }
    }
}
