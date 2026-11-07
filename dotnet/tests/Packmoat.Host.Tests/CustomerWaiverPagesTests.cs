using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Customer;
using Packmoat.Host.Web;

namespace Packmoat.Host.Tests;

/// <summary>
/// #157 PR 2: the customer site's Waivers page end to end through the fake Microsoft sign-in. Everyone reads /waivers; only a freshly
/// signed-in Admin adds or revokes. The tenant comes only from the session; everything shown is HTML-encoded.
/// </summary>
[Collection("sqlite-pools")]
public class CustomerWaiverPagesTests
{
    private const string AppHost = CustomerSiteTests.AppHost;
    private const string DirA = CustomerSiteTests.DirA;
    private const string DirB = CustomerSiteTests.DirB;
    private const string Ada = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    private const string Grace = "ffffffff-ffff-4fff-8fff-ffffffffffff";
    private const string Mallory = "11111111-1111-4111-8111-111111111111";
    private const string Origin = "https://" + AppHost;
    private const string GoodReason = "CVE-2026-1234 fix";

    private static async Task<AdminSiteFixture> StartAsync()
    {
        var f = await AdminSiteFixture.StartAsync(o =>
        {
            o.AppHost = AppHost;
            o.AppClientId = CustomerSiteTests.AppClient;
            o.AppRemoteSchemeOverride = auth => auth.AddScheme<AuthenticationSchemeOptions, FakeAppEntraHandler>(AppSite.RemoteScheme, null);
        });
        await f.Directories.BindAsync(new DirectoryBinding(DirA, "acme", f.Clock.UtcNow, "op"), default);
        await f.Directories.BindAsync(new DirectoryBinding(DirB, "globex", f.Clock.UtcNow, "op"), default);
        return f;
    }

    private static ClaimsPrincipal Person(string oid, string name, string role, string tid, DateTimeOffset? authTime)
    {
        var claims = new List<Claim>
        {
            new("iss", $"https://login.microsoftonline.com/{tid}/v2.0"), new("aud", CustomerSiteTests.AppClient), new("tid", tid),
            new("oid", oid), new("name", name), new("roles", role),
        };
        if (authTime is { } at) claims.Add(new Claim("auth_time", at.ToUnixTimeSeconds().ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "FakeEntra", "name", "roles"));
    }

