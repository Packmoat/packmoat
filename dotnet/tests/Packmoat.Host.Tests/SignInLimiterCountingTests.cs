using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Packmoat.Host.Admin;
using Packmoat.Host.Customer;

namespace Packmoat.Host.Tests;

/// <summary>
/// Microsoft's token endpoint without the network: answers each code redemption with <see cref="Answer"/>. Set as the handler's
/// backchannel, so the REAL OpenID Connect handler redeems the code here.
/// </summary>
internal sealed class FakeTokenEndpoint : HttpMessageHandler
{
    public Func<HttpResponseMessage> Answer { get; set; } = () => Json(HttpStatusCode.BadRequest,
        "{\"error\":\"invalid_grant\",\"error_description\":\"" + ConsentDeclineTests.Aadsts65004.Replace("<", "\\u003c").Replace(">", "\\u003e") + "\"}");
    public int Calls;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(Answer());
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Uses this endpoint for the named scheme's code redemption.</summary>
    public void Use(IServiceCollection s, string scheme) => s.PostConfigure<OpenIdConnectOptions>(scheme, o => o.Backchannel = new HttpClient(this));
}

/// <summary>
/// The failed sign-in limiter counts guesses, not page loads. The state is data-protected, and PKCE, the nonce and the correlation
/// cookie bind the code to this browser, so a callback cannot be brute-forced; counting bare callbacks, declines or a cross-site POST
/// would only hand a hostile page a free lockout. What counts is a failure after Microsoft issued a code: a redemption that fails, a
/// refused person (<see cref="Packmoat.Host.Web.SignInDeniedException"/>), a different person at a step-up. Everything here runs through
/// the REAL OpenID Connect handler.
/// </summary>
[Collection("sqlite-pools")]
public class SignInLimiterCountingTests
{
    private const string AppHost = CustomerSiteTests.AppHost;
    private const int Rounds = 6;   // one more than the limiter allows

    // ---- the customer sign-in

    [Theory]
    [InlineData("bare")]               // the callback path with nothing on it
    [InlineData("forged")]             // a made-up code and state
    [InlineData("matched-decline")]    // OnAccessDenied
    [InlineData("unmatched-decline")]  // OnRemoteFailure, correlation lost
    [InlineData("error")]              // any other error reply, matched
    public async Task Customer_callbacks_without_a_code_from_microsoft_never_trip_the_limiter(string kind)
    {
        await using var site = await ConsentDeclineTests.StartAsync();
        var b = site.F.Browser(AppHost);
        for (var i = 0; i < Rounds; i++)
        {
            var r = await CustomerCallback(b, kind);
            Assert.NotEqual((HttpStatusCode)429, r.StatusCode);
            ConsentDeclineTests.DropCorrelationCookies(b);   // keep under the pending sign-in cap
        }
        Assert.NotEqual((HttpStatusCode)429, (await CustomerCallback(b, kind)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await b.GetAsync("/signin")).StatusCode);
    }

    private static async Task<HttpResponseMessage> CustomerCallback(AdminBrowser b, string kind)
    {
        switch (kind)
        {
            case "bare": return await b.GetAsync(AppSite.CallbackPath);
            case "forged": return await b.GetAsync(AppSite.CallbackPath + "?code=guess&state=guess");
        }
        var state = ConsentDeclineTests.StateOf(await b.GetAsync("/signin"), b);
        if (kind == "unmatched-decline") ConsentDeclineTests.DropCorrelationCookies(b);
        var reply = ConsentDeclineTests.Reply(kind == "unmatched-decline" ? null : state, kind == "error" ? "server_error" : "access_denied");
        return await ConsentDeclineTests.CallbackAsync(b, AppSite.CallbackPath, reply, formPost: false);
    }

    [Fact]
    public async Task Five_failed_code_redemptions_on_the_customer_site_trip_the_limiter()
    {
        var token = new FakeTokenEndpoint();
        await using var site = await ConsentDeclineTests.StartAsync(s => token.Use(s, AppSite.RemoteScheme));
        var b = site.F.Browser(AppHost);
        for (var i = 0; i < 5; i++)
        {
            var state = ConsentDeclineTests.StateOf(await b.GetAsync("/signin"), b);
            var r = await b.GetAsync(AppSite.CallbackPath + "?code=code-" + i + "&state=" + Uri.EscapeDataString(state));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("<h1>Sign-in failed</h1>", await r.Content.ReadAsStringAsync());
            ConsentDeclineTests.DropCorrelationCookies(b);
        }
        Assert.Equal(5, token.Calls);   // each one reached Microsoft's token endpoint
        Assert.Contains(site.Logs.Lines, l => l.Contains("Customer sign-in failed (OpenIdConnectProtocolException, error none, token invalid_grant)"));
        ConsentDeclineTests.AssertLogsHoldNoReply(site.Logs);   // the token endpoint's error_description included
        var blocked = await b.GetAsync("/signin");
        Assert.Equal((HttpStatusCode)429, blocked.StatusCode);
        Assert.Null(blocked.Headers.Location);
        Assert.Equal((HttpStatusCode)429, (await b.GetAsync(AppSite.CallbackPath + "?code=x&state=y")).StatusCode);
    }

