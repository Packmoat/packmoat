using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host;
using Packmoat.Host.Customer;
using Packmoat.Host.Web;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// The Azure setup wizard through the running host (#63 H2): the switch, Admins only, the pages and their wording, the step-up sign-in
/// and its callback (same person, Admin, fresh, no cookie), feed_checks written for self-made tenants only, check-only for an
/// operator-made shared tenant, the limits, and a sentinel token that never reaches a response, a log line or the database.
/// </summary>
[Collection("sqlite-pools")]
public partial class AzureSetupSiteTests
{
    // ---- the switch

    [Theory]
    [InlineData(null)]
    [InlineData("off")]
    [InlineData("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")]   // allowlist without this directory
    public async Task With_the_switch_off_for_the_directory_nothing_exists_and_home_is_unchanged(string? flag)
    {
        await using var site = await StartAsync(flag);
        var f = site.F;
        var b = await SignIn(f);
        var home = (await b.GetTextAsync("/home")).Body;
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.HomeCardSentence), home);
        Assert.DoesNotContain(AppAzureSetup.Path, home);
        Assert.Equal(404, (await b.GetTextAsync(AppAzureSetup.Path)).Status);
        var post = await Post(b, home, AppAzureSetup.CheckPath, true, GoodNames);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal(0, site.SignIn.Challenges);
        site.SignIn.Next = Person(DirS, authTime: f.Clock.UtcNow);
        site.SignIn.LastChallenge = new AuthenticationProperties();
        Assert.Equal(404, (await b.GetTextAsync(AppAzureSetup.CallbackPath + "?code=x&state=y")).Status);   // the callback is not handled
        Assert.Equal(0L, await FeedCheckRows(f));
        Assert.Empty(site.Ado.Seen);
        if (flag is null or "off")
        {
            Assert.Equal(404, (await b.GetTextAsync(AppAzureSetup.FeedAppAddedPath)).Status);
            var consented = await b.GetTextAsync(AppAzureSetup.ConsentedPath);   // the Packmoat app's return page always exists (#118)
            Assert.Equal(200, consented.Status);
            Assert.Contains(AppHtml.E(AppAzureSetup.ConsentedLine), consented.Body);
            Assert.Null(await f.App.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync(AppAzureSetup.Scheme));
            Assert.Equal("/home", AppSite.ReturnPath(AppAzureSetup.Path));
        }
    }

    [Fact]
    public void The_switch_parses_like_the_checkout_switch()
    {
        Assert.Equal(AzureSetupMode.Off, AzureSetupSettings.Parse(null).Mode);
        Assert.Equal(AzureSetupMode.Off, AzureSetupSettings.Parse(" off ").Mode);
        Assert.Equal(AzureSetupMode.On, AzureSetupSettings.Parse("on").Mode);
        var list = AzureSetupSettings.Parse(DirS.ToUpperInvariant() + ", " + DirA);
        Assert.Equal(AzureSetupMode.Allowlist, list.Mode);
        Assert.True(list.Allows(DirS) && list.Allows(DirA) && !list.Allows(CustomerSiteTests.DirB));
        var bad = Assert.Throws<InvalidOperationException>(() => AzureSetupSettings.Parse("yes-please"));
        Assert.DoesNotContain("yes-please", bad.Message);
        Assert.Throws<InvalidOperationException>(() => AzureSetupSettings.Parse(CustomerSiteTests.Consumer));
    }

    // ---- who sees what

    [Fact]
    public async Task An_admin_of_a_self_made_tenant_sees_the_home_card_and_the_wizard_with_the_draft_wording()
    {
        await using var site = await StartAsync(flag: DirS);
        var b = await SignIn(site.F);
        var home = (await b.GetTextAsync("/home")).Body;
        Assert.Contains(AppHtml.E(AppAzureSetup.HomeCardSentence), home);
        Assert.Contains($"<a class=\"btn\" href=\"/setup/azure\">{AppAzureSetup.HomeCardButton}</a>", home);

        var (status, page, r) = await b.GetTextAsync(AppAzureSetup.Path);
        Assert.Equal(200, status);
        foreach (var sentence in new[] { AppAzureSetup.Intro, AppAzureSetup.OrgHelp, AppAzureSetup.ProjectHelp, AppAzureSetup.FeedHelp, AppAzureSetup.Step2,
                     AppAzureSetup.Step2Button, AppAzureSetup.Step3, AppAzureSetup.Step4, AppAzureSetup.Step4Button, AppAzureSetup.PrivacyLine })
            Assert.Contains(AppHtml.E(sentence), page);
        foreach (var line in AppAzureSetup.Checklist) Assert.Contains(AppHtml.E(line), page);
        Assert.Contains(AppHtml.E("az ad sp create --id " + FakeAzureDevOps.SharedApp), page);
        // The approval page: the feed access app, in the person's own directory, returning to the fixed page (now through step 2's
        // button, which keeps step 1 first: #119 item 2).
        var approve = await Post(b, page, AppAzureSetup.ApprovePath, true, GoodNames);
        Assert.Equal($"https://login.microsoftonline.com/{DirS}/adminconsent?client_id={FakeAzureDevOps.SharedApp}&redirect_uri=https%3A%2F%2F{AppHost}%2Fsetup%2Fazure%2Ffeed-app-added",
            approve.Headers.Location!.OriginalString);
        Assert.Contains($"action=\"{AppAzureSetup.CheckPath}\"", page);
        Assert.Contains("https://login.microsoftonline.com", r.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task A_developer_sees_the_sentence_without_the_button_and_gets_403_on_the_wizard()
    {
        await using var site = await StartAsync();
        var b = await SignIn(site.F, role: CustomerSignIn.DeveloperRole);
        var home = (await b.GetTextAsync("/home")).Body;
        Assert.Contains(AppHtml.E(AppAzureSetup.HomeCardSentence), home);
        Assert.DoesNotContain("href=\"/setup/azure\"", home);
        var (status, page, _) = await b.GetTextAsync(AppAzureSetup.Path);
        Assert.Equal(403, status);
        Assert.Contains("Admins only", page);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(b, home, AppAzureSetup.CheckPath, true, GoodNames)).StatusCode);
        Assert.Equal(0, site.SignIn.Challenges);
    }

    [Fact]
    public async Task The_feed_app_consent_return_page_needs_no_sign_in_changes_nothing_and_ignores_the_query()
    {
        await using var site = await StartAsync();
        foreach (var path in new[] { AppAzureSetup.FeedAppAddedPath })
        {
            var b = site.F.Browser(AppHost);   // signed out: the approver may not use Packmoat at all
            var r = await b.GetAsync(path + "?admin_consent=True&tenant=" + DirS + "&error=<script>ECHO</script>");
            var body = await r.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Contains(AppHtml.E(AppAzureSetup.Step2Return), body);
            Assert.DoesNotContain("ECHO", body);
            Assert.DoesNotContain(DirS, body);
            Assert.False(r.Headers.Contains("Set-Cookie"));
            Assert.Null(r.Headers.Location);
        }
        Assert.Equal(0L, await FeedCheckRows(site.F));
        Assert.Equal(0, site.SignIn.Challenges);
    }

    [Fact]
    public void The_organisation_consent_link_is_microsofts_v2_admin_consent_for_sign_in_and_the_azure_devops_scopes()
    {
        var s = new AppSiteSettings { Host = AppHost, ClientId = AppClient, SignInAvailable = true };
        Assert.Equal(PackmoatConsentUrl(DirS), AppAzureSetup.OrganisationConsentUrl(s, DirS));
        Assert.Equal(PackmoatConsentUrl("organizations"), AppAzureSetup.OrganisationConsentUrl(s, null));
        Assert.Equal(PackmoatConsentUrl("organizations"), AppAzureSetup.OrganisationConsentUrl(null, AppClient, "https://" + AppHost + "/setup/azure/consented"));
        // anything odd in a value stays inside its own parameter
        Assert.Equal("https://login.microsoftonline.com/a%2Fb%3Fc/v2.0/adminconsent?client_id=x%26y&redirect_uri=https%3A%2F%2Fh%2Fp%3Fq%3D1" +
            "&scope=openid%20profile%20499b84ac-1321-427f-aa17-267ca6975798%2Fvso.packaging%20499b84ac-1321-427f-aa17-267ca6975798%2Fvso.graph" +
            "%20499b84ac-1321-427f-aa17-267ca6975798%2Fvso.identity%20499b84ac-1321-427f-aa17-267ca6975798%2Fvso.project",
            AppAzureSetup.OrganisationConsentUrl("a/b?c", "x&y", "https://h/p?q=1"));
        // one source of truth: every Azure DevOps scope the check asks for is in the link, and nothing besides sign-in
        var url = AppAzureSetup.OrganisationConsentUrl(s, DirS);
        Assert.Equal(new[] { "openid", "profile" }.Concat(AppAzureSetup.AzureDevOpsScopes), Uri.UnescapeDataString(url.Split("&scope=")[1]).Split(' '));
        Assert.DoesNotContain(".default", url);
        Assert.DoesNotContain("prompt", url);
        Assert.DoesNotContain("offline_access", url);
        // the feed-access app's link stays v1 (that app asks for no permissions)
        Assert.Equal($"https://login.microsoftonline.com/{DirS}/adminconsent?client_id=c&redirect_uri=https%3A%2F%2Fh%2Fp",
            AppAzureSetup.AdminConsentUrl(DirS, "c", "https://h/p"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("off")]
    [InlineData("on")]
    public async Task The_packmoat_consent_return_page_always_exists_says_something_neutral_and_shows_nothing_from_the_reply(string? flag)
    {
        await using var site = await StartAsync(flag);
        var b = site.F.Browser(AppHost);   // signed out: the approver may not use Packmoat at all
        var ok = await b.GetAsync(AppAzureSetup.ConsentedPath + "?admin_consent=True&tenant=" + DirS + "&scope=ECHO&state=ECHO");
        var okBody = await ok.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("<h1>" + AppAzureSetup.ConsentedTitle + "</h1>", okBody);
        Assert.Contains(AppHtml.E(AppAzureSetup.ConsentedLine), okBody);
        Assert.Contains("<a class=\"btn\" href=\"/home\">Go to Packmoat</a>", okBody);
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.NotApprovedLine), okBody);
        Assert.DoesNotContain("ECHO", okBody);
        Assert.DoesNotContain(DirS, okBody);   // the tenant in the reply is never shown or trusted
        Assert.False(ok.Headers.Contains("Set-Cookie"));
        Assert.Null(ok.Headers.Location);

        foreach (var reply in new[] { "?error=access_denied&error_description=ECHO%3Cscript%3E&tenant=" + DirS, "?error=", "?admin_consent=True&error=<script>ECHO</script>" })
        {
            var no = await b.GetAsync(AppAzureSetup.ConsentedPath + reply);
            var noBody = await no.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, no.StatusCode);
            Assert.Contains("<h1>" + AppAzureSetup.NotApprovedTitle + "</h1>", noBody);
            Assert.Contains(AppHtml.E(AppAzureSetup.NotApprovedLine), noBody);
            Assert.DoesNotContain(AppHtml.E(AppAzureSetup.ConsentedLine), noBody);
            Assert.Contains("<a class=\"btn\" href=\"/home\">Go to Packmoat</a>", noBody);
            Assert.DoesNotContain("ECHO", noBody);
            Assert.DoesNotContain("access_denied", noBody);
            Assert.DoesNotContain(DirS, noBody);
            Assert.False(no.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(0, site.SignIn.Challenges);
        Assert.Equal(0L, await FeedCheckRows(site.F));
    }

    // ---- the check

    [Fact]
    public async Task The_whole_check_records_a_passed_feed_check_on_GUID_urls_writes_no_cookie_and_changes_no_tenant()
    {
        await using var site = await StartAsync();
        var f = site.F;
        var b = await SignIn(f);

        var start = await StartCheck(b, GoodNames);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var challenge = site.SignIn.LastChallenge!;
        Assert.Equal(AppAzureSetup.Path, challenge.RedirectUri);
        // The typed names travel in the protected state; the challenge asks for a fresh sign-in.
        Assert.Equal(FakeAzureDevOps.Org, challenge.Items[AppAzureSetup.OrgItem]);
        Assert.Equal(FakeAzureDevOps.ProjectName, challenge.Items[AppAzureSetup.ProjectItem]);
        Assert.Equal(FakeAzureDevOps.FeedName, challenge.Items[AppAzureSetup.FeedItem]);
        Assert.Equal("login", challenge.GetParameter<string>(OpenIdConnectParameterNames.Prompt));
        Assert.Equal(CustomerTokenRules.FreshSignIn, challenge.GetParameter<TimeSpan?>(OpenIdConnectParameterNames.MaxAge));
        Assert.False(challenge.Items.ContainsKey(AppAzureSetup.SilentItem));

        site.SignIn.Next = Person(DirS, authTime: f.Clock.UtcNow);
        var callback = await b.GetAsync(start.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.SeeOther, callback.StatusCode);
        Assert.Equal(AppAzureSetup.Path, callback.Headers.Location!.OriginalString);
        Assert.False(WroteSessionCookie(callback));

        var check = await f.App.Services.GetRequiredService<IFeedCheckStore>().LatestAsync("selfco", FeedCheckKind.Settings, default);
        Assert.NotNull(check);
        Assert.Equal(FeedCheckOutcome.Passed, check.Outcome);
        Assert.Equal($"https://pkgs.dev.azure.com/contoso/{FakeAzureDevOps.ProjectId:D}/_packaging/{FakeAzureDevOps.FeedId:D}/nuget/v3/index.json", check.FeedIndexUrl);
        Assert.Equal((DirS, Ada, "Ada Lovelace", DirS, FakeAzureDevOps.SharedApp), (check.CheckedByTid, check.CheckedByOid, check.CheckedByName, check.AzureTenantId, check.ClientId));
        Assert.Equal(new[] { "feed", "identity", "upstreams", "permissions", "views", "project", "private" }, check.Results.Select(x => x.Id));
        Assert.Empty(check.ConfirmedAdmins);

        // H2 records only the check: the tenant's URLs, its binding and any verification are part I's.
        var tenant = (await f.Tenants.GetAsync("selfco", default))!;
        Assert.Equal(("", "", ""), (tenant.UpstreamBase, tenant.AllowedPathPrefix, tenant.UpstreamFeedIndexUrl));
        Assert.Null(await f.Tenants.GetUpstreamBindingAsync("selfco", default));
        Assert.Null(await f.App.Services.GetRequiredService<ISharedAppCredentialStore>().GetVerificationAsync("selfco", default));

        var page = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains(AppHtml.E(AppAzureSetup.WaitingOnDeveloper), page);
        Assert.Contains($">{AppAzureSetup.CheckAgainButton}</button>", page);
        Assert.Contains($"value=\"{FakeAzureDevOps.FeedName}\"", page);   // prefilled from the check just run
    }

    [Fact]
    public async Task A_named_admin_is_confirmed_with_the_box_and_checked_again_without_a_second_password_prompt()
    {
        await using var site = await StartAsync();
        site.Ado.NamedAdmin = true;
        var f = site.F;
        var b = await SignIn(f);
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, b)).StatusCode);
        var first = await f.App.Services.GetRequiredService<IFeedCheckStore>().LatestAsync("selfco", FeedCheckKind.Settings, default);
        Assert.Equal(FeedCheckOutcome.Incomplete, first!.Outcome);

        var page = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains(AppHtml.E(AppAzureSetup.NamedAdminsSentence), page);
        Assert.Contains("<li>Ada Lovelace</li>", page);
        Assert.Contains(AppHtml.E(AppAzureSetup.ConfirmCheckbox), page);
        Assert.Contains(AppHtml.E(AppAzureSetup.ConfirmButton), page);
        Assert.DoesNotContain(AppHtml.E(FakeAzureDevOps.AdaDescriptor), page);   // descriptors stay on the server

        var confirm = await Post(b, page, AppAzureSetup.CheckPath, true, GoodNames.Append(("confirmed", "yes")).ToArray());
        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        var challenge = site.SignIn.LastChallenge!;
        Assert.Equal("1", challenge.Items[AppAzureSetup.SilentItem]);
        Assert.Contains(FakeAzureDevOps.AdaDescriptor.Replace("\\", "\\\\"), challenge.Items[AppAzureSetup.ConfirmItem]);
        // Microsoft reuses the sign-in from a minute ago: its auth_time is still fresh.
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        site.SignIn.Next = Person(DirS, authTime: f.Clock.UtcNow - TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.SeeOther, (await b.GetAsync(confirm.Headers.Location!.OriginalString)).StatusCode);
        var second = await f.App.Services.GetRequiredService<IFeedCheckStore>().LatestAsync("selfco", FeedCheckKind.Settings, default);
        Assert.Equal(FeedCheckOutcome.Passed, second!.Outcome);
        Assert.Equal(new[] { "Ada Lovelace" }, second.ConfirmedAdmins);
    }

    [Fact]
    public async Task The_silent_confirm_drops_prompt_login_but_keeps_max_age()
    {
        var o = new OpenIdConnectOptions();
        AppAzureSetup.ConfigureOidc(o, new AppSiteSettings { Host = AppHost, ClientId = AppClient, SignInAvailable = true }, new FakeClock());
        var scheme = new AuthenticationScheme(AppAzureSetup.Scheme, null, typeof(OpenIdConnectHandler));
        foreach (var silent in new[] { false, true })
        {
            var props = new AuthenticationProperties();
            if (silent) props.Items[AppAzureSetup.SilentItem] = "1";
            var redirect = new RedirectContext(new Microsoft.AspNetCore.Http.DefaultHttpContext(), scheme, o, props)
            { ProtocolMessage = new OpenIdConnectMessage { Prompt = "login", MaxAge = "900", RedirectUri = "http://evil.example/x" } };
            await o.Events.RedirectToIdentityProvider(redirect);
            Assert.Equal(silent ? null : "login", redirect.ProtocolMessage.Prompt);
            Assert.Equal("900", redirect.ProtocolMessage.MaxAge);
            Assert.Equal($"https://{AppHost}{AppAzureSetup.CallbackPath}", redirect.ProtocolMessage.RedirectUri);
        }
    }

    [Fact]
    public async Task The_scheme_asks_for_exactly_the_read_only_scopes_with_prompt_login_and_max_age_and_keeps_no_token()
    {
        var pem = Path.Combine(Path.GetTempPath(), "app-" + Guid.NewGuid().ToString("N") + ".pem");
        using (var rsa = System.Security.Cryptography.RSA.Create(2048))
        {
            var req = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=test", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            File.WriteAllText(pem, cert.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem());
        }
        try
        {
            await using var site = await StartAsync(configure: o => { o.AppRemoteSchemeOverride = null; o.AppAzureDevOpsSchemeOverride = null; o.AppCertPath = pem; });
            var o = site.F.App.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(AppAzureSetup.Scheme);
            Assert.Equal(new[]
            {
                "499b84ac-1321-427f-aa17-267ca6975798/vso.graph", "499b84ac-1321-427f-aa17-267ca6975798/vso.identity",
                "499b84ac-1321-427f-aa17-267ca6975798/vso.packaging", "499b84ac-1321-427f-aa17-267ca6975798/vso.project", "openid", "profile",
            }, o.Scope.OrderBy(x => x, StringComparer.Ordinal));
            Assert.DoesNotContain("offline_access", o.Scope);
            Assert.Equal("login", o.Prompt);
            Assert.Equal(TimeSpan.FromMinutes(15), o.MaxAge);
            Assert.False(o.SaveTokens);
            Assert.False(o.GetClaimsFromUserInfoEndpoint);
            Assert.True(o.UsePkce);
            Assert.Equal(OpenIdConnectResponseType.Code, o.ResponseType);
            Assert.Equal(new Microsoft.AspNetCore.Http.PathString("/signin-azure-devops"), o.CallbackPath);
            Assert.Equal(AppClient, o.ClientId);
            Assert.Null(o.ClientSecret);
            Assert.Equal("https://login.microsoftonline.com/organizations/v2.0", o.Authority);
            Assert.Equal(AppClient, o.TokenValidationParameters.ValidAudience);
            Assert.Empty(o.ClaimActions);
            // the code redemption carries the certificate assertion, never a secret
            var scheme = new AuthenticationScheme(AppAzureSetup.Scheme, null, typeof(OpenIdConnectHandler));
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = site.F.App.Services };
            var received = new AuthorizationCodeReceivedContext(http, scheme, o, new AuthenticationProperties()) { TokenEndpointRequest = new OpenIdConnectMessage { ClientSecret = "should-go" } };
            await o.Events.AuthorizationCodeReceived(received);
            Assert.Null(received.TokenEndpointRequest!.ClientSecret);
            Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", received.TokenEndpointRequest.ClientAssertionType);
            Assert.Equal($"https://{AppHost}/signin-azure-devops", received.TokenEndpointRequest.RedirectUri);
            // and nobody is ever signed in by it
            var ticket = new TicketReceivedContext(http, scheme, o, new AuthenticationTicket(new ClaimsPrincipal(), AppAzureSetup.Scheme));
            await o.Events.TicketReceived(ticket);
            Assert.NotNull(ticket.Result?.Failure);
        }
        finally { File.Delete(pem); }
    }

    [Theory]
    [InlineData("oid")]          // another person in the same directory
    [InlineData("two-oids")]
    [InlineData("tid")]          // the same object id from another directory
    [InlineData("developer")]    // no Packmoat.Admin role in the new token
    [InlineData("stale")]        // auth_time more than 15 minutes ago: a silent single sign-on
    [InlineData("no-auth-time")]
    [InlineData("future")]
    public async Task The_callback_refuses_another_person_a_developer_or_a_stale_sign_in_and_runs_nothing(string what)
    {
        await using var site = await StartAsync();
        var f = site.F;
        var b = await SignIn(f);
        var now = f.Clock.UtcNow;
        var person = what switch
        {
            "oid" => Person(DirS, oid: "dddddddd-dddd-4ddd-8ddd-dddddddddddd", authTime: now),
            "two-oids" => Person(DirS, authTime: now, extraOid: "dddddddd-dddd-4ddd-8ddd-dddddddddddd"),
            "tid" => Person(DirS, authTime: now, tid: CustomerSiteTests.DirB),
            "developer" => Person(DirS, role: CustomerSignIn.DeveloperRole, authTime: now),
            "stale" => Person(DirS, authTime: now - TimeSpan.FromMinutes(16)),
            "future" => Person(DirS, authTime: now + TimeSpan.FromMinutes(5)),
            _ => Person(DirS),
        };
        var r = await RunCheck(site, b, person);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains(what switch
        {
            "oid" or "two-oids" or "tid" => "Different account",
            "developer" => "Admins only",
            _ => AppHtml.E("Microsoft didn't confirm a recent sign-in."),
        }, body);
        Assert.False(WroteSessionCookie(r));
        Assert.Empty(site.Ado.Seen);   // the token was never used
        Assert.Equal(0L, await FeedCheckRows(f));
    }

    [Fact]
    public async Task The_callback_without_a_session_runs_nothing()
    {
        await using var site = await StartAsync();
        var b = await SignIn(site.F);
        var start = await StartCheck(b, GoodNames);
        b.SetCookie(AppSite.CookieName, "garbage");
        site.SignIn.Next = Person(DirS, authTime: site.F.Clock.UtcNow);
        var r = await b.GetAsync(start.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Empty(site.Ado.Seen);
        Assert.Equal(0L, await FeedCheckRows(site.F));
    }

    [Fact]
    public async Task The_start_needs_antiforgery_and_this_sites_origin()
    {
        await using var site = await StartAsync();
        var b = await SignIn(site.F);
        var page = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(b, page, AppAzureSetup.CheckPath, origin: false, GoodNames)).StatusCode);
        var noToken = await b.SendAsync(HttpMethod.Post, AppAzureSetup.CheckPath, GoodNames.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)),
            r => r.Headers.TryAddWithoutValidation("Origin", Origin));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        var foreign = await b.SendAsync(HttpMethod.Post, AppAzureSetup.CheckPath,
            new[] { new KeyValuePair<string, string>(SignedInSite.AntiforgeryField, AdminBrowser.Field(page)!) }.Concat(GoodNames.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2))),
            r => r.Headers.TryAddWithoutValidation("Origin", "https://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(0, site.SignIn.Challenges);
    }

    [Theory]
    [InlineData("https://dev.azure.com/contoso", "", "feed", "org")]
    [InlineData("contoso", "", "my feed", "feed")]
    [InlineData("contoso", "", "a%2e%2e", "feed")]
    [InlineData("contoso", "a/b", "feed", "project")]
    [InlineData("<script>alert(1)</script>", "", "feed", "org")]
    public async Task Names_that_break_the_rules_are_refused_with_the_draft_errors_and_never_echoed(string org, string project, string feed, string which)
    {
        await using var site = await StartAsync();
        var b = await SignIn(site.F);
        var r = await StartCheck(b, ("org", org), ("project", project), ("feed", feed));
        var body = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains(AppHtml.E(which switch { "org" => AppAzureSetup.OrgError, "feed" => AppAzureSetup.FeedError, _ => AppAzureSetup.ProjectError }), body);
        Assert.DoesNotContain("<script>alert", body);
        Assert.DoesNotContain("value=\"" + AppHtml.E(which == "org" ? org : which == "feed" ? feed : project) + "\"", body);
        Assert.Equal(0, site.SignIn.Challenges);
    }

    [Fact]
    public async Task An_operator_made_shared_tenant_is_check_only_and_nothing_is_recorded()
    {
        await using var site = await StartAsync();
        var f = site.F;
        site.Ado.Directory = DirA;
        var stored = $"https://pkgs.dev.azure.com/Contoso/{FakeAzureDevOps.ProjectId:D}/_packaging/{FakeAzureDevOps.FeedName}/nuget/v3/index.json";
        await using (var c = await f.Db.OpenAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE tenants SET upstream_base='https://pkgs.dev.azure.com/', allowed_path_prefix='Contoso/', feed_index_url=@f WHERE id='acme'";
            cmd.Parameters.AddWithValue("@f", stored);
            await cmd.ExecuteNonQueryAsync();
        }
        await f.Tenants.BindSharedIdentityAsync("acme", DirA, default);
        var b = await SignIn(f, dir: DirA);
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.HomeCardSentence), (await b.GetTextAsync("/home")).Body);

        var page = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains(AppHtml.E(AppAzureSetup.ReadOnlyLine(TestOperator.Support)), page);
        Assert.Contains(AppHtml.E(AppAzureSetup.CheckOnlyLine), page);
        Assert.DoesNotContain("name=\"org\"", page);   // no names to type: the stored feed is checked
        var start = await Post(b, page, AppAzureSetup.CheckPath, true);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Assert.False(site.SignIn.LastChallenge!.Items.ContainsKey(AppAzureSetup.OrgItem));
        site.SignIn.Next = Person(DirA, authTime: f.Clock.UtcNow);
        var callback = await b.GetAsync(start.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.SeeOther, callback.StatusCode);

        Assert.NotEmpty(site.Ado.Seen);
        Assert.Equal(0L, await FeedCheckRows(f));   // never written for an operator-made tenant
        Assert.Null(await f.App.Services.GetRequiredService<ISharedAppCredentialStore>().GetVerificationAsync("acme", default));
        Assert.Equal(stored, (await f.Tenants.GetAsync("acme", default))!.UpstreamFeedIndexUrl);
        var after = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains(AppHtml.E(AppAzureSetup.SettingsPassLine), after);   // shown from memory, for this person only (no developer step: check only)
        Assert.DoesNotContain(AppHtml.E(AppAzureSetup.WaitingOnDeveloper), after);
    }

    [Fact]
    public async Task An_own_app_tenant_gets_the_status_card_and_no_check()
    {
        await using var site = await StartAsync();
        await site.F.SetUpOwnAppAsync("acme", DirA);
        var b = await SignIn(site.F, dir: DirA);
        var page = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.Contains(AppHtml.E(AppAzureSetup.ReadOnlyLine(TestOperator.Support)), page);
        Assert.Contains("<dd>Connected</dd>", page);
        Assert.DoesNotContain(AppAzureSetup.CheckAgainButton, page);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(b, page, AppAzureSetup.CheckPath, true)).StatusCode);
        Assert.Equal(0, site.SignIn.Challenges);
    }

    [Fact]
    public async Task Without_the_shared_app_configured_the_wizard_offers_no_check()
    {
        await using var site = await StartAsync(sharedApp: false);
        var b = await SignIn(site.F);
        var page = (await b.GetTextAsync(AppAzureSetup.Path)).Body;
        Assert.DoesNotContain($">{AppAzureSetup.Step4Button}</button>", page);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post(b, page, AppAzureSetup.CheckPath, true, GoodNames)).StatusCode);
    }

    /// <summary>
    /// The production wiring of the named HttpClient, resolved from the running app: the hosts-only handler sits in front of the transport,
    /// so a request anywhere but Azure DevOps' API hosts is refused before it reaches the transport (here the fake).
    /// </summary>
    [Fact]
    public async Task The_named_client_from_the_app_refuses_other_hosts_before_the_transport()
    {
        await using var site = await StartAsync();
        var http = site.F.App.Services.GetRequiredService<IHttpClientFactory>().CreateClient(DelegatedFeedCheck.ClientName);
        foreach (var url in new[] { "https://evil.example/", "https://pkgs.dev.azure.com/contoso/_packaging/f/nuget/v3/index.json", "http://feeds.dev.azure.com/contoso/x" })
            await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(url));
        Assert.Empty(site.Ado.Seen);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("https://feeds.dev.azure.com/contoso/x")).StatusCode);   // an allowed host does reach it
        Assert.Single(site.Ado.Seen);
    }

    /// <summary>One Azure DevOps call timing out inside the deadline records the check as incomplete (not verified), never as failed.</summary>
    [Fact]
    public async Task A_call_that_times_out_is_recorded_as_incomplete_not_failed()
    {
        await using var site = await StartAsync();
        site.Ado.TimeoutOn = "/permissions";
        var b = await SignIn(site.F);
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, b)).StatusCode);
        var check = await site.F.App.Services.GetRequiredService<IFeedCheckStore>().LatestAsync("selfco", FeedCheckKind.Settings, default);
        Assert.Equal(FeedCheckOutcome.Incomplete, check!.Outcome);
        var permissions = check.Results.Single(r => r.Id == "permissions");
        Assert.Equal((CheckStatus.Skip, AzureSetupCommands.NoAnswerInTime), (permissions.Status, permissions.Detail));
        Assert.DoesNotContain(check.Results, r => r.Status == CheckStatus.Fail);
    }

    // ---- limits

    [Fact]
    public async Task Ten_checks_an_hour_per_person()
    {
        await using var site = await StartAsync();
        var b = await SignIn(site.F);
        for (var i = 0; i < AzureSetupRules.MaxChecksPerWindow; i++) Assert.Equal(HttpStatusCode.Redirect, (await StartCheck(b, GoodNames)).StatusCode);
        var eleventh = await StartCheck(b, GoodNames);
        Assert.Equal((HttpStatusCode)429, eleventh.StatusCode);
        Assert.Contains("Too many checks", await eleventh.Content.ReadAsStringAsync());
        site.F.Clock.Advance(TimeSpan.FromHours(1));
        b = await SignIn(site.F);   // the session's 30 minutes are over too
        Assert.Equal(HttpStatusCode.Redirect, (await StartCheck(b, GoodNames)).StatusCode);
    }

    [Fact]
    public async Task One_check_at_a_time_per_tenant()
    {
        await using var site = await StartAsync();
        var b = await SignIn(site.F);
        var state = site.F.App.Services.GetRequiredService<AzureSetupState>();
        Assert.True(state.TryBegin("selfco"));   // another Admin's check is running
        var r = await RunCheck(site, b);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("A check is running", await r.Content.ReadAsStringAsync());
        Assert.Empty(site.Ado.Seen);
        state.End("selfco");
        Assert.Equal(HttpStatusCode.SeeOther, (await RunCheck(site, b)).StatusCode);
    }

    [Fact]
    public async Task The_access_denied_page_says_an_administrator_must_approve_and_links_the_approval_for_the_packmoat_app()
    {
        await using var site = await StartAsync();
        var f = site.F;
        var b = await SignIn(f);
        var o = new OpenIdConnectOptions();
        AppAzureSetup.ConfigureOidc(o, f.App.Services.GetRequiredService<AppSiteSettings>(), f.Clock);
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = f.App.Services };
        http.Request.Headers.Cookie = string.Join("; ", b.Cookies.Select(c => c.Key + "=" + c.Value));
        http.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?error=access_denied&error_description=ECHO-ME");
        http.Response.Body = new MemoryStream();
        var ctx = new AccessDeniedContext(http, new AuthenticationScheme(AppAzureSetup.Scheme, null, typeof(OpenIdConnectHandler)), o);
        await o.Events.AccessDenied(ctx);
        Assert.True(ctx.Result?.Handled);
        Assert.Equal(403, http.Response.StatusCode);
        http.Response.Body.Position = 0;
        var body = new StreamReader(http.Response.Body).ReadToEnd();
        Assert.Contains(AppHtml.E(AppAzureSetup.ConsentBlocked), body);
        Assert.Contains("<pre class=\"snippet\">" + AppHtml.E(PackmoatConsentUrl(DirS)) + "</pre>", body);
        Assert.Contains("href=\"/setup/azure\">Back to setup</a>", body);   // no purpose: the settings check's page
        Assert.DoesNotContain("ECHO-ME", body);
    }

    [Theory]
    [InlineData("settings", "href=\"/setup/azure\">Back to setup</a>")]
    [InlineData("developer", "href=\"/setup/developer\">Back to the check</a>")]
    public async Task The_access_denied_page_links_back_to_the_page_the_sign_in_came_from(string purpose, string back)
    {
        await using var site = await StartAsync();
        var f = site.F;
        var b = await SignIn(f);
        var o = new OpenIdConnectOptions();
        AppAzureSetup.ConfigureOidc(o, f.App.Services.GetRequiredService<AppSiteSettings>(), f.Clock);
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = f.App.Services };
        http.Request.Headers.Cookie = string.Join("; ", b.Cookies.Select(c => c.Key + "=" + c.Value));
        http.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?error=access_denied");
        http.Response.Body = new MemoryStream();
        var props = new AuthenticationProperties(new Dictionary<string, string?> { [AppAzureSetup.PurposeItem] = purpose });
        var ctx = new AccessDeniedContext(http, new AuthenticationScheme(AppAzureSetup.Scheme, null, typeof(OpenIdConnectHandler)), o) { Properties = props };
        await o.Events.AccessDenied(ctx);
        http.Response.Body.Position = 0;
        var body = new StreamReader(http.Response.Body).ReadToEnd();
        Assert.Contains(back, body);
        Assert.Single(Regex.Matches(body, "<div class=\"actions\"><a class=\"btn\"").Cast<Match>());
    }

    // ---- the token

    [Fact]
    public async Task The_token_never_reaches_a_response_a_log_line_or_the_database()
    {
        await using var site = await StartAsync();
        site.Ado.NamedAdmin = true;
        var f = site.F;
        var b = await SignIn(f);
        var responses = new List<string>();
        async Task Keep(HttpResponseMessage r) => responses.Add(r.StatusCode + "\n" + r.Headers + "\n" + r.Content.Headers + "\n" + await r.Content.ReadAsStringAsync());

        var callback = await RunCheck(site, b);
        await Keep(callback);
        var page = await b.GetAsync(AppAzureSetup.Path);
        var html = await page.Content.ReadAsStringAsync();
        await Keep(page);
        var confirm = await Post(b, html, AppAzureSetup.CheckPath, true, GoodNames.Append(("confirmed", "yes")).ToArray());
        await Keep(confirm);
        site.SignIn.Next = Person(DirS, authTime: f.Clock.UtcNow);
        await Keep(await b.GetAsync(confirm.Headers.Location!.OriginalString));
        await Keep(await b.GetAsync(AppAzureSetup.Path));
        // a refused callback too (another person), and a failing Azure DevOps
        site.SignIn.Next = Person(DirS, oid: "dddddddd-dddd-4ddd-8ddd-dddddddddddd", authTime: f.Clock.UtcNow);
        var again = await StartCheck(b, GoodNames);
        await Keep(await b.GetAsync(again.Headers.Location!.OriginalString));
        site.Ado.GetFeedStatus = 203;
        await Keep(await RunCheck(site, b));
        await Keep(await b.GetAsync(AppAzureSetup.Path));

        Assert.True(site.Ado.Seen.Count > 5);   // the token was used
        // Never Assert.DoesNotContain(token, ...): a failure message would print the token.
        Assert.False(responses.Any(r => r.Contains(FakeAzureDevOps.Token, StringComparison.Ordinal)), "a response carried the token");
        Assert.False(b.SetCookieLog.Any(c => c.Contains(FakeAzureDevOps.Token, StringComparison.Ordinal)), "a cookie carried the token");
        lock (site.Logs.Lines) Assert.False(site.Logs.Lines.Any(l => l.Contains(FakeAzureDevOps.Token, StringComparison.Ordinal)), "a log line carried the token");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(f.Dir, "packmoat.db*"))
        {
            var bytes = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
            Assert.False(bytes.Contains(FakeAzureDevOps.Token, StringComparison.Ordinal), Path.GetFileName(file) + " holds the token");
        }
        Assert.True(await FeedCheckRows(f) >= 3);
    }

    [Fact]
    public void A_signed_out_deep_link_to_the_wizard_survives_sign_in_only_while_the_switch_is_on()
    {
        Assert.Equal(AppAzureSetup.Path, AppSite.ReturnPath(AppAzureSetup.Path, azureSetup: true));
        Assert.Equal("/home", AppSite.ReturnPath(AppAzureSetup.Path, azureSetup: false));
        Assert.Equal("/home", AppSite.ReturnPath(AppAzureSetup.CheckPath, azureSetup: true));
        Assert.Equal("/home", AppSite.ReturnPath("/setup/azure/../../evil", azureSetup: true));
        Assert.Equal("/home", AppSite.ReturnPath("/SETUP/AZURE", azureSetup: true));
    }

    [Fact]
    public async Task A_failed_callback_counts_against_the_sign_in_limiter()
    {
        await using var site = await StartAsync();
        var b = await SignIn(site.F);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Forbidden, (await RunCheck(site, b, Person(DirS, oid: "dddddddd-dddd-4ddd-8ddd-dddddddddddd", authTime: site.F.Clock.UtcNow))).StatusCode);
        // the limiter now answers on the callback path itself
        var limited = await b.GetAsync(AppAzureSetup.CallbackPath + "?code=x");
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
    }
}
