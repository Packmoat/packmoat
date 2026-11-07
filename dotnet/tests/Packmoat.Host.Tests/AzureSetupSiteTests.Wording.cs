using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Application;
using Packmoat.Host.Customer;

namespace Packmoat.Host.Tests;

/// <summary>
/// #119 PR A through the running host: the results banner once connected or ready to connect (item 10), the bypass sentence shown once
/// on the developer's page (item 8) and addressed to the Admin on the Connect card (item 9), no operator words on the customer's pages
/// (item 5), and step 3's default-feed sentence (item 4).
/// </summary>
public partial class AzureSetupSiteTests
{
    private const string OneStepLeft = "One step left";
    private const string SettingsPass = "Your feed's settings pass.";
    private const string DeveloperBypass = "Feeds that let you skip Packmoat's cooldown: team-packages. Ask your Admin to remove nuget.org, and any upstream that points to Packmoat's feed, from them.";
    private const string AdminBypass = "Feeds that let developers skip Packmoat's cooldown: team-packages. Remove nuget.org, and any upstream that points to Packmoat's feed, from them.";

    private static int Count(string page, string text) => Regex.Matches(page, Regex.Escape(text)).Count;

    /// <summary>The page's own text, without tags (so attribute values like https:// links and the antiforgery token are left out).</summary>
    private static string Visible(string page) => WebUtility.HtmlDecode(Regex.Replace(page, "<[^>]*>", " "));

    private static async Task<(string Developer, string Admin)> ProofWithBypassAsync(Site site)
    {
        await AdminCheckPassedAsync(site);
        site.Ado.OtherFeeds[0].Upstreams = new JsonArray(new JsonObject { ["protocol"] = "nuget", ["upstreamSourceType"] = "public", ["location"] = "https://api.nuget.org/v3/index.json" });
        var dev = await SignInDeveloper(site.F);
        Assert.Equal(HttpStatusCode.SeeOther, (await RunDeveloperCheck(site, dev)).StatusCode);
        return ((await dev.GetTextAsync(AppAzureSetup.DeveloperPath)).Body, (await (await SignIn(site.F)).GetTextAsync(AppAzureSetup.Path)).Body);
    }

