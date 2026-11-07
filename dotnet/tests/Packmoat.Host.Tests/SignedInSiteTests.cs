using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Packmoat.Host.Admin;
using Packmoat.Host.Web;
using static Packmoat.Host.Tests.AdminSiteFixture;

namespace Packmoat.Host.Tests;

/// <summary>A sign-in handler for some other site that would answer EVERY request if anything ran it. The admin branch must never run it.</summary>
public sealed class GreedyOtherSiteHandler : AuthenticationHandler<AuthenticationSchemeOptions>, IAuthenticationRequestHandler
{
    public const string SchemeName = "OtherSiteRemote";
    public GreedyOtherSiteHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, Microsoft.Extensions.Logging.ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : base(options, logger, encoder) { }

    public Task<bool> HandleRequestAsync()
    {
        Response.StatusCode = 418;
        return Task.FromResult(true);
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
}

/// <summary>The admin site after the shared signed-in-site plumbing was extracted: it behaves as before and leaves room for a second site.</summary>
[Collection("sqlite-pools")]
public class SignedInSiteTests
{
    [Fact]
    public async Task There_is_no_app_wide_default_scheme_yet_the_admin_site_still_knows_its_signed_in_person()
    {
        await using var f = await StartAsync();
        var o = f.App.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        Assert.Null(o.DefaultScheme);
        Assert.Null(o.DefaultAuthenticateScheme);
        Assert.Null(o.DefaultChallengeScheme);
        Assert.Null(o.DefaultSignInScheme);

        var b = f.Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await b.SignInAsync()).StatusCode);
        var (status, body, _) = await b.GetTextAsync("/tenants");
        Assert.Equal(200, status);
        Assert.Contains("acme", body);
        // and a POST still passes the antiforgery check, which compares the token with the signed-in user the branch set
        var token = await b.TokenFromAsync("/tenants");
        var signOut = await b.SendAsync(HttpMethod.Post, "/signout", new[] { new KeyValuePair<string, string>(AdminSite.AntiforgeryField, token) });
        Assert.Equal(HttpStatusCode.SeeOther, signOut.StatusCode);
        Assert.False(b.HasCookie(AdminSite.CookieName));
    }

    [Fact]
    public async Task Another_sites_callback_handler_never_runs_on_the_admin_host_or_the_proxy()
    {
        await using var f = await StartAsync(services: s =>
            s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, GreedyOtherSiteHandler>(GreedyOtherSiteHandler.SchemeName, null));
        var probe = f.Browser();
        foreach (var p in new[] { "/", "/signin-oidc", "/tenants", "/anything" })
            Assert.NotEqual(418, (int)(await probe.GetAsync(p)).StatusCode);
        var b = f.Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await b.SignInAsync()).StatusCode);   // the admin's own callback still works
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/tenants")).StatusCode);
        var proxy = f.Browser("127.0.0.1");
        foreach (var p in new[] { "/signin-oidc", "/v3/index.json" })
            Assert.NotEqual(418, (int)(await proxy.GetAsync(p)).StatusCode);
    }

    [Fact]
    public async Task The_antiforgery_cookie_has_one_neutral_host_only_name()
    {
        await using var f = await StartAsync();
        var b = f.Browser();
        await b.GetAsync("/");
        var af = b.SetCookieLog.Single(c => c.StartsWith(SignedInSite.AntiforgeryCookieName + "="));
        Assert.Equal("__Host-packmoat-af", SignedInSite.AntiforgeryCookieName);
        Assert.DoesNotContain("admin", af.Split(';')[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", af, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", af, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", af, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", af, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_admin_sign_in_settings_are_unchanged()
    {
        await using var f = await StartAsync(fakeProvider: false);
        var o = f.App.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(AdminSite.RemoteScheme);
        Assert.Equal(new PathString("/signin-oidc"), o.CallbackPath);
        Assert.Equal(AdminSite.CookieScheme, o.SignInScheme);
        Assert.Equal(OpenIdConnectResponseType.Code, o.ResponseType);
        Assert.Equal(OpenIdConnectResponseMode.Query, o.ResponseMode);
        Assert.True(o.UsePkce);
        Assert.False(o.SaveTokens);
        Assert.False(o.MapInboundClaims);
        Assert.Equal(new[] { "openid", "profile" }, o.Scope.OrderBy(x => x));
        Assert.Equal($"https://login.microsoftonline.com/{Tid}/v2.0", o.TokenValidationParameters.ValidIssuer);
        Assert.Equal($"https://login.microsoftonline.com/{Tid}/v2.0", o.Authority);

        var cookie = f.App.Services.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>().Get(AdminSite.CookieScheme);
        Assert.Equal(AdminSite.CookieName, cookie.Cookie.Name);
        Assert.Equal(TimeSpan.FromMinutes(30), cookie.ExpireTimeSpan);
        Assert.True(cookie.SlidingExpiration);
        Assert.Equal(SameSiteMode.Lax, cookie.Cookie.SameSite);
        Assert.Equal(TimeSpan.FromHours(8), SignedInSite.AbsoluteCap);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/nothing-here")]
    [InlineData("/admin.css")]
    public async Task Every_admin_response_still_carries_the_security_headers(string path)
    {
        await using var f = await StartAsync();
        var r = await f.Browser().GetAsync(path);
        string H(string name) => r.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : r.Content.Headers.TryGetValues(name, out var c) ? string.Join(",", c) : "";
        Assert.Equal("same-origin", H("Referrer-Policy"));
        Assert.Equal("DENY", H("X-Frame-Options"));
        Assert.Contains("no-store", H("Cache-Control"));
        Assert.Equal("max-age=31536000", H("Strict-Transport-Security"));
        Assert.Contains("frame-ancestors 'none'", H("Content-Security-Policy"));
        Assert.Equal("nosniff", H("X-Content-Type-Options"));
    }
}
