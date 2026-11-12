using Packmoat.Application;

namespace Packmoat.Application.Tests;

/// <summary>
/// The customer's wording: the private check names one feed or many and explains ADO's default feed; the
/// developer proof's site text says what happened in plain words; the bypass advisory names the feeds first and speaks to the reader. The
/// CLI's shared verdict text (<see cref="DeveloperProbeVerdict.Index"/>, <see cref="DeveloperProbeVerdict.ListControl"/>) is unchanged.
/// </summary>
public class DeveloperProofWordingTests
{
    private static readonly Guid FeedId = Guid.Parse("0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f");
    private const string Hint = "Remove its access there (often through the project's Readers group, or a view shared with your organisation), then check again.";
    private const string DefaultHint = "Azure DevOps made a feed called Contoso that everyone in your organisation can read. In its Permissions, remove Project Collection Valid Users, or delete the feed if you don't use it.";

    // ---- item 4: one feed or many, and the organisation's default feed

    [Fact]
    public void One_readable_feed_is_named_in_the_singular()
    {
        var r = PrivateFeedVerdict.Decide(CheckStatus.Pass, 200, 2, new[] { new PrivateProbe("hr", 200), new PrivateProbe("b", 401) });
        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Equal("Packmoat feed access can read 1 other feed: hr. " + Hint, r.Detail);
    }

    [Fact]
    public void Two_readable_feeds_are_counted_in_the_plural()
    {
        var r = PrivateFeedVerdict.Decide(CheckStatus.Pass, 200, 2, new[] { new PrivateProbe("hr", 200), new PrivateProbe("finance@Release", 302) });
        Assert.Equal("Packmoat feed access can read 2 other feeds: hr, finance@Release. " + Hint, r.Detail);
    }

    [Fact]
    public void The_organisations_default_feed_gets_the_project_collection_valid_users_hint()
    {
        var r = PrivateFeedVerdict.Decide(CheckStatus.Pass, 200, 1, new[] { new PrivateProbe("Contoso", 200, OrganisationDefault: true) });
        Assert.Equal("Packmoat feed access can read 1 other feed: Contoso. " + DefaultHint, r.Detail);
    }

    [Fact]
    public void The_default_feed_and_another_feed_get_both_hints()
    {
        var r = PrivateFeedVerdict.Decide(CheckStatus.Pass, 200, 2, new[] { new PrivateProbe("Contoso", 200, OrganisationDefault: true), new PrivateProbe("hr", 200) });
        Assert.Equal("Packmoat feed access can read 2 other feeds: Contoso, hr. " + DefaultHint + " " + Hint, r.Detail);
    }

    [Fact]
    public void A_refused_default_feed_gives_no_default_hint()
    {
        var r = PrivateFeedVerdict.Decide(CheckStatus.Pass, 200, 2, new[] { new PrivateProbe("Contoso", 403, OrganisationDefault: true), new PrivateProbe("hr", 200) });
        Assert.Equal("Packmoat feed access can read 1 other feed: hr. " + Hint, r.Detail);
    }

    [Theory]
    [InlineData("Contoso", false, "contoso", true)]    // the name equals the organisation, ignoring case
    [InlineData("contoso", false, "Contoso", true)]
    [InlineData("CONTOSO", false, "contoso", true)]
    [InlineData("contoso", true, "contoso", false)]    // a project feed of the same name is never the default feed
    [InlineData("contoso-packages", false, "contoso", false)]
    [InlineData("hr", false, "contoso", false)]
    public void The_default_feed_is_outside_any_project_and_named_after_the_organisation(string name, bool hasProject, string org, bool expected) =>
        Assert.Equal(expected, PrivateFeedVerdict.IsOrganisationDefault(name, hasProject, org));

    [Fact]
    public void The_private_texts_say_packmoats_feed_not_the_public_only_feed()
    {
        var pass = PrivateFeedVerdict.Decide(CheckStatus.Pass, 200, 2, new[] { new PrivateProbe("a", 401) });
        Assert.Equal("Packmoat feed access can read Packmoat's feed and is refused on the 2 other feeds your account can see. Feeds your account can't see weren't checked.", pass.Detail);
        var control = PrivateFeedVerdict.Decide(CheckStatus.Pass, 401, 1, new[] { new PrivateProbe("a", 401) });
        Assert.Equal("NOT VERIFIED: Packmoat feed access couldn't read Packmoat's feed, so a refusal elsewhere would prove nothing. Check that it has the Collaborator role on that feed.", control.Detail);
        var unclear = PrivateFeedVerdict.Decide(CheckStatus.Pass, 200, 2, new[] { new PrivateProbe("hr", 404), new PrivateProbe("tools", null) });
        Assert.Equal("NOT VERIFIED: Azure DevOps gave an answer that doesn't prove a refusal for hr, tools. Check again in a few minutes.", unclear.Detail);   // no status codes
    }

    // ---- item 5 and 8: the developer proof's site text

    [Fact]
    public void The_site_title_has_no_checklist_reference()
    {
        Assert.Equal("A developer account is refused on Packmoat's feed", DeveloperProbeVerdict.Title);
    }

