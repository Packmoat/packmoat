using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Packmoat.Host.Admin;
using Packmoat.Infrastructure;
using static Packmoat.Host.Tests.AdminSiteFixture;

namespace Packmoat.Host.Tests;

/// <summary>Review follow-ups: each allowlist check alone, error pages, host filtering, the redirect uri and the key folder.</summary>
[Collection("sqlite-pools")]
public class AdminSiteHardeningTests
{
    private static async Task<AdminBrowser> SignedIn(AdminSiteFixture f)
    {
        var b = f.Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await b.SignInAsync()).StatusCode);
        Assert.True(b.HasCookie(AdminSite.CookieName));
        return b;
    }

    /// <summary>Refuses exactly one numbered call to IsAllowed (counted from <see cref="Arm"/>); the other checks on the same request still say yes.</summary>
    private sealed class ScriptedAllowlist : AdminAllowlist
    {
        private int _calls, _denyCall;
        public ScriptedAllowlist() : base(new[] { (Guid.Parse(Tid), Guid.Parse(Oid)) }) { }
        public void Arm(int denyCall) { _calls = 0; _denyCall = denyCall; }
        public override bool IsAllowed(System.Security.Claims.ClaimsPrincipal? p) => Interlocked.Increment(ref _calls) != _denyCall && base.IsAllowed(p);
    }

    private static async Task<(AdminSiteFixture F, ScriptedAllowlist List, AdminBrowser B)> ScriptedAsync()
    {
        var list = new ScriptedAllowlist();
        var f = await StartAsync(services: s => s.AddSingleton<AdminAllowlist>(list));
        var b = await SignedIn(f);
        Assert.Equal(200, (int)(await b.GetAsync("/tenants")).StatusCode);   // all checks say yes while unarmed
        return (f, list, b);
    }

    [Fact]
    public async Task The_cookie_check_alone_refuses_and_ends_the_session()
    {
        var (f, list, b) = await ScriptedAsync();
        await using var _ = f;
        list.Arm(1);                                   // the first ask on a request is the cookie validation
        var r = await b.GetAsync("/tenants");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.False(b.HasCookie(AdminSite.CookieName));
    }

    [Fact]
    public async Task The_page_check_alone_refuses_and_leaves_the_cookie_alone()
    {
        var (f, list, b) = await ScriptedAsync();
        await using var _ = f;
        list.Arm(2);                                   // the second ask is the page's own check
        var r = await b.GetAsync("/tenants");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/", r.Headers.Location!.OriginalString);
        Assert.True(b.HasCookie(AdminSite.CookieName));
    }

    [Fact]
    public async Task The_landing_check_alone_shows_the_sign_in_form_instead_of_skipping_ahead()
    {
        var (f, list, b) = await ScriptedAsync();
        await using var _ = f;
        list.Arm(2);
        var r = await b.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Sign in with Microsoft", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_sign_in_check_alone_refuses_with_403()
    {
        var list = new ScriptedAllowlist();
        await using var f = await StartAsync(services: s => s.AddSingleton<AdminAllowlist>(list));
        list.Arm(1);                                   // the first ask during sign-in is the cookie's sign-in event
        var b = f.Browser();
        Assert.Equal(HttpStatusCode.Forbidden, (await b.SignInAsync()).StatusCode);
        Assert.False(b.HasCookie(AdminSite.CookieName));
    }

    [Fact]
    public async Task An_error_after_the_cookie_was_written_sends_no_session_cookie()
    {
        await using var f = await StartAsync();
        f.Identity.ThrowAfterSignIn = true;
        var b = f.Browser();
        var r = await b.SignInAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        Assert.False(b.HasCookie(AdminSite.CookieName));
        Assert.DoesNotContain(b.SetCookieLog, c => c.StartsWith(AdminSite.CookieName + "="));
    }

    [Fact]
    public async Task With_production_host_filtering_the_admin_host_is_served_and_other_names_are_refused()
    {
        using var hf = TempDir.Create("packmoat-hf-");   // declared first, so deleted after the site has stopped
        await using var f = await StartAsync(o =>
        {
            o.DatabasePath = hf.File("packmoat.db");
            o.MasterKeyBase64 = AesGcmSecretProtector.GenerateKeyBase64();
            o.AllowedHosts = "feeds.packmoat.test";      // the admin host is NOT listed here; the app adds it
        });
        var admin = f.Browser();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Browser("feeds.packmoat.test").GetAsync("/healthz")).StatusCode);
        foreach (var bad in new[] { "evil.example", "admin.packmoat.test.evil.example", "xadmin.packmoat.test" })
            Assert.Equal(HttpStatusCode.BadRequest, (await f.Browser(bad).GetAsync("/")).StatusCode);
        // a client-sent X-Forwarded-Host does not move the request to the other site
        var forged = await f.Browser("feeds.packmoat.test").SendAsync(HttpMethod.Get, "/tenants", tweak: r => r.Headers.TryAddWithoutValidation("X-Forwarded-Host", AdminHost));
        Assert.NotEqual(HttpStatusCode.OK, forged.StatusCode);
    }

    [Theory]
    [InlineData("admin.packmoat.test:8443")]
    [InlineData("evil.example")]
    [InlineData("ADMIN.packmoat.test:80")]
    public async Task The_redirect_uri_comes_from_the_configured_host_not_the_request(string requestHost)
    {
        await using var f = await StartAsync(fakeProvider: false);
        var o = f.App.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(AdminSite.RemoteScheme);
        var scheme = new AuthenticationScheme(AdminSite.RemoteScheme, null, typeof(OpenIdConnectHandler));
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = f.App.Services };
        http.Request.Scheme = "http";
        http.Request.Host = new Microsoft.AspNetCore.Http.HostString(requestHost);
        var expected = $"https://{AdminHost}{AdminSite.CallbackPath}";
        var wrong = "http://" + requestHost + AdminSite.CallbackPath;

        var redirect = new RedirectContext(http, scheme, o, new AuthenticationProperties()) { ProtocolMessage = new OpenIdConnectMessage { RedirectUri = wrong } };
        await o.Events.RedirectToIdentityProvider(redirect);
        Assert.Equal(expected, redirect.ProtocolMessage.RedirectUri);

        var props = new AuthenticationProperties();
        var received = new AuthorizationCodeReceivedContext(http, scheme, o, props) { TokenEndpointRequest = new OpenIdConnectMessage { RedirectUri = wrong } };
        await o.Events.AuthorizationCodeReceived(received);
        Assert.Equal(expected, received.TokenEndpointRequest!.RedirectUri);
        Assert.Equal(expected, props.Items[OpenIdConnectDefaults.RedirectUriForCodePropertiesKey]);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void A_key_folder_that_stays_open_to_others_stops_the_start()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("packmoat-keys-").FullName;
        try
        {
            File.SetUnixFileMode(dir, (UnixFileMode)Convert.ToInt32("755", 8));
            var ex = Assert.Throws<InvalidOperationException>(() => Packmoat.Host.Web.SignedInSite.EnsureOwnerOnly(dir, (_, _) => { }, File.GetUnixFileMode));      // a chmod that does nothing
            Assert.Contains(dir, ex.Message);
            Assert.Throws<InvalidOperationException>(() => Packmoat.Host.Web.SignedInSite.EnsureOwnerOnly(dir, (_, _) => throw new IOException("denied"), File.GetUnixFileMode));   // a chmod that fails
            Packmoat.Host.Web.SignedInSite.EnsureOwnerOnly(dir, File.SetUnixFileMode, File.GetUnixFileMode);                                                        // a real one fixes it
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir); }
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task An_existing_open_key_folder_is_tightened_at_start()
    {
        if (OperatingSystem.IsWindows()) return;
        using var keys = TempDir.Create("packmoat-keys-");
        var dir = keys.Path;
        File.SetUnixFileMode(dir, (UnixFileMode)Convert.ToInt32("777", 8));
        await using (await StartAsync(keysDir: dir)) { }
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
    }
}