    /// <summary>A token endpoint refusal with this OAuth error code and Microsoft's usual error_description (with the ECHO marker).</summary>
    private static FakeTokenEndpoint Refusing(string error) => new()
    {
        Answer = () => FakeTokenEndpoint.Json(HttpStatusCode.BadRequest,
            "{\"error\":\"" + error + "\",\"error_description\":\"AADSTS700027: Client assertion failed. ECHO-ME \\u003cscript\\u003ex\\u003c/script\\u003e\"}"),
    };

    [Theory]
    [InlineData("invalid_client", "invalid_client")]                   // for example an expired client certificate
    [InlineData("invalid_grant", "invalid_grant")]                     // a reused or expired code
    [InlineData("unauthorized_client", "unauthorized_client")]
    [InlineData("invalid_request", "invalid_request")]
    [InlineData("temporarily_unavailable", "temporarily_unavailable")]
    [InlineData("x_echo_me", "other")]                                 // an unknown code is never logged as sent
    [InlineData("INVALID_CLIENT", "other")]
    public async Task A_failed_redemption_logs_the_token_endpoints_code_through_the_allowlist_and_never_its_description(string sent, string logged)
    {
        var token = Refusing(sent);
        await using var site = await ConsentDeclineTests.StartAsync(s => token.Use(s, AppSite.RemoteScheme));
        var b = site.F.Browser(AppHost);
        var state = ConsentDeclineTests.StateOf(await b.GetAsync("/signin"), b);
        var r = await b.GetAsync(AppSite.CallbackPath + "?code=code&state=" + Uri.EscapeDataString(state));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        ConsentDeclineTests.AssertNoEcho(await r.Content.ReadAsStringAsync());
        Assert.Equal(1, token.Calls);
        Assert.Contains(site.Logs.Lines, l => l.Contains("Customer sign-in failed (OpenIdConnectProtocolException, error none, token " + logged + ")"));
        string all;
        lock (site.Logs.Lines) all = string.Join("\n", site.Logs.Lines);
        Assert.DoesNotContain("AADSTS700027", all);
        Assert.DoesNotContain("x_echo_me", all, StringComparison.OrdinalIgnoreCase);
        ConsentDeclineTests.AssertLogsHoldNoReply(site.Logs);
    }

