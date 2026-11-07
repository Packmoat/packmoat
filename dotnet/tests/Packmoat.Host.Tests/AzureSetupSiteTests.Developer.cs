using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Packmoat.Application;
using Packmoat.Host.Customer;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// The developer proof through the running host (#64 I2): who may run it and when, the step-up (read-only Azure Artifacts scope,
/// prompt=login, max_age, the developer purpose), the callback (a Developer, never an Admin; the same person; fresh), the probe through
/// the fake Azure DevOps, the developer row, the Admin's waiting and proof cards, Connect, and the sentinel tokens.
/// </summary>
public partial class AzureSetupSiteTests
{
    private static ClaimsPrincipal Developer(string oid = Grace, string? role = CustomerSignIn.DeveloperRole, DateTimeOffset? authTime = null, string? acct = null,
        string? secondRole = null, string dir = DirS)
    {
        var claims = new List<Claim>
        {
            new("iss", $"https://login.microsoftonline.com/{dir}/v2.0"), new("aud", AppClient), new("tid", dir), new("oid", oid), new("name", "Grace Hopper"),
        };
        if (role is not null) claims.Add(new Claim("roles", role));
        if (secondRole is not null) claims.Add(new Claim("roles", secondRole));
        if (acct is not null) claims.Add(new Claim("acct", acct));
        if (authTime is { } at) claims.Add(new Claim("auth_time", at.ToUnixTimeSeconds().ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "FakeEntra", "name", "roles"));
    }

