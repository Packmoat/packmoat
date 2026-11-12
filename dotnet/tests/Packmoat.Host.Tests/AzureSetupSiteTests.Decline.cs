using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Host.Customer;
using Packmoat.Host.Web;

namespace Packmoat.Host.Tests;

/// <summary>
/// on the Azure DevOps check's sign-in, through the REAL OpenID Connect handler (the customer's own sign-in stays the fake): a decline
/// of Microsoft's consent page shows Approval needed with the person's own approval link, matched to the sign-in or not; any other error
/// code shows Sign-in failed.
/// </summary>
public partial class AzureSetupSiteTests
{
    private sealed record RealCheckSite(Site Site, TempDir Keys) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await Site.DisposeAsync(); }
            finally { Keys.Dispose(); }
        }
    }

    private static async Task<RealCheckSite> StartWithRealCheckSchemeAsync(FakeTokenEndpoint? token = null)
    {
        var keys = ConsentDeclineTests.WritePem(out var pem);
        try
        {
            var site = await StartAsync(configure: o => { o.AppAzureDevOpsSchemeOverride = null; o.AppCertPath = pem; },
                services: s =>
                {
                    s.PostConfigure<OpenIdConnectOptions>(AppAzureSetup.Scheme, ConsentDeclineTests.OfflineMetadata);
                    token?.Use(s, AppAzureSetup.Scheme);
                }, logEverything: false);   // these tests check every log line production would write
            return new RealCheckSite(site, keys);
        }
        catch
        {
            keys.Dispose();
            throw;
        }
    }

    [Theory]
    [InlineData(false, "access_denied", true)]
    [InlineData(true, "access_denied", true)]
    [InlineData(false, "consent_required", true)]
    [InlineData(false, "access_denied", false)]   // the browser lost the correlation cookie
    [InlineData(true, "consent_required", false)]
    public async Task A_decline_of_the_check_sign_in_shows_approval_needed_with_the_link_and_back_to_setup(bool formPost, string error, bool withCookie)
    {
        await using var real = await StartWithRealCheckSchemeAsync();
        var site = real.Site;
        var b = await SignIn(site.F);
        var state = ConsentDeclineTests.StateOf(await StartCheck(b), b);
        if (!withCookie) ConsentDeclineTests.DropCorrelationCookies(b);
        var r = await ConsentDeclineTests.CallbackAsync(b, AppAzureSetup.CallbackPath, ConsentDeclineTests.Reply(state, error), formPost);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("<h1>Approval needed</h1>", body);
        Assert.Contains(AppHtml.E(AppAzureSetup.ConsentBlocked), body);
        Assert.Contains("<pre class=\"snippet\">" + AppHtml.E(PackmoatConsentUrl(DirS)) + "</pre>", body);
        Assert.Contains("href=\"/setup/azure\">Back to setup</a>", body);
        Assert.DoesNotContain("Sign-in failed", body);
        ConsentDeclineTests.AssertNoEcho(body);
        ConsentDeclineTests.AssertLogsHoldNoReply(site.Logs);
    }

    [Theory]
    [InlineData(false, "interaction_required")]
    [InlineData(true, "server_error")]
    [InlineData(false, "ACCESS_DENIED")]
    [InlineData(false, null)]
    public async Task Any_other_error_on_the_check_sign_in_shows_sign_in_failed(bool formPost, string? error)
    {
        await using var real = await StartWithRealCheckSchemeAsync();
        var b = await SignIn(real.Site.F);
        var state = ConsentDeclineTests.StateOf(await StartCheck(b), b);
        var r = await ConsentDeclineTests.CallbackAsync(b, AppAzureSetup.CallbackPath, ConsentDeclineTests.Reply(state, error), formPost);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Sign-in failed", body);
        Assert.DoesNotContain("Approval needed", body);
        ConsentDeclineTests.AssertNoEcho(body);
        // The log line names the failure type and only an allowlisted code.
        Assert.Contains(real.Site.Logs.Lines, l => l.Contains("Azure DevOps check sign-in failed (OpenIdConnectProtocolException, error " + SignInReply.LogCode(error) + ", token none)"));
        ConsentDeclineTests.AssertLogsHoldNoReply(real.Site.Logs);
    }

    /// <summary>The developer proof's sign-in cannot be started through the real handler here (it needs a passed settings check), so its
    /// back link is shown through the failure event the handler raises for an unmatched decline, with the purpose the state would carry.</summary>
    [Theory]
    [InlineData("settings", "href=\"/setup/azure\">Back to setup</a>")]
    [InlineData("developer", "href=\"/setup/developer\">Back to the check</a>")]
    [InlineData(null, "href=\"/setup/azure\">Back to setup</a>")]
    public async Task A_decline_reaching_the_failure_event_links_back_to_the_page_the_sign_in_came_from(string? purpose, string back)
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
        var props = purpose is null ? null : new AuthenticationProperties(new Dictionary<string, string?> { [AppAzureSetup.PurposeItem] = purpose });
        var ctx = new RemoteFailureContext(http, new AuthenticationScheme(AppAzureSetup.Scheme, null, typeof(OpenIdConnectHandler)), o,
            new AuthenticationFailureException("Correlation failed.")) { Properties = props };
        await o.Events.RemoteFailure(ctx);
        Assert.True(ctx.Result?.Handled);
        Assert.Equal(403, http.Response.StatusCode);
        http.Response.Body.Position = 0;
        var body = new StreamReader(http.Response.Body).ReadToEnd();
        Assert.Contains("<h1>Approval needed</h1>", body);
        Assert.Contains(back, body);
        Assert.DoesNotContain("ECHO-ME", body);
    }

    /// <summary>
    /// The failure event counts a check sign-in only when Microsoft had issued a code for it (the request is marked when the code
    /// arrives). Declines, bare and forged callbacks reach the event unmarked and never count, whatever the error code.
    /// </summary>
    [Fact]
    public async Task A_check_sign_in_reaching_the_failure_event_counts_only_after_microsoft_issued_a_code()
    {
        await using var site = await StartAsync();
        var f = site.F;
        var o = new OpenIdConnectOptions();
        AppAzureSetup.ConfigureOidc(o, f.App.Services.GetRequiredService<AppSiteSettings>(), f.Clock);
        var limiter = f.App.Services.GetRequiredService<AppSignInLimiter>();
        Microsoft.AspNetCore.Http.DefaultHttpContext Http(string error, bool codeReceived = false)
        {
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = f.App.Services };
            http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.44");
            http.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?error=" + error);
            http.Response.Body = new MemoryStream();
            if (codeReceived) SignedInSite.MarkCodeReceived(http);
            return http;
        }
        Task Fail(Microsoft.AspNetCore.Http.HttpContext http) => o.Events.RemoteFailure(new RemoteFailureContext(http,
            new AuthenticationScheme(AppAzureSetup.Scheme, null, typeof(OpenIdConnectHandler)), o, new AuthenticationFailureException("Correlation failed.")));
        for (var i = 0; i < 6; i++) await Fail(Http(i % 3 == 0 ? "access_denied" : i % 3 == 1 ? "consent_required" : "server_error"));
        Assert.Null(limiter.RetryAfterSeconds(Http("x")));
        for (var i = 0; i < 5; i++)
        {
            Assert.Null(limiter.RetryAfterSeconds(Http("x")));
            await Fail(Http("", codeReceived: true));
        }
        Assert.NotNull(limiter.RetryAfterSeconds(Http("x")));
    }

    [Theory]
    [InlineData("bare")]
    [InlineData("forged")]
    [InlineData("matched-decline")]
    [InlineData("unmatched-decline")]
    [InlineData("error")]
    public async Task Check_callbacks_without_a_code_from_microsoft_never_trip_the_limiter(string kind)
    {
        await using var real = await StartWithRealCheckSchemeAsync();
        var b = await SignIn(real.Site.F);
        for (var i = 0; i < 6; i++)
        {
            HttpResponseMessage r;
            if (kind == "bare") r = await b.GetAsync(AppAzureSetup.CallbackPath);
            else if (kind == "forged") r = await b.GetAsync(AppAzureSetup.CallbackPath + "?code=guess&state=guess");
            else
            {
                var state = ConsentDeclineTests.StateOf(await StartCheck(b), b);
                if (kind == "unmatched-decline") ConsentDeclineTests.DropCorrelationCookies(b);
                r = await ConsentDeclineTests.CallbackAsync(b, AppAzureSetup.CallbackPath,
                    ConsentDeclineTests.Reply(kind == "unmatched-decline" ? null : state, kind == "error" ? "server_error" : "access_denied"), formPost: false);
            }
            Assert.NotEqual((HttpStatusCode)429, r.StatusCode);
            ConsentDeclineTests.DropCorrelationCookies(b);
        }
        Assert.NotEqual((HttpStatusCode)429, (await b.GetAsync(AppAzureSetup.CallbackPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await StartCheck(b)).StatusCode);
    }

    [Fact]
    public async Task Five_failed_code_redemptions_on_the_check_sign_in_trip_the_limiter()
    {
        var token = new FakeTokenEndpoint();
        await using var real = await StartWithRealCheckSchemeAsync(token);
        var b = await SignIn(real.Site.F);
        for (var i = 0; i < 5; i++)
        {
            var state = ConsentDeclineTests.StateOf(await StartCheck(b), b);
            var r = await b.GetAsync(AppAzureSetup.CallbackPath + "?code=code-" + i + "&state=" + Uri.EscapeDataString(state));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            ConsentDeclineTests.DropCorrelationCookies(b);
        }
        Assert.Equal(5, token.Calls);
        Assert.Contains(real.Site.Logs.Lines, l => l.Contains("Azure DevOps check sign-in failed (OpenIdConnectProtocolException, error none, token invalid_grant)"));
        Assert.Equal((HttpStatusCode)429, (await b.GetAsync(AppAzureSetup.CallbackPath + "?code=x&state=y")).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await StartCheck(b)).StatusCode);
    }
}