    private static async Task<AdminBrowser> SignIn(AdminSiteFixture f, ClaimsPrincipal person)
    {
        f.AppIdentity.Next = person;
        var b = f.Browser(AppHost);
        var start = await b.GetAsync("/");
        var callback = await b.GetAsync(start.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return b;
    }

    private static Task<AdminBrowser> Developer(AdminSiteFixture f) => SignIn(f, Person(Ada, "Ada Lovelace", CustomerSignIn.DeveloperRole, DirA, f.Clock.UtcNow));
    private static Task<AdminBrowser> Admin(AdminSiteFixture f, string oid = Grace, string name = "Grace Hopper", string tid = DirA) =>
        SignIn(f, Person(oid, name, CustomerSignIn.AdminRole, tid, f.Clock.UtcNow));

    private static string? Nonce(string html) => Regex.Match(html, "name=\"nonce\" value=\"([^\"]+)\"") is { Success: true } m ? m.Groups[1].Value : null;

    private static Task<HttpResponseMessage> PostWith(AdminBrowser b, string html, string action, params (string Name, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>> { new(SignedInSite.AntiforgeryField, AdminBrowser.Field(html) ?? throw new InvalidOperationException("no form on the page")) };
        form.AddRange(fields.Select(x => new KeyValuePair<string, string>(x.Name, x.Value)));
        return b.SendAsync(HttpMethod.Post, action, form, r => r.Headers.TryAddWithoutValidation("Origin", Origin));
    }

    private static async Task<(HttpResponseMessage Response, string Body)> Create(AdminBrowser b, string package, string version, string days = "7", string reason = GoodReason,
        params (string Name, string Value)[] extra)
    {
        var page = await (await b.GetAsync("/waivers")).Content.ReadAsStringAsync();
        var r = await PostWith(b, page, "/waivers/create", new[] { ("nonce", Nonce(page) ?? ""), ("package", package), ("version", version), ("days", days), ("reason", reason) }.Concat(extra).ToArray());
        return (r, await r.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> Revoke(AdminBrowser b, string id)
    {
        var confirm = await b.GetAsync($"/waivers/revoke?waiver={id}");
        if (confirm.StatusCode != HttpStatusCode.OK)
        {
            // no confirm page (gone): post from the list page, as a forged or stale form would
            var list = await (await b.GetAsync("/waivers")).Content.ReadAsStringAsync();
            return await PostWith(b, list, "/waivers/revoke", ("nonce", Nonce(list) ?? ""), ("waiver", id));
        }
        var html = await confirm.Content.ReadAsStringAsync();
        return await PostWith(b, html, "/waivers/revoke", ("nonce", Nonce(html) ?? ""), ("waiver", id));
    }

    private static string Where(HttpResponseMessage r) => r.Headers.Location?.OriginalString ?? "";

    private static string Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : r.Content.Headers.TryGetValues(name, out var c) ? string.Join(",", c) : "";

    /// <summary>The page's visible text: tags dropped, entities decoded, white space collapsed.</summary>
    private static string Text(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")), @"\s+", " ");

    private static async Task<IReadOnlyList<Waiver>> ActiveIn(AdminSiteFixture f, string tenant) => (await f.Tenants.GetAsync(tenant, default))!.Policy.Waivers;

    // ---------------------------------------------------------------- the admin's page

    [Fact]
    public async Task An_admin_adds_a_waiver_sees_it_listed_and_it_is_in_the_tenants_policy_at_once()
    {
        await using var f = await StartAsync();
        var grace = await Admin(f);
        var (status, page, _) = await grace.GetTextAsync("/waivers");
        Assert.Equal(200, status);
        Assert.Contains(AppWaivers.PageText, Text(page));   // the page's wording, exactly
        Assert.Contains("action=\"/waivers/create\"", page);
        Assert.Contains("There are no active waivers.", page);

        var (r, _) = await Create(grace, "Newtonsoft.Json", "13.0.4", "7");
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal("/waivers?m=added", Where(r));

        var w = Assert.Single(await ActiveIn(f, "acme"));
        Assert.Equal(("newtonsoft.json", "13.0.4", WaiverSource.Customer, GoodReason), (w.PackageId, w.Version, w.Source, w.Reason));
        Assert.Equal($"Grace Hopper ({Grace})", w.CreatedBy);
        Assert.Equal(f.Clock.UtcNow.AddDays(7), w.ExpiresAt);
        Assert.Empty(await ActiveIn(f, "globex"));

        var list = (await grace.GetTextAsync("/waivers?m=added")).Body;
        Assert.Contains("Waiver added.", list);
        Assert.Contains("<td>newtonsoft.json</td><td>13.0.4</td>", list);
        Assert.Contains("Grace Hopper", list);
        Assert.DoesNotContain(Grace, Text(list));   // the object id is not shown
        Assert.Contains($"/waivers/revoke?waiver={w.Id}", list);

        var trail = (await f.Actions.LatestAsync(10, default)).Reverse().ToList();
        Assert.Equal(new[] { "waiver.add", "waiver.add.id" }, trail.Select(x => x.Action));
        Assert.All(trail, x => Assert.Equal(("app", "acme", DirA, Grace), (x.Site, x.TenantId, x.ActorTid, x.ActorOid)));
    }

    [Fact]
    public async Task The_home_page_links_the_waivers_page_for_every_role_and_a_signed_out_link_survives_sign_in()
    {
        await using var f = await StartAsync();
        Assert.Contains("href=\"/waivers\"", (await (await Developer(f)).GetTextAsync("/home")).Body);
        Assert.Contains("href=\"/waivers\"", (await (await Admin(f)).GetTextAsync("/home")).Body);
        Assert.Equal("/waivers", AppSite.ReturnPath("/waivers"));
        Assert.Equal("/home", AppSite.ReturnPath("/waivers/create"));
        Assert.Equal("/home", AppSite.ReturnPath("/Waivers"));
    }

    [Fact]
    public async Task The_token_pages_nu1102_help_sends_developers_to_an_admin_and_the_waivers_page()
    {
        await using var f = await StartAsync();
        await f.SetUpOwnAppAsync("acme", DirA);
        var ada = await Developer(f);
        var page = await (await ada.GetAsync("/tokens")).Content.ReadAsStringAsync();
        var r = await PostWith(ada, page, "/tokens/create", ("nonce", Nonce(page)!), ("label", "laptop"));
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("ask a Packmoat Admin in your organisation to waive that exact version", body);
        Assert.Contains("href=\"/waivers\"", body);
        Assert.DoesNotContain("request a waiver from", body);
    }

    // ---------------------------------------------------------------- developers read only

    [Fact]   // mutation: drop the role check on POST /waivers/create (the page and the command both check)
    public async Task A_developer_sees_the_active_list_read_only_and_cannot_add_or_revoke()
    {
        await using var f = await StartAsync();
        var op = await f.Tenants.AddWaiverAsync("acme", "Moq", "4.21.0", f.Clock.UtcNow.AddDays(3), "operator made this", "olivia", default);
        var ada = await Developer(f);
        var (status, page, _) = await ada.GetTextAsync("/waivers");
        Assert.Equal(200, status);
        Assert.Contains(AppWaivers.PageText, Text(page));
        Assert.Contains("<td>moq</td><td>4.21.0</td>", page);
        Assert.Contains("ask a Packmoat Admin in your organisation to waive that exact version", page);
        Assert.DoesNotContain("action=\"/waivers/create\"", page);
        Assert.DoesNotContain("/waivers/revoke", page);
        Assert.DoesNotContain("name=\"nonce\"", page);
        Assert.DoesNotContain("History", page);

        var forged = await PostWith(ada, page, "/waivers/create", ("nonce", "x"), ("package", "Evil"), ("version", "1.0.0"), ("days", "7"), ("reason", GoodReason));
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.Contains("Admins only", await forged.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await PostWith(ada, page, "/waivers/revoke", ("nonce", "x"), ("waiver", op.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ada.GetAsync($"/waivers/revoke?waiver={op.Id}")).StatusCode);
        Assert.Equal(new[] { op.Id }, (await ActiveIn(f, "acme")).Select(w => w.Id));
        Assert.Empty(await f.Actions.LatestAsync(10, default));
    }

    // ---------------------------------------------------------------- forms: antiforgery, origin, nonce

    [Fact]   // mutation: skip the nonce check on create
    public async Task A_used_create_form_is_refused_and_makes_no_second_waiver()
    {
        await using var f = await StartAsync();
        var grace = await Admin(f);
        var page = await (await grace.GetAsync("/waivers")).Content.ReadAsStringAsync();
        var fields = new[] { ("nonce", Nonce(page)!), ("package", "Moq"), ("version", "4.21.0"), ("days", "7"), ("reason", GoodReason) };
        Assert.Equal(HttpStatusCode.SeeOther, (await PostWith(grace, page, "/waivers/create", fields)).StatusCode);
        var again = await PostWith(grace, page, "/waivers/create", fields.Select(x => x.Item1 == "package" ? ("package", "Other") : x).ToArray());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("Open the form again", await again.Content.ReadAsStringAsync());
        Assert.Single(await ActiveIn(f, "acme"));
    }

    [Fact]
    public async Task A_used_revoke_form_is_refused()
    {
        await using var f = await StartAsync();
        var a = await f.Tenants.AddWaiverAsync("acme", "Moq", "4.21.0", f.Clock.UtcNow.AddDays(3), GoodReason, "olivia", default);
        var grace = await Admin(f);
        var html = await (await grace.GetAsync($"/waivers/revoke?waiver={a.Id}")).Content.ReadAsStringAsync();
        var nonce = Nonce(html)!;
        Assert.Equal("/waivers?m=revoked", Where(await PostWith(grace, html, "/waivers/revoke", ("nonce", nonce), ("waiver", a.Id))));
        var again = await PostWith(grace, html, "/waivers/revoke", ("nonce", nonce), ("waiver", a.Id));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Writes_need_the_sites_own_origin_and_the_antiforgery_token()
    {
        await using var f = await StartAsync();
        var grace = await Admin(f);
        var page = await (await grace.GetAsync("/waivers")).Content.ReadAsStringAsync();
        var form = new[]
        {
            new KeyValuePair<string, string>(SignedInSite.AntiforgeryField, AdminBrowser.Field(page)!), new("nonce", Nonce(page)!), new("package", "Moq"),
            new("version", "4.21.0"), new("days", "7"), new("reason", GoodReason),
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await grace.SendAsync(HttpMethod.Post, "/waivers/create", form.Skip(1), r => r.Headers.TryAddWithoutValidation("Origin", Origin))).StatusCode);   // no antiforgery
        Assert.Equal(HttpStatusCode.BadRequest, (await grace.SendAsync(HttpMethod.Post, "/waivers/revoke", form.Skip(1), r => r.Headers.TryAddWithoutValidation("Origin", Origin))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await grace.SendAsync(HttpMethod.Post, "/waivers/create", form)).StatusCode);   // no Origin
        Assert.Equal(HttpStatusCode.Forbidden, (await grace.SendAsync(HttpMethod.Post, "/waivers/create", form, r => r.Headers.TryAddWithoutValidation("Origin", "https://evil.example"))).StatusCode);
        Assert.Empty(await ActiveIn(f, "acme"));
    }

    [Fact]
    public async Task A_get_never_changes_anything()
    {
        await using var f = await StartAsync();
        var a = await f.Tenants.AddWaiverAsync("acme", "Moq", "4.21.0", f.Clock.UtcNow.AddDays(3), GoodReason, "olivia", default);
        var grace = await Admin(f);
        Assert.Equal(404, (await grace.GetTextAsync("/waivers/create?package=Evil&version=1.0.0&days=7&reason=xxxxxxxxxxxx")).Status);
        Assert.Contains("Revoke the waiver for moq 4.21.0?", (await grace.GetTextAsync($"/waivers/revoke?waiver={a.Id}")).Body);
        Assert.Equal(new[] { a.Id }, (await ActiveIn(f, "acme")).Select(w => w.Id));
    }

    // ---------------------------------------------------------------- the tenant is the session's

    [Fact]   // mutation: read a tenant from the form
    public async Task A_tenant_field_in_the_form_is_ignored()
    {
        await using var f = await StartAsync();
        var grace = await Admin(f);
        var (r, _) = await Create(grace, "Moq", "4.21.0", extra: new[] { ("tenant", "globex"), ("tenantId", "globex") });
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Single(await ActiveIn(f, "acme"));
        Assert.Empty(await ActiveIn(f, "globex"));
    }

    [Fact]   // mutation: revoke by id alone (no tenant), or a different message for another tenant's id
    public async Task Revoking_another_tenants_waiver_looks_exactly_like_not_found_and_changes_nothing()
    {
        await using var f = await StartAsync();
        var theirs = await f.Tenants.AddWaiverAsync("globex", "Moq", "4.21.0", f.Clock.UtcNow.AddDays(3), GoodReason, "olivia", default);
        var grace = await Admin(f);

        var cross = await grace.GetAsync($"/waivers/revoke?waiver={theirs.Id}");
        var unknown = await grace.GetAsync("/waivers/revoke?waiver=wvr_0123456789ab");
        Assert.Equal((cross.StatusCode, Where(cross)), (unknown.StatusCode, Where(unknown)));
        Assert.Equal("/waivers?m=gone", Where(cross));

        var list = await (await grace.GetAsync("/waivers")).Content.ReadAsStringAsync();
        var crossPost = await PostWith(grace, list, "/waivers/revoke", ("nonce", Nonce(list)!), ("waiver", theirs.Id));
        list = await (await grace.GetAsync("/waivers")).Content.ReadAsStringAsync();
        var unknownPost = await PostWith(grace, list, "/waivers/revoke", ("nonce", Nonce(list)!), ("waiver", "wvr_0123456789ab"));
        Assert.Equal((crossPost.StatusCode, Where(crossPost)), (unknownPost.StatusCode, Where(unknownPost)));
        Assert.Equal("/waivers?m=gone", Where(crossPost));

        Assert.Equal(new[] { theirs.Id }, (await ActiveIn(f, "globex")).Select(w => w.Id));
        Assert.Empty(await f.Actions.LatestAsync(10, default));
        Assert.Contains("That waiver was not found or has already ended. Nothing changed.", (await grace.GetTextAsync("/waivers?m=gone")).Body);
    }

    [Theory]   // mutation: drop the waiver-id shape check in the page
    [InlineData("WVR_0123456789AB")]
    [InlineData("wvr_0123456789abc")]
    [InlineData("wvr_0123456789a")]
    [InlineData("wvr_0123456789ag")]
    [InlineData("../wvr_0123456789ab")]
    [InlineData("<script>")]
    public async Task A_waiver_id_that_is_not_the_shape_is_not_found(string id)
    {
        await using var f = await StartAsync();
        var grace = await Admin(f);
        Assert.Equal("/waivers?m=gone", Where(await grace.GetAsync($"/waivers/revoke?waiver={Uri.EscapeDataString(id)}")));
        var list = await (await grace.GetAsync("/waivers")).Content.ReadAsStringAsync();
        Assert.Equal("/waivers?m=gone", Where(await PostWith(grace, list, "/waivers/revoke", ("nonce", Nonce(list)!), ("waiver", id))));
    }

    // ---------------------------------------------------------------- revoke

    [Fact]
    public async Task An_admin_revokes_a_waiver_operator_made_ones_too_and_it_moves_to_the_history()
    {
        await using var f = await StartAsync();
        var op = await f.Tenants.AddWaiverAsync("acme", "Moq", "4.21.0", f.Clock.UtcNow.AddDays(3), "operator made this", "olivia", default);
        var grace = await Admin(f);
        var r = await Revoke(grace, op.Id);
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal("/waivers?m=revoked", Where(r));
        Assert.Empty(await ActiveIn(f, "acme"));

        var page = (await grace.GetTextAsync("/waivers?m=revoked")).Body;
        Assert.Contains("Waiver revoked.", page);
        Assert.Contains("History", page);
        Assert.Contains("Revoked", page);
        Assert.Contains("There are no active waivers.", page);
        var row = Assert.Single(await f.Actions.LatestAsync(10, default));
        Assert.Equal("waiver.revoke", row.Action);
        var stored = Assert.Single(await f.Tenants.ListWaiversAsync("acme", 10, default));
        Assert.Equal($"Grace Hopper ({Grace})", stored.RevokedBy);
    }

    // ---------------------------------------------------------------- the fresh sign-in

    [Fact]   // mutation: drop the freshness check on the page (the command also checks)
    public async Task A_stale_admin_is_asked_to_sign_in_again_and_cannot_add_or_revoke()
    {
        await using var f = await StartAsync();
        var op = await f.Tenants.AddWaiverAsync("acme", "Moq", "4.21.0", f.Clock.UtcNow.AddDays(3), GoodReason, "olivia", default);
        var grace = await Admin(f);
        var fresh = await (await grace.GetAsync("/waivers")).Content.ReadAsStringAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(16));

        var (status, page, r) = await grace.GetTextAsync("/waivers");
        Assert.Equal(200, status);
        Assert.DoesNotContain("action=\"/waivers/create\"", page);
        Assert.Contains("Sign in again to make changes.", page);
        Assert.Contains("action=\"/waivers/reauth\"", page);
        Assert.Equal(SignedInSite.LandingContentSecurityPolicy, Header(r, "Content-Security-Policy"));

        // a form loaded while fresh is refused once stale, and its nonce is not spent
        var create = await PostWith(grace, fresh, "/waivers/create", ("nonce", Nonce(fresh)!), ("package", "Other"), ("version", "1.0.0"), ("days", "7"), ("reason", GoodReason));
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Contains("Sign in again first", await create.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await grace.GetAsync($"/waivers/revoke?waiver={op.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostWith(grace, fresh, "/waivers/revoke", ("nonce", Nonce(fresh)!), ("waiver", op.Id))).StatusCode);
        Assert.Equal(new[] { op.Id }, (await ActiveIn(f, "acme")).Select(w => w.Id));

        // the fresh sign-in returns to the waivers page
        f.AppIdentity.Next = Person(Grace, "Grace Hopper", CustomerSignIn.AdminRole, DirA, f.Clock.UtcNow);
        var start = await PostWith(grace, page, "/waivers/reauth");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var props = Assert.IsType<OpenIdConnectChallengeProperties>(f.AppIdentity.LastChallenge);
        Assert.Equal(("login", "/waivers?m=reauth"), (props.Prompt, props.RedirectUri));
        Assert.Equal(HttpStatusCode.Redirect, (await grace.GetAsync(Where(start))).StatusCode);
        Assert.Contains("action=\"/waivers/create\"", (await grace.GetTextAsync("/waivers?m=reauth")).Body);
        Assert.Equal("/waivers?m=revoked", Where(await Revoke(grace, op.Id)));
    }

    // ---------------------------------------------------------------- refusals

    [Theory]
    [InlineData("Moq", "*", "7", GoodReason, 400, "Wildcards are not allowed.")]
    [InlineData("Moq", "4.*", "7", GoodReason, 400, "Wildcards are not allowed.")]
    [InlineData("Moq", "4.21.0", "31", GoodReason, 400, "A waiver lasts from 1 to 30 days.")]
    [InlineData("Moq", "4.21.0", "0", GoodReason, 400, "A waiver lasts from 1 to 30 days.")]
    [InlineData("Moq", "4.21.0", "seven", GoodReason, 400, "A waiver lasts from 1 to 30 days.")]
    [InlineData("Moq", "4.21.0", "7", "too short", 400, "A reason of 10 to 200 characters is required")]
    [InlineData("Moq pkg", "4.21.0", "7", GoodReason, 400, "The package id may use letters")]
    public async Task Bad_input_gets_a_fixed_message_and_changes_nothing(string package, string version, string days, string reason, int status, string message)
    {
        await using var f = await StartAsync();
        var grace = await Admin(f);
        var (r, body) = await Create(grace, package, version, days, reason);
        Assert.Equal(status, (int)r.StatusCode);
        Assert.Contains(WebUtility.HtmlEncode(message), body);
        Assert.Contains("href=\"/waivers\"", body);
        Assert.Empty(await ActiveIn(f, "acme"));
    }

    [Fact]
    public async Task A_duplicate_says_already_waived_and_an_internal_package_is_refused()
    {
        await using var f = await StartAsync();
        await f.Tenants.AddInternalPrefixAsync("acme", "Contoso.*", default);
        var grace = await Admin(f);
        Assert.Equal(HttpStatusCode.SeeOther, (await Create(grace, "Moq", "4.21.0")).Response.StatusCode);
        var (dup, dupBody) = await Create(grace, "moq", "4.21");
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Contains("already has an active waiver", dupBody);
        var (internalR, internalBody) = await Create(grace, "Contoso.Core", "1.0.0");
        Assert.Equal(HttpStatusCode.BadRequest, internalR.StatusCode);
        Assert.Contains("internal prefixes", internalBody);
        Assert.Single(await ActiveIn(f, "acme"));
    }

    [Fact]
    public async Task At_the_cap_a_new_waiver_is_refused_with_the_cap_message()
    {
        await using var f = await StartAsync();
        for (var i = 0; i < WaiverRules.MaxActiveCustomerWaivers; i++)
            await f.Tenants.AddCustomerWaiverAsync("acme", "pkg" + i, "1.0.0", f.Clock.UtcNow.AddDays(7), GoodReason, "x", WaiverRules.MaxActiveCustomerWaivers, default);
        var grace = await Admin(f);
        var (r, body) = await Create(grace, "Moq", "4.21.0");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("already has 25 active waivers", body);
        var (dup, dupBody) = await Create(grace, "pkg0", "1.0.0");   // a duplicate at the cap is a duplicate
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Contains("already has an active waiver", dupBody);
    }

    // ---------------------------------------------------------------- encoding

    [Fact]   // mutation: drop E() around the package, the version, the reason or the created-by name
    public async Task Everything_shown_is_html_encoded()
    {
        await using var f = await StartAsync();
        // The operator's store call takes text the customer limits would refuse: the page must still encode it.
        await f.Tenants.AddWaiverAsync("acme", "<img src=x onerror=alert(1)>", "1.0.0\"><b>v</b>", f.Clock.UtcNow.AddDays(3), "<script>alert('r')</script>", "<i>op</i>", default);
        var grace = await Admin(f, name: "<svg onload=alert(1)>");
        var (r, _) = await Create(grace, "Moq", "4.21.0", reason: "fix <b>now</b> & \"quickly\"");
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);

        foreach (var page in new[] { (await grace.GetTextAsync("/waivers")).Body, (await (await Developer(f)).GetTextAsync("/waivers")).Body })
        {
            Assert.DoesNotContain("<img src=x", page);
            Assert.DoesNotContain("<b>v</b>", page);
            Assert.DoesNotContain("<script>alert", page);
            Assert.DoesNotContain("<b>now</b>", page);
            Assert.DoesNotContain("<svg onload", page);
            Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", page);
            Assert.Contains("&lt;script&gt;", page);
            Assert.Contains("fix &lt;b&gt;now&lt;/b&gt; &amp; &quot;quickly&quot;", page);
        }
        var id = (await ActiveIn(f, "acme")).First(w => w.PackageId.StartsWith('<')).Id;
        var confirm = (await grace.GetTextAsync($"/waivers/revoke?waiver={id}")).Body;
        Assert.DoesNotContain("<img src=x", confirm);
        Assert.DoesNotContain("<b>v</b>", confirm);
        Assert.DoesNotContain("<script>alert", confirm);
        Assert.Contains("&lt;img src=x", confirm);
        Assert.Contains("&lt;script&gt;", confirm);

        // Grace's own waiver: her name (a member) and her reason, both encoded on the confirm page
        var mine = (await ActiveIn(f, "acme")).First(w => w.PackageId == "moq").Id;
        var own = (await grace.GetTextAsync($"/waivers/revoke?waiver={mine}")).Body;
        Assert.DoesNotContain("<svg onload", own);
        Assert.DoesNotContain("<b>now</b>", own);
        Assert.Contains("&lt;svg onload=alert(1)&gt;", own);
        Assert.Contains("fix &lt;b&gt;now&lt;/b&gt; &amp; &quot;quickly&quot;", own);
    }

    // ---------------------------------------------------------------- names: never the operator's own

    [Fact]   // review of #163: an operator revoke or an operator waiver shows "Packmoat", never the operator's name
    public async Task The_operator_is_shown_as_Packmoat_as_maker_and_as_revoker()
    {
        await using var f = await StartAsync();
        var op = await f.Tenants.AddWaiverAsync("acme", "Moq", "4.21.0", f.Clock.UtcNow.AddDays(3), "operator made this", "Olivia Operator", default);
        var grace = await Admin(f);
        await Create(grace, "Serilog", "4.0.0");
        var customer = (await ActiveIn(f, "acme")).Single(w => w.PackageId == "serilog");
        // the operator revokes the customer's waiver from the admin site (named as the admin site names its operator)
        Assert.True(await f.Tenants.RevokeWaiverAsync(null, customer.Id, "Olivia Operator", default));

        var page = (await grace.GetTextAsync("/waivers")).Body;
        Assert.DoesNotContain("Olivia", page);
        Assert.Contains("<td>moq</td><td>4.21.0</td><td>operator made this</td><td>Packmoat</td>", page);
        Assert.Contains(" by Packmoat</td>", page);
        Assert.Contains("<td>serilog</td><td>4.0.0</td><td>" + WebUtility.HtmlEncode(GoodReason) + "</td><td>Grace Hopper</td>", page);
        Assert.DoesNotContain("Olivia", (await grace.GetTextAsync($"/waivers/revoke?waiver={op.Id}")).Body);
    }

    // ---------------------------------------------------------------- lifetime

    [Fact]   // review of #163: the rate limits and the cap-alert throttle live in these instances, so they must be singletons
    public async Task The_waiver_commands_and_queries_are_singletons()
    {
        await using var f = await StartAsync();
        Assert.Same(f.App.Services.GetRequiredService<CustomerWaiverCommands>(), f.App.Services.GetRequiredService<CustomerWaiverCommands>());
        Assert.Same(f.App.Services.GetRequiredService<CustomerWaiverQueries>(), f.App.Services.GetRequiredService<CustomerWaiverQueries>());

        // and over HTTP: the eleventh add in an hour is refused
        var grace = await Admin(f);
        for (var i = 0; i < CustomerWaiverRules.MaxAddsPerWindow; i++)
            Assert.Equal(HttpStatusCode.SeeOther, (await Create(grace, "pkg" + i, "1.0.0")).Response.StatusCode);
        var (r, body) = await Create(grace, "one-more", "1.0.0");
        Assert.Equal((HttpStatusCode)429, r.StatusCode);
        Assert.Contains("Too many waiver changes", body);
    }
}