    [Theory]
    [InlineData(DeveloperFinding.Refused, "Your account could sign in to Azure DevOps, and Azure DevOps refused it on Packmoat's feed.")]
    [InlineData(DeveloperFinding.CanRead, "Your account can read Packmoat's feed directly.")]
    [InlineData(DeveloperFinding.NotMember, "Azure DevOps didn't list this organisation's feeds for your account.")]
    [InlineData(DeveloperFinding.ControlFailed, "Packmoat's own feed access couldn't read the feed, so a refusal wouldn't prove anything. Ask your Admin to check the feed's settings again.")]
    [InlineData(DeveloperFinding.Unclear, "Azure DevOps didn't give a clear answer. Check again in a few minutes.")]
    public void Each_finding_has_its_own_site_sentence(DeveloperFinding finding, string sentence) =>
        Assert.Equal(sentence, DeveloperProbeVerdict.SiteDetail(finding));

    /// <summary>The CLI's text comes from the shared verdict: byte for byte what it printed in an earlier version.</summary>
    [Fact]
    public void The_shared_verdict_text_the_cli_prints_is_unchanged()
    {
        Assert.Equal("the developer credential is valid (it can list feeds) and got HTTP 403 on the public-only feed (refused)", DeveloperProbeVerdict.Index(403).Detail);
        Assert.Equal("the developer credential is valid (it can list feeds) and got HTTP 401 on the public-only feed (refused)", DeveloperProbeVerdict.Index(401).Detail);
        Assert.Equal("developer account got HTTP 200: developers can use the public-only feed directly and bypass Packmoat. Remove their groups (Contributors, Build Service) from the feed and set views to specific people.", DeveloperProbeVerdict.Index(200).Detail);
        Assert.Equal("NOT VERIFIED: developer account got HTTP 404, which does not prove a refusal (404 can mean a wrong name; 429 or 5xx mean we were throttled or the service failed).", DeveloperProbeVerdict.Index(404).Detail);
        Assert.Equal("NOT VERIFIED: control failed: the developer credential got HTTP 401 even where it should work (listing feeds), so it is expired or wrong. Get a fresh one and rerun.", DeveloperProbeVerdict.ListControl(401)!.Detail);
        Assert.Equal("control failed: Packmoat's own identity got HTTP 403 on the public-only feed index, so a refusal elsewhere would prove nothing. Fix Packmoat's access to the public-only feed first.", DeveloperProbeVerdict.ControlFailure(403));
    }

    // ---- item 9: the bypass advisory

    private static VisibleFeed F(string name, params FeedUpstream[] ups) => new(Guid.NewGuid().ToString("D"), name, ups);
    private static readonly FeedUpstream NuGet = new("nuget", "public", null);

    [Fact]
    public void The_developers_bypass_sentence_names_the_feeds_first_and_keeps_the_names()
    {
        var r = BypassAdvisory.Check(new[] { F("private", NuGet), F("tools"), F("other", NuGet) }, FeedId);
        Assert.Equal(CheckStatus.Warn, r.Status);
        Assert.Equal("Feeds that let you skip Packmoat's cooldown: private, other. Ask your Admin to remove nuget.org, and any upstream that points to Packmoat's feed, from them.", r.Detail);
        Assert.Equal(new[] { "private", "other" }, r.Names);
        Assert.Null(BypassAdvisory.Check(new[] { F("tools") }, FeedId).Names);
    }

    [Fact]
    public void The_admins_bypass_sentence_is_addressed_to_the_admin()
    {
        var r = BypassAdvisory.Check(new[] { F("private", NuGet) }, FeedId);
        Assert.Equal("Feeds that let developers skip Packmoat's cooldown: private. Remove nuget.org, and any upstream that points to Packmoat's feed, from them.", BypassAdvisory.AdminSentence(r));
    }

    [Fact]
    public void An_older_row_without_names_gets_the_general_admin_sentence()
    {
        var old = new CheckResult(BypassAdvisory.Id, BypassAdvisory.Title, CheckStatus.Warn,
            "You can also get packages from nuget.org through these feeds, which skips Packmoat's cooldown: private. Ask your Admin to remove nuget.org, and any upstream that points to Packmoat's feed, from them.");
        Assert.Equal("A developer's check found feeds that skip Packmoat's cooldown. Remove nuget.org, and any upstream that points to Packmoat's feed, from them.", BypassAdvisory.AdminSentence(old));
    }

    [Fact]
    public void A_skipped_bypass_speaks_of_the_developers_account_on_the_admins_card_and_a_pass_has_none()
    {
        const string admin = "Bypass not checked: Azure DevOps didn't show the developer's account the upstream sources of every feed, so Packmoat couldn't tell whether any of them get packages from nuget.org.";
        Assert.Equal(admin, BypassAdvisory.AdminSentence(BypassAdvisory.NotChecked()));
        Assert.Equal(admin, BypassAdvisory.AdminSentence(BypassAdvisory.Check(new[] { new VisibleFeed(Guid.NewGuid().ToString("D"), "hidden", null) }, FeedId)));
        Assert.Null(BypassAdvisory.AdminSentence(BypassAdvisory.Check(new[] { F("tools") }, FeedId)));
    }
}