    [Fact]
    public async Task A_failed_admin_redemption_logs_the_token_endpoints_code_and_never_its_description()
    {
        var logs = new CapturingLoggerProvider();
        var token = Refusing("invalid_client");
        await using var f = await AdminSiteFixture.StartAsync(fakeProvider: false, services: s =>
        {
            s.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs);
            s.PostConfigure<OpenIdConnectOptions>(AdminSite.RemoteScheme, ConsentDeclineTests.OfflineMetadata);
            token.Use(s, AdminSite.RemoteScheme);
        });
        var b = f.Browser();
        var state = ConsentDeclineTests.StateOf(await StartAdminSignIn(b), b);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.GetAsync(AdminSite.CallbackPath + "?code=code&state=" + Uri.EscapeDataString(state))).StatusCode);
        Assert.Contains(logs.Lines, l => l.Contains("Admin sign-in refused (OpenIdConnectProtocolException, token invalid_client)"));
        ConsentDeclineTests.AssertLogsHoldNoReply(logs);
    }

    [Fact]
    public async Task Cross_site_posts_to_the_customer_sign_in_never_trip_the_limiter()
    {
        await using var site = await ConsentDeclineTests.StartAsync();
        var b = site.F.Browser(AppHost);
        for (var i = 0; i < Rounds; i++)
        {
            // What a hostile page's auto-submitting form sends: no antiforgery cookie, no token.
            var r = await b.SendAsync(HttpMethod.Post, "/signin", Array.Empty<KeyValuePair<string, string>>(), sendCookies: false,
                tweak: req => { req.Headers.TryAddWithoutValidation("Origin", "https://evil.example"); req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site"); });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
        Assert.Equal(HttpStatusCode.Redirect, (await b.GetAsync("/signin")).StatusCode);
    }

    // ---- the admin site

    private static Task<AdminSiteFixture> StartAdminAsync(FakeTokenEndpoint? token = null, RsaSecurityKey? key = null) =>
        AdminSiteFixture.StartAsync(fakeProvider: false, services: s =>
        {
            s.PostConfigure<OpenIdConnectOptions>(AdminSite.RemoteScheme, ConsentDeclineTests.OfflineMetadata);
            if (key is not null) s.PostConfigure<OpenIdConnectOptions>(AdminSite.RemoteScheme, o => o.TokenValidationParameters.IssuerSigningKey = key);
            token?.Use(s, AdminSite.RemoteScheme);
        });

    /// <summary>POST /signin with the landing page's token: the admin site's one way to Microsoft.</summary>
    private static async Task<HttpResponseMessage> StartAdminSignIn(AdminBrowser b)
    {
        var field = AdminBrowser.Field(await (await b.GetAsync("/")).Content.ReadAsStringAsync())!;
        return await b.SendAsync(HttpMethod.Post, "/signin", new[] { new KeyValuePair<string, string>(AdminSite.AntiforgeryField, field) });
    }

    [Theory]
    [InlineData("bare")]
    [InlineData("forged")]
    [InlineData("matched-decline")]
    [InlineData("unmatched-decline")]
    public async Task Admin_callbacks_without_a_code_from_microsoft_never_trip_the_limiter(string kind)
    {
        await using var f = await StartAdminAsync();
        var b = f.Browser();
        for (var i = 0; i < Rounds; i++)
        {
            HttpResponseMessage r;
            if (kind == "bare") r = await b.GetAsync(AdminSite.CallbackPath);
            else if (kind == "forged") r = await b.GetAsync(AdminSite.CallbackPath + "?code=guess&state=guess");
            else
            {
                var state = ConsentDeclineTests.StateOf(await StartAdminSignIn(b), b);
                if (kind == "unmatched-decline") ConsentDeclineTests.DropCorrelationCookies(b);
                r = await ConsentDeclineTests.CallbackAsync(b, AdminSite.CallbackPath, ConsentDeclineTests.Reply(kind == "matched-decline" ? state : null, "access_denied"), formPost: false);
            }
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
        Assert.Equal(HttpStatusCode.Redirect, (await StartAdminSignIn(b)).StatusCode);
    }

    [Fact]
    public async Task Cross_site_posts_to_the_admin_sign_in_never_trip_the_limiter()
    {
        await using var f = await StartAdminAsync();
        var b = f.Browser();
        for (var i = 0; i < Rounds; i++)
        {
            var r = await b.SendAsync(HttpMethod.Post, "/signin", Array.Empty<KeyValuePair<string, string>>(), sendCookies: false,
                tweak: req => req.Headers.TryAddWithoutValidation("Origin", "https://evil.example"));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
        Assert.Equal(HttpStatusCode.Redirect, (await StartAdminSignIn(b)).StatusCode);
    }

    [Fact]
    public async Task Five_failed_code_redemptions_on_the_admin_site_trip_the_limiter()
    {
        var token = new FakeTokenEndpoint();
        await using var f = await StartAdminAsync(token);
        var b = f.Browser();
        for (var i = 0; i < 5; i++)
        {
            var state = ConsentDeclineTests.StateOf(await StartAdminSignIn(b), b);
            var r = await b.GetAsync(AdminSite.CallbackPath + "?code=code-" + i + "&state=" + Uri.EscapeDataString(state));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
        Assert.Equal(5, token.Calls);
        Assert.Equal((HttpStatusCode)429, (await StartAdminSignIn(b)).StatusCode);
    }

    /// <summary>
    /// A person not on the allowlist, five times: Microsoft redeems the code and the ID token validates (signed with a test key), then the
    /// site refuses the person (<see cref="AdminSignInDeniedException"/> through the failure event). Each one counts.
    /// </summary>
    [Fact]
    public async Task Five_refused_people_on_the_admin_site_trip_the_limiter()
    {
        var key = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test-key" };
        string? nonce = null;
        var token = new FakeTokenEndpoint();
        token.Answer = () => FakeTokenEndpoint.Json(HttpStatusCode.OK,
            "{\"token_type\":\"Bearer\",\"access_token\":\"at\",\"id_token\":\"" + AdminIdToken(key, nonce!, oid: "99999999-9999-4999-8999-999999999999") + "\"}");
        await using var f = await StartAdminAsync(token, key);
        var b = f.Browser();
        for (var i = 0; i < 5; i++)
        {
            var start = await StartAdminSignIn(b);
            var state = ConsentDeclineTests.StateOf(start, b);
            nonce = QueryHelpers.ParseQuery(start.Headers.Location!.Query)["nonce"].ToString();
            var r = await b.GetAsync(AdminSite.CallbackPath + "?code=code-" + i + "&state=" + Uri.EscapeDataString(state));
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Contains("This account is not allowed to use the admin site.", await r.Content.ReadAsStringAsync());
            Assert.False(b.HasCookie(AdminSite.CookieName));
        }
        Assert.Equal((HttpStatusCode)429, (await StartAdminSignIn(b)).StatusCode);
    }

    private static string AdminIdToken(SecurityKey key, string nonce, string oid)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = $"https://login.microsoftonline.com/{AdminSiteFixture.Tid}/v2.0",
            Audience = "33333333-3333-4333-8333-333333333333",
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = "subject", ["tid"] = AdminSiteFixture.Tid, ["oid"] = oid, ["nonce"] = nonce, ["name"] = "Someone Else" },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
    }
}
