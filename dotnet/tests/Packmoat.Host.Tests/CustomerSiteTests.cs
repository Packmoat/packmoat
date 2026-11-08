using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host;
using Packmoat.Host.Admin;
using Packmoat.Host.Customer;
using Packmoat.Host.Web;

namespace Packmoat.Host.Tests;

[Collection("sqlite-pools")]
public partial class CustomerSiteTests
{
    private static async Task<AdminSiteFixture> StartAsync(bool configured = true, Action<PackmoatOptions>? configure = null, Action<IServiceCollection>? services = null)
    {
        var f = await AdminSiteFixture.StartAsync(o =>
        {
            o.AppHost = AppHost;
            if (configured)
            {
                o.AppClientId = AppClient;
                o.AppRemoteSchemeOverride = auth => auth.AddScheme<AuthenticationSchemeOptions, FakeAppEntraHandler>(AppSite.RemoteScheme, null);
            }
            configure?.Invoke(o);
        }, services: services);
        await f.Directories.BindAsync(new DirectoryBinding(DirA, "acme", f.Clock.UtcNow, "op"), default);
        await f.Directories.BindAsync(new DirectoryBinding(DirB, "globex", f.Clock.UtcNow, "op"), default);
        return f;
    }

    /// <summary>A person as Microsoft would describe them after validating the token: issuer and audience included.</summary>
    private static ClaimsPrincipal Customer(string tid = DirA, string oid = Oid, string? iss = null, string aud = AppClient, string name = "Ada Lovelace", string? email = null,
        DateTimeOffset? authTime = null, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("iss", iss ?? $"https://login.microsoftonline.com/{tid}/v2.0"), new("aud", aud), new("tid", tid), new("oid", oid), new("name", name),
        };
        if (authTime is { } at) claims.Add(new Claim("auth_time", at.ToUnixTimeSeconds().ToString()));   // the optional ID-token claim
        claims.AddRange(roles.Select(r => new Claim("roles", r)));
        if (email is not null) { claims.Add(new Claim("email", email)); claims.Add(new Claim("preferred_username", email)); claims.Add(new Claim("upn", email)); }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "FakeEntra", "name", "roles"));
    }

    private static async Task<(AdminBrowser Browser, HttpResponseMessage Callback)> SignInToAppAsync(AdminSiteFixture f, ClaimsPrincipal person)
    {
        f.AppIdentity.Next = person;
        var b = f.Browser(AppHost);
        var start = await b.GetAsync("/");   // a signed-out visit goes straight to Microsoft
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        return (b, await b.GetAsync(start.Headers.Location!.OriginalString));
    }

    private static async Task<AdminBrowser> SignedInAsync(AdminSiteFixture f, ClaimsPrincipal person)
    {
        var (b, callback) = await SignInToAppAsync(f, person);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.True(b.HasCookie(AppSite.CookieName));
        return b;
    }

    private static string Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : r.Content.Headers.TryGetValues(name, out var c) ? string.Join(",", c) : "";

    // The approved sentences, as the pages encode them.
    public static readonly string SignedOutText = AppHtml.E("You're signed out of Packmoat.");
    /// <summary>The core's not-linked card (#192). The hosted module's card, which offers sign-up or early access, is the hosted tests' (#197).</summary>
    public static readonly string NotLinkedText = AppHtml.E(NoCustomerSiteModule.NotLinkedSentence);
    public static readonly string PersonalText = AppHtml.E("Personal Microsoft accounts can't sign in. Choose your work or school account.");
    public static readonly string NoRoleText = AppHtml.E("Your organisation only lets in people with a Packmoat role. Ask your administrator to give you the Packmoat Developer role.");
    /// <summary>A person with no role and no acct claim (as here) has an unknown member status while auto-join is on (#80): the #79 no-role wording.</summary>
    public static readonly string MemberUnknownText = AppHtml.E("Your account has no Packmoat role. Ask your administrator to assign Packmoat.Admin or Packmoat.Developer.");
    public static readonly string CancelledText = AppHtml.E("Sign-in was cancelled, or your organisation hasn't approved Packmoat yet.");
    public static readonly string FailedText = AppHtml.E("Sign-in couldn't be completed. If it keeps happening, email support@contoso.example.");
    private const string SignInAgain = "<a class=\"btn\" href=\"/signin\">Sign in again</a>";

    /// <summary>
    /// Not signed in: a GET either goes to Microsoft (the fake's challenge sends the browser to the callback), or, within two minutes of the
    /// last automatic start, shows the signed-out card without redirecting. Never the page itself.
    /// </summary>
    private static async Task AssertNotSignedInAsync(AdminBrowser b, string path = "/home")
    {
        var r = await b.GetAsync(path);
        var body = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Signed in as", body);
        if (r.StatusCode == HttpStatusCode.Redirect) Assert.StartsWith(AppSite.CallbackPath + "?fake=1", r.Headers.Location!.OriginalString);
        else
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Null(r.Headers.Location);
            Assert.Contains(SignedOutText, body);
        }
    }

    // ---- who gets in, and as what

    [Fact]
    public async Task A_developer_of_a_linked_directory_sees_the_home_page_with_the_same_count_as_the_cli()
    {
        await using var f = await StartAsync();
        var token = await f.Tokens.IssueAsync("acme", "laptop", default);
        await f.Activity.RecordAsync(token.TokenId, DateOnly.FromDateTime(f.Clock.UtcNow.UtcDateTime), default);
        var cliCount = (await new AdminQueries(f.Tenants, f.Audit, f.Tokens, f.Activity).ActiveDevelopersAsync(ActorScope.Operator, "acme", 30, default)).Count;
        Assert.Equal(1, cliCount);

        var b = await SignedInAsync(f, Customer(roles: CustomerSignIn.DeveloperRole));
        var (status, body, r) = await b.GetTextAsync("/home");
        Assert.Equal(200, status);
        Assert.Contains("<h1>Acme</h1>", body);
        Assert.Contains("Ada Lovelace", body);
        Assert.Contains("<dt>Role</dt><dd><span class=\"role\">Developer</span></dd>", body);
        Assert.Contains($"Active developers, last 30 days</dt><dd>{cliCount}</dd>", body);
        Assert.Contains("action=\"/signout\"", body);
        Assert.DoesNotContain("Globex", body);
        Assert.Equal("same-origin", Header(r, "Referrer-Policy"));
    }

    [Fact]
    public async Task The_home_page_shows_the_tenants_own_cooldown()
    {
        await using var f = await StartAsync();   // the fixture makes acme with 14 days and globex with 7
        var acme = (await (await SignedInAsync(f, Customer(roles: CustomerSignIn.DeveloperRole))).GetTextAsync("/home")).Body;
        Assert.Contains("<dt>Cooldown: new versions wait this long</dt><dd>14<span class=\"unit\">days</span></dd>", acme);
        Assert.DoesNotContain("<dd>7<span", acme);
        var globex = (await (await SignedInAsync(f, Customer(tid: DirB, roles: CustomerSignIn.DeveloperRole))).GetTextAsync("/home")).Body;
        Assert.Contains("<dd>7<span class=\"unit\">days</span></dd>", globex);
        Assert.DoesNotContain("<dd>14<span", globex);
    }

    [Fact]
    public async Task The_home_page_tells_each_role_where_to_change_the_cooldown()
    {
        await using var f = await StartAsync();
        var dev = (await (await SignedInAsync(f, Customer(roles: CustomerSignIn.DeveloperRole))).GetTextAsync("/home")).Body;
        Assert.Contains("To change the cooldown, ask your Admin.</p>", dev);
        Assert.DoesNotContain("or other settings", dev);
        var admin = (await (await SignedInAsync(f, Customer(roles: CustomerSignIn.AdminRole))).GetTextAsync("/home")).Body;
        Assert.Contains($"To change the cooldown or other settings, email {TestOperator.SupportEmail}.</p>", admin);
        Assert.DoesNotContain("ask your Admin", admin);
    }

    [Fact]
    public void No_customer_page_sends_people_to_an_unnamed_packmoat_contact()
    {
        // self-service or support@: never "contact Packmoat", "your Packmoat contact" or "Ask Packmoat" (#135)
        // The core's dotnet folder (Packmoat.sln), so the scan runs in core/ alone too (#197).
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Packmoat.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var files = Directory.GetFiles(Path.Combine(dir, "src", "Packmoat.Host", "Customer"), "*.cs");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (var banned in new[] { "contact Packmoat", "Packmoat contact", "Ask Packmoat" })
                Assert.False(source.Contains(banned, StringComparison.OrdinalIgnoreCase), $"{Path.GetFileName(file)} says \"{banned}\"");
        }
    }

    [Fact]
    public async Task Both_roles_show_as_admin()
    {
        await using var f = await StartAsync();
        var b = await SignedInAsync(f, Customer(roles: new[] { CustomerSignIn.DeveloperRole, CustomerSignIn.AdminRole }));
        Assert.Contains("<span class=\"role admin\">Admin</span>", (await b.GetTextAsync("/home")).Body);
    }

    [Theory]
    [InlineData(DirX, null, AppClient, "Admin", "not linked")]
    [InlineData(DirA, null, AppClient, null, "no role")]
    [InlineData(Consumer, null, AppClient, "Admin", "personal")]
    [InlineData(DirA, "https://login.microsoftonline.com/bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb/v2.0", AppClient, "Admin", "failed")]
    [InlineData(DirA, null, "33333333-3333-4333-8333-333333333333", "Admin", "failed")]   // the admin app's audience
    public async Task A_refused_person_gets_a_403_card_and_no_session(string tid, string? iss, string aud, string? role, string kind)
    {
        await using var f = await StartAsync();
        var roles = role is null ? Array.Empty<string>() : new[] { "Packmoat." + role };
        var (b, callback) = await SignInToAppAsync(f, Customer(tid: tid, iss: iss, aud: aud, roles: roles));
        Assert.Equal(HttpStatusCode.Forbidden, callback.StatusCode);
        Assert.Null(callback.Headers.Location);
        Assert.Equal(SignedInSite.ContentSecurityPolicy, Header(callback, "Content-Security-Policy"));
        var body = await callback.Content.ReadAsStringAsync();
        var sentence = kind switch { "not linked" => NotLinkedText, "no role" => MemberUnknownText, "personal" => PersonalText, _ => FailedText };
        Assert.Contains("<section class=\"card notice\"><h1>", body);
        Assert.Contains("</h1><p>" + sentence + "</p>", body);
        // every card offers "Sign in again", the core's not-linked one too (the hosted module's offers early access: the hosted tests')
        Assert.Contains(SignInAgain, body);
        Assert.False(b.HasCookie(AppSite.CookieName));
        Assert.DoesNotContain(b.SetCookieLog, c => c.StartsWith(AppSite.CookieName + "="));
        // the start marker from a moment ago stops a second automatic trip to Microsoft: the card, not a redirect
        var again = await b.GetAsync("/home");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Null(again.Headers.Location);
        Assert.Contains(SignedOutText, await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_unlinked_page_names_the_directory_and_nothing_else()
    {
        await using var f = await StartAsync();
        var (_, callback) = await SignInToAppAsync(f, Customer(tid: DirX, email: "ada@contoso.example", roles: CustomerSignIn.AdminRole));
        var body = await callback.Content.ReadAsStringAsync();
        Assert.Contains(NotLinkedText, body);
        Assert.Contains($"<p class=\"fine\">Directory id: {DirX}</p>", body);
        // the core's action is to sign in again (the hosted module's early-access card is the hosted tests'), never "send this id to your Packmoat contact"
        Assert.Contains($"<div class=\"actions\">{AppHtml.SignInAgainLink}</div>", body);
        var main = System.Text.RegularExpressions.Regex.Match(body, "<main id=\"main\">(.*)</main>", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        Assert.DoesNotContain("contact", main, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ada@contoso.example", body);
        Assert.DoesNotContain("Ada Lovelace", body);
        Assert.DoesNotContain(Oid, body);
        Assert.DoesNotContain(DirA, body);
    }

    [Fact]
    public async Task The_tenant_comes_only_from_the_binding_so_customer_a_never_sees_tenant_b()
    {
        await using var f = await StartAsync();
        // an email or a name that points at the other tenant changes nothing
        var a = await SignedInAsync(f, Customer(tid: DirA, name: "Globex admin", email: "admin@globex.example", roles: CustomerSignIn.AdminRole));
        foreach (var p in new[] { "/home", "/home?tenant=globex", "/home?tenant_id=globex" })
        {
            var body = (await a.GetTextAsync(p)).Body;
            Assert.Contains("<h1>Acme</h1>", body);
            Assert.DoesNotContain("<h1>Globex</h1>", body);
        }
        Assert.Equal(404, (await a.GetTextAsync("/tenants/globex")).Status);
        var b = await SignedInAsync(f, Customer(tid: DirB, roles: CustomerSignIn.DeveloperRole));
        Assert.Contains("<h1>Globex</h1>", (await b.GetTextAsync("/home")).Body);
    }

    [Fact]
    public async Task Unbinding_the_directory_ends_the_session_on_the_next_request()
    {
        await using var f = await StartAsync();
        var b = await SignedInAsync(f, Customer(roles: CustomerSignIn.DeveloperRole));
        Assert.Equal(200, (await b.GetTextAsync("/home")).Status);
        await f.Directories.UnbindAsync("acme", DirA, f.Clock.UtcNow, "op", default);
        await AssertNotSignedInAsync(b);
        Assert.False(b.HasCookie(AppSite.CookieName));
    }

    [Fact]
    public async Task The_session_holds_only_directory_object_name_and_role()
    {
        await using var f = await StartAsync();
        var b = await SignedInAsync(f, Customer(email: "ada@contoso.example", authTime: f.Clock.UtcNow.AddMinutes(-3), roles: new[] { CustomerSignIn.AdminRole, "Other.Role" }));
        var format = f.App.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AppSite.CookieScheme).TicketDataFormat;
        var ticket = format.Unprotect(b.Cookie(AppSite.CookieName));
        Assert.NotNull(ticket);
        Assert.Equal(new[] { AppSite.AuthTimeClaim, "name", "oid", AppSite.RoleClaim, "tid" }, ticket!.Principal.Claims.Select(c => c.Type).OrderBy(t => t, StringComparer.Ordinal));
        Assert.Equal(f.Clock.UtcNow.AddMinutes(-3).ToUnixTimeSeconds().ToString(), ticket.Principal.FindFirst(AppSite.AuthTimeClaim)!.Value);   // Microsoft's, not the callback time

        // Without Microsoft's auth_time the session holds no sign-in time at all (fail closed): never "now".
        var c = await SignedInAsync(f, Customer(oid: "99999999-9999-4999-8999-999999999999", roles: CustomerSignIn.AdminRole));
        var plain = format.Unprotect(c.Cookie(AppSite.CookieName))!;
        Assert.Equal(new[] { "name", "oid", AppSite.RoleClaim, "tid" }, plain.Principal.Claims.Select(x => x.Type).OrderBy(t => t, StringComparer.Ordinal));
        Assert.Equal("Admin", ticket.Principal.FindFirst(AppSite.RoleClaim)!.Value);
    }

    [Fact]
    public async Task Sign_out_needs_the_form_token_and_ends_the_session()
    {
        await using var f = await StartAsync();
        var b = await SignedInAsync(f, Customer(roles: CustomerSignIn.DeveloperRole));
        Assert.Equal(HttpStatusCode.BadRequest, (await b.SendAsync(HttpMethod.Post, "/signout", new[] { new KeyValuePair<string, string>("x", "y") })).StatusCode);
        Assert.True(b.HasCookie(AppSite.CookieName));
        var token = await b.TokenFromAsync("/home");
        var r = await b.SendAsync(HttpMethod.Post, "/signout", new[] { new KeyValuePair<string, string>(SignedInSite.AntiforgeryField, token) });
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal("/signed-out", r.Headers.Location!.OriginalString);
        Assert.False(b.HasCookie(AppSite.CookieName));
        Assert.False(b.HasCookie(AppSite.StartCookieName));
        // signed out and the marker gone: the next visit goes to Microsoft again
        var home = await b.GetAsync("/home");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.StartsWith(AppSite.CallbackPath, home.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Nothing_from_the_token_is_logged()
    {
        var logs = new CapturingLoggerProvider();
        await using var f = await StartAsync(services: s => s.AddSingleton<ILoggerProvider>(logs));
        await SignInToAppAsync(f, Customer(tid: DirX, email: "ada@contoso.example", name: "Ada Secretname", roles: CustomerSignIn.AdminRole));
        await SignInToAppAsync(f, Customer(tid: DirA, email: "bob@contoso.example", name: "Bob Secretname"));
        string all;
        lock (logs.Lines) all = string.Join("\n", logs.Lines);
        Assert.Contains("DirectoryNotLinked", all);
        Assert.Contains("MemberStatusUnknown", all);   // no role and no acct claim, with auto-join on (#80)
        foreach (var leak in new[] { "contoso", "Secretname", DirX, DirA, Oid }) Assert.DoesNotContain(leak, all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Repeated_refused_sign_ins_are_slowed_down()
    {
        await using var f = await StartAsync();
        for (var i = 0; i < 5; i++) await SignInToAppAsync(f, Customer(tid: DirX, roles: CustomerSignIn.AdminRole));
        // every way of starting a sign-in is slowed: the automatic start, a deep link and the explicit button. None redirects.
        f.AppIdentity.LastChallenge = null;
        foreach (var path in new[] { "/", "/tokens", "/team", "/signin" })
        {
            var b = f.Browser(AppHost);
            var r = await b.GetAsync(path);
            Assert.True(r.StatusCode == HttpStatusCode.TooManyRequests, path + ": " + r.StatusCode);
            Assert.Null(r.Headers.Location);
            Assert.False(string.IsNullOrEmpty(Header(r, "Retry-After")), path);
            Assert.Contains("Too many failed sign-ins. Wait a few minutes.", await r.Content.ReadAsStringAsync());
            Assert.False(b.HasCookie(AppSite.StartCookieName), path);
        }
        Assert.Null(f.AppIdentity.LastChallenge);
        // the admin site's own limiter is separate
        Assert.Equal(HttpStatusCode.Redirect, (await f.Browser().SignInAsync()).StatusCode);
    }

    // ---- the sites keep apart

    [Fact]
    public async Task The_app_cookie_means_nothing_on_the_admin_host_or_the_feed()
    {
        await using var f = await StartAsync();
        var app = await SignedInAsync(f, Customer(roles: CustomerSignIn.AdminRole));
        var value = app.Cookie(AppSite.CookieName)!;

        var admin = f.Browser();
        admin.SetCookie(AppSite.CookieName, value);
        Assert.Equal(HttpStatusCode.Redirect, (await admin.GetAsync("/tenants")).StatusCode);
        admin.SetCookie(AdminSite.CookieName, value);   // even renamed, it is not the admin scheme's ticket
        Assert.Equal(HttpStatusCode.Redirect, (await admin.GetAsync("/tenants")).StatusCode);

        var feed = f.Browser("127.0.0.1");
        feed.SetCookie(AppSite.CookieName, value);
        Assert.Equal(HttpStatusCode.Unauthorized, (await feed.GetAsync("/v3/index.json")).StatusCode);
    }

    [Fact]
    public async Task The_admin_cookie_means_nothing_on_the_app_host()
    {
        await using var f = await StartAsync();
        var admin = f.Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await admin.SignInAsync()).StatusCode);
        var value = admin.Cookie(AdminSite.CookieName)!;
        var app = f.Browser(AppHost);
        app.SetCookie(AdminSite.CookieName, value);
        await AssertNotSignedInAsync(app);
        app.SetCookie(AppSite.CookieName, value);
        await AssertNotSignedInAsync(app);
        await AssertNotSignedInAsync(app, "/");
    }

    [Fact]
    public async Task Each_sites_callback_is_handled_only_on_its_own_host()
    {
        await using var f = await StartAsync();
        f.AppIdentity.Next = Customer(roles: CustomerSignIn.AdminRole);
        // the app callback on the admin host and the feed: not run
        var onAdmin = f.Browser();
        var r1 = await onAdmin.GetAsync(AppSite.CallbackPath);
        Assert.NotEqual("/home", r1.Headers.Location?.OriginalString);
        Assert.False(onAdmin.HasCookie(AppSite.CookieName));
        Assert.False(onAdmin.HasCookie(AdminSite.CookieName));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Browser("127.0.0.1").GetAsync(AppSite.CallbackPath)).StatusCode);

        // the admin callback on the app host: not run (the admin fake would sign the operator in)
        var onApp = f.Browser(AppHost);
        var r2 = await onApp.GetAsync(AdminSite.CallbackPath);
        Assert.NotEqual("/tenants", r2.Headers.Location?.OriginalString);
        Assert.False(onApp.HasCookie(AdminSite.CookieName));
        Assert.False(onApp.HasCookie(AppSite.CookieName));

        // and each still works on its own host
        Assert.Equal(HttpStatusCode.Redirect, (await f.Browser().SignInAsync()).StatusCode);
        await SignedInAsync(f, Customer(roles: CustomerSignIn.AdminRole));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/home")]
    [InlineData("/nothing-here")]
    [InlineData("/app.css")]
    public async Task Every_app_response_carries_the_security_headers(string path)
    {
        await using var f = await StartAsync();
        var r = await f.Browser(AppHost).GetAsync(path);
        Assert.Equal("same-origin", Header(r, "Referrer-Policy"));
        Assert.Equal("DENY", Header(r, "X-Frame-Options"));
        Assert.Contains("no-store", Header(r, "Cache-Control"));
        Assert.Equal("max-age=31536000", Header(r, "Strict-Transport-Security"));
        Assert.Contains("frame-ancestors 'none'", Header(r, "Content-Security-Policy"));
    }

    // ---- not configured

    [Fact]
    public async Task Without_a_client_id_and_certificate_every_app_page_answers_503()
    {
        await using var f = await StartAsync(configured: false);
        var b = f.Browser(AppHost);
        foreach (var p in new[] { "/", "/home", AppSite.CallbackPath, AdminSite.CallbackPath, "/nothing", "/signin" })
        {
            var r = await b.GetAsync(p);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
            Assert.Equal("DENY", Header(r, "X-Frame-Options"));
        }
        // Only the two static files answer, so the 503 page is styled. They carry the same security headers and no cookie.
        foreach (var (p, type) in new[] { ("/app.css", "text/css"), ("/favicon.svg", "image/svg+xml") })
        {
            var r = await b.GetAsync(p);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.StartsWith(type, Header(r, "Content-Type"));
            Assert.Equal("DENY", Header(r, "X-Frame-Options"));
            Assert.Equal(SignedInSite.ContentSecurityPolicy, Header(r, "Content-Security-Policy"));
        }
        Assert.False(b.HasCookie(AppSite.CookieName));
        Assert.False(f.App.Services.GetRequiredService<AppSiteSettings>().SignInAvailable);
        // the admin site is untouched
        Assert.Equal(HttpStatusCode.Redirect, (await f.Browser().SignInAsync()).StatusCode);
    }

    [Fact]
    public async Task An_unreadable_certificate_file_also_means_503_not_a_crash()
    {
        var keys = TempDir.Create("packmoat-bad-");
        var bad = keys.File("app.pem");
        try
        {
            await File.WriteAllTextAsync(bad, "not a pem");
            await using var f = await StartAsync(configured: false, configure: o => { o.AppClientId = AppClient; o.AppCertPath = bad; });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Browser(AppHost).GetAsync("/")).StatusCode);
        }
        finally { keys.Dispose(); }
    }

    [Fact]
    public async Task Without_an_app_host_there_is_no_customer_site_at_all()
    {
        await using var f = await AdminSiteFixture.StartAsync();
        Assert.Null(f.App.Services.GetService<AppSiteSettings>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Browser(AppHost).GetAsync("/")).StatusCode);   // just the proxy, as before
    }

    [Fact]
    public async Task The_app_host_must_be_its_own_hostname()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdminSiteFixture.StartAsync(o => o.AppHost = AdminSiteFixture.AdminHost));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AdminSiteFixture.StartAsync(o => o.AppHost = "app.example.com/path"));
    }

    // ---- the real Microsoft settings (no network: options and events only)

    /// <summary>A key and certificate in <paramref name="path"/>, in a folder of its own that the caller disposes (#292).</summary>
    private static TempDir WritePem(out string path, out X509Certificate2 cert, DateTimeOffset? notAfter = null)
    {
        var dir = TempDir.Create("packmoat-app-");
        try
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=packmoat-app-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var until = notAfter ?? new DateTimeOffset(2027, 10, 10, 0, 0, 0, TimeSpan.Zero);
            cert = req.CreateSelfSigned(until.AddYears(-2), until);
            path = dir.File("app.pem");
            File.WriteAllText(path, cert.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem());
            return dir;
        }
        catch
        {
            dir.Dispose();
            throw;
        }
    }

    [Fact]
    public async Task The_real_sign_in_uses_organizations_pkce_its_own_callback_and_a_certificate()
    {
        var keys = WritePem(out var pem, out _);
        try
        {
            await using var f = await StartAsync(configured: false, configure: o => { o.AppClientId = AppClient.ToUpperInvariant(); o.AppCertPath = pem; });
            Assert.True(f.App.Services.GetRequiredService<AppSiteSettings>().SignInAvailable);
            var o = f.App.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(AppSite.RemoteScheme);
            Assert.Equal("https://login.microsoftonline.com/organizations/v2.0", o.Authority);
            Assert.Equal(AppClient, o.ClientId);
            Assert.Null(o.ClientSecret);
            Assert.Equal(new Microsoft.AspNetCore.Http.PathString("/signin-microsoft"), o.CallbackPath);
            Assert.Equal(AppSite.CookieScheme, o.SignInScheme);
            Assert.Equal(OpenIdConnectResponseType.Code, o.ResponseType);
            Assert.Equal(OpenIdConnectResponseMode.Query, o.ResponseMode);
            Assert.True(o.UsePkce);
            Assert.Equal(new[] { "openid", "profile" }, o.Scope.OrderBy(x => x));
            Assert.Equal(AppClient, o.TokenValidationParameters.ValidAudience);
            Assert.NotNull(o.TokenValidationParameters.IssuerValidator);

            // redirect uri pinned to the configured host; the code redemption carries a certificate assertion, never a secret
            var scheme = new AuthenticationScheme(AppSite.RemoteScheme, null, typeof(OpenIdConnectHandler));
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = f.App.Services };
            http.Request.Host = new Microsoft.AspNetCore.Http.HostString("evil.example");
            var redirect = new RedirectContext(http, scheme, o, new AuthenticationProperties()) { ProtocolMessage = new OpenIdConnectMessage { RedirectUri = "http://evil.example/signin-microsoft" } };
            await o.Events.RedirectToIdentityProvider(redirect);
            Assert.Equal($"https://{AppHost}/signin-microsoft", redirect.ProtocolMessage.RedirectUri);
            var received = new AuthorizationCodeReceivedContext(http, scheme, o, new AuthenticationProperties()) { TokenEndpointRequest = new OpenIdConnectMessage { RedirectUri = "x", ClientSecret = "should-go" } };
            await o.Events.AuthorizationCodeReceived(received);
            Assert.Equal($"https://{AppHost}/signin-microsoft", received.TokenEndpointRequest!.RedirectUri);
            Assert.Null(received.TokenEndpointRequest.ClientSecret);
            Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", received.TokenEndpointRequest.ClientAssertionType);
            Assert.False(string.IsNullOrEmpty(received.TokenEndpointRequest.ClientAssertion));
        }
        finally { keys.Dispose(); }
    }

    [Fact]
    public async Task The_client_assertion_is_a_short_signed_jwt_for_the_token_endpoint()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        using var rsa = RSA.Create(2048);
        using var signing = new CertificateRequest("CN=assertion", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        var jwt = AppSite.ClientAssertion(signing, AppClient, AppSiteSettings.TokenEndpoint, now);
        var token = new JsonWebToken(jwt);
        Assert.Equal("RS256", token.Alg);
        Assert.True(token.TryGetHeaderValue<string>("x5t", out var x5t) && x5t.Length > 0);
        Assert.Equal(AppClient, token.Issuer);
        Assert.Equal(AppClient, token.Subject);
        Assert.Equal(new[] { AppSiteSettings.TokenEndpoint }, token.Audiences);
        Assert.Equal(TimeSpan.FromMinutes(5), token.ValidTo - token.ValidFrom);
        Assert.False(string.IsNullOrEmpty(token.Id));
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(jwt, new TokenValidationParameters
        {
            ValidIssuer = AppClient, ValidAudience = AppSiteSettings.TokenEndpoint, IssuerSigningKey = new X509SecurityKey(signing), ValidateLifetime = false,
        });
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(DirA, "https://login.microsoftonline.com/" + DirA + "/v2.0", true)]
    [InlineData(DirA, "https://login.microsoftonline.com/" + DirB + "/v2.0", false)]
    [InlineData(DirA, "https://login.microsoftonline.com/{tenantid}/v2.0", false)]
    [InlineData(DirA, "https://sts.windows.net/" + DirA + "/", false)]
    [InlineData(Consumer, "https://login.microsoftonline.com/" + Consumer + "/v2.0", false)]
    [InlineData(null, "https://login.microsoftonline.com/" + DirA + "/v2.0", false)]
    public void The_issuer_validator_accepts_only_the_tokens_own_directory(string? tid, string issuer, bool ok)
    {
        var claims = new Dictionary<string, object> { ["oid"] = Oid };
        if (tid is not null) claims["tid"] = tid;
        var token = new JsonWebToken(new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor { Issuer = issuer, Claims = claims }));
        if (ok) Assert.Equal(issuer, AppSite.ValidateIssuer(issuer, token, new TokenValidationParameters()));
        else Assert.Throws<SecurityTokenInvalidIssuerException>(() => AppSite.ValidateIssuer(issuer, token, new TokenValidationParameters()));
    }

    // ---- security review follow-ups

    /// <summary>
    /// Pretend Microsoft metadata with one signing key whose published "issuer" is <paramref name="keyIssuer"/>, and an ID token for DirA
    /// signed by it. The token is validated with the customer site's real parameters.
    /// </summary>
    private static async Task<TokenValidationResult> ValidateWithKeyIssuerAsync(string? keyIssuer)
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = "k1" };
        var published = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = "k1" });
        if (keyIssuer is not null) published.AdditionalData["issuer"] = keyIssuer;
        var config = new OpenIdConnectConfiguration { Issuer = "https://login.microsoftonline.com/{tenantid}/v2.0", JsonWebKeySet = new JsonWebKeySet() };
        config.JsonWebKeySet.Keys.Add(published);
        config.SigningKeys.Add(published);

        var now = DateTime.UtcNow;
        var jwt = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = $"https://login.microsoftonline.com/{DirA}/v2.0",
            Audience = AppClient,
            IssuedAt = now, NotBefore = now, Expires = now.AddMinutes(10),
            Claims = new Dictionary<string, object> { ["tid"] = DirA, ["oid"] = Oid },
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
        });
        var p = AppSite.TokenValidation(AppClient);
        p.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(config);
        return await new JsonWebTokenHandler().ValidateTokenAsync(jwt, p);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/{tenantid}/v2.0", true)]                // Microsoft's common keys
    [InlineData("https://login.microsoftonline.com/" + DirA + "/v2.0", true)]              // a key of the token's own directory
    [InlineData("https://login.microsoftonline.com/" + DirB + "/v2.0", false)]             // another directory's key cannot sign for DirA
    public async Task A_key_published_for_another_directory_cannot_sign_a_token(string keyIssuer, bool valid)
    {
        var result = await ValidateWithKeyIssuerAsync(keyIssuer);
        Assert.True(result.IsValid == valid, result.Exception?.GetType().Name);
    }

    [Fact]
    public async Task An_id_token_in_the_callback_fails_the_sign_in_on_both_sites()
    {
        var keys = WritePem(out var pem, out _);
        try
        {
            await using var f = await AdminSiteFixture.StartAsync(o => { o.AppHost = AppHost; o.AppClientId = AppClient; o.AppCertPath = pem; }, fakeProvider: false);
            var monitor = f.App.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();
            foreach (var name in new[] { AdminSite.RemoteScheme, AppSite.RemoteScheme })
            {
                var o = monitor.Get(name);
                var scheme = new AuthenticationScheme(name, null, typeof(OpenIdConnectHandler));
                var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = f.App.Services };

                var injected = new MessageReceivedContext(http, scheme, o, new AuthenticationProperties()) { ProtocolMessage = new OpenIdConnectMessage { Code = "c", State = "s", IdToken = "eyJ.injected.token" } };
                await o.Events.MessageReceived(injected);
                Assert.True(injected.Result?.Failure is not null, name + " accepted an ID token from the callback");

                var normal = new MessageReceivedContext(http, scheme, o, new AuthenticationProperties()) { ProtocolMessage = new OpenIdConnectMessage { Code = "c", State = "s" } };
                await o.Events.MessageReceived(normal);
                Assert.Null(normal.Result);
            }
        }
        finally { keys.Dispose(); }
    }

    [Fact]
    public async Task People_of_a_suspended_tenant_see_only_that_it_is_suspended()
    {
        await using var f = await StartAsync();
        await f.Tokens.IssueAsync("acme", "laptop", default);
        var b = await SignedInAsync(f, Customer(roles: CustomerSignIn.AdminRole));
        await f.Tenants.SetActiveAsync("acme", false, default);
        var (status, body, _) = await b.GetTextAsync("/home");
        Assert.Equal(403, status);
        Assert.Contains(TestOperator.Name + " has paused this account, so feeds and tokens are off.", body);
        Assert.Contains(AppHtml.E("Email " + TestOperator.SupportEmail + " to restore access.") + "</p>", body);   // no module: no note (#197 C3)
        Assert.Contains($"<a class=\"btn\" href=\"mailto:{TestOperator.SupportEmail}\">Email {TestOperator.Name} support</a>", body);
        Assert.DoesNotContain("Acme", body);
        Assert.DoesNotContain("Active developers", body);
        // and a fresh sign-in sees the same
        var again = await SignedInAsync(f, Customer(roles: CustomerSignIn.DeveloperRole));
        Assert.Contains(TestOperator.Name + " has paused this account", (await again.GetTextAsync("/home")).Body);
        await f.Tenants.SetActiveAsync("acme", true, default);
        Assert.Contains("<h1>Acme</h1>", (await again.GetTextAsync("/home")).Body);
    }

    [Theory]
    [InlineData(2026, 10, 20, "Warning", "expires on 2026-10-20")]   // the fixture clock is 2026-10-10
    [InlineData(2026, 10, 1, "Error", "EXPIRED on 2026-10-01")]
    [InlineData(2027, 1, 1, null, null)]
    public async Task The_certificate_expiry_is_logged_at_start_without_key_material(int y, int m, int d, string? level, string? text)
    {
        var keys = WritePem(out var pem, out var cert, new DateTimeOffset(y, m, d, 0, 0, 0, TimeSpan.Zero));
        var logs = new CapturingLoggerProvider();
        try
        {
            await using var f = await StartAsync(configured: false, configure: o => { o.AppClientId = AppClient; o.AppCertPath = pem; }, services: s => s.AddSingleton<ILoggerProvider>(logs));
            string all;
            lock (logs.Lines) all = string.Join("\n", logs.Lines);
            var lines = all.Split('\n').Where(l => l.Contains("sign-in certificate", StringComparison.Ordinal)).ToArray();
            if (text is null) Assert.Empty(lines);
            else Assert.Contains(lines, l => l.Contains(text, StringComparison.Ordinal));
            foreach (var leak in new[] { "PRIVATE KEY", "BEGIN", cert.Thumbprint, Convert.ToBase64String(cert.RawData)[..40] })
                Assert.DoesNotContain(leak, all, StringComparison.OrdinalIgnoreCase);
        }
        finally { keys.Dispose(); }
        // the level is checked on the pure function, where the logger records it
        var probe = new LevelLogger();
        AppSite.CheckCertificateExpiry(probe, new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc), new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(level, probe.Level?.ToString());
    }

    private sealed class LevelLogger : ILogger
    {
        public LogLevel? Level { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Level = logLevel;
    }

    /// <summary>A store that wrongly answers every lookup with DirA's binding. The site must still refuse a person from another directory.</summary>
    private sealed class WrongBindingStore : ISignInDirectoryStore
    {
        private readonly DirectoryBinding _always;
        public WrongBindingStore(DirectoryBinding always) => _always = always;
        public Task<bool> BindAsync(DirectoryBinding binding, CancellationToken ct) => Task.FromResult(false);
        public Task<int?> UnbindAsync(string tenantId, string azureTenantId, DateTimeOffset at, string revokedBy, CancellationToken ct) => Task.FromResult<int?>(null);
        public Task<DirectoryBinding?> GetAsync(string azureTenantId, CancellationToken ct) => Task.FromResult<DirectoryBinding?>(_always);
        public Task<IReadOnlyList<DirectoryBinding>> ListForTenantAsync(string tenantId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DirectoryBinding>>(new[] { _always });
    }

    [Fact]
    public async Task A_binding_for_another_directory_does_not_let_anyone_in()
    {
        await using var f = await StartAsync(services: s => s.AddSingleton<ISignInDirectoryStore>(new WrongBindingStore(new DirectoryBinding(DirA, "acme", DateTimeOffset.UnixEpoch, "op"))));
        var (b, callback) = await SignInToAppAsync(f, Customer(tid: DirX, roles: CustomerSignIn.AdminRole));
        Assert.Equal(HttpStatusCode.Forbidden, callback.StatusCode);
        Assert.Contains(NotLinkedText, await callback.Content.ReadAsStringAsync());
        Assert.False(b.HasCookie(AppSite.CookieName));
        await AssertNotSignedInAsync(b);
    }

    // ---- how the pages look: the restyle adds no script, no inline style, no other-origin load, no new header and no CSP change

    private static readonly string[] AllowedOtherOrigin = { TestOperator.MarketingUrl, TestOperator.PrivacyUrl, TestOperator.EarlyAccessUrl, "mailto:" + TestOperator.SupportEmail };

    /// <summary>Every header a site page may carry. The restyle must not add one.</summary>
    private static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Security-Policy", "X-Content-Type-Options", "Referrer-Policy", "Cache-Control", "Pragma", "Strict-Transport-Security",
        "X-Frame-Options", "X-Robots-Tag", "Content-Type", "Content-Length", "Transfer-Encoding", "Date", "Set-Cookie", "Retry-After", "Location",
    };

    /// <summary>No script, no inline style, no event handler; every URL is same-origin except the packmoat.com links and the support address.</summary>
    private static void AssertSelfContained(string html)
    {
        Assert.StartsWith("<!doctype html><html lang=\"en\">", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"(?i)\sstyle\s*=", html);
        Assert.DoesNotMatch(@"(?i)\son[a-z]+\s*=", html);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, @"(?i)\b(?:href|src|srcset|action|formaction|xlink:href|data|poster)\s*=\s*""([^""]*)"""))
        {
            var url = m.Groups[1].Value;
            var sameOrigin = (url.StartsWith('/') && !url.StartsWith("//")) || url.StartsWith('#');
            Assert.True(sameOrigin || AllowedOtherOrigin.Contains(url), "unexpected URL " + url);
        }
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, @"(?i)(?:[a-z][a-z0-9+.-]*:)?//[^\s""'<>]+"))
            Assert.Contains(m.Value, AllowedOtherOrigin);
    }

    private static void AssertHeadersUnchanged(HttpResponseMessage r, string csp)
    {
        Assert.Equal(csp, Header(r, "Content-Security-Policy"));
        foreach (var name in r.Headers.Select(h => h.Key).Concat(r.Content.Headers.Select(h => h.Key)))
            Assert.True(AllowedHeaders.Contains(name), "unexpected header " + name);
    }

    [Fact]
    public async Task The_stylesheet_loads_nothing_and_keeps_none_of_the_old_sign_in_page()
    {
        await using var f = await StartAsync();
        var (status, css, r) = await f.Browser(AppHost).GetTextAsync("/app.css");
        Assert.Equal(200, status);
        Assert.StartsWith("text/css", Header(r, "Content-Type"));
        foreach (var banned in new[] { "url(", "@import", "//", "@font-face" }) Assert.DoesNotContain(banned, css, StringComparison.OrdinalIgnoreCase);
        // the sign-in page is gone, and so are its rules: Microsoft's button, the form, the preview list and the early-access line
        foreach (var dead in new[] { ".ms-signin", ".ms-logo", ".signin-form", ".preview", ".ticks", ".alt{" }) Assert.DoesNotContain(dead, css);
        Assert.Contains("--moat:#0a6a5b", css);
        Assert.Contains(".card{", css);
        Assert.Contains(":focus-visible", css);
        AssertHeadersUnchanged(r, SignedInSite.ContentSecurityPolicy);
    }

    [Fact]
    public async Task Every_restyled_page_keeps_the_csp_and_headers_and_loads_nothing_from_elsewhere()
    {
        await using var f = await StartAsync();
        var pages = new List<(string Name, int Status, string Body, HttpResponseMessage Response, string Csp)>();
        async Task Add(string name, HttpResponseMessage r, string csp) => pages.Add((name, (int)r.StatusCode, await r.Content.ReadAsStringAsync(), r, csp));

        await Add("signed out", await f.Browser(AppHost).GetAsync("/signed-out"), SignedInSite.ContentSecurityPolicy);
        var dev = await SignedInAsync(f, Customer(roles: CustomerSignIn.DeveloperRole));
        await Add("home", await dev.GetAsync("/home"), SignedInSite.ContentSecurityPolicy);
        await Add("not found", await dev.GetAsync("/nothing-here"), SignedInSite.ContentSecurityPolicy);
        await Add("not linked", (await SignInToAppAsync(f, Customer(tid: DirX, roles: CustomerSignIn.AdminRole))).Callback, SignedInSite.ContentSecurityPolicy);
        await Add("no role", (await SignInToAppAsync(f, Customer())).Callback, SignedInSite.ContentSecurityPolicy);
        await Add("personal", (await SignInToAppAsync(f, Customer(tid: Consumer, roles: CustomerSignIn.AdminRole))).Callback, SignedInSite.ContentSecurityPolicy);
        await Add("not accepted", (await SignInToAppAsync(f, Customer(aud: "33333333-3333-4333-8333-333333333333", roles: CustomerSignIn.AdminRole))).Callback, SignedInSite.ContentSecurityPolicy);
        // that was four refusals from this address; one more and the limiter answers 429
        await SignInToAppAsync(f, Customer(tid: DirX, roles: CustomerSignIn.AdminRole));
        await Add("too many", await f.Browser(AppHost).GetAsync("/signin"), SignedInSite.ContentSecurityPolicy);
        await f.Tenants.SetActiveAsync("acme", false, default);
        await Add("suspended", await dev.GetAsync("/home"), SignedInSite.ContentSecurityPolicy);

        await using var off = await StartAsync(configured: false);
        await Add("not configured", await off.Browser(AppHost).GetAsync("/"), SignedInSite.ContentSecurityPolicy);

        var expected = new Dictionary<string, (int Status, string Text)>
        {
            ["signed out"] = (200, SignedOutText),
            ["home"] = (200, "Active developers, last 30 days"),
            ["not found"] = (404, "<h1>Not found</h1>"),
            ["not linked"] = (403, NotLinkedText),
            ["no role"] = (403, MemberUnknownText),
            ["personal"] = (403, PersonalText),
            ["not accepted"] = (403, FailedText),
            ["too many"] = (429, "Too many failed sign-ins. Wait a few minutes."),
            ["suspended"] = (403, TestOperator.Name + " has paused this account, so feeds and tokens are off."),
            ["not configured"] = (503, "Sign-in is not configured on this server yet."),
        };
        Assert.Equal(expected.Keys.Order(), pages.Select(p => p.Name).Order());
        foreach (var p in pages)
        {
            Assert.True(expected[p.Name].Status == p.Status, $"{p.Name}: status {p.Status}");
            Assert.True(p.Body.Contains(expected[p.Name].Text, StringComparison.Ordinal), $"{p.Name}: missing \"{expected[p.Name].Text}\"");
            // the shared shell: the stylesheet, the mark and the footer
            Assert.Contains("<link rel=\"stylesheet\" href=\"/app.css\">", p.Body);
            Assert.Contains("<svg class=\"mark\"", p.Body);
            Assert.Contains("<footer>", p.Body);
            Assert.Contains($"<a href=\"{TestOperator.PrivacyUrl}\">Privacy</a></nav>", p.Body);   // the core's footer: no module links
            // the error cards say what to do next; the sign-in cards are one sentence and one action
            if (p.Name is "not found" or "too many" or "suspended" or "not configured") Assert.Contains("What to do next", p.Body);
            AssertSelfContained(p.Body);
            AssertHeadersUnchanged(p.Response, p.Csp);
        }
    }

    [Fact]
    public async Task Customer_pages_carry_none_of_the_operator_signals()
    {
        await using var f = await StartAsync();
        var bodies = new List<string> { (await f.Browser(AppHost).GetTextAsync("/signed-out")).Body };
        var dev = await SignedInAsync(f, Customer(roles: CustomerSignIn.DeveloperRole));
        bodies.Add((await dev.GetTextAsync("/home")).Body);
        bodies.Add((await dev.GetTextAsync("/nothing-here")).Body);
        var css = (await f.Browser(AppHost).GetTextAsync("/app.css")).Body;
        var favicon = (await f.Browser(AppHost).GetTextAsync("/favicon.svg")).Body;
        foreach (var body in bodies)
        {
            Assert.Contains("</head><body><a class=\"skip\"", body);
            Assert.DoesNotContain("operator", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("role=\"note\"", body);
            Assert.DoesNotContain("class=\"pill\"", body);
            Assert.DoesNotContain("/admin.css", body);
            Assert.Matches("<title>[^<]* - Packmoat</title>", body);
            Assert.Contains("<link rel=\"stylesheet\" href=\"/app.css\">", body);
            Assert.Contains($"<a href=\"{TestOperator.MarketingUrl}\">packages.contoso.example</a>", body);
            Assert.Contains($"<a href=\"mailto:{TestOperator.SupportEmail}\">", body);
        }
        Assert.DoesNotContain("operator", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--accent", css);
        Assert.Equal(AppHtml.FaviconSvg, favicon);
        Assert.NotEqual(AdminHtml.OperatorFaviconSvg, favicon);
    }

    // ---- direct sign-in: no sign-in page, straight to Microsoft (#79)

    private static string? SetCookie(AdminBrowser b, string name) => b.SetCookieLog.LastOrDefault(c => c.StartsWith(name + "=", StringComparison.Ordinal));

    [Fact]
    public async Task A_signed_out_visit_to_the_root_goes_straight_to_microsoft_as_a_silent_non_fresh_start()
    {
        await using var f = await StartAsync();
        var b = f.Browser(AppHost);
        var r = await b.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(AppSite.CallbackPath + "?fake=1", r.Headers.Location!.OriginalString);   // the fake's stand-in for login.microsoftonline.com
        var props = f.AppIdentity.LastChallenge!;
        Assert.Equal("/home", props.RedirectUri);
        // an automatic start is never a fresh sign-in: no prompt, no max_age
        Assert.False(props.Parameters.ContainsKey(OpenIdConnectParameterNames.Prompt));
        Assert.False(props.Parameters.ContainsKey(OpenIdConnectParameterNames.MaxAge));
        Assert.Null((props as OpenIdConnectChallengeProperties)?.Prompt);
        Assert.Null((props as OpenIdConnectChallengeProperties)?.MaxAge);
        // the default CSP: a GET followed by a 302 is not a form post, so form-action does not need Microsoft's host
        Assert.Equal(SignedInSite.ContentSecurityPolicy, Header(r, "Content-Security-Policy"));
        Assert.Contains("frame-ancestors 'none'", Header(r, "Content-Security-Policy"));
        // the loop guard: one marker, HttpOnly, Secure, SameSite=Lax, two minutes, holding the clock's Unix seconds
        var marker = SetCookie(b, AppSite.StartCookieName)!;
        Assert.StartsWith($"{AppSite.StartCookieName}={f.Clock.UtcNow.ToUnixTimeSeconds()};", marker);
        foreach (var part in new[] { "max-age=120", "path=/", "secure", "samesite=lax", "httponly" }) Assert.Contains(part, marker, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", marker, StringComparison.OrdinalIgnoreCase);

        // signed in, the root is just the way home
        var dev = await SignedInAsync(f, Customer(roles: CustomerSignIn.DeveloperRole));
        var home = await dev.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal("/home", home.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("/tokens", "/tokens")]
    [InlineData("/team", "/team")]
    [InlineData("/home", "/home")]
    [InlineData("/", "/home")]
    [InlineData("/nothing-here", "/home")]
    [InlineData("/tokens/revoke", "/home")]
    [InlineData("//evil.example/x", "/home")]
    [InlineData("/\\evil.example", "/home")]
    [InlineData("/TOKENS", "/home")]
    [InlineData("/tokens/", "/home")]
    [InlineData("/tokens?x=//evil.example", "/tokens")]
    [InlineData("/team?return=https://evil.example/", "/team")]
    public async Task A_signed_out_deep_link_returns_only_to_a_fixed_list_of_pages(string path, string expected)
    {
        await using var f = await StartAsync();
        var r = await f.Browser(AppHost).GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(expected, f.AppIdentity.LastChallenge!.RedirectUri);
    }

    [Theory]
    [InlineData("/tokens", "/tokens", "Create my token")]
    [InlineData("/team", "/team", "Create an automation token")]   // the section, shown with or without its form (#200)
    [InlineData("/nothing-here", "/home", "Active developers, last 30 days")]
    public async Task A_deep_link_survives_the_round_trip_through_microsoft(string path, string landsOn, string text)
    {
        await using var f = await StartAsync();
        f.AppIdentity.Next = Customer(authTime: f.Clock.UtcNow, roles: CustomerSignIn.AdminRole);
        var b = f.Browser(AppHost);
        var start = await b.GetAsync(path);
        var callback = await b.GetAsync(start.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(landsOn, callback.Headers.Location!.OriginalString);
        var (status, body, _) = await b.GetTextAsync(landsOn);
        Assert.Equal(200, status);
        Assert.Contains(text, body);
    }

    [Theory]
    [InlineData("/tokens", "/tokens")]
    [InlineData("/team", "/team")]
    [InlineData("/home", "/home")]
    [InlineData("/", "/home")]
    [InlineData("", "/home")]
    [InlineData("//evil.example", "/home")]
    [InlineData("/\\evil.example", "/home")]
    [InlineData("https://evil.example/tokens", "/home")]
    [InlineData("/Tokens", "/home")]
    [InlineData("/team/create", "/home")]
    [InlineData("/signout", "/home")]
    public void The_return_path_is_an_exact_case_sensitive_allowlist(string path, string expected) => Assert.Equal(expected, AppSite.ReturnPath(path));

    [Fact]
    public async Task The_sign_in_link_asks_microsoft_to_offer_an_account_picker_and_a_post_starts_nothing()
    {
        await using var f = await StartAsync();
        var b = f.Browser(AppHost);
        var r = await b.GetAsync("/signin");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(AppSite.CallbackPath + "?fake=1", r.Headers.Location!.OriginalString);
        var props = Assert.IsType<OpenIdConnectChallengeProperties>(f.AppIdentity.LastChallenge);
        Assert.Equal("select_account", props.Prompt);
        Assert.Null(props.MaxAge);
        Assert.Equal("/home", props.RedirectUri);
        Assert.Equal(SignedInSite.ContentSecurityPolicy, Header(r, "Content-Security-Policy"));

        // POST /signin is gone: without the form token it is refused, with one (from a signed-in page) it starts nothing
        f.AppIdentity.LastChallenge = null;
        var post = await f.Browser(AppHost).SendAsync(HttpMethod.Post, "/signin", new[] { new KeyValuePair<string, string>("x", "y") });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Null(post.Headers.Location);
        var dev = await SignedInAsync(f, Customer(roles: CustomerSignIn.DeveloperRole));
        f.AppIdentity.LastChallenge = null;
        var token = await dev.TokenFromAsync("/home");
        var signedInPost = await dev.SendAsync(HttpMethod.Post, "/signin", new[] { new KeyValuePair<string, string>(SignedInSite.AntiforgeryField, token) });
        Assert.Null(signedInPost.Headers.Location);
        Assert.NotEqual(HttpStatusCode.Redirect, signedInPost.StatusCode);
        Assert.Null(f.AppIdentity.LastChallenge);
        // signed out, a token minted for someone else starts nothing either (a valid signed-out token: the 401 test further down)
        var anon = f.Browser(AppHost);
        anon.SetCookie(SignedInSite.AntiforgeryCookieName, dev.Cookie(SignedInSite.AntiforgeryCookieName)!);
        var anonPost = await anon.SendAsync(HttpMethod.Post, "/signin", new[] { new KeyValuePair<string, string>(SignedInSite.AntiforgeryField, token) });
        Assert.Null(anonPost.Headers.Location);
        Assert.Null(f.AppIdentity.LastChallenge);
    }

    [Fact]
    public async Task A_second_automatic_start_within_two_minutes_shows_the_card_instead_of_looping()
    {
        await using var f = await StartAsync();
        var b = f.Browser(AppHost);
        Assert.Equal(HttpStatusCode.Redirect, (await b.GetAsync("/")).StatusCode);   // the first start; the browser comes back without a session

        f.Clock.Advance(TimeSpan.FromSeconds(119));
        f.AppIdentity.LastChallenge = null;
        foreach (var path in new[] { "/", "/home", "/tokens" })
        {
            var again = await b.GetAsync(path);
            Assert.True(again.StatusCode == HttpStatusCode.OK, path + ": " + again.StatusCode);
            Assert.Null(again.Headers.Location);
            var body = await again.Content.ReadAsStringAsync();
            Assert.Contains("<h1>Signed out</h1><p>" + SignedOutText + "</p>", body);
            Assert.Contains(SignInAgain, body);
            Assert.DoesNotContain("http-equiv", body, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(SignedInSite.ContentSecurityPolicy, Header(again, "Content-Security-Policy"));
        }
        Assert.Null(f.AppIdentity.LastChallenge);

        // "Sign in again" (a link on this site) is never blocked by the marker
        var explicitStart = await b.SendAsync(HttpMethod.Get, "/signin", tweak: SameOrigin);
        Assert.Equal(HttpStatusCode.Redirect, explicitStart.StatusCode);
        Assert.Equal("select_account", Assert.IsType<OpenIdConnectChallengeProperties>(f.AppIdentity.LastChallenge).Prompt);

        // the explicit start refreshed the marker; 121 seconds after it, an automatic start goes to Microsoft again
        f.Clock.Advance(TimeSpan.FromSeconds(121));
        f.AppIdentity.LastChallenge = null;
        var later = await b.GetAsync("/tokens");
        Assert.Equal(HttpStatusCode.Redirect, later.StatusCode);
        Assert.Equal("/tokens", f.AppIdentity.LastChallenge!.RedirectUri);

        // a marker that is not a number counts as recent
        var odd = f.Browser(AppHost);
        odd.SetCookie(AppSite.StartCookieName, "not-a-time");
        var r = await odd.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Null(r.Headers.Location);
    }

    [Fact]
    public async Task The_marker_survives_a_successful_sign_in_so_a_lost_session_cannot_loop()
    {
        await using var f = await StartAsync();
        var b = await SignedInAsync(f, Customer(roles: CustomerSignIn.DeveloperRole));
        Assert.True(b.HasCookie(AppSite.StartCookieName));
        Assert.Equal(200, (await b.GetTextAsync("/home")).Status);
        // the session cookie disappears (a browser that refuses it, say): the next visit shows the card, not another trip to Microsoft
        var lost = f.Browser(AppHost);
        lost.SetCookie(AppSite.StartCookieName, b.Cookie(AppSite.StartCookieName)!);
        var r = await lost.GetAsync("/home");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Null(r.Headers.Location);
        Assert.Contains(SignedOutText, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_signed_out_page_never_redirects_and_sign_out_lands_there()
    {
        await using var f = await StartAsync();
        var anon = await f.Browser(AppHost).GetAsync("/signed-out");
        Assert.Equal(HttpStatusCode.OK, anon.StatusCode);
        Assert.Null(anon.Headers.Location);

        var b = await SignedInAsync(f, Customer(roles: CustomerSignIn.DeveloperRole));
        f.AppIdentity.LastChallenge = null;
        var withSession = await b.GetAsync("/signed-out");   // even with a session, no redirect anywhere
        Assert.Equal(HttpStatusCode.OK, withSession.StatusCode);
        Assert.Null(withSession.Headers.Location);
        var body = await withSession.Content.ReadAsStringAsync();
        Assert.Contains("<h1>Signed out</h1><p>" + SignedOutText + "</p>", body);
        Assert.Contains(SignInAgain, body);
        Assert.DoesNotContain("http-equiv", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SignedInSite.ContentSecurityPolicy, Header(withSession, "Content-Security-Policy"));
        Assert.Null(f.AppIdentity.LastChallenge);

        var token = await b.TokenFromAsync("/home");
        var r = await b.SendAsync(HttpMethod.Post, "/signout", new[] { new KeyValuePair<string, string>(SignedInSite.AntiforgeryField, token) });
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal("/signed-out", r.Headers.Location!.OriginalString);
        // both cookies are cleared: the session and the start marker
        Assert.Contains("expires=Thu, 01 Jan 1970", SetCookie(b, AppSite.CookieName)!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=Thu, 01 Jan 1970", SetCookie(b, AppSite.StartCookieName)!, StringComparison.OrdinalIgnoreCase);
        Assert.False(b.HasCookie(AppSite.CookieName));
        Assert.False(b.HasCookie(AppSite.StartCookieName));
        var after = await b.GetAsync("/signed-out");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Null(after.Headers.Location);
        Assert.Null(f.AppIdentity.LastChallenge);
    }

    [Fact]
    public async Task The_cancelled_and_failed_events_render_the_card_echo_nothing_and_only_failures_after_a_code_count()
    {
        await using var f = await StartAsync();
        var o = new OpenIdConnectOptions();
        AppSite.ConfigureOidc(o, new AppSiteSettings { Host = AppHost, ClientId = AppClient, SignInAvailable = true, Operator = TestOperator.Links }, f.Clock);
        var scheme = new AuthenticationScheme(AppSite.RemoteScheme, null, typeof(OpenIdConnectHandler));
        var limiter = f.App.Services.GetRequiredService<AppSignInLimiter>();
        Microsoft.AspNetCore.Http.DefaultHttpContext Http(string error = "access_denied", bool codeReceived = false)
        {
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = f.App.Services };
            http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
            http.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?error=" + error + "&error_description=ECHO-ME%3Cscript%3E");
            http.Response.Body = new MemoryStream();
            if (codeReceived) SignedInSite.MarkCodeReceived(http);   // as the code-received event does before the redemption
            return http;
        }
        static string Body(Microsoft.AspNetCore.Http.HttpContext h) { h.Response.Body.Position = 0; return new StreamReader(h.Response.Body).ReadToEnd(); }

        var denied = Http();
        var deniedCtx = new AccessDeniedContext(denied, scheme, o) { ReturnUrl = "/ECHO-ME" };
        await o.Events.AccessDenied(deniedCtx);
        Assert.True(deniedCtx.Result?.Handled);
        Assert.Equal(403, denied.Response.StatusCode);
        var deniedBody = Body(denied);
        Assert.Contains("</h1><p>" + CancelledText + "</p>", deniedBody);
        // #118: no directory is known here, so the approval link is Microsoft's "organizations" one
        Assert.Contains($"<p>{AppHtml.E(AppSite.CancelledNeedAdminSentence)}</p><pre class=\"snippet\">{AppHtml.E(AzureSetupSiteTests.PackmoatConsentUrl("organizations"))}</pre>", deniedBody);
        Assert.Contains(SignInAgain, deniedBody);
        Assert.DoesNotContain("ECHO-ME", deniedBody);
        Assert.False(denied.Response.Headers.ContainsKey("Location"));

        for (var i = 0; i < 4; i++)
        {
            var failed = Http("server_error", codeReceived: true);   // not a decline (#143: a decline reaching this event shows the cancelled card)
            var failCtx = new RemoteFailureContext(failed, scheme, o, new InvalidOperationException("ECHO-ME <script>alert(1)</script>"));
            await o.Events.RemoteFailure(failCtx);
            Assert.True(failCtx.Result?.Handled);
            Assert.Equal(400, failed.Response.StatusCode);
            var failedBody = Body(failed);
            Assert.Contains("</h1><p>" + FailedText + "</p>", failedBody);
            Assert.Contains(SignInAgain, failedBody);
            Assert.DoesNotContain("ECHO-ME", failedBody);
            Assert.DoesNotContain("<script", failedBody);
            Assert.False(failed.Response.Headers.ContainsKey("Location"));
        }
        // one cancelled and four failed after a code: the decline does not count (#145), so the limiter waits for a fifth failure
        Assert.Null(limiter.RetryAfterSeconds(Http()));
        await o.Events.RemoteFailure(new RemoteFailureContext(Http("server_error", codeReceived: true), scheme, o, new InvalidOperationException("x")));
        Assert.NotNull(limiter.RetryAfterSeconds(Http()));
    }

    [Fact]
    public async Task A_cancelled_sign_in_never_counts_as_a_failure()
    {
        await using var f = await StartAsync();
        var o = new OpenIdConnectOptions();
        AppSite.ConfigureOidc(o, new AppSiteSettings { Host = AppHost, ClientId = AppClient, SignInAvailable = true, Operator = TestOperator.Links }, f.Clock);
        var scheme = new AuthenticationScheme(AppSite.RemoteScheme, null, typeof(OpenIdConnectHandler));
        var limiter = f.App.Services.GetRequiredService<AppSignInLimiter>();
        Microsoft.AspNetCore.Http.DefaultHttpContext Http(bool codeReceived = false)
        {
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = f.App.Services };
            http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
            http.Response.Body = new MemoryStream();
            if (codeReceived) SignedInSite.MarkCodeReceived(http);
            return http;
        }
        for (var i = 0; i < 4; i++) await o.Events.RemoteFailure(new RemoteFailureContext(Http(codeReceived: true), scheme, o, new InvalidOperationException("x")));
        Assert.Null(limiter.RetryAfterSeconds(Http()));
        for (var i = 0; i < 5; i++) await o.Events.AccessDenied(new AccessDeniedContext(Http(), scheme, o));
        Assert.Null(limiter.RetryAfterSeconds(Http()));   // #145: a decline is not a guess
        for (var i = 0; i < 5; i++) await o.Events.RemoteFailure(new RemoteFailureContext(Http(), scheme, o, new InvalidOperationException("x")));
        Assert.Null(limiter.RetryAfterSeconds(Http()));   // nor is a failure before Microsoft issued a code
        await o.Events.RemoteFailure(new RemoteFailureContext(Http(codeReceived: true), scheme, o, new InvalidOperationException("x")));
        Assert.NotNull(limiter.RetryAfterSeconds(Http()));
    }

    [Fact]
    public async Task A_session_whose_tenant_is_gone_gets_the_card_not_a_redirect_loop()
    {
        await using var f = await StartAsync(services: s => s.AddSingleton<ISignInDirectoryStore>(new WrongBindingStore(new DirectoryBinding(DirA, "ghost", DateTimeOffset.UnixEpoch, "op"))));
        var b = await SignedInAsync(f, Customer(roles: CustomerSignIn.DeveloperRole));
        var r = await b.GetAsync("/home");
        Assert.Null(r.Headers.Location);
        Assert.NotEqual(HttpStatusCode.Redirect, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains(AppHtml.E("Your Packmoat account could not be found."), body);
        Assert.Contains(SignInAgain, body);
        Assert.False(b.HasCookie(AppSite.CookieName));   // the session is ended
        Assert.Equal(SignedInSite.ContentSecurityPolicy, Header(r, "Content-Security-Policy"));
    }

    [Fact]
    public async Task The_old_sign_in_page_is_gone_from_every_response()
    {
        await using var f = await StartAsync();
        var responses = new List<HttpResponseMessage>
        {
            await f.Browser(AppHost).GetAsync("/"),
            await f.Browser(AppHost).GetAsync("/signin"),
            await f.Browser(AppHost).GetAsync("/signed-out"),
            await f.Browser(AppHost).GetAsync("/app.css"),
        };
        var b = f.Browser(AppHost);
        await b.GetAsync("/");
        responses.Add(await b.GetAsync("/"));   // the loop breaker's card
        var dev = await SignedInAsync(f, Customer(roles: CustomerSignIn.DeveloperRole));
        responses.Add(await dev.GetAsync("/home"));
        responses.Add(await dev.GetAsync("/tokens"));
        responses.Add(await dev.GetAsync("/nothing-here"));
        responses.Add((await SignInToAppAsync(f, Customer(tid: DirX, roles: CustomerSignIn.AdminRole))).Callback);
        responses.Add((await SignInToAppAsync(f, Customer())).Callback);
        foreach (var r in responses)
        {
            var body = await r.Content.ReadAsStringAsync();
            foreach (var gone in new[] { "Sign in with Microsoft", "Sign in to Packmoat", "Once you're signed in", "Once you&#x27;re signed in", "Not a customer yet",
                "signin-form", "ms-signin", "ms-logo", "class=\"preview\"", "class=\"ticks\"", "Packmoat never sees your password", "Back to the start page", "start page" })
                Assert.DoesNotContain(gone, body);
            Assert.NotEqual(SignedInSite.LandingContentSecurityPolicy, Header(r, "Content-Security-Policy"));
        }
    }

    // ---- security review follow-ups (#88)

    private static void SameOrigin(HttpRequestMessage r) => r.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
    private static Action<HttpRequestMessage> Fetch(string? mode, string accept) => r =>
    {
        r.Headers.Remove("Accept");
        r.Headers.TryAddWithoutValidation("Accept", accept);
        if (mode is not null) r.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", mode);
    };

    [Theory]   // mutation: an absent Sec-Fetch-Site counted as from another site
    [InlineData(null, true)]   // an older browser that sends no Sec-Fetch-Site (Safari before 16.4, Firefox before 90)
    [InlineData("cross-site", false)]
    [InlineData("same-site", false)]
    [InlineData("same-origin", true)]
    [InlineData("none", true)]
    public async Task Only_a_navigation_another_site_made_is_held_to_the_loop_guard_on_sign_in_again(string? fetchSite, bool challenged)
    {
        await using var f = await StartAsync();
        var b = f.Browser(AppHost);
        Assert.Equal(HttpStatusCode.Redirect, (await b.GetAsync("/")).StatusCode);   // sets the marker
        f.AppIdentity.LastChallenge = null;
        var r = await b.SendAsync(HttpMethod.Get, "/signin", tweak: req => { if (fetchSite is not null) req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite); });
        if (challenged)
        {
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal("select_account", Assert.IsType<OpenIdConnectChallengeProperties>(f.AppIdentity.LastChallenge).Prompt);
        }
        else
        {
            // another site navigating to /signin again and again gets the card, not one more set of sign-in cookies each time
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Null(r.Headers.Location);
            Assert.Contains(SignedOutText, await r.Content.ReadAsStringAsync());
            Assert.Null(f.AppIdentity.LastChallenge);
        }
    }

    [Fact]
    public async Task Without_sec_fetch_site_sign_in_again_still_stops_at_the_pending_sign_in_cap()
    {
        await using var f = await StartAsync();
        var b = f.Browser(AppHost);
        for (var i = 0; i < AppSite.MaxPendingSignIns + 1; i++) b.SetCookie(".AspNetCore.Correlation.x" + i, "N");
        var r = await b.SendAsync(HttpMethod.Get, "/signin");   // no Sec-Fetch-Site: the loop guard is skipped, the cap is not
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Null(r.Headers.Location);
        Assert.Contains(SignedOutText, await r.Content.ReadAsStringAsync());
        Assert.Null(f.AppIdentity.LastChallenge);
        Assert.False(b.HasCookie(AppSite.StartCookieName));
    }

    [Theory]   // mutations: a signed-out POST with a valid token starts an automatic sign-in; or gets 404 instead of 401
    [InlineData("/signin")]
    [InlineData("/tokens")]
    public async Task A_signed_out_post_with_a_valid_antiforgery_token_gets_401_and_no_challenge(string path)
    {
        await using var f = await StartAsync();
        // A token pair minted for nobody (an anonymous request), so the antiforgery check passes and the request reaches the route.
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = f.App.Services };
        http.Request.Scheme = "https";
        var tokens = f.App.Services.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>().GetAndStoreTokens(http);
        var b = f.Browser(AppHost);
        b.SetCookie(SignedInSite.AntiforgeryCookieName, tokens.CookieToken!);
        f.AppIdentity.LastChallenge = null;
        var r = await b.SendAsync(HttpMethod.Post, path, new[] { new KeyValuePair<string, string>(SignedInSite.AntiforgeryField, tokens.RequestToken!) });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Null(r.Headers.Location);
        Assert.Null(f.AppIdentity.LastChallenge);
        Assert.False(b.HasCookie(AppSite.StartCookieName));
    }

    [Theory]
    [InlineData("/signin", 5, true)]
    [InlineData("/signin", 6, false)]
    [InlineData("/", 5, true)]
    [InlineData("/", 6, false)]
    [InlineData("/tokens", 40, false)]
    public async Task Too_many_unfinished_sign_ins_in_one_browser_stop_adding_cookies(string path, int pending, bool challenged)
    {
        await using var f = await StartAsync();
        var b = f.Browser(AppHost);
        for (var i = 0; i < pending; i++) b.SetCookie(".AspNetCore.Correlation.x" + i, "N");
        b.SetCookie(".AspNetCore.OpenIdConnect.Nonce.unrelated", "N");   // only correlation cookies count
        var r = await b.SendAsync(HttpMethod.Get, path, tweak: SameOrigin);
        if (challenged) Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        else
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Null(r.Headers.Location);
            Assert.Contains(SignedOutText, await r.Content.ReadAsStringAsync());
            Assert.Null(f.AppIdentity.LastChallenge);
            Assert.False(b.HasCookie(AppSite.StartCookieName));
        }
    }

    [Theory]
    [InlineData("/favicon.ico", "no-cors", "image/avif,image/webp,*/*")]          // Safari, from /signed-out
    [InlineData("/apple-touch-icon.png", null, "image/png,*/*")]                  // iOS, which sends no Sec-Fetch-Mode
    [InlineData("/apple-touch-icon-precomposed.png", null, "*/*")]
    [InlineData("/tokens", "cors", "text/html")]                                  // a script's fetch, even one that asks for HTML
    [InlineData("/", "no-cors", "text/html")]
    [InlineData("/signin", "no-cors", "image/png")]
    [InlineData("/signin", null, "*/*")]
    public async Task A_fetch_that_is_not_a_page_load_starts_no_sign_in(string path, string? mode, string accept)
    {
        await using var f = await StartAsync();
        var b = f.Browser(AppHost);
        var r = await b.SendAsync(HttpMethod.Get, path, tweak: Fetch(mode, accept));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Null(r.Headers.Location);
        Assert.Null(f.AppIdentity.LastChallenge);
        Assert.False(b.HasCookie(AppSite.StartCookieName));
        Assert.Empty(b.SetCookieLog);
        Assert.Equal(SignedInSite.ContentSecurityPolicy, Header(r, "Content-Security-Policy"));
        // and a page load right after still goes to Microsoft: the fetch set no marker
        Assert.Equal(HttpStatusCode.Redirect, (await b.GetAsync("/")).StatusCode);
    }

    [Theory]
    [InlineData("navigate", "*/*")]
    [InlineData("navigate", "image/png")]                // the mode decides when it is sent
    [InlineData(null, "text/html,application/xhtml+xml")] // an older browser: Accept decides
    public async Task A_page_load_starts_the_sign_in(string? mode, string accept)
    {
        await using var f = await StartAsync();
        var r = await f.Browser(AppHost).SendAsync(HttpMethod.Get, "/tokens", tweak: Fetch(mode, accept));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/tokens", f.AppIdentity.LastChallenge!.RedirectUri);
    }

    [Fact]
    public async Task The_start_marker_cookie_name_and_attributes_are_pinned()
    {
        Assert.Equal("__Host-packmoat-app-start", AppSite.StartCookieName);
        await using var f = await StartAsync();
        var b = f.Browser(AppHost);
        await b.GetAsync("/");
        var line = Assert.Single(b.SetCookieLog, c => c.StartsWith("__Host-packmoat-app-start=", StringComparison.Ordinal));
        var parts = line.Split(';').Select(p => p.Trim().ToLowerInvariant()).ToArray();
        Assert.Equal("__host-packmoat-app-start=" + f.Clock.UtcNow.ToUnixTimeSeconds(), parts[0]);
        foreach (var want in new[] { "secure", "path=/", "samesite=lax", "httponly", "max-age=120" }) Assert.Contains(want, parts);
        Assert.DoesNotContain(parts, p => p.StartsWith("domain", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_admin_sign_in_keeps_the_admin_error_box_not_the_customer_card()
    {
        await using var f = await StartAsync();
        f.SignInAs("44444444-4444-4444-8444-444444444444", "45444444-4444-4444-8444-444444444444");
        var b = f.Browser();
        var r = await b.SignInAsync();
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("<div class=\"box\"><h1>", body);
        Assert.DoesNotContain("Sign in again", body);
        Assert.DoesNotContain("href=\"/signin\"", body);
        Assert.DoesNotContain("class=\"card notice\"", body);
        Assert.DoesNotContain(TestOperator.EarlyAccessUrl, body);
        Assert.False(b.HasCookie(AdminSite.CookieName));
    }

    [Fact]
    public void The_sign_in_card_encodes_its_title_sentence_and_detail()
    {
        var html = AppHtml.SignInCard("<x>title&\"", "<x>sentence'", "<x>detail</p><script>");
        Assert.DoesNotContain("<x>", html);
        Assert.DoesNotContain("<script", html);
        Assert.Contains("<h1>&lt;x&gt;title&amp;&quot;</h1>", html);
        Assert.Contains("<p>&lt;x&gt;sentence&#x27;</p>", html);
        Assert.Contains("<p class=\"fine\">&lt;x&gt;detail&lt;/p&gt;&lt;script&gt;</p>", html);
        Assert.EndsWith("<div class=\"actions\">" + AppHtml.SignInAgainLink + "</div></section>", html);
    }

    [Fact]
    public async Task The_failure_events_never_log_the_exception_or_protocol_values()
    {
        var logs = new CapturingLoggerProvider();
        await using var f = await StartAsync(services: s => s.AddSingleton<ILoggerProvider>(logs));
        var o = new OpenIdConnectOptions();
        AppSite.ConfigureOidc(o, new AppSiteSettings { Host = AppHost, ClientId = AppClient, SignInAvailable = true, Operator = TestOperator.Links }, f.Clock);
        var scheme = new AuthenticationScheme(AppSite.RemoteScheme, null, typeof(OpenIdConnectHandler));
        Microsoft.AspNetCore.Http.DefaultHttpContext Http()
        {
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = f.App.Services };
            http.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?error=SECRET-ERROR&error_description=SECRET-DESC&state=SECRET-STATE&code=SECRET-CODE");
            http.Response.Body = new MemoryStream();
            return http;
        }
        var props = new AuthenticationProperties { RedirectUri = "/SECRET-RETURN" };
        props.Items["x"] = "SECRET-ITEM";
        await o.Events.RemoteFailure(new RemoteFailureContext(Http(), scheme, o, new InvalidOperationException("SECRET-MESSAGE " + DirA, new Exception("SECRET-INNER"))) { Properties = props });
        await o.Events.AccessDenied(new AccessDeniedContext(Http(), scheme, o) { Properties = props, ReturnUrl = "/SECRET-RETURN", AccessDeniedPath = "/SECRET-PATH" });
        string all;
        lock (logs.Lines) all = string.Join("\n", logs.Lines);
        Assert.Contains("Customer sign-in failed (InvalidOperationException, error other, token none)", all);   // an unknown error code is "other" (#143)
        Assert.Contains("Customer sign-in cancelled or not approved", all);
        foreach (var leak in new[] { "SECRET", DirA }) Assert.DoesNotContain(leak, all);
    }

    [Fact]
    public async Task The_real_handler_event_keeps_issuer_and_audience_when_the_security_token_is_not_set()
    {
        // The first live Microsoft sign-in was refused with IssuerMismatch: the handler's default ClaimActions deleted iss/aud after
        // OnTokenValidated. This drives the real event and then the claim actions, as the handler does.
        var o = new OpenIdConnectOptions();
        AppSite.ConfigureOidc(o, new AppSiteSettings { Host = AppHost, ClientId = AppClient, SignInAvailable = true }, new Packmoat.Domain.SystemClock());
        var scheme = new AuthenticationScheme(AppSite.RemoteScheme, null, typeof(OpenIdConnectHandler));
        var person = Customer(roles: CustomerSignIn.DeveloperRole);
        var ctx = new TokenValidatedContext(new Microsoft.AspNetCore.Http.DefaultHttpContext(), scheme, o, person, new AuthenticationProperties());
        Assert.Null(ctx.SecurityToken);

        await o.Events.TokenValidated(ctx);
        // Then what the real handler does next (GetClaimsFromUserInfoEndpoint=false): run ClaimActions on the event's principal.
        using (var empty = System.Text.Json.JsonDocument.Parse("{}"))
            foreach (var action in o.ClaimActions)
                foreach (var identity in ctx.Principal!.Identities) action.Run(empty.RootElement, identity, "https://login.microsoftonline.com/");

        Assert.Null(ctx.Result);
        Assert.Equal(new[] { $"https://login.microsoftonline.com/{DirA}/v2.0" }, ctx.Principal!.FindAll("iss").Select(c => c.Value).ToArray());
        Assert.Equal(new[] { AppClient }, ctx.Principal.FindAll("aud").Select(c => c.Value).ToArray());
        Assert.Empty(ctx.Principal.FindAll("email"));
        var decision = CustomerSignIn.Decide(AppSite.ToVerifiedClaims(ctx.Principal), AppClient, new DirectoryBinding(DirA, "acme", DateTimeOffset.UtcNow, "test"));
        Assert.IsType<SignInDecision.Allowed>(decision);
    }

    [Fact]
    public async Task The_one_time_sign_in_code_never_reaches_the_logs()
    {
        var logs = new CapturingLoggerProvider();
        await using var f = await StartAsync(services: s => s.AddSingleton<ILoggerProvider>(logs));
        var b = f.Browser(AppHost);
        await b.GetAsync("/signin-microsoft?code=SECRETCODE123&state=SECRETSTATE456");
        var admin = f.Browser(AdminSiteFixture.AdminHost);
        await admin.GetAsync("/signin-oidc?code=SECRETCODE789&state=SECRETSTATE012");
        string all;
        lock (logs.Lines) all = string.Join("\n", logs.Lines);
        foreach (var leak in new[] { "SECRETCODE123", "SECRETSTATE456", "SECRETCODE789", "SECRETSTATE012" }) Assert.DoesNotContain(leak, all);

        // The host ships no appsettings file, so tests and production log alike; a file could still be added one day. So also pin the
        // code-level rule that holds whatever the configuration says.
        var rules = f.App.Services.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;
        Assert.Contains(rules, r => r.CategoryName == "Microsoft.AspNetCore.Hosting.Diagnostics" && r.ProviderName is null && r.LogLevel == LogLevel.Warning);
    }
}
