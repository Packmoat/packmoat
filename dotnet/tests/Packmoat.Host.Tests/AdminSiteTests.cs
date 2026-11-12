using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Admin;
using static Packmoat.Host.Tests.AdminSiteFixture;

namespace Packmoat.Host.Tests;

[Collection("sqlite-pools")]
public class AdminSiteTests
{
    private static readonly string[] AllPages = { "/", "/tenants", "/tenants/acme", "/tenants/globex", "/audit", "/audit?tenant=acme", "/tenants/nope", "/nothing-here" };

    private static async Task<AdminBrowser> SignedIn(AdminSiteFixture f)
    {
        var b = f.Browser();
        var done = await b.SignInAsync();
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        Assert.True(b.HasCookie(AdminSite.CookieName));
        return b;
    }

    // ---- who gets in

    [Fact]
    public async Task The_allowlisted_person_signs_in_and_sees_the_pages()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);

        var (status, body, _) = await b.GetTextAsync("/tenants");
        Assert.Equal(200, status);
        Assert.Contains("acme", body);
        Assert.Contains("globex", body);

        Assert.Equal(200, (await b.GetTextAsync("/tenants/acme")).Status);
        Assert.Equal(200, (await b.GetTextAsync("/audit")).Status);
        Assert.Equal(404, (await b.GetTextAsync("/tenants/nope")).Status);
        Assert.Equal(404, (await b.GetTextAsync("/nothing-here")).Status);
        // already signed in: the landing page goes straight to the tenants
        Assert.Equal(HttpStatusCode.Redirect, (await b.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Without_signing_in_every_page_sends_you_to_the_landing_page()
    {
        await using var f = await StartAsync();
        var b = f.Browser();
        foreach (var p in new[] { "/tenants", "/tenants/acme", "/audit", "/anything" })
        {
            var r = await b.GetAsync(p);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal("/", r.Headers.Location!.OriginalString);
        }
    }

    public static IEnumerable<object?[]> RefusedPeople() => new[]
    {
        new object?[] { "wrong oid", Tid, "99999999-9999-4999-8999-999999999999", null },
        new object?[] { "wrong tid", "99999999-9999-4999-8999-999999999999", Oid, null },
        new object?[] { "missing tid", null, Oid, null },
        new object?[] { "missing oid", Tid, null, null },
        new object?[] { "missing both", null, null, null },
        new object?[] { "email only", null, null, "olivia@contoso.example" },
        new object?[] { "right pair but also an email", Tid + "x", Oid, "olivia@contoso.example" },
        new object?[] { "not guids", "not-a-guid", "also-not", null },
        new object?[] { "oid swapped with tid", Oid, Tid, null },
    };

    [Theory]
    [MemberData(nameof(RefusedPeople))]
    public async Task A_person_who_is_not_on_the_list_gets_403_and_no_session_cookie(string why, string? tid, string? oid, string? email)
    {
        await using var f = await StartAsync();
        f.SignInAs(tid, oid, email);
        var b = f.Browser();

        var res = await b.SignInAsync();

        Assert.True(res.StatusCode == HttpStatusCode.Forbidden, why + ": " + res.StatusCode);
        Assert.False(b.HasCookie(AdminSite.CookieName), why);
        Assert.True(!b.SetCookieLog.Any(c => c.StartsWith(AdminSite.CookieName + "=")), string.Join(" | ", b.SetCookieLog));
        Assert.Equal(HttpStatusCode.Redirect, (await b.GetAsync("/tenants")).StatusCode);   // still signed out
    }

    [Fact]
    public async Task An_email_that_matches_the_founder_is_not_enough_even_with_the_right_pair_elsewhere_in_the_list()
    {
        await using var f = await StartAsync();
        f.Identity.Next = FakeIdentityProvider.Person(Tid, "44444444-4444-4444-8444-444444444444", email: "olivia@contoso.example");
        var b = f.Browser();
        Assert.Equal(HttpStatusCode.Forbidden, (await b.SignInAsync()).StatusCode);
        Assert.False(b.HasCookie(AdminSite.CookieName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-pair,also:bad")]
    public async Task An_empty_or_unusable_allowlist_lets_nobody_in(string? allow)
    {
        await using var f = await StartAsync(o => o.AdminAllow = allow);
        var b = f.Browser();

        var res = await b.SignInAsync();

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.False(b.HasCookie(AdminSite.CookieName));
    }

    [Fact]
    public async Task The_pair_match_ignores_letter_case_and_braces_free_formats_of_the_same_guids()
    {
        await using var f = await StartAsync(o => o.AdminAllow = "x:y; " + Tid.ToUpperInvariant() + ":" + Oid.ToUpperInvariant());
        var b = f.Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await b.SignInAsync()).StatusCode);
        Assert.True(b.HasCookie(AdminSite.CookieName));
    }

    [Fact]
    public async Task A_cookie_for_someone_removed_from_the_allowlist_stops_working()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        Assert.Equal(200, (await b.GetTextAsync("/tenants")).Status);

        // the same keys, a new process, a shorter list
        var cookie = b.Cookie(AdminSite.CookieName)!;
        await using var g = await StartAsync(o => o.AdminAllow = Tid + ":99999999-9999-4999-8999-999999999999", keysDir: f.KeysDir);
        var other = g.Browser();
        other.SetCookie(AdminSite.CookieName, cookie);
        Assert.Equal(HttpStatusCode.Redirect, (await other.GetAsync("/tenants")).StatusCode);
    }

    // ---- disabled, and the two doors never mix

    [Fact]
    public async Task With_no_admin_host_configured_the_admin_site_does_not_exist()
    {
        await using var f = await StartAsync(o => o.AdminHost = null);
        var b = f.Browser(AdminHost);
        foreach (var p in new[] { "/", "/tenants", "/audit", "/signin-oidc", "/admin.css" })
        {
            var r = await b.GetAsync(p);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);       // the proxy's answer: it wants a token
            Assert.False(r.Headers.Contains("Content-Security-Policy"));
        }
        var post = await b.SendAsync(HttpMethod.Post, "/signin", new[] { new KeyValuePair<string, string>("a", "b") });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    [Fact]
    public async Task An_empty_admin_host_also_disables_it()
    {
        await using var f = await StartAsync(o => o.AdminHost = "  ");
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Browser().GetAsync("/tenants")).StatusCode);
    }

    [Fact]
    public async Task The_proxy_host_ignores_a_valid_admin_cookie()
    {
        await using var f = await StartAsync();
        var admin = await SignedIn(f);
        var cookie = admin.Cookie(AdminSite.CookieName)!;
        var onProxy = f.Browser("127.0.0.1");
        onProxy.SetCookie(AdminSite.CookieName, cookie);

        foreach (var p in new[] { "/v3/index.json", "/tenants", "/audit", "/", "/tenants/acme" })
        {
            var r = await onProxy.GetAsync(p);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.False(r.Headers.Contains("Content-Security-Policy"));
        }
    }

    [Fact]
    public async Task The_admin_host_ignores_a_valid_packmoat_token()
    {
        await using var f = await StartAsync();
        var token = await f.Tokens.IssueAsync("acme", "ci", default);
        var b = f.Browser();
        var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x:" + token.Plaintext));

        foreach (var header in new[] { new AuthenticationHeaderValue("Basic", basic), new AuthenticationHeaderValue("Bearer", token.Plaintext) })
            foreach (var p in new[] { "/tenants", "/tenants/acme", "/audit", "/v3/index.json" })
            {
                var r = await b.SendAsync(HttpMethod.Get, p, tweak: req => req.Headers.Authorization = header);
                Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
                Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
                Assert.Equal("/", r.Headers.Location!.OriginalString);
            }
        // and the token is also no way to sign in: a POST without the antiforgery token is refused
        var post = await b.SendAsync(HttpMethod.Post, "/signout", tweak: req => req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Plaintext));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task The_proxy_still_works_for_its_own_host_with_the_admin_site_on()
    {
        await using var f = await StartAsync();
        var token = await f.Tokens.IssueAsync("acme", "ci", default);
        var proxy = f.Browser("127.0.0.1");
        var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x:" + token.Plaintext));

        var ok = await proxy.SendAsync(HttpMethod.Get, "/v3/index.json", tweak: r => r.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.False(ok.Headers.Contains("Content-Security-Policy"));
        Assert.Equal(HttpStatusCode.OK, (await proxy.GetAsync("/healthz")).StatusCode);
        // the proxy's own gates are unchanged: GET only, 405 with Allow: GET
        var post = await proxy.SendAsync(HttpMethod.Post, "/v3/index.json", new[] { new KeyValuePair<string, string>("a", "b") });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        // the proxy has no admin pages and no login: a path that exists only on the admin site is a plain 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await proxy.GetAsync("/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await proxy.GetAsync("/signin-oidc")).StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("feeds.packmoat.test")]
    [InlineData("admin.packmoat.test.evil.example")]
    [InlineData("xadmin.packmoat.test")]
    [InlineData("evil.example")]
    [InlineData("packmoat.test")]
    public async Task A_request_that_does_not_name_the_admin_host_never_reaches_admin_pages(string host)
    {
        await using var f = await StartAsync();
        var admin = await SignedIn(f);
        var b = f.Browser(host);
        b.SetCookie(AdminSite.CookieName, admin.Cookie(AdminSite.CookieName)!);

        foreach (var p in new[] { "/tenants", "/audit", "/tenants/acme", "/" })
        {
            var r = await b.GetAsync(p);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.DoesNotContain("acme", await r.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task The_admin_host_name_matches_whatever_the_case_or_port()
    {
        await using var f = await StartAsync();
        var b = f.Browser(AdminHost.ToUpperInvariant() + ":443");
        Assert.Equal(200, (await b.GetTextAsync("/")).Status);
    }

    [Fact]
    public async Task The_admin_host_cannot_also_be_a_proxy_host()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(o => o.AllowedHosts = "feeds.packmoat.test " + AdminHost));
        Assert.Contains("PACKMOAT_HOSTS", ex.Message);
    }

    [Theory]
    [InlineData("https://admin.example.com")]
    [InlineData("admin.example.com/path")]
    [InlineData("*.example.com")]
    [InlineData("admin.example.com:8443")]
    public async Task A_malformed_admin_host_refuses_to_start(string host)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(o => o.AdminHost = host));
    }

    // ---- headers

    [Fact]
    public async Task Every_admin_response_carries_the_security_headers()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var anonymous = f.Browser();
        var responses = new List<(string, HttpResponseMessage)>();
        foreach (var p in AllPages) responses.Add((p, await b.GetAsync(p)));
        responses.Add(("anon /tenants", await anonymous.GetAsync("/tenants")));          // redirect
        responses.Add(("anon /", await anonymous.GetAsync("/")));                         // landing
        responses.Add(("bad method", await anonymous.SendAsync(HttpMethod.Put, "/tenants")));
        responses.Add(("post no token", await anonymous.SendAsync(HttpMethod.Post, "/signout")));
        responses.Add(("css", await anonymous.GetAsync("/admin.css")));

        foreach (var (name, r) in responses)
        {
            var h = r.Headers.Concat(r.Content.Headers).ToDictionary(x => x.Key, x => string.Join(",", x.Value), StringComparer.OrdinalIgnoreCase);
            Assert.True(h.ContainsKey("Content-Security-Policy"), name);
            var csp = h["Content-Security-Policy"];
            foreach (var d in new[] { "default-src 'none'", "style-src 'self'", "img-src 'self'", "base-uri 'none'", "frame-ancestors 'none'" }) Assert.Contains(d, csp);
            Assert.Contains("form-action 'self'", csp);
            Assert.DoesNotContain("unsafe", csp);
            Assert.DoesNotContain("script-src", csp);      // default-src 'none' already forbids every script
            Assert.Equal("nosniff", h["X-Content-Type-Options"]);
            Assert.Equal("same-origin", h["Referrer-Policy"]);
            Assert.Equal("no-store", h["Cache-Control"]);
            Assert.StartsWith("max-age=", h["Strict-Transport-Security"]);
            Assert.Equal("DENY", h["X-Frame-Options"]);
            Assert.False(h.ContainsKey("Server"), name);
        }
        // only the landing page (the one with the sign-in form) may post to Microsoft; no other page widens form-action
        foreach (var (name, r) in responses.Where(x => x.Item1 != "anon /"))
            Assert.DoesNotContain("microsoftonline", string.Join(",", r.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task Pages_have_no_scripts_no_inline_styles_and_no_third_party_requests()
    {
        await using var f = await StartAsync();
        await SeedAsync(f);
        var b = await SignedIn(f);
        foreach (var p in AllPages)
        {
            var html = await (await b.GetAsync(p)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(" style=", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(" onclick", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http://", html);
            Assert.DoesNotContain("https://", html);
            Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- cookie

    [Fact]
    public async Task The_session_cookie_has_the_required_attributes()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var set = b.SetCookieLog.Single(c => c.StartsWith(AdminSite.CookieName + "="));
        Assert.StartsWith("__Host-", set);
        Assert.Contains("secure", set, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", set, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", set, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", set, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", set, StringComparison.OrdinalIgnoreCase);
        // the antiforgery cookie is just as strict
        var af = b.SetCookieLog.First(c => c.StartsWith(AdminSite.AntiforgeryCookieName + "="));
        Assert.Contains("secure", af, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", af, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", af, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_session_slides_for_thirty_minutes_and_ends_after_eight_hours_whatever_you_do()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);

        // idle for 31 minutes: gone
        var idle = f.Browser();
        idle.SetCookie(AdminSite.CookieName, b.Cookie(AdminSite.CookieName)!);
        f.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.Redirect, (await idle.GetAsync("/tenants")).StatusCode);

        // busy: a request every 20 minutes keeps it alive...
        var c = f.Browser();
        f.Clock.Advance(TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.Redirect, (await c.SignInAsync()).StatusCode);
        for (var i = 1; i <= 23; i++)
        {
            f.Clock.Advance(TimeSpan.FromMinutes(20));
            Assert.True(200 == (await c.GetTextAsync("/tenants")).Status, "still signed in after " + i * 20 + " minutes");
        }
        // ...until the eight hour cap
        f.Clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/tenants")).StatusCode);
    }

    [Fact]
    public async Task Sessions_survive_a_restart_because_the_keys_are_on_disk_with_restricted_access()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var cookie = b.Cookie(AdminSite.CookieName)!;
        Assert.NotEmpty(Directory.GetFiles(f.KeysDir, "*.xml"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(f.KeysDir));

        await using var g = await StartAsync(keysDir: f.KeysDir);
        var again = g.Browser();
        again.SetCookie(AdminSite.CookieName, cookie);
        Assert.Equal(200, (await again.GetTextAsync("/tenants")).Status);
    }

    [Fact]
    public async Task A_cookie_made_with_other_keys_is_refused()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        await using var g = await StartAsync(keysDir: Path.Combine(f.Dir, "other-keys"));
        var again = g.Browser();
        again.SetCookie(AdminSite.CookieName, b.Cookie(AdminSite.CookieName)!);
        Assert.Equal(HttpStatusCode.Redirect, (await again.GetAsync("/tenants")).StatusCode);
    }

    // ---- POST safety

    [Fact]
    public async Task Sign_out_needs_the_antiforgery_token_and_a_get_does_nothing()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);

        var noToken = await b.SendAsync(HttpMethod.Post, "/signout");
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        var wrong = await b.SendAsync(HttpMethod.Post, "/signout", new[] { new KeyValuePair<string, string>(AdminSite.AntiforgeryField, "forged") });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var getOut = await b.GetAsync("/signout");
        Assert.Equal(HttpStatusCode.NotFound, getOut.StatusCode);
        Assert.Equal(200, (await b.GetTextAsync("/tenants")).Status);            // still signed in after all three

        // a token from a different browser session does not work either
        var other = await SignedIn(f);
        var foreign = await other.TokenFromAsync("/tenants");
        var crossSession = await b.SendAsync(HttpMethod.Post, "/signout", new[] { new KeyValuePair<string, string>(AdminSite.AntiforgeryField, foreign) });
        Assert.Equal(HttpStatusCode.BadRequest, crossSession.StatusCode);

        var token = await b.TokenFromAsync("/tenants");
        var ok = await b.SendAsync(HttpMethod.Post, "/signout", new[] { new KeyValuePair<string, string>(AdminSite.AntiforgeryField, token) });
        Assert.Equal(HttpStatusCode.SeeOther, ok.StatusCode);
        Assert.False(b.HasCookie(AdminSite.CookieName));
        Assert.Equal(HttpStatusCode.Redirect, (await b.GetAsync("/tenants")).StatusCode);
    }

    [Fact]
    public async Task Signing_in_also_needs_the_antiforgery_token()
    {
        await using var f = await StartAsync();
        var b = f.Browser();
        var r = await b.SendAsync(HttpMethod.Post, "/signin");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(r.Headers.Location);
        var forged = await b.SendAsync(HttpMethod.Post, "/signin", new[] { new KeyValuePair<string, string>(AdminSite.AntiforgeryField, "forged") });
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Null(forged.Headers.Location);
    }

    [Fact]
    public async Task Only_get_and_post_exist_and_a_get_never_changes_anything()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        foreach (var m in new[] { HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch, HttpMethod.Head, HttpMethod.Options })
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await b.SendAsync(m, "/tenants")).StatusCode);
        // the read pages offer no write route at all
        foreach (var p in new[] { "/tenants", "/tenants/acme", "/audit" })
        {
            var post = await b.SendAsync(HttpMethod.Post, p, new[] { new KeyValuePair<string, string>(AdminSite.AntiforgeryField, await b.TokenFromAsync("/tenants")) });
            Assert.True(post.StatusCode is HttpStatusCode.NotFound, p + " " + post.StatusCode);
        }
    }

    [Fact]
    public async Task A_request_body_over_the_admin_limit_is_refused()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var big = new string('a', 20_000);
        var r = await b.SendAsync(HttpMethod.Post, "/signout", new[] { new KeyValuePair<string, string>(AdminSite.AntiforgeryField, big) });
        Assert.True(r.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest, r.StatusCode.ToString());
        Assert.Equal(200, (await b.GetTextAsync("/tenants")).Status);
    }

    [Fact]
    public async Task A_get_with_a_body_is_refused()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var r = await b.SendAsync(HttpMethod.Get, "/tenants", tweak: req => req.Content = new StringContent("x"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Repeated_refused_sign_ins_from_one_address_are_slowed()
    {
        await using var f = await StartAsync();
        f.SignInAs(Tid, "99999999-9999-4999-8999-999999999999");
        var b = f.Browser();
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Forbidden, (await b.SignInAsync()).StatusCode);

        var landing = await b.GetAsync("/");
        var field = AdminBrowser.Field(await landing.Content.ReadAsStringAsync())!;
        var sixth = await b.SendAsync(HttpMethod.Post, "/signin", new[] { new KeyValuePair<string, string>(AdminSite.AntiforgeryField, field) });
        Assert.Equal((HttpStatusCode)429, sixth.StatusCode);
        Assert.True(sixth.Headers.Contains("Retry-After"));
        Assert.Equal((HttpStatusCode)429, (await b.GetAsync(AdminSite.CallbackPath)).StatusCode);

        f.Clock.Advance(TimeSpan.FromMinutes(11));
        f.SignInAs(Tid, Oid);
        Assert.Equal(HttpStatusCode.Redirect, (await b.SignInAsync()).StatusCode);   // the block was short
    }

    // ---- what the pages show

    private static async Task SeedAsync(AdminSiteFixture f)
    {
        await f.Tenants.AddInternalPrefixAsync("acme", "Contoso.", default);
        await f.Tenants.SetOverrideAsync("acme", "Newtonsoft.Json", 3, default);
        await f.Tenants.AddWaiverAsync("acme", "Left.Pad", "1.2.3", f.Clock.UtcNow.AddDays(5), "hotfix for ticket 42", "olivia", default);
        await f.Tenants.SetUpstreamIdentityAsync("acme", new UpstreamIdentity("aaaaaaaa-0000-4000-8000-000000000000", "bbbbbbbb-0000-4000-8000-000000000000",
            "-----BEGIN PRIVATE KEY-----\n" + PemMarker + "\n-----END PRIVATE KEY-----"), default);
        var dev = await f.Tokens.IssueAsync("acme", "alice laptop", default);
        await f.Tokens.IssueAsync("acme", "ci pipeline", default, TokenKind.Automation);
        await f.Activity.RecordAsync(dev.TokenId, DateOnly.FromDateTime(f.Clock.UtcNow.UtcDateTime), default);
        await f.Audit.AppendAsync(new AuditEvent(f.Clock.UtcNow, "req1", "acme", dev.TokenId, AuditKind.Download, "Left.Pad", "1.2.3", Decision.Allow, VerdictReason.Waiver, null), default);
    }

    [Fact]
    public async Task The_tenant_page_shows_prefixes_overrides_waivers_tokens_and_the_active_developer_count()
    {
        await using var f = await StartAsync();
        await SeedAsync(f);
        var b = await SignedIn(f);

        var (status, html, _) = await b.GetTextAsync("/tenants/acme");

        Assert.Equal(200, status);
        Assert.Contains("Contoso.", html);
        Assert.Contains("newtonsoft.json", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("left.pad", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.2.3", html);
        Assert.Contains("hotfix for ticket 42", html);
        Assert.Contains("alice laptop", html);
        Assert.Contains("ci pipeline", html);
        Assert.Contains("Developer", html);
        Assert.Contains("Automation", html);
        Assert.Contains(f.Clock.UtcNow.UtcDateTime.ToString("yyyy-MM-dd"), html);        // last active day
        Assert.Matches(@"Active developers, last 30 days: <strong>1</strong>", html);
    }

    [Fact]   // mutation: drop the Source cell
    public async Task The_waivers_table_has_a_source_column_saying_customer_or_operator()
    {
        await using var f = await StartAsync();
        var op = await f.Tenants.AddWaiverAsync("acme", "Op.Pkg", "*", f.Clock.UtcNow.AddDays(5), "operator reason", "olivia", default);
        var mine = (await f.Tenants.AddCustomerWaiverAsync("acme", "cust.pkg", "1.0.0", f.Clock.UtcNow.AddDays(5), "customer reason", "Alice (oid)", 25, default)).Waiver!;
        var b = await SignedIn(f);

        var html = (await b.GetTextAsync("/tenants/acme")).Body;

        Assert.Contains("<th>Source</th>", html);
        Assert.Matches($"<td>{mine.Id}</td><td>cust\\.pkg</td><td>1\\.0\\.0</td><td>[^<]*</td><td>customer reason</td><td>Alice \\(oid\\)</td><td>customer</td>", html);
        Assert.Matches($"<td>{op.Id}</td><td>op\\.pkg</td><td>\\*</td><td>[^<]*</td><td>operator reason</td><td>olivia</td><td>operator</td>", html);
    }

    [Fact]
    public async Task The_tenants_list_is_the_operator_view_of_every_tenant()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var html = (await b.GetTextAsync("/tenants")).Body;
        Assert.Contains("href=\"/tenants/acme\"", html);
        Assert.Contains("href=\"/tenants/globex\"", html);
    }

    [Fact]
    public async Task The_audit_page_shows_the_last_hundred_for_one_tenant_or_all()
    {
        await using var f = await StartAsync();
        var token = await f.Tokens.IssueAsync("acme", "ci", default);
        for (var i = 0; i < 130; i++)
            await f.Audit.AppendAsync(new AuditEvent(f.Clock.UtcNow.AddSeconds(i), "r" + i, "acme", token.TokenId, AuditKind.Listing, "Pkg." + i, "1.0." + i, Decision.Hide, VerdictReason.Cooldown, null), default);
        await f.Audit.AppendAsync(new AuditEvent(f.Clock.UtcNow, "g1", "globex", "tok", AuditKind.Download, "Globex.Only", "1.0.0", Decision.Allow, VerdictReason.CooldownElapsed, null), default);
        var b = await SignedIn(f);

        var acme = (await b.GetTextAsync("/audit?tenant=acme")).Body;
        Assert.Equal(100, System.Text.RegularExpressions.Regex.Matches(acme, "<td>Pkg\\.").Count);
        Assert.Contains("Pkg.129", acme);                // newest first
        Assert.DoesNotContain("Pkg.29<", acme);
        Assert.DoesNotContain("Globex.Only", acme);
        Assert.Contains("Globex.Only", (await b.GetTextAsync("/audit")).Body);
    }

    [Fact]
    public async Task No_page_ever_contains_the_private_key_the_master_key_the_client_secret_or_a_token()
    {
        await using var f = await StartAsync();
        await SeedAsync(f);
        var issued = await f.Tokens.IssueAsync("acme", "extra", default);
        var b = await SignedIn(f);

        var secrets = new[] { PemMarker, "PRIVATE KEY", "BEGIN", f.MasterKey, ClientSecret, issued.Plaintext, "aaaaaaaa-0000-4000-8000-000000000000", "bbbbbbbb-0000-4000-8000-000000000000" };
        foreach (var p in AllPages.Concat(new[] { "/admin.css", "/signin-oidc" }))
        {
            var r = await b.GetAsync(p);
            var text = await r.Content.ReadAsStringAsync() + string.Join("\n", r.Headers.Select(h => h.Key + ": " + string.Join(",", h.Value)));
            foreach (var s in secrets) Assert.DoesNotContain(s, text);
        }
        foreach (var s in secrets) Assert.DoesNotContain(s, string.Join("\n", b.SetCookieLog));
    }

    [Fact]
    public async Task Untrusted_text_is_encoded_on_every_page()
    {
        await using var f = await StartAsync();
        const string evil = "<script>alert(1)</script>";
        const string evilAttr = "\"><img src=x onerror=alert(2)>";
        await f.Tenants.AddInternalPrefixAsync("acme", evil, default);
        await f.Tenants.SetOverrideAsync("acme", evilAttr, 1, default);
        await f.Tenants.AddWaiverAsync("acme", evil, evilAttr, f.Clock.UtcNow.AddDays(1), "<b>why</b>", "<i>me</i>", default);
        var tok = await f.Tokens.IssueAsync("acme", "<svg onload=alert(3)>", default);
        await f.Audit.AppendAsync(new AuditEvent(f.Clock.UtcNow, "r", "acme", tok.TokenId, AuditKind.Download, evil, evilAttr, Decision.Hide, VerdictReason.Cooldown, null), default);
        var b = await SignedIn(f);

        foreach (var p in new[] { "/audit", "/audit?tenant=acme", "/tenants/acme", "/audit?tenant=" + Uri.EscapeDataString(evil) })
        {
            var html = (await b.GetTextAsync(p)).Body;
            Assert.DoesNotContain("<script>alert", html);
            Assert.DoesNotContain("<img src=x", html);
            // the only svg element on the page is the chrome's own Packmoat mark (exactly one); the token label's svg stays encoded
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<svg"));
            Assert.Contains("<svg class=\"mark\" viewBox=\"0 0 32 32\"", html);
            Assert.DoesNotContain("<svg onload", html);
            Assert.DoesNotContain("<b>why", html);
            Assert.DoesNotContain("<i>me", html);
            Assert.DoesNotContain("\"><img", html);
        }
        var audit = (await b.GetTextAsync("/audit?tenant=acme")).Body;
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", audit);
        Assert.Contains("&quot;&gt;&lt;img src=x onerror=alert(2)&gt;", audit);
        var tenantHtml = (await b.GetTextAsync("/tenants/acme")).Body;
        Assert.Contains("&lt;svg onload=alert(3)&gt;", tenantHtml);
        Assert.Contains("&lt;b&gt;why&lt;/b&gt;", tenantHtml);
    }

    [Fact]
    public async Task A_hostile_tenant_id_in_the_url_is_encoded_in_the_not_found_page_and_the_title()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var r = await b.GetTextAsync("/tenants/" + Uri.EscapeDataString("<script>x</script>"));
        Assert.Equal(404, r.Status);
        Assert.DoesNotContain("<script>x", r.Body);
    }

    // ---- errors

    private sealed class DenyingAudit : IAuditQuery
    {
        public Task<IReadOnlyList<AuditEvent>> QueryAsync(string? tenantId, int limit, CancellationToken ct) => throw new AccessDeniedException("nope, secret detail xyz");
    }

    private sealed class BrokenAudit : IAuditQuery
    {
        public Task<IReadOnlyList<AuditEvent>> QueryAsync(string? tenantId, int limit, CancellationToken ct) => throw new InvalidOperationException("database exploded: C:\\secret\\path");
    }

    [Fact]
    public async Task An_access_denied_exception_becomes_403_not_401_and_says_nothing_more()
    {
        await using var f = await StartAsync(auditQuery: new DenyingAudit());
        var b = await SignedIn(f);
        var r = await b.GetTextAsync("/audit");
        Assert.Equal(403, r.Status);
        Assert.DoesNotContain("xyz", r.Body);
        Assert.Equal("no-store", string.Join(",", r.Response.Headers.GetValues("Cache-Control")));
        Assert.True(r.Response.Headers.Contains("Content-Security-Policy"));
        Assert.Equal(200, (await b.GetTextAsync("/tenants")).Status);
    }

    [Fact]
    public async Task Any_other_failure_is_a_plain_500_with_no_detail()
    {
        await using var f = await StartAsync(auditQuery: new BrokenAudit());
        var b = await SignedIn(f);
        var r = await b.GetTextAsync("/audit");
        Assert.Equal(500, r.Status);
        Assert.DoesNotContain("exploded", r.Body);
        Assert.DoesNotContain("secret", r.Body);
        Assert.True(r.Response.Headers.Contains("Content-Security-Policy"));
    }

    // ---- real Microsoft configuration (no network is used)

    [Fact]
    public async Task The_real_sign_in_is_configured_for_code_flow_with_pkce_for_this_one_directory()
    {
        await using var f = await StartAsync(fakeProvider: false);
        var o = f.App.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(AdminSite.RemoteScheme);

        Assert.Equal($"https://login.microsoftonline.com/{Tid}/v2.0", o.Authority);
        Assert.Equal("33333333-3333-4333-8333-333333333333", o.ClientId);
        Assert.Equal(ClientSecret, o.ClientSecret);
        Assert.Equal("code", o.ResponseType);
        Assert.Equal("query", o.ResponseMode);
        Assert.True(o.UsePkce);
        Assert.True(o.RequireHttpsMetadata);
        Assert.False(o.MapInboundClaims);                 // tid and oid arrive under their own names
        Assert.False(o.SaveTokens);
        Assert.False(o.GetClaimsFromUserInfoEndpoint);
        Assert.Equal(AdminSite.CallbackPath, o.CallbackPath.Value);
        Assert.Equal(AdminSite.CookieScheme, o.SignInScheme);
        Assert.Equal(new[] { "openid", "profile" }, o.Scope.OrderBy(x => x).ToArray());
        Assert.True(o.TokenValidationParameters.ValidateIssuer);
        Assert.Equal($"https://login.microsoftonline.com/{Tid}/v2.0", o.TokenValidationParameters.ValidIssuer);
        Assert.Null(o.TokenValidationParameters.ValidIssuers);
        Assert.True(o.TokenValidationParameters.ValidateAudience);
        Assert.Equal("33333333-3333-4333-8333-333333333333", o.TokenValidationParameters.ValidAudience);
        Assert.Equal(Microsoft.AspNetCore.Http.SameSiteMode.Lax, o.CorrelationCookie.SameSite);
    }

    [Fact]
    public async Task The_microsoft_handler_refuses_a_validated_token_for_someone_not_on_the_list_and_trims_the_one_it_accepts()
    {
        await using var f = await StartAsync(fakeProvider: false);
        var o = f.App.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(AdminSite.RemoteScheme);
        var scheme = new AuthenticationScheme(AdminSite.RemoteScheme, null, typeof(OpenIdConnectHandler));

        async Task<Microsoft.AspNetCore.Authentication.OpenIdConnect.TokenValidatedContext> Run(System.Security.Claims.ClaimsPrincipal p)
        {
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = f.App.Services };
            var ctx = new Microsoft.AspNetCore.Authentication.OpenIdConnect.TokenValidatedContext(http, scheme, o, p, new AuthenticationProperties());
            await o.Events.TokenValidated(ctx);
            return ctx;
        }

        var denied = await Run(FakeIdentityProvider.Person(Tid, "99999999-9999-4999-8999-999999999999"));
        Assert.False(denied.Result is null);
        Assert.False(denied.Result!.Succeeded);
        Assert.IsType<AdminSignInDeniedException>(denied.Result.Failure);
        Assert.False((await Run(FakeIdentityProvider.Person(null, null, "olivia@contoso.example"))).Result is null);

        var ok = await Run(FakeIdentityProvider.Person(Tid, Oid, "olivia@contoso.example"));
        Assert.Null(ok.Result);
        Assert.Equal(new[] { "name", "oid", "tid" }, ok.Principal!.Claims.Select(c => c.Type).OrderBy(x => x).ToArray());   // the email is not carried into the cookie
    }

    [Fact]
    public async Task Without_the_microsoft_settings_every_admin_page_says_not_configured_and_the_proxy_is_untouched()
    {
        await using var f = await StartAsync(o => { o.AdminClientSecret = null; }, fakeProvider: false);
        var b = f.Browser();
        Assert.Equal(503, (await b.GetTextAsync("/")).Status);
        var post = await b.SendAsync(HttpMethod.Post, "/signin");
        Assert.True(post.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.ServiceUnavailable);
        Assert.Equal(HttpStatusCode.Redirect, (await b.GetAsync("/tenants")).StatusCode);
        var token = await f.Tokens.IssueAsync("acme", "ci", default);
        var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x:" + token.Plaintext));
        var proxy = await f.Browser("127.0.0.1").SendAsync(HttpMethod.Get, "/v3/index.json", tweak: r => r.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic));
        Assert.Equal(HttpStatusCode.OK, proxy.StatusCode);
    }

    // ---- allowlist unit tests

    [Fact]
    public void The_allowlist_parser_keeps_good_pairs_and_counts_bad_ones()
    {
        var a = AdminAllowlist.Parse($"{Tid}:{Oid}, junk ; {Tid}:{Oid}:extra {Tid}", null, out var rejected);
        Assert.Equal(1, a.Count);
        Assert.Equal(3, rejected);
        Assert.Equal(0, AdminAllowlist.Parse(null, null, out _).Count);
        Assert.Equal(0, AdminAllowlist.Parse("00000000-0000-0000-0000-000000000000:" + Oid, null, out _).Count);
    }

    [Fact]
    public void A_principal_with_two_different_tid_or_oid_claims_is_refused()
    {
        var a = AdminAllowlist.Parse($"{Tid}:{Oid}", null, out _);
        var two = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim("tid", Tid), new System.Security.Claims.Claim("tid", Guid.NewGuid().ToString()), new System.Security.Claims.Claim("oid", Oid),
        }, "x"));
        Assert.False(a.IsAllowed(two));
        Assert.True(a.IsAllowed(FakeIdentityProvider.Person(Tid, Oid)));
        Assert.False(a.IsAllowed(null));
        Assert.False(a.IsAllowed(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));   // not authenticated
    }

    [Fact]
    public void The_configured_directory_must_match_even_if_the_pair_is_listed()
    {
        var a = AdminAllowlist.Parse($"{Tid}:{Oid}", Guid.NewGuid(), out _);
        Assert.False(a.IsAllowed(FakeIdentityProvider.Person(Tid, Oid)));
    }

    // ---- identity mode on the tenant page

    private const string SharedDir = "44444444-5555-4666-8777-888888888888";
    private const string SharedKeyMarker = "SHARED-KEY-DO-NOT-LEAK-0b7e";

    [Fact]
    public async Task The_tenant_page_shows_the_identity_mode_without_any_key_material()
    {
        await using var f = await StartAsync();
        await SeedAsync(f);   // acme: own app
        await f.Tenants.BindSharedIdentityAsync("globex", SharedDir, default);
        var shared = new Packmoat.Infrastructure.SqliteSharedAppCredentialStore(f.Db, f.Clock, Packmoat.Infrastructure.AesGcmSecretProtector.FromBase64(f.MasterKey));
        await shared.SetAsync(SharedAppSlot.Active, new SharedAppCredential("bbbbbbbb-0000-4000-8000-000000000000",
            "-----BEGIN PRIVATE KEY-----\n" + SharedKeyMarker + "\n-----END PRIVATE KEY-----"), default);
        var b = await SignedIn(f);

        var acme = (await b.GetTextAsync("/tenants/acme")).Body;
        var globex = (await b.GetTextAsync("/tenants/globex")).Body;

        Assert.Contains("Upstream identity: <strong>own app</strong>", acme);
        Assert.Contains($"Upstream identity: <strong>Packmoat app, directory {SharedDir}</strong>", globex);
        foreach (var html in new[] { acme, globex })
            foreach (var s in new[] { PemMarker, SharedKeyMarker, "PRIVATE KEY", "BEGIN", f.MasterKey })
                Assert.DoesNotContain(s, html);
    }

    [Fact]
    public async Task The_tenant_page_never_decrypts_the_key()
    {
        await using var f = await StartAsync();
        await SeedAsync(f);
        // Make the stored key undecryptable: if the page decrypted it, it would fail.
        await using (var c = await f.Db.OpenAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE upstream_identities SET cert_encrypted=x'0100' WHERE tenant_id='acme'";
            await cmd.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() => f.Tenants.GetUpstreamIdentityAsync("acme", default));
        var b = await SignedIn(f);

        var (status, html, _) = await b.GetTextAsync("/tenants/acme");

        Assert.Equal(200, status);
        Assert.Contains("own app", html);
    }

    [Fact]
    public async Task A_tenant_without_an_identity_says_so()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        Assert.Contains("Upstream identity: <strong>not set</strong>", (await b.GetTextAsync("/tenants/globex")).Body);
    }
}