    private static async Task<AdminBrowser> SignInDeveloper(AdminSiteFixture f, string? role = CustomerSignIn.DeveloperRole, string? acct = null)
    {
        f.AppIdentity.Next = Developer(role: role, authTime: f.Clock.UtcNow, acct: acct);
        var b = f.Browser(AppHost);
        var start = await b.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, (await b.GetAsync(start.Headers.Location!.OriginalString)).StatusCode);
        return b;
    }

    /// <summary>The Admin's settings check, passed (private included), as the wizard records it.</summary>
    private static async Task AdminCheckPassedAsync(Site site)
    {
        var admin = await SignIn(site.F);
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, admin)).StatusCode);
        var settings = await site.F.App.Services.GetRequiredService<IFeedCheckStore>().LatestAsync("selfco", FeedCheckKind.Settings, default);
        Assert.Equal(FeedCheckOutcome.Passed, settings!.Outcome);
    }

    /// <summary>The developer's start, then the step-up callback as <paramref name="person"/> (default: the same Developer, signed in just now), with the developer's token.</summary>
    private static async Task<HttpResponseMessage> RunDeveloperCheck(Site site, AdminBrowser b, ClaimsPrincipal? person = null)
    {
        var (_, page, _) = await b.GetTextAsync(AppAzureSetup.DeveloperPath);
        var start = await Post(b, page, AppAzureSetup.DeveloperCheckPath, true);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        site.SignIn.Next = person ?? Developer(authTime: site.F.Clock.UtcNow);
        site.SignIn.AccessToken = FakeAzureDevOps.DeveloperToken;
        return await b.GetAsync(start.Headers.Location!.OriginalString);
    }

    private static Task<FeedCheck?> LatestDeveloperRow(AdminSiteFixture f) => f.App.Services.GetRequiredService<IFeedCheckStore>().LatestAsync("selfco", FeedCheckKind.Developer, default);

    private static List<(Uri Url, string? Authorization)> WithDeveloperToken(Site site) =>
        site.Ado.Seen.Where(s => s.Authorization == "Bearer " + FakeAzureDevOps.DeveloperToken).ToList();

    // ---- who, and when

    [Theory]
    [InlineData("off")]
    [InlineData("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")]
    public async Task With_the_switch_off_for_the_directory_the_developer_pages_do_not_exist(string flag)
    {
        await using var site = await StartAsync(flag);
        var b = await SignInDeveloper(site.F);
        var home = (await b.GetTextAsync("/home")).Body;
        Assert.DoesNotContain(AppAzureSetup.DeveloperPath, home);
        Assert.Equal(404, (await b.GetTextAsync(AppAzureSetup.DeveloperPath)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(b, home, AppAzureSetup.DeveloperCheckPath, true)).StatusCode);
        Assert.Equal(0, site.SignIn.Challenges);
        Assert.Equal("/home", AppSite.ReturnPath(AppAzureSetup.DeveloperPath, azureSetup: false));
    }

    [Fact]
    public async Task An_admin_gets_developers_only_and_no_sign_in_starts()
    {
        await using var site = await StartAsync();
        await AdminCheckPassedAsync(site);
        var challenges = site.SignIn.Challenges;
        var admin = await SignIn(site.F);
        var (status, page, _) = await admin.GetTextAsync(AppAzureSetup.DeveloperPath);
        Assert.Equal(403, status);
        Assert.Contains(AppAzureSetup.DevelopersOnlyTitle, page);
        Assert.Contains(AppHtml.E(AppAzureSetup.DevelopersOnlyLine), page);
        var post = await Post(admin, page, AppAzureSetup.DeveloperCheckPath, true);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(challenges, site.SignIn.Challenges);
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.DeveloperCardSentence), (await admin.GetTextAsync("/home")).Body);
    }

    [Fact]
    public async Task A_developer_before_the_settings_check_passes_is_told_not_ready_yet()
    {
        await using var site = await StartAsync();
        var b = await SignInDeveloper(site.F);
        var (status, page, _) = await b.GetTextAsync(AppAzureSetup.DeveloperPath);
        Assert.Equal(409, status);
        Assert.Contains(AppAzureSetup.NotReadyTitle, page);
        Assert.Contains(AppHtml.E(AppAzureSetup.NotReadyLine), page);
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.DeveloperCardSentence), (await b.GetTextAsync("/home")).Body);
        // a failing settings check is not ready either
        site.Ado.Upstreams = new JsonArray(new JsonObject { ["name"] = "evil", ["protocol"] = "nuget", ["location"] = "https://evil.example/v3/index.json" });
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, await SignIn(site.F))).StatusCode);
        Assert.Equal(409, (await b.GetTextAsync(AppAzureSetup.DeveloperPath)).Status);
    }

    // ---- the Admin's waiting card

    [Fact]
    public async Task After_a_passing_settings_check_the_admin_waits_for_a_developer_with_the_link_to_send()
    {
        await using var site = await StartAsync();
        await AdminCheckPassedAsync(site);
        var admin = await SignIn(site.F);
        var page = (await admin.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains(AppAzureSetup.WaitingTitle, page);
        Assert.Contains(AppHtml.E(AppAzureSetup.WaitingSendLink), page);
        Assert.Contains($"<pre class=\"snippet\">https://{AppHost}/setup/developer</pre>", page);
        Assert.Contains(AppHtml.E(AppAzureSetup.WaitingHow), page);
        // #118: if Microsoft wants an administrator's approval, the link for the Admin's own directory
        Assert.Contains($"<p>{AppHtml.E(AppAzureSetup.WaitingNeedAdmin)}</p><pre class=\"snippet\">{AppHtml.E(PackmoatConsentUrl(DirS))}</pre>", page);
        Assert.Contains(AppHtml.E(AppAzureSetup.WaitingAutoJoinOn), page);   // no setting stored: auto-join is on
        Assert.Contains(AppHtml.E(AppAzureSetup.WaitingSolo), page);
        Assert.DoesNotContain(AppAzureSetup.ConnectPath, page);
        await site.F.AutoJoin.SetAsync("selfco", false, site.F.Clock.UtcNow, "test", default);
        var off = (await admin.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains(AppHtml.E(AppAzureSetup.WaitingAutoJoinOff), off);
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.WaitingAutoJoinOn), off);
    }

    // ---- the developer's card, page and step-up

    [Fact]
    public async Task The_developer_gets_the_home_card_and_a_page_that_targets_the_checked_feed_with_nothing_to_type()
    {
        await using var site = await StartAsync();
        await AdminCheckPassedAsync(site);
        var b = await SignInDeveloper(site.F);
        var home = (await b.GetTextAsync("/home")).Body;
        Assert.Contains(AppHtml.E(AppAzureSetup.DeveloperCardSentence), home);
        Assert.Contains($"<a class=\"btn\" href=\"/setup/developer\">{AppAzureSetup.DeveloperCardButton}</a>", home);

        var (status, page, r) = await b.GetTextAsync(AppAzureSetup.DeveloperPath);
        Assert.Equal(200, status);
        Assert.Contains(AppHtml.E(CheckedFeed), page);
        Assert.Contains(AppHtml.E(AppAzureSetup.DeveloperStep), page);
        Assert.Contains($">{AppAzureSetup.DeveloperStepButton}</button>", page);
        Assert.Contains(AppHtml.E(AppAzureSetup.PrivacyLine), page);
        Assert.DoesNotContain("<input id=", page);   // nothing to type
        Assert.Contains("https://login.microsoftonline.com", r.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(AppAzureSetup.DeveloperPath, AppSite.ReturnPath(AppAzureSetup.DeveloperPath, azureSetup: true));   // the link survives a sign-in
    }

    [Fact]
    public async Task The_developer_step_up_asks_for_exactly_the_read_scope_with_prompt_login_max_age_and_the_developer_purpose()
    {
        await using var site = await StartAsync();
        await AdminCheckPassedAsync(site);
        var b = await SignInDeveloper(site.F);
        var (_, page, _) = await b.GetTextAsync(AppAzureSetup.DeveloperPath);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(b, page, AppAzureSetup.DeveloperCheckPath, true)).StatusCode);
        var challenge = site.SignIn.LastChallenge!;
        Assert.Equal(new[] { "openid", "profile", "499b84ac-1321-427f-aa17-267ca6975798/vso.packaging" },
            challenge.GetParameter<ICollection<string>>(OpenIdConnectParameterNames.Scope)!.ToArray());
        Assert.DoesNotContain(challenge.GetParameter<ICollection<string>>(OpenIdConnectParameterNames.Scope)!, s => s.Contains("vso.graph") || s.Contains("vso.identity") || s.Contains("vso.project") || s == "offline_access");
        Assert.Equal("login", challenge.GetParameter<string>(OpenIdConnectParameterNames.Prompt));
        Assert.Equal(TimeSpan.FromMinutes(15), challenge.GetParameter<TimeSpan?>(OpenIdConnectParameterNames.MaxAge));
        Assert.Equal(AppAzureSetup.DeveloperPurpose, challenge.Items[AppAzureSetup.PurposeItem]);
        Assert.Equal(AppAzureSetup.DeveloperPath, challenge.RedirectUri);
        // the Admin's settings step-up carries the settings purpose and keeps the scheme's scopes
        await StartCheck(await SignIn(site.F), GoodNames);
        Assert.Equal(AppAzureSetup.SettingsPurpose, site.SignIn.LastChallenge!.Items[AppAzureSetup.PurposeItem]);
        Assert.Null(site.SignIn.LastChallenge.GetParameter<ICollection<string>>(OpenIdConnectParameterNames.Scope));
    }

    [Fact]
    public async Task The_developer_start_needs_antiforgery_and_this_sites_origin()
    {
        await using var site = await StartAsync();
        await AdminCheckPassedAsync(site);
        var challenges = site.SignIn.Challenges;
        var b = await SignInDeveloper(site.F);
        var page = (await b.GetTextAsync(AppAzureSetup.DeveloperPath)).Body;
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(b, page, AppAzureSetup.DeveloperCheckPath, origin: false)).StatusCode);
        var noToken = await b.SendAsync(HttpMethod.Post, AppAzureSetup.DeveloperCheckPath, Array.Empty<KeyValuePair<string, string>>(), r => r.Headers.TryAddWithoutValidation("Origin", Origin));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal(challenges, site.SignIn.Challenges);
    }

    // ---- the whole proof, then Connect

    [Fact]
    public async Task The_whole_developer_proof_records_a_developer_row_and_the_admin_can_connect()
    {
        await using var site = await StartAsync();
        var f = site.F;
        await AdminCheckPassedAsync(site);
        var dev = await SignInDeveloper(f);
        var callback = await RunDeveloperCheck(site, dev);
        Assert.Equal(HttpStatusCode.SeeOther, callback.StatusCode);
        Assert.Equal(AppAzureSetup.DeveloperPath, callback.Headers.Location!.OriginalString);
        Assert.False(WroteSessionCookie(callback));

        var row = await LatestDeveloperRow(f);
        Assert.NotNull(row);
        Assert.Equal((FeedCheckOutcome.Passed, DirS, Grace, "Grace Hopper", DirS, CheckedFeed, FakeAzureDevOps.SharedApp),
            (row.Outcome, row.CheckedByTid, row.CheckedByOid, row.CheckedByName, row.AzureTenantId, row.FeedIndexUrl, row.ClientId));
        Assert.Equal(new[] { "developer", "bypass" }, row.Results.Select(r => r.Id));
        // the developer's token went to the feed list and the exact index only; Packmoat's identity read the index first
        Assert.Equal(new[] { "https://feeds.dev.azure.com/contoso/_apis/packaging/feeds?api-version=7.1", CheckedFeed }, WithDeveloperToken(site).Select(s => s.Url.AbsoluteUri));
        Assert.All(site.Packmoat.SignIns, s => Assert.Equal((DirS, FakeAzureDevOps.SharedApp), (s.AuthorityTenantId, s.ClientId)));

        var page = (await dev.GetTextAsync(AppAzureSetup.DeveloperPath)).Body;
        Assert.Contains(AppHtml.E(AppAzureSetup.DeveloperPass), page);
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.DeveloperCardSentence), (await dev.GetTextAsync("/home")).Body);   // done: no card

        var admin = await SignIn(f);
        var setup = (await admin.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains(AppHtml.E("Grace Hopper confirmed on " + f.Clock.UtcNow.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture) + " that they can't reach the feed directly."), setup);
        Assert.Contains($">{AppAzureSetup.ConnectButton}</button>", setup);
        Assert.DoesNotContain(AppAzureSetup.WaitingTitle, setup);
        var connect = await Post(admin, setup, AppAzureSetup.ConnectPath, true, ConnectFields((
            (await f.App.Services.GetRequiredService<IFeedCheckStore>().LatestAsync("selfco", FeedCheckKind.Settings, default))!.Id, row.Id)));
        Assert.Equal(HttpStatusCode.SeeOther, connect.StatusCode);
        Assert.Equal(row.Id, await Scalar(f, "SELECT developer_check_id FROM shared_app_verifications WHERE tenant_id='selfco'"));
        Assert.Contains(AppHtml.E(AppAzureSetup.ConnectedLine), (await admin.GetTextAsync(AppAzureSetup.Path)).Body);

        // and the developer can now make a token (the core serves every tenant; the hosted module's gate is the hosted tests')
        Assert.NotNull((await CreateToken(dev)).Token);
    }

    [Fact]
    public async Task An_auto_joined_developer_can_prove_while_auto_join_is_on()
    {
        await using var site = await StartAsync();
        await AdminCheckPassedAsync(site);
        var dev = await SignInDeveloper(site.F, role: null, acct: "0");   // no role: auto-joined as Developer
        Assert.Equal(HttpStatusCode.SeeOther, (await RunDeveloperCheck(site, dev, Developer(role: null, acct: "0", authTime: site.F.Clock.UtcNow))).StatusCode);
        Assert.Equal(FeedCheckOutcome.Passed, (await LatestDeveloperRow(site.F))!.Outcome);
    }

    /// <summary>
    /// The auto-join setting is read again at the callback: an auto-joined session is over once auto-join is off, even when the new
    /// sign-in itself would pass (here it carries the Developer role).
    /// </summary>
    [Fact]
    public async Task An_auto_joined_session_is_refused_at_the_callback_once_auto_join_is_turned_off()
    {
        await using var site = await StartAsync();
        await AdminCheckPassedAsync(site);
        var dev = await SignInDeveloper(site.F, role: null, acct: "0");   // auto-joined
        var (_, page, _) = await dev.GetTextAsync(AppAzureSetup.DeveloperPath);
        var start = await Post(dev, page, AppAzureSetup.DeveloperCheckPath, true);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        await site.F.AutoJoin.SetAsync("selfco", false, site.F.Clock.UtcNow, "test", default);   // turned off while the person is at Microsoft
        site.SignIn.Next = Developer(authTime: site.F.Clock.UtcNow);
        site.SignIn.AccessToken = FakeAzureDevOps.DeveloperToken;
        var r = await dev.GetAsync(start.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains("Signed out", await r.Content.ReadAsStringAsync());
        Assert.False(WroteSessionCookie(r));
        Assert.Empty(WithDeveloperToken(site));
        Assert.Null(await LatestDeveloperRow(site.F));
    }

    [Theory]
    [InlineData("admin")]        // an Admin in the new token: Admins can often read the feed directly
    [InlineData("both")]         // both roles mean Admin
    [InlineData("oid")]          // someone else in the same directory
    [InlineData("stale")]        // auth_time more than 15 minutes ago
    [InlineData("no-auth-time")]
    [InlineData("no-role")]      // auto-join is off for this tenant: not a Developer at all
    public async Task The_developer_callback_refuses_an_admin_another_person_or_a_stale_sign_in_and_probes_nothing(string what)
    {
        await using var site = await StartAsync();
        await AdminCheckPassedAsync(site);
        var dev = await SignInDeveloper(site.F);
        if (what == "no-role") await site.F.AutoJoin.SetAsync("selfco", false, site.F.Clock.UtcNow, "test", default);
        var now = site.F.Clock.UtcNow;
        var person = what switch
        {
            "admin" => Developer(role: CustomerSignIn.AdminRole, authTime: now),
            "both" => Developer(secondRole: CustomerSignIn.AdminRole, authTime: now),
            "oid" => Developer(oid: "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee", authTime: now),
            "stale" => Developer(authTime: now - TimeSpan.FromMinutes(16)),
            "no-role" => Developer(role: null, acct: "0", authTime: now),
            _ => Developer(),
        };
        var r = await RunDeveloperCheck(site, dev, person);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains(what switch
        {
            "admin" or "both" => AppAzureSetup.DevelopersOnlyTitle,
            "oid" => "Different account",
            "no-role" => "No Packmoat role",
            _ => AppHtml.E("Microsoft didn't confirm a recent sign-in."),
        }, body);
        Assert.False(WroteSessionCookie(r));
        Assert.Empty(WithDeveloperToken(site));
        Assert.Null(await LatestDeveloperRow(site.F));
    }

    [Theory]
    [InlineData(AppAzureSetup.SettingsPurpose, AppAzureSetup.DeveloperPath)]   // a settings sign-in sent back to the developer page
    [InlineData(AppAzureSetup.DeveloperPurpose, AppAzureSetup.Path)]           // a developer sign-in sent back to the settings page
    [InlineData(null, AppAzureSetup.DeveloperPath)]
    [InlineData(null, AppAzureSetup.Path)]
    [InlineData("other", AppAzureSetup.DeveloperPath)]
    public async Task A_purpose_that_does_not_match_the_return_page_is_refused_and_nothing_runs(string? purpose, string redirect)
    {
        await using var site = await StartAsync();
        await AdminCheckPassedAsync(site);
        var seen = site.Ado.Seen.Count;
        var rows = await FeedCheckRows(site.F);
        foreach (var asAdmin in new[] { false, true })
        {
            var b = asAdmin ? await SignIn(site.F) : await SignInDeveloper(site.F);
            if (asAdmin) await StartCheck(b, GoodNames);
            else Assert.Equal(HttpStatusCode.Redirect, (await Post(b, (await b.GetTextAsync(AppAzureSetup.DeveloperPath)).Body, AppAzureSetup.DeveloperCheckPath, true)).StatusCode);
            var challenge = site.SignIn.LastChallenge!;
            if (purpose is null) challenge.Items.Remove(AppAzureSetup.PurposeItem);
            else challenge.Items[AppAzureSetup.PurposeItem] = purpose;
            challenge.RedirectUri = redirect;
            site.SignIn.Next = asAdmin ? Person(DirS, authTime: site.F.Clock.UtcNow) : Developer(authTime: site.F.Clock.UtcNow);
            site.SignIn.AccessToken = asAdmin ? FakeAzureDevOps.Token : FakeAzureDevOps.DeveloperToken;
            var r = await b.GetAsync(AppAzureSetup.CallbackPath + "?fake=1");
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.False(WroteSessionCookie(r));
        }
        Assert.Equal(seen, site.Ado.Seen.Count);   // no token was used
        Assert.Equal(rows, await FeedCheckRows(site.F));
    }

    // ---- what the developer sees

    [Fact]
    public async Task A_developer_who_can_read_the_feed_is_told_so_and_it_is_recorded_as_failed()
    {
        await using var site = await StartAsync();
        await AdminCheckPassedAsync(site);
        site.Ado.DeveloperIndexStatus = 200;
        var dev = await SignInDeveloper(site.F);
        Assert.Equal(HttpStatusCode.SeeOther, (await RunDeveloperCheck(site, dev)).StatusCode);
        Assert.Equal(FeedCheckOutcome.Failed, (await LatestDeveloperRow(site.F))!.Outcome);
        Assert.Contains(AppHtml.E(AppAzureSetup.DeveloperFail), (await dev.GetTextAsync(AppAzureSetup.DeveloperPath)).Body);
        Assert.DoesNotContain(AppAzureSetup.ConnectPath, (await (await SignIn(site.F)).GetTextAsync(AppAzureSetup.Path)).Body);
    }

    [Fact]
    public async Task A_developer_who_cannot_list_feeds_or_gets_404_is_not_verified()
    {
        await using var site = await StartAsync();
        await AdminCheckPassedAsync(site);
        var dev = await SignInDeveloper(site.F);
        site.Ado.DeveloperListStatus = 401;
        Assert.Equal(HttpStatusCode.SeeOther, (await RunDeveloperCheck(site, dev)).StatusCode);
        Assert.Equal(FeedCheckOutcome.Incomplete, (await LatestDeveloperRow(site.F))!.Outcome);
        Assert.Contains(AppHtml.E(AppAzureSetup.DeveloperNotMember), (await dev.GetTextAsync(AppAzureSetup.DeveloperPath)).Body);

        site.Ado.DeveloperListStatus = 200;
        site.Ado.DeveloperIndexStatus = 404;
        Assert.Equal(HttpStatusCode.SeeOther, (await RunDeveloperCheck(site, dev)).StatusCode);
        Assert.Equal(FeedCheckOutcome.Incomplete, (await LatestDeveloperRow(site.F))!.Outcome);
        Assert.Contains(AppHtml.E(AppAzureSetup.DeveloperUnclear), (await dev.GetTextAsync(AppAzureSetup.DeveloperPath)).Body);
        Assert.DoesNotContain(AppAzureSetup.ConnectPath, (await (await SignIn(site.F)).GetTextAsync(AppAzureSetup.Path)).Body);
    }

    [Fact]
    public async Task A_bypass_is_shown_to_the_developer_and_on_the_admins_proof_card_without_blocking_connect()
    {
        await using var site = await StartAsync();
        await AdminCheckPassedAsync(site);
        site.Ado.OtherFeeds[0].Upstreams = new JsonArray(new JsonObject { ["protocol"] = "nuget", ["upstreamSourceType"] = "public", ["location"] = "https://api.nuget.org/v3/index.json" });
        var dev = await SignInDeveloper(site.F);
        Assert.Equal(HttpStatusCode.SeeOther, (await RunDeveloperCheck(site, dev)).StatusCode);
        Assert.Contains(AppHtml.E(string.Format(BypassAdvisory.FoundSentence, "team-packages")), (await dev.GetTextAsync(AppAzureSetup.DeveloperPath)).Body);
        var setup = (await (await SignIn(site.F)).GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains(AppHtml.E(string.Format(BypassAdvisory.AdminFoundSentence, "team-packages")), setup);   // addressed to the Admin (#119)
        Assert.Contains($">{AppAzureSetup.ConnectButton}</button>", setup);
    }

    [Fact]
    public async Task Upstream_sources_hidden_from_the_developer_say_bypass_not_checked()
    {
        await using var site = await StartAsync();
        await AdminCheckPassedAsync(site);
        site.Ado.DeveloperSeesUpstreams = false;
        var dev = await SignInDeveloper(site.F);
        Assert.Equal(HttpStatusCode.SeeOther, (await RunDeveloperCheck(site, dev)).StatusCode);
        Assert.Contains(AppHtml.E(string.Format(BypassAdvisory.NotCheckedSentence, 1)), (await dev.GetTextAsync(AppAzureSetup.DeveloperPath)).Body);
        Assert.Equal(CheckStatus.Skip, (await LatestDeveloperRow(site.F))!.Results.Single(r => r.Id == "bypass").Status);
    }

    // ---- private, through the host

    [Fact]
    public async Task The_admins_check_tests_packmoats_own_identity_on_the_other_feeds_and_fails_when_it_can_read_one()
    {
        await using var site = await StartAsync();
        site.Ado.OtherFeeds[0].PackmoatStatus = 200;
        var b = await SignIn(site.F);
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, b)).StatusCode);
        var check = (await site.F.App.Services.GetRequiredService<IFeedCheckStore>().LatestAsync("selfco", FeedCheckKind.Settings, default))!;
        Assert.Equal(FeedCheckOutcome.Failed, check.Outcome);
        Assert.Equal("Packmoat feed access can read 1 other feed: team-packages. " + PrivateFeedVerdict.FailHint, check.Results.Single(r => r.Id == "private").Detail);
        Assert.All(site.Ado.Seen.Where(s => s.Url.Host == "pkgs.dev.azure.com"), s => Assert.Equal(FakeAzureDevOps.PackmoatAuthorization, s.Authorization));
        Assert.Equal((DirS, FakeAzureDevOps.SharedApp), (site.Packmoat.SignIns.First().AuthorityTenantId, site.Packmoat.SignIns.First().ClientId));
        Assert.Contains(AppHtml.E(PrivateFeedVerdict.Title), (await b.GetTextAsync(AppAzureSetup.Path)).Body);
    }

    // ---- the clients and the tokens

    /// <summary>The production wiring of the two new named clients: each guard sits in front of the transport (here the fake).</summary>
    [Fact]
    public async Task The_developer_and_shared_app_clients_from_the_app_refuse_other_hosts_before_the_transport()
    {
        await using var site = await StartAsync();
        var factory = site.F.App.Services.GetRequiredService<IHttpClientFactory>();
        var developer = factory.CreateClient(DeveloperFeedProbe.ClientName);
        foreach (var url in new[] { "https://vssps.dev.azure.com/contoso/_apis/graph/users", "https://dev.azure.com/contoso/_apis/projects", "https://evil.example/",
                     "https://pkgs.dev.azure.com/contoso/_packaging/public-packages/nuget/v3/index.json" })
            await Assert.ThrowsAsync<HttpRequestException>(() => developer.GetAsync(url));
        var shared = factory.CreateClient(SharedAppFeedProbe.ClientName);
        foreach (var url in new[] { "https://feeds.dev.azure.com/contoso/_apis/packaging/feeds", "https://evil.example/", CheckedFeed + "?x=1" })
            await Assert.ThrowsAsync<HttpRequestException>(() => shared.GetAsync(url));
        Assert.Empty(site.Ado.Seen);
        Assert.Equal(HttpStatusCode.Unauthorized, (await developer.GetAsync("https://feeds.dev.azure.com/contoso/_apis/packaging/feeds")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await shared.GetAsync(CheckedFeed)).StatusCode);
        Assert.Equal(2, site.Ado.Seen.Count);
    }

    [Fact]
    public async Task No_token_reaches_a_response_a_log_line_or_the_database()
    {
        await using var site = await StartAsync();
        var f = site.F;
        var responses = new List<string>();
        async Task Keep(HttpResponseMessage r) => responses.Add(r.StatusCode + "\n" + r.Headers + "\n" + r.Content.Headers + "\n" + await r.Content.ReadAsStringAsync());

        var admin = await SignIn(f);
        await Keep(await RunCheck(site, admin));
        await Keep(await admin.GetAsync(AppAzureSetup.Path));
        var dev = await SignInDeveloper(f);
        await Keep(await RunDeveloperCheck(site, dev));
        await Keep(await dev.GetAsync(AppAzureSetup.DeveloperPath));
        await Keep(await dev.GetAsync("/home"));
        // a refused callback, a failing probe and a can-read result too
        await Keep(await RunDeveloperCheck(site, dev, Developer(role: CustomerSignIn.AdminRole, authTime: f.Clock.UtcNow)));
        site.Ado.DeveloperListStatus = 203;
        await Keep(await RunDeveloperCheck(site, dev));
        site.Ado.DeveloperListStatus = 200;
        site.Ado.DeveloperIndexStatus = 200;
        await Keep(await RunDeveloperCheck(site, dev));
        await Keep(await dev.GetAsync(AppAzureSetup.DeveloperPath));
        await Keep(await (await SignIn(f)).GetAsync(AppAzureSetup.Path));

        Assert.True(WithDeveloperToken(site).Count >= 4);   // the developer's token was used
        Assert.NotEmpty(site.Packmoat.SignIns);              // and Packmoat's identity
        foreach (var token in new[] { FakeAzureDevOps.DeveloperToken, FakeAzureDevOps.PackmoatToken, FakeAzureDevOps.Token, FakeAzureDevOps.PackmoatAuthorization["Basic ".Length..] })
        {
            // Never Assert.DoesNotContain(token, ...): a failure message would print the token.
            Assert.False(responses.Any(r => r.Contains(token, StringComparison.Ordinal)), "a response carried a token");
            Assert.False(dev.SetCookieLog.Concat(admin.SetCookieLog).Any(c => c.Contains(token, StringComparison.Ordinal)), "a cookie carried a token");
            lock (site.Logs.Lines) Assert.False(site.Logs.Lines.Any(l => l.Contains(token, StringComparison.Ordinal)), "a log line carried a token");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(f.Dir, "packmoat.db*"))
        {
            var bytes = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
            foreach (var token in new[] { FakeAzureDevOps.DeveloperToken, FakeAzureDevOps.PackmoatToken, FakeAzureDevOps.Token })
                Assert.False(bytes.Contains(token, StringComparison.Ordinal), Path.GetFileName(file) + " holds a token");
        }
        Assert.True(await Scalar(f, "SELECT COUNT(*) FROM feed_checks WHERE kind='developer'") is long n && n >= 3);
    }
}