    [Fact]
    public async Task One_step_left_shows_only_while_waiting_for_a_developer()
    {
        await using var site = await StartAsync();
        var f = site.F;
        await AdminCheckPassedAsync(site);
        var admin = await SignIn(f);
        var waiting = (await admin.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains(AppHtml.E(AppAzureSetup.WaitingOnDeveloper), waiting);

        Assert.Equal(HttpStatusCode.SeeOther, (await RunDeveloperCheck(site, await SignInDeveloper(f))).StatusCode);
        var ready = (await admin.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains($">{AppAzureSetup.ConnectButton}</button>", ready);
        Assert.DoesNotContain(OneStepLeft, ready);
        Assert.Contains(AppHtml.E(SettingsPass), ready);

        var settings = (await f.App.Services.GetRequiredService<IFeedCheckStore>().LatestAsync("selfco", FeedCheckKind.Settings, default))!;
        var developer = (await LatestDeveloperRow(f))!;
        Assert.Equal(HttpStatusCode.SeeOther, (await Post(admin, ready, AppAzureSetup.ConnectPath, true, ConnectFields((settings.Id, developer.Id)))).StatusCode);
        var connected = (await admin.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains(AppHtml.E(AppAzureSetup.ConnectedLine), connected);
        Assert.DoesNotContain(OneStepLeft, connected);
        Assert.Contains(AppHtml.E(SettingsPass), connected);
    }

    [Fact]
    public async Task The_developer_sees_the_bypass_sentence_once()
    {
        await using var site = await StartAsync();
        var (developer, _) = await ProofWithBypassAsync(site);
        Assert.Equal(1, Count(developer, AppHtml.E(DeveloperBypass)));
        Assert.Contains(AppHtml.E(BypassAdvisory.Title), developer);   // the result is still listed, without repeating the sentence
    }

    [Fact]
    public async Task The_admins_connect_card_names_the_feeds_and_speaks_to_the_admin()
    {
        await using var site = await StartAsync();
        var (_, admin) = await ProofWithBypassAsync(site);
        Assert.Contains(AppHtml.E(AdminBypass), admin);
        Assert.DoesNotContain(AppHtml.E("Ask your Admin"), admin);
        Assert.Contains($">{AppAzureSetup.ConnectButton}</button>", admin);
    }

    [Fact]
    public async Task An_older_developer_row_without_feed_names_gets_the_general_admin_sentence()
    {
        await using var site = await StartAsync();
        await ProofWithBypassAsync(site);
        const string old = "You can also get packages from nuget.org through these feeds, which skips Packmoat's cooldown: team-packages. Ask your Admin to remove nuget.org, and any upstream that points to Packmoat's feed, from them.";
        var results = new JsonArray(
            new JsonObject { ["id"] = "developer", ["title"] = "A normal developer account is refused on the public-only feed (checklist 3b)", ["status"] = "pass", ["detail"] = "the developer credential is valid (it can list feeds) and got HTTP 401 on the public-only feed (refused)" },
            new JsonObject { ["id"] = "bypass", ["title"] = BypassAdvisory.Title, ["status"] = "warn", ["detail"] = old });
        await Scalar(site.F, $"UPDATE feed_checks SET results = '{results.ToJsonString().Replace("'", "''")}' WHERE kind = 'developer'");
        var admin = (await (await SignIn(site.F)).GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains(AppHtml.E("A developer's check found feeds that skip Packmoat's cooldown. Remove nuget.org, and any upstream that points to Packmoat's feed, from them."), admin);
        Assert.DoesNotContain(AppHtml.E(old), admin);
    }

    [Fact]
    public async Task No_customer_page_shows_operator_words()
    {
        await using var site = await StartAsync();
        site.Ado.NamedAdmin = true;
        var admin = await SignIn(site.F);
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, admin)).StatusCode);
        var pages = new List<string> { (await admin.GetTextAsync(AppAzureSetup.Path)).Body };   // the named admin waiting for confirmation

        site.Ado.NamedAdmin = false;
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, admin)).StatusCode);
        pages.Add((await admin.GetTextAsync(AppAzureSetup.Path)).Body);
        var (developer, ready) = await ProofWithBypassAsync(site);
        pages.Add(developer);
        pages.Add(ready);
        var dev = await SignInDeveloper(site.F);
        foreach (var arrange in new Action<FakeAzureDevOps>[]
                 {
                     a => a.DeveloperIndexStatus = 200, a => { a.DeveloperIndexStatus = 401; a.DeveloperListStatus = 401; },
                     a => { a.DeveloperListStatus = 200; a.DeveloperIndexStatus = 404; }, a => { a.DeveloperIndexStatus = 401; a.DeveloperSeesUpstreams = false; },
                 })
        {
            arrange(site.Ado);
            Assert.Equal(HttpStatusCode.SeeOther, (await RunDeveloperCheck(site, dev)).StatusCode);
            pages.Add((await dev.GetTextAsync(AppAzureSetup.DeveloperPath)).Body);
        }
        foreach (var page in pages) OnboardingChecksTests.AssertNoOperatorWords(Visible(page));
    }

    /// <summary>The Admin's page after one settings check with <paramref name="arrange"/>: the page, and the recorded check.</summary>
    private static async Task<(string Page, FeedCheck Check)> AdminPageAfter(Site site, Action<FakeAzureDevOps> arrange)
    {
        arrange(site.Ado);
        var admin = await SignIn(site.F);
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, admin)).StatusCode);
        var check = (await site.F.App.Services.GetRequiredService<IFeedCheckStore>().LatestAsync("selfco", FeedCheckKind.Settings, default))!;
        return ((await admin.GetTextAsync(AppAzureSetup.Path)).Body, check);
    }

    [Fact]
    public async Task Failing_and_not_verified_pages_show_no_operator_words()
    {
        var cases = new (string Name, Action<FakeAzureDevOps> Arrange, string Expected)[]
        {
            ("not a member", a => a.SharedAppListed = false,
                "Packmoat feed access isn't in your organisation yet. Add it in Organization settings > Users (step 3), after a directory administrator has approved it (step 2)."),
            ("upstreams", a => a.Upstreams = new JsonArray(new JsonObject { ["name"] = "evil", ["protocol"] = "nuget", ["location"] = "https://evil.example/v3/index.json" }),
                "Remove these upstream sources from the feed's settings, keeping only nuget.org and npmjs: https://evil.example/v3/index.json."),
            ("feeds not listed", a => a.AdminListStatus = 403, "NOT VERIFIED: your organisation's feeds couldn't be listed, so Packmoat's access to them wasn't tested."),
            ("control", a => a.PackmoatIndexStatus = 401,
                "NOT VERIFIED: Packmoat feed access couldn't read Packmoat's feed, so a refusal elsewhere would prove nothing. Check that it has the Collaborator role on that feed."),
            ("unclear", a => a.OtherFeeds[0].PackmoatStatus = 404,
                "NOT VERIFIED: Azure DevOps gave an answer that doesn't prove a refusal for team-packages. Check again in a few minutes."),
            ("errored", a => a.PermissionsStatus = 403, AppAzureSetup.CouldNotFinishLine(TestOperator.Support)),
            ("other directory", a => a.Directory = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
                "Packmoat feed access in this organisation comes from a different Microsoft directory from the one your organisation uses with Packmoat. Email support@contoso.example."),
        };
        foreach (var (name, arrange, expected) in cases)
        {
            await using var site = await StartAsync();
            var (page, _) = await AdminPageAfter(site, arrange);
            Assert.True(page.Contains(AppHtml.E(expected), StringComparison.Ordinal), name);
            OnboardingChecksTests.AssertNoOperatorWords(Visible(page));
        }
    }

    [Fact]
    public async Task The_status_code_goes_to_the_log_with_the_tenant_id()
    {
        await using var site = await StartAsync();
        var (page, _) = await AdminPageAfter(site, a => a.AdminListStatus = 403);
        Assert.DoesNotContain("403", Visible(page));
        lock (site.Logs.Lines)
            Assert.Contains(site.Logs.Lines, l => l.Contains("Private check: listing the organisation's feeds answered HTTP 403 (tenant selfco)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_errored_check_is_reworded_only_on_the_page_and_stays_not_a_definite_fail()
    {
        await using var site = await StartAsync();
        var (page, check) = await AdminPageAfter(site, a => a.PermissionsStatus = 403);
        var stored = check.Results.Single(r => r.Id == "permissions");
        Assert.Equal(CheckStatus.Fail, stored.Status);
        Assert.StartsWith(ConnectRules.CouldNotComplete, stored.Detail);   // stored as before
        Assert.False(ConnectRules.IsDefiniteFail(stored));                   // so the withdraw rule is unaffected
        Assert.Contains(AppHtml.E(AppAzureSetup.CouldNotFinishLine(TestOperator.Support)), page);
        Assert.DoesNotContain(AppHtml.E(stored.Detail), page);
        Assert.Equal(AppAzureSetup.CouldNotFinishLine(TestOperator.Support), AppAzureSetup.Shown(stored, TestOperator.Support));
        Assert.Equal("remove 1 thing", AppAzureSetup.Shown(new CheckResult("upstreams", "U", CheckStatus.Fail, "remove 1 thing"), TestOperator.Support));
        // a skipped result that happens to start the same way is shown as it is
        Assert.Equal("could not complete: x", AppAzureSetup.Shown(new CheckResult("u", "U", CheckStatus.Skip, "could not complete: x"), TestOperator.Support));

        // and the timeout rule still turns one call's timeout into not verified
        await using var slow = await StartAsync();
        var (slowPage, slowCheck) = await AdminPageAfter(slow, a => a.TimeoutOn = "/permissions");
        Assert.Equal((CheckStatus.Skip, AzureSetupCommands.NoAnswerInTime), (slowCheck.Results.Single(r => r.Id == "permissions").Status, slowCheck.Results.Single(r => r.Id == "permissions").Detail));
        Assert.Contains(AppHtml.E(AzureSetupCommands.NoAnswerInTime), slowPage);
    }

    [Fact]
    public async Task The_admins_card_with_bypass_not_checked_speaks_of_the_developers_account()
    {
        await using var site = await StartAsync();
        await AdminCheckPassedAsync(site);
        site.Ado.DeveloperSeesUpstreams = false;
        Assert.Equal(HttpStatusCode.SeeOther, (await RunDeveloperCheck(site, await SignInDeveloper(site.F))).StatusCode);
        var admin = (await (await SignIn(site.F)).GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains(AppHtml.E(BypassAdvisory.AdminNotCheckedSentence), admin);
        var card = admin[admin.IndexOf("id=\"connect-h\"", StringComparison.Ordinal)..];
        card = card[..card.IndexOf("</section>", StringComparison.Ordinal)];
        Assert.Contains(AppHtml.E(BypassAdvisory.AdminNotCheckedSentence), card);
        Assert.DoesNotContain("your account", Visible(card), StringComparison.OrdinalIgnoreCase);   // the Admin's own private check may say it; the card may not
        OnboardingChecksTests.AssertNoOperatorWords(Visible(admin));
    }

    [Fact]
    public async Task Step_three_mentions_the_organisations_default_feed()
    {
        await using var site = await StartAsync();
        var page = (await (await SignIn(site.F)).GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains(AppHtml.E("Don't give it a role on any other feed. Azure DevOps also makes a feed named after your organisation that everyone in it can read. Remove Project Collection Valid Users from its Permissions, or delete it if you don't use it."), page);
    }
}
