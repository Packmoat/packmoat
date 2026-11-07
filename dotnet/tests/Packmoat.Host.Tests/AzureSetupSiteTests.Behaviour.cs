using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Application;
using Packmoat.Host.Customer;

namespace Packmoat.Host.Tests;

/// <summary>
/// #119 PR B through the running host: step 1 survives Microsoft's approval page (item 2), the developer's page names the feed (item 6),
/// and Connect's fresh sign-in says how it went when it comes back (item 10).
/// </summary>
public partial class AzureSetupSiteTests
{
    private const string Ben = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";   // a second Admin of selfco

    /// <summary>The value attribute of the input named <paramref name="name"/>.</summary>
    private static string InputValue(string page, string name) =>
        Regex.Match(page, $"<input id=\"{name}\" name=\"{name}\"[^>]* value=\"([^\"]*)\">") is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value)
            : throw new InvalidOperationException("no input " + name);

    private static (string, string, string) Step1(string page) => (InputValue(page, "org"), InputValue(page, "project"), InputValue(page, "feed"));

    private static readonly (string, string, string) Typed = (FakeAzureDevOps.Org, FakeAzureDevOps.ProjectName, FakeAzureDevOps.FeedName);
    private static readonly (string, string, string) Blank = ("", "", "");

    private static async Task<AdminBrowser> SignInAs(AdminSiteFixture f, string oid, string role = CustomerSignIn.AdminRole)
    {
        f.AppIdentity.Next = Person(DirS, oid: oid, role: role, authTime: f.Clock.UtcNow);
        var b = f.Browser(AppHost);
        var start = await b.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, (await b.GetAsync(start.Headers.Location!.OriginalString)).StatusCode);
        return b;
    }

    private static async Task<HttpResponseMessage> Approve(AdminBrowser b, params (string, string)[] fields)
    {
        var (_, page, _) = await b.GetTextAsync(AppAzureSetup.Path);
        return await Post(b, page, AppAzureSetup.ApprovePath, true, fields);
    }

    private static readonly string ApprovalPage =
        $"https://login.microsoftonline.com/{DirS}/adminconsent?client_id={FakeAzureDevOps.SharedApp}&redirect_uri=https%3A%2F%2F{AppHost}%2Fsetup%2Fazure%2Ffeed-app-added";

    // ---- item 2: step 1 survives the approval round trip

    [Fact]
    public async Task Step_two_is_a_button_in_step_ones_form_that_keeps_the_names_and_goes_to_microsoft()
    {
        await using var site = await StartAsync();
        var b = await SignIn(site.F);
        var page = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains($"<button type=\"submit\" class=\"btn ghost\" formaction=\"{AppAzureSetup.ApprovePath}\" formnovalidate>{AppHtml.E(AppAzureSetup.Step2Button)}</button>", page);
        var form = page[page.IndexOf($"action=\"{AppAzureSetup.CheckPath}\" class=\"setup\"", StringComparison.Ordinal)..];
        Assert.True(form.IndexOf("formaction", StringComparison.Ordinal) < form.IndexOf("</form>", StringComparison.Ordinal));   // inside step 1's form
        Assert.DoesNotContain("href=\"https://login.microsoftonline.com", page);

        var r = await Approve(b, GoodNames);
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal(ApprovalPage, r.Headers.Location!.OriginalString);
        Assert.Contains("https://login.microsoftonline.com", r.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(0, site.SignIn.Challenges);   // no sign-in, no check
        Assert.Empty(site.Ado.Seen);
        Assert.Equal(0L, await FeedCheckRows(site.F));

        Assert.Equal(200, (await b.GetTextAsync(AppAzureSetup.FeedAppAddedPath)).Status);   // Microsoft sends them back here
        var back = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Equal(Typed, Step1(back));
        OnboardingChecksTests.AssertNoOperatorWords(Visible(back));
    }

    [Fact]   // mutation: the draft keyed by tenant only
    public async Task The_draft_is_this_persons_only()
    {
        await using var site = await StartAsync();
        var ada = await SignIn(site.F);
        Assert.Equal(HttpStatusCode.SeeOther, (await Approve(ada, GoodNames)).StatusCode);
        var ben = await SignInAs(site.F, Ben);
        Assert.Equal(Blank, Step1((await ben.GetTextAsync(AppAzureSetup.Path)).Body));
        Assert.Equal(Typed, Step1((await ada.GetTextAsync(AppAzureSetup.Path)).Body));
    }

    [Fact]
    public async Task The_draft_lasts_30_minutes()
    {
        await using var site = await StartAsync();
        var b = await SignIn(site.F);
        Assert.Equal(HttpStatusCode.SeeOther, (await Approve(b, GoodNames)).StatusCode);
        site.F.Clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(Typed, Step1((await b.GetTextAsync(AppAzureSetup.Path)).Body));
        site.F.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(Blank, Step1((await b.GetTextAsync(AppAzureSetup.Path)).Body));
    }

    [Fact]
    public async Task Step_two_works_with_step_one_empty_and_keeps_only_names_that_pass_their_rule()
    {
        await using var site = await StartAsync();
        var b = await SignIn(site.F);
        Assert.Equal(ApprovalPage, (await Approve(b)).Headers.Location!.OriginalString);   // nothing typed yet
        Assert.Equal(Blank, Step1((await b.GetTextAsync(AppAzureSetup.Path)).Body));

        var r = await Approve(b, ("org", "bad org!"), ("project", ""), ("feed", FakeAzureDevOps.FeedName));
        Assert.Equal(ApprovalPage, r.Headers.Location!.OriginalString);
        var page = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Equal(("", "", FakeAzureDevOps.FeedName), Step1(page));
        Assert.DoesNotContain("bad org!", WebUtility.HtmlDecode(page));
    }

    [Fact]
    public async Task Step_two_keeps_nothing_for_a_developer_or_without_the_origin()
    {
        await using var site = await StartAsync();
        var dev = await SignIn(site.F, role: CustomerSignIn.DeveloperRole);
        var home = (await dev.GetTextAsync("/home")).Body;
        var refused = await Post(dev, home, AppAzureSetup.ApprovePath, true, GoodNames);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("Admins only", await refused.Content.ReadAsStringAsync());

        var admin = await SignIn(site.F);
        var page = (await admin.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(admin, page, AppAzureSetup.ApprovePath, false, GoodNames)).StatusCode);
        Assert.Equal(Blank, Step1((await admin.GetTextAsync(AppAzureSetup.Path)).Body));
    }

    [Fact]
    public async Task Step_two_does_not_exist_with_the_switch_off()
    {
        await using var site = await StartAsync(flag: "off");
        var b = await SignIn(site.F);
        var home = (await b.GetTextAsync("/home")).Body;
        Assert.Equal(HttpStatusCode.NotFound, (await Post(b, home, AppAzureSetup.ApprovePath, true, GoodNames)).StatusCode);
    }

    [Fact]
    public async Task Without_a_draft_or_a_held_check_step_one_comes_from_the_latest_recorded_check()
    {
        await using var site = await StartAsync();
        var ada = await SignIn(site.F);
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, ada)).StatusCode);
        var ben = await SignInAs(site.F, Ben);   // holds no check and no draft of his own
        Assert.Equal(Typed, Step1((await ben.GetTextAsync(AppAzureSetup.Path)).Body));

        // his own draft comes first
        Assert.Equal(HttpStatusCode.SeeOther, (await Approve(ben, ("org", "fabrikam"), ("project", ""), ("feed", "other-feed"))).StatusCode);
        Assert.Equal(("fabrikam", "", "other-feed"), Step1((await ben.GetTextAsync(AppAzureSetup.Path)).Body));
    }

    [Fact]   // review F3. mutation: the held check always wins, or the draft always wins
    public async Task Of_the_held_check_and_the_draft_the_newer_one_fills_step_one()
    {
        await using var site = await StartAsync();
        var ada = await SignIn(site.F);
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, ada)).StatusCode);   // held: the typed names
        site.F.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.SeeOther, (await Approve(ada, ("org", "fabrikam"), ("project", ""), ("feed", "other-feed"))).StatusCode);
        Assert.Equal(("fabrikam", "", "other-feed"), Step1((await ada.GetTextAsync(AppAzureSetup.Path)).Body));   // the draft is newer

        site.F.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, ada)).StatusCode);   // a newer check than the draft
        Assert.Equal(Typed, Step1((await ada.GetTextAsync(AppAzureSetup.Path)).Body));
    }

    [Fact]   // review doubt 4. mutation: an empty step 2 keeps the earlier draft
    public async Task Step_two_with_nothing_valid_typed_forgets_an_earlier_draft()
    {
        await using var site = await StartAsync();
        var b = await SignIn(site.F);
        Assert.Equal(HttpStatusCode.SeeOther, (await Approve(b, GoodNames)).StatusCode);
        Assert.Equal(Typed, Step1((await b.GetTextAsync(AppAzureSetup.Path)).Body));
        Assert.Equal(ApprovalPage, (await Approve(b, ("org", ""), ("project", ""), ("feed", "bad feed"))).Headers.Location!.OriginalString);
        Assert.Equal(Blank, Step1((await b.GetTextAsync(AppAzureSetup.Path)).Body));
    }

    /// <summary>Step 1's form, from its start to its end.</summary>
    private static string SetupForm(string page)
    {
        var form = page[page.IndexOf($"action=\"{AppAzureSetup.CheckPath}\" class=\"setup\"", StringComparison.Ordinal)..];
        return form[..form.IndexOf("</form>", StringComparison.Ordinal)];
    }

    [Fact]   // review F1. mutation: no default button, or one with a formaction
    public async Task Enter_in_step_one_never_opens_the_approval_page()
    {
        await using var site = await StartAsync();
        var form = SetupForm((await (await SignIn(site.F)).GetTextAsync(AppAzureSetup.Path)).Body);
        var first = Regex.Match(form, "<button[^>]*>[^<]*</button>").Value;
        Assert.Equal("<button type=\"submit\" class=\"sr-only\" tabindex=\"-1\" aria-hidden=\"true\">Check your setup</button>", first);
        Assert.DoesNotContain("formaction", first);
        Assert.True(form.IndexOf(first, StringComparison.Ordinal) < form.IndexOf("formaction", StringComparison.Ordinal));
        Assert.Contains(".sr-only{position:absolute;", AppHtml.Stylesheet);
        Assert.DoesNotContain(".sr-only{display:none", AppHtml.Stylesheet);

        await using var cannot = await StartAsync(sharedApp: false);   // the check can't run: Enter does nothing
        var noCheck = SetupForm((await (await SignIn(cannot.F)).GetTextAsync(AppAzureSetup.Path)).Body);
        Assert.Equal("<button type=\"submit\" disabled hidden aria-hidden=\"true\"></button>", Regex.Match(noCheck, "<button[^>]*>[^<]*</button>").Value);
    }

    [Fact]   // review F2. mutation: the link to pass on is missing, or is a link
    public async Task An_admin_who_is_not_the_directory_administrator_can_pass_the_approval_page_on()
    {
        await using var site = await StartAsync();
        var page = (await (await SignIn(site.F)).GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains($"<p class=\"fine\">{AppHtml.E("Not a directory administrator? Send them this link:")}</p><pre class=\"snippet\">{AppHtml.E(ApprovalPage)}</pre>", page);
        Assert.DoesNotContain("href=\"https://login.microsoftonline.com", page);
        OnboardingChecksTests.AssertNoOperatorWords(Visible(page));
    }

    // ---- item 6: the developer's page names the feed

    [Fact]
    public async Task The_developers_page_names_the_feed_with_its_address_under_details()
    {
        await using var site = await StartAsync();
        await AdminCheckPassedAsync(site);
        var settings = (await site.F.App.Services.GetRequiredService<IFeedCheckStore>().LatestAsync("selfco", FeedCheckKind.Settings, default))!;
        Assert.Equal(new FeedNames(FakeAzureDevOps.Org, FakeAzureDevOps.ProjectName, FakeAzureDevOps.FeedName), settings.Names);

        var dev = await SignInDeveloper(site.F);
        var page = (await dev.GetTextAsync(AppAzureSetup.DeveloperPath)).Body;
        Assert.Contains("<dt>Feed</dt><dd>" + AppHtml.E($"{FakeAzureDevOps.Org} / {FakeAzureDevOps.ProjectName} / {FakeAzureDevOps.FeedName}") +
                        "<details><summary>Details</summary><p>" + AppHtml.E(settings.FeedIndexUrl) + "</p></details></dd>", page);
        OnboardingChecksTests.AssertNoOperatorWords(Visible(page));

        // an older check, recorded before migration 16: the address, as before
        await Scalar(site.F, "UPDATE feed_checks SET feed_names = NULL");
        var old = (await dev.GetTextAsync(AppAzureSetup.DeveloperPath)).Body;
        Assert.Contains("<dt>Feed</dt><dd>" + AppHtml.E(settings.FeedIndexUrl) + "</dd>", old);
    }

    [Fact]
    public void An_organisation_scoped_feed_is_named_organisation_then_feed() =>
        Assert.Equal("Contoso / public-packages", AppAzureSetup.FeedLabel(new FeedNames("Contoso", null, "public-packages")));

    // ---- item 10: Connect's fresh sign-in says how it went

    [Fact]   // mutation: m ignored
    public async Task Back_from_a_confirmed_fresh_sign_in_the_page_says_choose_connect()
    {
        await using var site = await StartAsync();
        var f = site.F;
        await SeedChecksAsync(f);
        var b = await SignIn(f);
        var page = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.ReauthDoneLine), page);
        f.Clock.Advance(TimeSpan.FromMinutes(16));   // stale: Connect would ask for a fresh sign-in

        var start = await Post(b, page, AppAzureSetup.ReauthPath, true);
        f.AppIdentity.Next = Person(DirS, authTime: f.Clock.UtcNow);   // Microsoft confirms a sign-in just now
        var back = await b.GetAsync(start.Headers.Location!.OriginalString);
        Assert.Equal("/setup/azure?m=reauth", back.Headers.Location!.OriginalString);
        var after = (await b.GetTextAsync(back.Headers.Location!.OriginalString)).Body;
        Assert.Contains(AppHtml.E(AppAzureSetup.ReauthDoneLine), after);
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.ReauthUnconfirmedLine(TestOperator.Support)), after);
        Assert.Contains($">{AppAzureSetup.ConnectButton}</button>", after);
        OnboardingChecksTests.AssertNoOperatorWords(Visible(after));
    }

    [Fact]   // mutation: m ignored
    public async Task Back_from_an_unconfirmed_sign_in_the_page_says_microsoft_did_not_confirm()
    {
        await using var site = await StartAsync();
        var f = site.F;
        await SeedChecksAsync(f);
        var b = await SignIn(f);
        var page = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        f.Clock.Advance(TimeSpan.FromMinutes(16));

        var start = await Post(b, page, AppAzureSetup.ReauthPath, true);
        f.AppIdentity.Next = Person(DirS, authTime: f.Clock.UtcNow.AddHours(-2));   // a silent single sign-on: not a recent sign-in
        var back = await b.GetAsync(start.Headers.Location!.OriginalString);
        var after = (await b.GetTextAsync(back.Headers.Location!.OriginalString)).Body;
        Assert.Contains(AppHtml.E(AppAzureSetup.ReauthUnconfirmedLine(TestOperator.Support)), after);
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.ReauthDoneLine), after);
        OnboardingChecksTests.AssertNoOperatorWords(Visible(after));

        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.ReauthUnconfirmedLine(TestOperator.Support)), (await b.GetTextAsync(AppAzureSetup.Path)).Body);   // only when coming back
    }

    [Fact]   // review F4. mutation: no line when signed in but nothing to connect
    public async Task Back_from_a_confirmed_sign_in_with_nothing_to_connect_the_page_says_check_again()
    {
        await using var site = await StartAsync();
        var b = await SignIn(site.F);   // fresh, but no checks: no Connect offer
        var page = (await b.GetTextAsync(AppAzureSetup.Path + "?m=reauth")).Body;
        Assert.Contains(AppHtml.E(AppAzureSetup.ConnectStaleLine), page);
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.ReauthDoneLine), page);
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.ConnectStaleLine), (await b.GetTextAsync(AppAzureSetup.Path)).Body);
    }

    [Fact]
    public async Task Once_connected_the_reauth_return_says_nothing_more()
    {
        await using var site = await StartAsync();
        var b = await Connected(site);
        var page = (await b.GetTextAsync(AppAzureSetup.Path + "?m=reauth")).Body;
        Assert.Contains(AppHtml.E(AppAzureSetup.ConnectedLine), page);
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.ReauthDoneLine), page);
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.ReauthUnconfirmedLine(TestOperator.Support)), page);
    }
}
