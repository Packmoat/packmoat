using System.Net;
using System.Text.RegularExpressions;
using Packmoat.Host.Admin;
using Packmoat.Host.Web;
using static Packmoat.Host.Tests.AdminSiteFixture;

namespace Packmoat.Host.Tests;

/// <summary>
/// The operator console wears the Packmoat brand and says, several independent ways, that it is the operator console: the "Operator" pill,
/// the role="note" banner, the title suffix, its own favicon and the body class. All of it inside the unchanged CSP (no inline style, no
/// script, no other origin).
/// </summary>
[Collection("sqlite-pools")]
public class AdminBrandingTests
{
    private static async Task<AdminBrowser> SignedIn(AdminSiteFixture f)
    {
        var b = f.Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await b.SignInAsync()).StatusCode);
        return b;
    }

    private static string Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : r.Content.Headers.TryGetValues(name, out var c) ? string.Join(",", c) : "";

    private static void AssertOperatorChrome(string name, string html)
    {
        Assert.True(html.StartsWith("<!doctype html><html lang=\"en\">", StringComparison.Ordinal), name);
        Assert.True(html.Contains("<body class=\"operator\">", StringComparison.Ordinal), name + ": body class");
        Assert.True(html.Contains("<svg class=\"mark\"", StringComparison.Ordinal), name + ": mark");
        Assert.True(html.Contains("<span>Packmoat</span><span class=\"pill\">Operator</span>", StringComparison.Ordinal), name + ": operator pill");
        Assert.True(Regex.IsMatch(html, "<div class=\"banner\" role=\"note\">Operator console: changes here affect customer tenants.</div>"), name + ": banner");
        Assert.True(Regex.IsMatch(html, "<title>[^<]* - Packmoat operator</title>"), name + ": title suffix");
        Assert.True(html.Contains("<link rel=\"icon\" href=\"/favicon.svg\" type=\"image/svg+xml\">", StringComparison.Ordinal), name + ": favicon");
        Assert.True(html.Contains("<link rel=\"stylesheet\" href=\"/admin.css\">", StringComparison.Ordinal), name + ": stylesheet");
        Assert.True(html.Contains("<footer><p>Packmoat operator console. Internal use only.</p></footer>", StringComparison.Ordinal), name + ": footer");
        Assert.False(html.Contains("/app.css", StringComparison.Ordinal), name + ": customer stylesheet");
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"(?i)\sstyle\s*=", html);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
    }

    [Fact]
    public async Task Every_admin_page_shows_the_operator_signals()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var pages = new List<(string Name, HttpResponseMessage Response)>();
        foreach (var p in new[] { "/tenants", "/tenants/acme", "/audit", "/actions", "/nothing-here" }) pages.Add((p, await b.GetAsync(p)));
        pages.Add(("anon /", await f.Browser().GetAsync("/")));
        pages.Add(("400 antiforgery", await f.Browser().SendAsync(HttpMethod.Post, "/signout")));
        f.SignInAs(Tid, "99999999-9999-4999-8999-999999999999");
        pages.Add(("403 refused", await f.Browser().SignInAsync()));

        await using var off = await StartAsync(o => { o.AdminClientSecret = null; }, fakeProvider: false);
        pages.Add(("503", await off.Browser().GetAsync("/")));

        var statuses = new Dictionary<string, int> { ["/nothing-here"] = 404, ["400 antiforgery"] = 400, ["403 refused"] = 403, ["503"] = 503 };
        foreach (var (name, r) in pages)
        {
            Assert.True((int)r.StatusCode == statuses.GetValueOrDefault(name, 200), $"{name}: status {(int)r.StatusCode}");
            AssertOperatorChrome(name, await r.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task The_header_carries_the_navigation_and_sign_out_only_when_signed_in()
    {
        await using var f = await StartAsync();
        var b = await SignedIn(f);
        var tenants = (await b.GetTextAsync("/tenants")).Body;
        Assert.Contains("<nav aria-label=\"Operator\"><a href=\"/tenants\">Tenants</a><a href=\"/audit\">Audit</a><a href=\"/actions\">Actions</a></nav>", tenants);
        Assert.Contains("action=\"/signout\"", tenants);
        Assert.Contains("<title>Tenants - Packmoat operator</title>", tenants);

        var landing = (await f.Browser().GetTextAsync("/")).Body;
        Assert.DoesNotContain("<nav", landing);
        Assert.DoesNotContain("/signout", landing);
    }

    [Fact]
    public async Task The_landing_keeps_the_sign_in_form_the_microsoft_button_and_the_landing_csp()
    {
        await using var f = await StartAsync();
        var (status, body, r) = await f.Browser().GetTextAsync("/");
        Assert.Equal(200, status);
        Assert.Equal(SignedInSite.LandingContentSecurityPolicy, Header(r, "Content-Security-Policy"));
        var form = Assert.Single(Regex.Matches(body, "<form\\b[^>]*>(.*?)</form>", RegexOptions.Singleline));
        Assert.Contains("method=\"post\" action=\"/signin\"", form.Value);
        Assert.Matches("<input type=\"hidden\" name=\"" + AdminSite.AntiforgeryField + "\" value=\"[^\"]+\">", form.Groups[1].Value);
        Assert.Contains("<button type=\"submit\" class=\"ms-signin\"><svg class=\"ms-logo\"", form.Groups[1].Value);
        Assert.Contains("<span>Sign in with Microsoft</span></button>", form.Groups[1].Value);
        Assert.Contains("class=\"card\"", body);
        // the button still starts the sign-in
        Assert.Equal(HttpStatusCode.Redirect, (await f.Browser().SignInAsync()).StatusCode);
    }

    [Fact]
    public async Task The_admin_stylesheet_is_the_brand_palette_with_the_operator_accent_and_loads_nothing()
    {
        await using var f = await StartAsync();
        var (status, css, r) = await f.Browser().GetTextAsync("/admin.css");
        Assert.Equal(200, status);
        Assert.StartsWith("text/css", Header(r, "Content-Type"));
        Assert.Contains("--moat:#0a6a5b", css);
        Assert.Contains("--accent:var(--moat)", css);     // the shared default
        Assert.Contains("--accent:#8a4307", css);         // the operator override
        Assert.Contains("body.operator", css);
        Assert.Contains(".banner", css);
        Assert.Contains(".pill", css);
        foreach (var banned in new[] { "url(", "@import", "https://", "http://", "@font-face" }) Assert.DoesNotContain(banned, css, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SignedInSite.ContentSecurityPolicy, Header(r, "Content-Security-Policy"));
    }

    [Fact]
    public async Task The_operator_favicon_is_served_same_origin_with_the_full_security_headers()
    {
        await using var f = await StartAsync();
        var r = await f.Browser().GetAsync("/favicon.svg");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.StartsWith("image/svg+xml", Header(r, "Content-Type"));
        var svg = await r.Content.ReadAsStringAsync();
        Assert.Equal(AdminHtml.OperatorFaviconSvg, svg);
        Assert.StartsWith("<svg xmlns=\"http://www.w3.org/2000/svg\"", svg);
        Assert.Contains("#0e1a18", svg);   // ink
        Assert.Contains("#8a4307", svg);   // amber
        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SignedInSite.ContentSecurityPolicy, Header(r, "Content-Security-Policy"));
        Assert.Equal("nosniff", Header(r, "X-Content-Type-Options"));
        Assert.Equal("same-origin", Header(r, "Referrer-Policy"));
        Assert.Equal("no-store", Header(r, "Cache-Control"));
        Assert.StartsWith("max-age=", Header(r, "Strict-Transport-Security"));
        Assert.Equal("DENY", Header(r, "X-Frame-Options"));
        Assert.Equal("noindex, nofollow", Header(r, "X-Robots-Tag"));
    }

    // ---- the signed-out page's layout and the 503 pages

    /// <summary>One CSS rule's declarations, by its exact selector.</summary>
    private static string Rule(string css, string selector)
    {
        var m = Regex.Match(css, "(?:^|[{}\\s])" + Regex.Escape(selector) + "\\{([^}]*)\\}", RegexOptions.Multiline);
        Assert.True(m.Success, "no rule for " + selector);
        return m.Groups[1].Value;
    }

    /// <summary>The rules inside the narrow-screen media query.</summary>
    private static string Narrow(string css)
    {
        var m = Regex.Match(css, @"@media \(max-width:520px\)\{(.*)\}\s*$", RegexOptions.Multiline);
        Assert.True(m.Success, "no narrow-screen media query");
        return m.Groups[1].Value;
    }

    [Fact]
    public void The_header_banner_and_footer_have_side_padding_at_every_width()
    {
        var css = AdminHtml.Stylesheet;
        Assert.Contains("padding:12px 24px", Rule(css, ".operator header.top"));
        Assert.Contains("padding:8px 24px", Rule(css, ".banner"));
        Assert.Contains("padding:20px 24px 32px", Rule(css, ".operator footer"));
        Assert.DoesNotContain("max-width", Rule(css, ".operator footer"));   // full width, in line with the header
        var narrow = Narrow(css);
        Assert.Contains("padding:10px 12px", Rule(narrow, ".operator header.top"));
        Assert.Contains("padding:8px 12px", Rule(narrow, ".banner"));
        Assert.Contains("padding:16px 12px 24px", Rule(narrow, ".operator footer"));
    }

    [Fact]
    public void The_sign_in_card_is_centred_in_a_stage()
    {
        var css = AdminHtml.Stylesheet;
        var stage = Rule(css, ".signin-stage");
        foreach (var d in new[] { "min-height:60vh", "display:flex", "align-items:center", "justify-content:center" }) Assert.Contains(d, stage);
        Assert.Contains("margin:0 auto", Rule(css, ".card"));
        Assert.Contains("color:var(--muted)", Rule(css, ".lede"));
        var pill = Rule(css, ".operator .pill");
        Assert.Contains("border:1px solid #fff", pill);
        Assert.Contains("font-size:12px", pill);
    }

    [Fact]
    public async Task The_signed_out_page_nests_the_card_in_the_stage_inside_the_page_column()
    {
        await using var f = await StartAsync();
        var body = (await f.Browser().GetTextAsync("/")).Body;
        Assert.Matches("<body class=\"operator\"><a class=\"skip\" href=\"#main\">Skip to content</a><div class=\"wrap\">.*" +
            "<main id=\"main\"><div class=\"signin-stage\"><section class=\"card\" aria-labelledby=\"signin-h\">" +
            "<h1 id=\"signin-h\">Packmoat operator console</h1><p class=\"lede\">Manage tenants, read the audit log and run operator actions.</p>" +
            ".*</form></section></div></main>", body);
    }

    [Fact]
    public async Task Both_not_configured_pages_say_packmoat_operator_console()
    {
        // Two sites on one key ring: the configured one hands out an antiforgery token the unconfigured one accepts, so the sign-in POST
        // gets past the antiforgery check and reaches its own 503.
        var keys = Directory.CreateTempSubdirectory("packmoat-admin-keys-").FullName;
        try
        {
            await using var on = await StartAsync(keysDir: keys);
            await using var off = await StartAsync(o => { o.AdminClientSecret = null; }, fakeProvider: false, keysDir: keys);
            var landing = await off.Browser().GetTextAsync("/");
            Assert.Equal(503, landing.Status);

            var b = on.Browser();
            var field = AdminBrowser.Field((await b.GetTextAsync("/")).Body)!;
            var post = new AdminBrowser(off.BaseUrl, AdminHost);
            foreach (var c in b.Cookies) post.SetCookie(c.Key, c.Value);
            var signIn = await post.SendAsync(HttpMethod.Post, "/signin", new[] { new KeyValuePair<string, string>(AdminSite.AntiforgeryField, field) });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, signIn.StatusCode);

            foreach (var html in new[] { landing.Body, await signIn.Content.ReadAsStringAsync() })
            {
                Assert.Contains("<h1>Packmoat operator console</h1>", html);
                Assert.DoesNotContain("Packmoat admin", html);
                Assert.Contains("Sign-in is not configured on this server.", html);
            }
        }
        catch when (TempDir.Failing(keys)) { throw; }
        finally { TempDir.Delete(keys); }
    }

    [Fact]
    public async Task A_hostile_tenant_id_from_the_url_is_encoded_in_the_title_and_the_heading()
    {
        await using var f = await StartAsync();
        // The store refuses such an id, so the row goes in with SQL: the page must stay safe even if one ever got there.
        const string evil = "<script>\"&'";
        await using (var c = await f.Db.OpenAsync())
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO tenants(id, name, upstream_base, allowed_path_prefix, feed_index_url, cooldown_days, created_at, origin) " +
                "SELECT @id, @id, upstream_base, allowed_path_prefix, feed_index_url, cooldown_days, created_at, origin FROM tenants WHERE id='acme'";
            cmd.Parameters.AddWithValue("@id", evil);
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }
        var b = await SignedIn(f);
        var (status, html, _) = await b.GetTextAsync("/tenants/" + Uri.EscapeDataString(evil));
        Assert.Equal(200, status);
        const string encoded = "&lt;script&gt;&quot;&amp;&#x27;";
        Assert.Contains("<title>Tenant " + encoded + " - Packmoat operator</title>", html);
        Assert.Contains("<h1>" + encoded + " <span class=\"muted\">(" + encoded + ")</span></h1>", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }
}
