using Packmoat.Application;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// #119 PR A through the fake Azure DevOps: the wizard's check uses the customer wording (never the CLI's), the private check explains
/// ADO's default feed (outside any project, named after the organisation), and the developer probe records the site's own sentences.
/// </summary>
public partial class DelegatedFeedCheckTests
{
    private const string DefaultHint = "In its Permissions, remove Project Collection Valid Users, or delete the feed if you don't use it.";
    private const string Hint = "Remove its access there (often through the project's Readers group, or a view shared with your organisation), then check again.";

    [Theory]
    [InlineData("Contoso")]
    [InlineData("CONTOSO")]
    [InlineData("contoso")]
    public async Task The_organisations_default_feed_readable_by_packmoat_gets_its_own_hint(string name)
    {
        var (check, ado) = Make();
        ado.OtherFeeds.Add(new FakeAzureDevOps.OtherFeed { Name = name, ProjectId = null, PackmoatStatus = 200 });
        var r = R(await check.CheckNewFeedAsync(Names(), Request(), FakeAzureDevOps.Token, default), "private");
        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Equal($"Packmoat feed access can read 1 other feed: {name}. Azure DevOps made a feed called {name} that everyone in your organisation can read. " + DefaultHint, r.Detail);
    }

    [Fact]
    public async Task A_project_feed_named_after_the_organisation_is_not_the_default_feed()
    {
        var (check, ado) = Make();
        ado.OtherFeeds.Add(new FakeAzureDevOps.OtherFeed { Name = "contoso", PackmoatStatus = 200 });   // in the project
        var r = R(await check.CheckNewFeedAsync(Names(), Request(), FakeAzureDevOps.Token, default), "private");
        Assert.Equal("Packmoat feed access can read 1 other feed: contoso. " + Hint, r.Detail);
    }

    [Fact]
    public async Task A_view_of_the_default_feed_shared_with_the_organisation_gets_the_general_hint()
    {
        var (check, ado) = Make();
        var feed = new FakeAzureDevOps.OtherFeed { Name = "Contoso", ProjectId = null };
        feed.Views.Add((Guid.NewGuid(), "Release", "organization", 200));
        ado.OtherFeeds.Add(feed);
        var r = R(await check.CheckNewFeedAsync(Names(), Request(), FakeAzureDevOps.Token, default), "private");
        Assert.Equal("Packmoat feed access can read 1 other feed: Contoso@Release. " + Hint, r.Detail);
    }

    [Fact]
    public async Task The_wizards_check_uses_the_customer_wording()
    {
        var (check, ado) = Make();
        ado.NamedAdmin = true;
        var run = await check.CheckNewFeedAsync(Names(), Request(), FakeAzureDevOps.Token, default);
        var permissions = R(run, "permissions");
        Assert.Equal(CheckStatus.Warn, permissions.Status);
        Assert.Contains("Administrators not confirmed yet: Ada Lovelace.", permissions.Detail);
        var confirmed = R(await check.CheckNewFeedAsync(Names(), Request("Ada Lovelace"), FakeAzureDevOps.Token, default), "permissions");
        Assert.EndsWith(" Confirmed by you: Ada Lovelace.", confirmed.Detail);
        foreach (var r in run.Results.Append(confirmed)) OnboardingChecksTests.AssertNoOperatorWords(r.Detail);
    }
}

/// <summary>#119 PR A: the developer probe's site sentences (the CLI keeps the shared verdict's text).</summary>
public partial class DeveloperFeedProbeTests
{
    [Theory]
    [InlineData(401, 200, 200, false, "Your account could sign in to Azure DevOps, and Azure DevOps refused it on Packmoat's feed.")]
    [InlineData(403, 200, 200, false, "Your account could sign in to Azure DevOps, and Azure DevOps refused it on Packmoat's feed.")]
    [InlineData(200, 200, 200, false, "Your account can read Packmoat's feed directly.")]
    [InlineData(401, 200, 200, true, "Your account can read Packmoat's feed directly.")]
    [InlineData(401, 401, 200, false, "Azure DevOps didn't list this organisation's feeds for your account.")]
    [InlineData(401, 200, 403, false, "Packmoat's own feed access couldn't read the feed, so a refusal wouldn't prove anything. Ask your Admin to check the feed's settings again.")]
    [InlineData(404, 200, 200, false, "Azure DevOps didn't give a clear answer. Check again in a few minutes.")]
    public async Task The_developer_probe_records_the_sites_sentence(int index, int list, int packmoat, bool listsFeed, string sentence)
    {
        var (run, _) = await Probe(ado =>
        {
            ado.DeveloperIndexStatus = index;
            ado.DeveloperListStatus = list;
            ado.PackmoatIndexStatus = packmoat;
            ado.DeveloperSeesPublicFeed = listsFeed;
        });
        Assert.Equal(sentence, Developer(run).Detail);
        Assert.Equal("A developer account is refused on Packmoat's feed", Developer(run).Title);
        OnboardingChecksTests.AssertNoOperatorWords(Developer(run).Detail);
    }

    [Fact]
    public async Task A_developer_probe_that_times_out_or_has_a_bad_address_gets_the_unclear_sentence()
    {
        var (slow, _) = await Probe(ado => ado.TimeoutOn = "/_apis/packaging/feeds");
        Assert.Equal("Azure DevOps didn't give a clear answer. Check again in a few minutes.", Developer(slow).Detail);
        var (byName, _) = await Probe(feed: "https://pkgs.dev.azure.com/contoso/_packaging/public-packages/nuget/v3/index.json");
        Assert.Equal("Azure DevOps didn't give a clear answer. Check again in a few minutes.", Developer(byName).Detail);
    }
}
