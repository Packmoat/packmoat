using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Packmoat.Application;
using Packmoat.Host.Customer;
using Packmoat.Host.Web;

namespace Packmoat.Host.Tests;

/// <summary>The customer site's token pages, end to end through the fake Microsoft sign-in: /tokens, /team and the fresh sign-in.</summary>
[Collection("sqlite-pools")]
public class CustomerTokenPagesTests
{
    private const string AppHost = CustomerSiteTests.AppHost;
    private const string DirA = CustomerSiteTests.DirA;
    private const string DirB = CustomerSiteTests.DirB;
    private const string Ada = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    private const string Bob = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
    private const string Grace = "ffffffff-ffff-4fff-8fff-ffffffffffff";
    private const string Origin = "https://" + AppHost;
    private const string FeedHost = "feeds.contoso.example";
    private const string FeedUrl = "https://" + FeedHost + "/v3/index.json";
    private static readonly Regex TokenValue = new("pmt_[0-9a-f]{48}");

    /// <param name="setUp">True: acme and globex are set-up own-app tenants (like e2e-test), so they issue tokens. False: no upstream binding.</param>
    private static async Task<AdminSiteFixture> StartAsync(Action<IServiceCollection>? services = null, bool setUp = true)
    {
        var f = await AdminSiteFixture.StartAsync(o =>
        {
            o.AppHost = AppHost;
            o.AllowedHosts = FeedHost;   // the feed address on the token pages is the proxy's own
            o.AppClientId = CustomerSiteTests.AppClient;
            o.AppRemoteSchemeOverride = auth => auth.AddScheme<AuthenticationSchemeOptions, FakeAppEntraHandler>(AppSite.RemoteScheme, null);
        }, services: services);
        await f.Directories.BindAsync(new DirectoryBinding(DirA, "acme", f.Clock.UtcNow, "op"), default);
        await f.Directories.BindAsync(new DirectoryBinding(DirB, "globex", f.Clock.UtcNow, "op"), default);
        if (setUp)
        {
            await f.SetUpOwnAppAsync("acme", DirA);
            await f.SetUpOwnAppAsync("globex", DirB);
        }
        return f;
    }

    /// <summary>A person as Microsoft describes them. <paramref name="authTime"/>: the optional auth_time ID-token claim (null: not sent).</summary>
    private static ClaimsPrincipal Person(string oid, string name, string role, string tid = DirA, DateTimeOffset? authTime = null)
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
        var start = await b.GetAsync("/");   // a signed-out visit goes straight to Microsoft
        var callback = await b.GetAsync(start.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return b;
    }

    private static Task<AdminBrowser> Developer(AdminSiteFixture f, string oid = Ada, string name = "Ada Lovelace", string tid = DirA) => SignIn(f, Person(oid, name, CustomerSignIn.DeveloperRole, tid, f.Clock.UtcNow));
    /// <summary>An admin who just signed in, with Microsoft's auth_time saying so.</summary>
    private static Task<AdminBrowser> Admin(AdminSiteFixture f, string oid = Grace, string name = "Grace Hopper", string tid = DirA) => SignIn(f, Person(oid, name, CustomerSignIn.AdminRole, tid, f.Clock.UtcNow));

    private static string? Nonce(string html) => Regex.Match(html, "name=\"nonce\" value=\"([^\"]+)\"") is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>A POST as the browser sends it from this site: the page's antiforgery field, the Origin header, and the fields.</summary>
    private static async Task<HttpResponseMessage> Post(AdminBrowser b, string page, string action, params (string Name, string Value)[] fields)
    {
        var html = await (await b.GetAsync(page)).Content.ReadAsStringAsync();
        return await PostWith(b, html, action, fields);
    }

    private static Task<HttpResponseMessage> PostWith(AdminBrowser b, string html, string action, params (string Name, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>> { new(SignedInSite.AntiforgeryField, AdminBrowser.Field(html) ?? throw new InvalidOperationException("no form on the page")) };
        form.AddRange(fields.Select(x => new KeyValuePair<string, string>(x.Name, x.Value)));
        return b.SendAsync(HttpMethod.Post, action, form, r => r.Headers.TryAddWithoutValidation("Origin", Origin));
    }

    private static async Task<(HttpResponseMessage Response, string Body, string? Token)> CreateMine(AdminBrowser b, string label)
    {
        var page = await (await b.GetAsync("/tokens")).Content.ReadAsStringAsync();
        var r = await PostWith(b, page, "/tokens/create", ("nonce", Nonce(page) ?? ""), ("label", label));
        var body = await r.Content.ReadAsStringAsync();
        return (r, body, TokenValue.Match(body) is { Success: true } m ? m.Value : null);
    }

    /// <summary>
    /// For a tenant that would refuse the issue: the page answers 200 with the refusal in the create form's place, and a POST made
    /// anyway (<see cref="HiddenTokenForm"/>) is still refused by the POST's own check. Returns the page, then the POST's answer.
    /// </summary>
    private static async Task<(string Page, HttpResponseMessage Response, string Body, string? Token)> CreateMineAnyway(AdminSiteFixture f, AdminBrowser b,
        string tid, string oid, string label)
    {
        var (status, page, _) = await b.GetTextAsync("/tokens");
        Assert.Equal(200, status);
        Assert.True(HiddenTokenForm.IsBlocked(page), "the create form is hidden");
        Assert.Null(Nonce(page));
        var r = await PostWith(b, page, "/tokens/create", ("nonce", await HiddenTokenForm.StoreNonceAsync(f.App.Services, tid, oid)), ("label", label));
        var body = await r.Content.ReadAsStringAsync();
        return (page, r, body, TokenValue.Match(body) is { Success: true } m ? m.Value : null);
    }

    private static async Task<(HttpResponseMessage Response, string Body, string? Token)> CreateAutomation(AdminBrowser b, string label, string days)
    {
        var page = await (await b.GetAsync("/team")).Content.ReadAsStringAsync();
        var r = await PostWith(b, page, "/team/create", ("nonce", Nonce(page) ?? ""), ("label", label), ("days", days));
        var body = await r.Content.ReadAsStringAsync();
        return (r, body, TokenValue.Match(body) is { Success: true } m ? m.Value : null);
    }

    private static string Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : r.Content.Headers.TryGetValues(name, out var c) ? string.Join(",", c) : "";

    private static string Where(HttpResponseMessage r) => r.Headers.Location?.OriginalString ?? "";

    // ---- the developer page

    [Fact]
    public async Task A_developer_creates_lists_and_revokes_their_own_token_and_it_is_shown_once()
    {
        await using var f = await StartAsync();
        var b = await Developer(f);
        var home = (await b.GetTextAsync("/home")).Body;
        Assert.Contains("href=\"/tokens\"", home);
        Assert.DoesNotContain("href=\"/team\"", home);
        Assert.Contains("You have no tokens yet.", (await b.GetTextAsync("/tokens")).Body);

        var (r, body, token) = await CreateMine(b, "work laptop");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotNull(token);
        Assert.Contains("no-store", Header(r, "Cache-Control"));
        Assert.Contains($"dotnet nuget add source {FeedUrl} -n packmoat -u packmoat -p {token} --store-password-in-clear-text", body);
        Assert.Contains("NU1102", body);
        Assert.Contains("newest version that has passed your cooldown", body);
        Assert.Contains("Package search in Visual Studio isn't supported", body);
        Assert.Contains("This is the only time Packmoat shows it", body);
        var auth = await f.Tokens.AuthenticateAsync(token!, default);
        Assert.Equal("acme", auth!.Tenant.Id);
        Assert.Equal(f.Clock.UtcNow.AddDays(90), auth.TokenExpiresAt);

        // never again: the list shows the name, never the value
        var (status, list, again) = await b.GetTextAsync("/tokens");
        Assert.Equal(200, status);
        Assert.Contains("work laptop", list);
        Assert.DoesNotContain(token!, list);
        Assert.DoesNotContain("pmt_", list.Replace("pmt_0000", ""), StringComparison.Ordinal);
        Assert.DoesNotContain(token!, Where(r));
        Assert.Contains("<span class=\"status active\">Active</span>", list);

        // revoke, with a confirm step
        var info = Assert.Single(await f.Tokens.ListAsync("acme", default));
        Assert.Equal((TokenIssuer.Self, Ada, "Ada Lovelace"), (info.IssuedVia, info.OwnerOid, info.OwnerName));
        var confirm = (await b.GetTextAsync($"/tokens/revoke?token={info.TokenId}")).Body;
        Assert.Contains("Revoke work laptop?", confirm);
        Assert.NotNull(await f.Tokens.AuthenticateAsync(token!, default));   // the confirm page changes nothing
        var done = await PostWith(b, confirm, "/tokens/revoke", ("token", info.TokenId));
        Assert.Equal(HttpStatusCode.SeeOther, done.StatusCode);
        Assert.Equal("/tokens?m=revoked", Where(done));
        Assert.Null(await f.Tokens.AuthenticateAsync(token!, default));
        Assert.Contains("<span class=\"status revoked\">Revoked</span>", (await b.GetTextAsync("/tokens?m=revoked")).Body);

        var trail = (await f.Actions.LatestAsync(10, default)).Reverse().ToList();
        Assert.Equal(new[] { "token.issue", "token.issue.id", "token.revoke" }, trail.Select(x => x.Action));
        Assert.All(trail, x => { Assert.Equal("app", x.Site); Assert.Equal(Ada, x.ActorOid); Assert.Equal(DirA, x.ActorTid); Assert.DoesNotContain(token!, x.Detail); });
    }

    [Fact]
    public async Task A_used_create_form_cannot_make_a_second_token()
    {
        await using var f = await StartAsync();
        var b = await Developer(f);
        var page = await (await b.GetAsync("/tokens")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, (await PostWith(b, page, "/tokens/create", ("nonce", Nonce(page)!), ("label", "one"))).StatusCode);
        var again = await PostWith(b, page, "/tokens/create", ("nonce", Nonce(page)!), ("label", "one"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Single(await f.Tokens.ListAsync("acme", default));
    }

    [Fact]
    public async Task Writes_need_the_sites_own_origin_and_the_form_token()
    {
        await using var f = await StartAsync();
        var b = await Developer(f);
        var page = await (await b.GetAsync("/tokens")).Content.ReadAsStringAsync();
        var form = new[] { new KeyValuePair<string, string>(SignedInSite.AntiforgeryField, AdminBrowser.Field(page)!), new("nonce", Nonce(page)!), new("label", "x") };
        Assert.Equal(HttpStatusCode.Forbidden, (await b.SendAsync(HttpMethod.Post, "/tokens/create", form)).StatusCode);   // no Origin
        Assert.Equal(HttpStatusCode.Forbidden, (await b.SendAsync(HttpMethod.Post, "/tokens/create", form, r => r.Headers.TryAddWithoutValidation("Origin", "https://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.SendAsync(HttpMethod.Post, "/tokens/create", form.Skip(1), r => r.Headers.TryAddWithoutValidation("Origin", Origin))).StatusCode);   // no antiforgery
        Assert.Empty(await f.Tokens.ListAsync("acme", default));
    }

    [Fact]
    public async Task A_developer_cannot_see_or_revoke_someone_elses_token_or_use_the_team_page()
    {
        await using var f = await StartAsync();
        var ada = await Developer(f);
        var (_, _, adaToken) = await CreateMine(ada, "ada laptop");
        var adaId = Assert.Single(await f.Tokens.ListAsync("acme", default)).TokenId;

        var bob = await Developer(f, Bob, "Bob");
        var list = (await bob.GetTextAsync("/tokens")).Body;
        Assert.DoesNotContain("ada laptop", list);
        Assert.DoesNotContain(adaId, list);
        Assert.Equal("/tokens?m=gone", Where(await bob.GetAsync($"/tokens/revoke?token={adaId}")));
        var r = await Post(bob, "/tokens", "/tokens/revoke", ("token", adaId));
        Assert.Equal("/tokens?m=gone", Where(r));
        Assert.NotNull(await f.Tokens.AuthenticateAsync(adaToken!, default));

        var team = await bob.GetTextAsync("/team");
        Assert.Equal(403, team.Status);
        Assert.Contains("Only a Packmoat admin in your organisation can do this.", team.Body);
        Assert.DoesNotContain("ada laptop", team.Body);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(bob, "/tokens", "/team/revoke", ("token", adaId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(bob, "/tokens", "/team/create", ("label", "ci"), ("days", "30"))).StatusCode);
        Assert.NotNull(await f.Tokens.AuthenticateAsync(adaToken!, default));
        Assert.Single(await f.Tokens.ListAsync("acme", default));
    }

    [Fact]
    public async Task Customer_a_cannot_touch_tenant_b()
    {
        await using var f = await StartAsync();
        var ada = await Developer(f);
        var (_, _, acmeToken) = await CreateMine(ada, "acme laptop");
        var acmeId = Assert.Single(await f.Tokens.ListAsync("acme", default)).TokenId;

        var globexAdmin = await Admin(f, "11111111-aaaa-4aaa-8aaa-111111111111", "Globex Admin", DirB);
        var team = (await globexAdmin.GetTextAsync("/team")).Body;
        Assert.DoesNotContain("acme laptop", team);
        Assert.Equal("/team?m=gone", Where(await globexAdmin.GetAsync($"/team/revoke?token={acmeId}")));
        Assert.Equal("/team?m=gone", Where(await Post(globexAdmin, "/team", "/team/revoke", ("token", acmeId))));
        Assert.NotNull(await f.Tokens.AuthenticateAsync(acmeToken!, default));
        // and what the globex admin creates lands in globex only
        var (_, _, ci) = await CreateAutomation(globexAdmin, "globex ci", "30");
        Assert.Equal("globex", (await f.Tokens.AuthenticateAsync(ci!, default))!.Tenant.Id);
        Assert.Single(await f.Tokens.ListAsync("acme", default));
    }

    // ---- the admin page and the fresh sign-in

    [Fact]
    public async Task An_admin_sees_every_token_with_owner_and_last_seen_and_revokes_anyones()
    {
        await using var f = await StartAsync();
        var ada = await Developer(f);
        var (_, _, adaToken) = await CreateMine(ada, "ada laptop");
        var operatorIssued = await f.Tokens.IssueAsync("acme", "legacy agent", default, TokenKind.Automation);
        f.Clock.Advance(TimeSpan.FromMinutes(1));

        var grace = await Admin(f);
        var home = (await grace.GetTextAsync("/home")).Body;
        Assert.Contains("href=\"/team\"", home);
        var (status, team, r) = await grace.GetTextAsync("/team");
        Assert.Equal(200, status);
        Assert.Contains("<td>Ada Lovelace</td><td>10 Oct 2026</td><td>ada laptop</td><td>Developer</td>", team);
        Assert.Contains("<td>Issued by Packmoat</td><td></td><td>legacy agent</td><td>Automation</td>", team);
        Assert.Contains("action=\"/team/create\"", team);
        Assert.Equal(SignedInSite.ContentSecurityPolicy, Header(r, "Content-Security-Policy"));

        var adaId = (await f.Tokens.ListAsync("acme", default)).Single(t => t.Label == "ada laptop").TokenId;
        var confirm = (await grace.GetTextAsync($"/team/revoke?token={adaId}")).Body;
        Assert.Contains("This token belongs to Ada Lovelace", confirm);
        Assert.Equal("/team?m=revoked", Where(await PostWith(grace, confirm, "/team/revoke", ("token", adaId))));
        Assert.Null(await f.Tokens.AuthenticateAsync(adaToken!, default));
        Assert.Equal("/team?m=revoked", Where(await Post(grace, "/team", "/team/revoke", ("token", operatorIssued.TokenId))));
        Assert.Null(await f.Tokens.AuthenticateAsync(operatorIssued.Plaintext, default));
        Assert.All(await f.Tokens.ListAsync("acme", default), t => Assert.Equal($"Grace Hopper ({Grace})", t.RevokedBy));
    }

    [Fact]
    public async Task An_admin_creates_an_automation_token_shown_once_with_a_chosen_expiry()
    {
        await using var f = await StartAsync();
        var grace = await Admin(f);
        var (r, body, token) = await CreateAutomation(grace, "build agents", "365");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotNull(token);
        Assert.Contains("no-store", Header(r, "Cache-Control"));
        var auth = await f.Tokens.AuthenticateAsync(token!, default);
        Assert.Equal(f.Clock.UtcNow.AddDays(365), auth!.TokenExpiresAt);
        var info = Assert.Single(await f.Tokens.ListAsync("acme", default));
        Assert.Equal((TokenKind.Automation, Grace), (info.Kind, info.OwnerOid));
        Assert.DoesNotContain(token!, (await grace.GetTextAsync("/team")).Body);

        foreach (var bad in new[] { "0", "366", "-1", "1e3", "", "abc" })
        {
            var (rr, rb, rt) = await CreateAutomation(grace, "x", bad);
            Assert.Equal(HttpStatusCode.BadRequest, rr.StatusCode);
            Assert.Contains("An automation token lasts 1 to 365 days.", rb);
            Assert.Null(rt);
        }
        Assert.Single(await f.Tokens.ListAsync("acme", default));
    }

    [Fact]
    public async Task Admin_writes_need_a_fresh_sign_in_and_the_role_is_decided_again_from_it()
    {
        await using var f = await StartAsync();
        var grace = await Admin(f);
        var operatorIssued = await f.Tokens.IssueAsync("acme", "legacy", default);
        f.Clock.Advance(TimeSpan.FromMinutes(16));   // the session is still valid (30-minute sliding window), but not fresh

        var (status, team, r) = await grace.GetTextAsync("/team");
        Assert.Equal(200, status);
        Assert.DoesNotContain("action=\"/team/create\"", team);
        Assert.Contains("Sign in again to make changes.", team);
        Assert.Contains("action=\"/reauth\"", team);
        Assert.Equal(SignedInSite.LandingContentSecurityPolicy, Header(r, "Content-Security-Policy"));   // the form leads to Microsoft

        var create = await Post(grace, "/team", "/team/create", ("nonce", "x"), ("label", "ci"), ("days", "30"));
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Contains("Sign in again first", await create.Content.ReadAsStringAsync());
        Assert.Equal(SignedInSite.LandingContentSecurityPolicy, Header(create, "Content-Security-Policy"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(grace, "/team", "/team/revoke", ("token", operatorIssued.TokenId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await grace.GetAsync($"/team/revoke?token={operatorIssued.TokenId}")).StatusCode);
        Assert.NotNull(await f.Tokens.AuthenticateAsync(operatorIssued.Plaintext, default));
        Assert.Single(await f.Tokens.ListAsync("acme", default));
        // her own developer token does not need it
        Assert.NotNull((await CreateMine(grace, "grace laptop")).Token);

        // the fresh sign-in: Microsoft is asked to make her sign in again
        f.AppIdentity.Next = Person(Grace, "Grace Hopper", CustomerSignIn.AdminRole, authTime: f.Clock.UtcNow);
        var start = await Post(grace, "/team", "/reauth");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var props = Assert.IsType<OpenIdConnectChallengeProperties>(f.AppIdentity.LastChallenge);
        Assert.Equal("login", props.Prompt);
        Assert.Equal(TimeSpan.FromMinutes(15), props.MaxAge);
        Assert.Equal("/team?m=reauth", props.RedirectUri);
        Assert.Equal(HttpStatusCode.Redirect, (await grace.GetAsync(Where(start))).StatusCode);
        Assert.Contains("action=\"/team/create\"", (await grace.GetTextAsync("/team")).Body);
        Assert.NotNull((await CreateAutomation(grace, "ci", "30")).Token);
        Assert.Equal("/team?m=revoked", Where(await Post(grace, "/team", "/team/revoke", ("token", operatorIssued.TokenId))));

        // demoted in Entra meanwhile: the fresh sign-in decides the role again, and the admin writes are refused
        f.Clock.Advance(TimeSpan.FromMinutes(16));
        f.AppIdentity.Next = Person(Grace, "Grace Hopper", CustomerSignIn.DeveloperRole, authTime: f.Clock.UtcNow);
        var again = await Post(grace, "/team", "/reauth");
        Assert.Equal(HttpStatusCode.Redirect, (await grace.GetAsync(Where(again))).StatusCode);
        Assert.Equal(403, (await grace.GetTextAsync("/team")).Status);
        var refused = await Post(grace, "/tokens", "/team/create", ("nonce", "x"), ("label", "ci2"), ("days", "30"));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("Admins only", await refused.Content.ReadAsStringAsync());
        Assert.Equal(3, (await f.Tokens.ListAsync("acme", default)).Count);
    }

    [Fact]
    public async Task Without_auth_time_from_microsoft_an_admin_session_is_never_fresh()
    {
        await using var f = await StartAsync();
        var op = await f.Tokens.IssueAsync("acme", "legacy", default);
        var grace = await SignIn(f, Person(Grace, "Grace Hopper", CustomerSignIn.AdminRole));   // no auth_time claim at all
        var (_, team, r) = await grace.GetTextAsync("/team");
        Assert.Contains("Sign in again to make changes.", team);
        Assert.DoesNotContain("action=\"/team/create\"", team);
        Assert.Equal(SignedInSite.LandingContentSecurityPolicy, Header(r, "Content-Security-Policy"));
        var refused = await Post(grace, "/team", "/team/create", ("nonce", "x"), ("label", "ci"), ("days", "30"));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("email " + TestOperator.SupportEmail + ": Microsoft may not be sending the sign-in time.", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(grace, "/team", "/team/revoke", ("token", op.TokenId))).StatusCode);
        Assert.NotNull(await f.Tokens.AuthenticateAsync(op.Plaintext, default));

        // and "Sign in again" does not help while Microsoft still sends no auth_time: the page says to email support
        f.AppIdentity.Next = Person(Grace, "Grace Hopper", CustomerSignIn.AdminRole);
        var start = await Post(grace, "/team", "/reauth");
        Assert.Equal(HttpStatusCode.Redirect, (await grace.GetAsync(Where(start))).StatusCode);
        var after = (await grace.GetTextAsync("/team?m=reauth")).Body;
        Assert.Contains("Microsoft didn&#x27;t confirm a recent sign-in, so admin changes stay locked.", after.Replace("didn't", "didn&#x27;t"));
        Assert.Contains($"Email {TestOperator.SupportEmail} to check the sign-in setup: Packmoat needs Microsoft to send the sign-in time.", after);
        Assert.DoesNotContain("action=\"/team/create\"", after);
        Assert.Single(await f.Tokens.ListAsync("acme", default));
    }

    [Fact]
    public async Task A_silent_single_sign_on_with_an_old_auth_time_is_not_fresh_even_right_after_the_callback()
    {
        await using var f = await StartAsync();
        // Microsoft completed the sign-in just now from a session started two hours ago (no password typed): auth_time is two hours old.
        var grace = await SignIn(f, Person(Grace, "Grace Hopper", CustomerSignIn.AdminRole, authTime: f.Clock.UtcNow.AddHours(-2)));
        Assert.Contains("Sign in again to make changes.", (await grace.GetTextAsync("/team")).Body);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(grace, "/team", "/team/create", ("nonce", "x"), ("label", "ci"), ("days", "30"))).StatusCode);

        // the fresh sign-in also counts only when Microsoft's auth_time is within 15 minutes of the callback
        f.AppIdentity.Next = Person(Grace, "Grace Hopper", CustomerSignIn.AdminRole, authTime: f.Clock.UtcNow.AddMinutes(-16));
        var start = await Post(grace, "/team", "/reauth");
        await grace.GetAsync(Where(start));
        Assert.Contains("Microsoft didn", (await grace.GetTextAsync("/team?m=reauth")).Body);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(grace, "/team", "/team/create", ("nonce", "x"), ("label", "ci"), ("days", "30"))).StatusCode);

        f.AppIdentity.Next = Person(Grace, "Grace Hopper", CustomerSignIn.AdminRole, authTime: f.Clock.UtcNow.AddMinutes(-1));
        start = await Post(grace, "/team", "/reauth");
        await grace.GetAsync(Where(start));
        var fresh = (await grace.GetTextAsync("/team?m=reauth")).Body;
        Assert.DoesNotContain("Microsoft didn", fresh);
        Assert.Contains("action=\"/team/create\"", fresh);
        Assert.NotNull((await CreateAutomation(grace, "ci", "30")).Token);
    }

    [Fact]
    public async Task An_auth_time_in_the_future_is_ignored()
    {
        await using var f = await StartAsync();
        var grace = await SignIn(f, Person(Grace, "Grace Hopper", CustomerSignIn.AdminRole, authTime: f.Clock.UtcNow.AddHours(3)));
        Assert.Contains("Sign in again to make changes.", (await grace.GetTextAsync("/team")).Body);
        // not stored at all, so it can never become "fresh" later
        var format = f.App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>().Get(AppSite.CookieScheme).TicketDataFormat;
        Assert.Null(format.Unprotect(grace.Cookie(AppSite.CookieName))!.Principal.FindFirst(AppSite.AuthTimeClaim));
    }

    [Fact]
    public async Task Each_site_has_its_own_nonce_store_and_one_key_cannot_evict_another()
    {
        await using var f = await StartAsync();
        var app = f.App.Services.GetRequiredService<AppFormNonces>();
        var admin = f.App.Services.GetRequiredService<Packmoat.Host.Admin.AdminFormNonces>();
        Assert.NotSame((object)app, admin);
        Assert.Equal((Packmoat.Application.FormNonceScopes.App, Packmoat.Application.FormNonceScopes.Admin), (app.Scope, admin.Scope));
        var operatorNonce = await admin.IssueAsync("acme", default);
        for (var i = 0; i < 3 * FormNonces.MaxPerKey + 10; i++) await app.IssueAsync("someone/" + (i % 3), default);   // customers opening forms over and over
        Assert.False(await app.ConsumeAsync(operatorNonce, "acme", default));   // the admin site's nonce is not the customer site's to use
        Assert.True(await admin.ConsumeAsync(operatorNonce, "acme", default));

        // within one site, a key that opens forms again and again only replaces its own oldest nonces
        var other = await app.IssueAsync("quiet/person", default);
        for (var i = 0; i < 2 * FormNonces.MaxPerKey + 5; i++) await app.IssueAsync("busy/person", default);
        Assert.True(await app.ConsumeAsync(other, "quiet/person", default));
    }

    // ---- refusals, limits, escaping

    [Fact]
    public async Task A_shared_app_tenant_without_a_passing_check_shows_not_ready_yet()
    {
        await using var f = await StartAsync();
        await f.Tenants.BindSharedIdentityAsync("acme", DirA, default);
        var b = await Developer(f);
        var (page, r, body, token) = await CreateMineAnyway(f, b, DirA, Ada, "laptop");
        Assert.Contains("<p><strong>Not ready yet</strong></p>", page);   // the page says why, in the refusal's own words
        Assert.Contains("hasn&#x27;t finished checking the connection", page);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("Not ready yet", body);
        Assert.Contains("hasn&#x27;t finished checking the connection", body);
        Assert.Null(token);
        Assert.Empty(await f.Tokens.ListAsync("acme", default));
    }

    [Fact]
    public async Task A_tenant_with_no_upstream_shows_not_connected_yet_and_issues_nothing()
    {
        await using var f = await StartAsync(setUp: false);
        await f.UseStrictFeedUrlsAsync("acme");   // good feed URLs, but no app bound to reach the feed
        foreach (var (b, oid) in new[] { (await Developer(f), Ada), (await Admin(f), Grace) })
        {
            var (page, r, body, token) = await CreateMineAnyway(f, b, DirA, oid, "laptop");
            Assert.Contains("This organisation isn&#x27;t connected to an Azure Artifacts feed yet.", page);
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
            Assert.Contains("Not connected yet", body);
            Assert.Contains("This organisation isn&#x27;t connected to an Azure Artifacts feed yet.", body);
            Assert.Null(token);
        }
        Assert.Empty(await f.Tokens.ListAsync("acme", default));
        Assert.DoesNotContain(await f.Actions.LatestAsync(50, default), a => a.Action.StartsWith("token.issue", StringComparison.Ordinal));
    }

    [Fact]   // mutations: drop the pages' check (the forms show); check only ServingRefused (the forms show); remove the POST guard (a token is issued)
    public async Task A_not_set_up_tenants_token_pages_show_why_in_place_of_the_create_forms_and_still_list_tokens_to_revoke()
    {
        await using var f = await StartAsync(setUp: false);
        await f.UseStrictFeedUrlsAsync("acme");   // good feed URLs, but no app bound to reach the feed
        // tokens from when it was set up: still listed, so they can be revoked
        await f.Tokens.IssueForMemberAsync("acme", DirA, Grace, "old laptop", f.Clock.UtcNow.AddDays(30), TokenKind.Developer, 5, default);
        await f.Tokens.IssueForMemberAsync("acme", DirA, Grace, "old ci", f.Clock.UtcNow.AddDays(30), TokenKind.Automation, 5, default);
        var grace = await Admin(f);

        var (page, r, body, token) = await CreateMineAnyway(f, grace, DirA, Grace, "laptop");
        const string Words = "<p><strong>Not connected yet</strong></p><p>This organisation isn&#x27;t connected to an Azure Artifacts feed yet.</p>" +
            "<p class=\"next\"><strong>What to do next</strong>Tokens can be created once the feed is connected.";
        Assert.Contains(Words, page);
        Assert.Contains("old laptop", page);
        Assert.Contains("/tokens/revoke?token=", page);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);   // the POST still refuses, in the same words
        Assert.Contains("<h1>Not connected yet</h1><p>This organisation isn&#x27;t connected to an Azure Artifacts feed yet.</p>", body);
        Assert.Null(token);

        var (teamStatus, team, _) = await grace.GetTextAsync("/team");
        Assert.Equal(200, teamStatus);
        Assert.True(HiddenTokenForm.IsBlocked(team, "/team/create"), "the automation form is hidden");
        Assert.Null(Nonce(team));
        Assert.Contains(Words, team);
        Assert.Contains("old ci", team);
        Assert.Contains("/team/revoke?token=", team);
        var auto = await PostWith(grace, team, "/team/create", ("nonce", await HiddenTokenForm.StoreNonceAsync(f.App.Services, DirA, Grace)), ("label", "ci"), ("days", "30"));
        Assert.Equal(HttpStatusCode.Conflict, auto.StatusCode);
        Assert.DoesNotMatch(TokenValue, await auto.Content.ReadAsStringAsync());
        Assert.Equal(2, (await f.Tokens.ListAsync("acme", default)).Count);
    }

    [Fact]   // mutation: show the blocker on /team whatever the sign-in's age (drop `fresh &&`)
    public async Task A_stale_admin_on_a_blocked_tenant_gets_the_sign_in_again_form_on_team_not_the_refusal()
    {
        await using var f = await StartAsync(setUp: false);
        await f.UseStrictFeedUrlsAsync("acme");   // not set up: the POST would refuse with "Not connected yet"
        var grace = await Admin(f);
        f.Clock.Advance(TimeSpan.FromMinutes(16));   // the session is still valid, but not fresh: the POST refuses a stale admin first
        var (status, team, _) = await grace.GetTextAsync("/team");
        Assert.Equal(200, status);
        Assert.Contains("Sign in again to make changes.", team);
        Assert.Contains("action=\"/reauth\"", team);
        Assert.DoesNotContain(HiddenTokenForm.BlockedMarker, team);
        Assert.DoesNotContain("Not connected yet", team);
        Assert.DoesNotContain("action=\"/team/create\"", team);
    }

    /// <summary>A gate that refuses every tenant, with a notice of its own.</summary>
    private sealed record MarkupNotice : ServingNotice;

    private sealed class RefusingGate : IServingGate
    {
        public Task<ServingAnswer> AskAsync(string tenantId, CancellationToken ct) => Task.FromResult<ServingAnswer>(new ServingAnswer.Refuse(new MarkupNotice()));
    }

    /// <summary>A customer-site module that words the gate's refusal with markup in every field, which the page must encode.</summary>
    private sealed class MarkupWordsModule : Packmoat.Host.Modules.ICustomerSiteModule
    {
        public bool OffersSetup(string directoryId) => false;
        public Packmoat.Host.Modules.NotLinkedCard NotLinkedCard(string directoryId, bool needsAdmin) => new("x", "x");
        public IReadOnlyList<Packmoat.Host.Modules.CustomerRoute<CustomerActor>> MemberRoutes => [];
        public IReadOnlyList<Packmoat.Host.Modules.CustomerRoute<Prospect>> ProspectRoutes => [];
        public string HomeBanner(CustomerActor actor, CustomerHome view) => "";
        public string HomeLinks(CustomerActor actor, CustomerHome view) => "";
        public string ConnectSentence(CustomerActor actor) => "";
        public Packmoat.Host.Modules.ServingRefusedWords? ServingRefused(ServingNotice? notice, SupportContact support) => notice is MarkupNotice
            ? new("<b>Title</b>", "<i>Message</i>", "<script>next()</script>")
            : null;
    }

    [Fact]   // mutation: drop E() from the title, the message or the next step in Blocked()
    public async Task The_refusal_in_place_of_the_create_form_encodes_every_field()
    {
        await using var f = await StartAsync(s =>
        {
            s.AddSingleton<IServingGate>(new RefusingGate());
            s.AddSingleton<Packmoat.Host.Modules.ICustomerSiteModule>(new MarkupWordsModule());
        });
        var grace = await Admin(f);
        foreach (var (path, action) in new[] { ("/tokens", "/tokens/create"), ("/team", "/team/create") })
        {
            var (status, page, _) = await grace.GetTextAsync(path);
            Assert.Equal(200, status);
            Assert.True(HiddenTokenForm.IsBlocked(page, action), path);
            Assert.Contains("<p><strong>&lt;b&gt;Title&lt;/b&gt;</strong></p><p>&lt;i&gt;Message&lt;/i&gt;</p>" +
                "<p class=\"next\"><strong>What to do next</strong>&lt;script&gt;next()&lt;/script&gt;</p>", page);
            Assert.DoesNotContain("<b>Title</b>", page);
            Assert.DoesNotContain("<i>Message</i>", page);
            Assert.DoesNotContain("<script>next()</script>", page);
        }
    }

    [Fact]
    public async Task An_own_app_operator_tenant_with_an_http_feed_still_issues_tokens()
    {
        await using var f = await StartAsync(setUp: false);   // acme keeps the fake upstream's http URLs, as the CLI allows for own-app tenants
        await f.Tenants.SetUpstreamIdentityAsync("acme", new UpstreamIdentity(DirA, "99999999-9999-4999-8999-999999999999", "-----BEGIN PRIVATE KEY-----\nTEST\n-----END PRIVATE KEY-----"), default);   // gitleaks:allow (the body is the word TEST)
        var (r, _, token) = await CreateMine(await Developer(f), "laptop");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotNull(token);
    }

    [Fact]
    public async Task A_serving_self_made_tenant_with_no_upstream_is_refused_as_not_connected_yet()
    {
        const string DirX = "12121212-1212-4212-8212-121212121212";
        await using var f = await StartAsync();
        // as self sign-up leaves it: no feed URLs, no upstream binding, its directory bound. The core serves every tenant (the hosted
        // module's gate is the hosted tests'), so only the upstream refuses.
        await f.Tenants.CreateAsync(new NewTenant("orgx", "Orgx", f.Upstream.BaseUrl, "org/", f.Upstream.FeedIndexUrl, 14, TenantOrigin.Self) { CreatedByOid = Grace }, default);
        await using (var c = await f.Db.OpenAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE tenants SET upstream_base='', allowed_path_prefix='', feed_index_url='' WHERE id='orgx'";
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }
        await f.Directories.BindAsync(new DirectoryBinding(DirX, "orgx", f.Clock.UtcNow, "self-signup (" + Grace + ")"), default);
        var b = await Admin(f, tid: DirX);
        var (page, r, body, token) = await CreateMineAnyway(f, b, DirX, Grace, "laptop");
        Assert.Contains("This organisation isn&#x27;t connected to an Azure Artifacts feed yet.", page);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("This organisation isn&#x27;t connected to an Azure Artifacts feed yet.", body);
        Assert.Null(token);
        Assert.Empty(await f.Tokens.ListAsync("orgx", default));
        // the set-up tenant next door still issues
        Assert.NotNull((await CreateMine(await Developer(f), "laptop")).Token);
    }

    [Fact]
    public async Task A_shared_operator_tenant_with_a_passing_check_issues_tokens()
    {
        const string SharedClient = "bbbbbbbb-0000-4000-8000-000000000000";
        Packmoat.Infrastructure.SqliteSharedAppCredentialStore? shared = null;
        await using var f = await StartAsync(s => s.AddSingleton<ISharedAppCredentialStore>(sp => shared!));
        shared = new Packmoat.Infrastructure.SqliteSharedAppCredentialStore(f.Db, f.Clock, Packmoat.Infrastructure.AesGcmSecretProtector.FromBase64(f.MasterKey));
        await f.Tenants.BindSharedIdentityAsync("acme", DirA, default);   // like e2e-shared
        await shared.SetAsync(SharedAppSlot.Active, new SharedAppCredential(SharedClient, "-----BEGIN PRIVATE KEY-----\nTEST\n-----END PRIVATE KEY-----"), default);
        var b = await Developer(f);
        Assert.Contains("Not ready yet", (await CreateMineAnyway(f, b, DirA, Ada, "before")).Body);   // bound but not checked

        Assert.True(await shared.RecordVerificationAsync(new SharedAppVerification("acme", DirA, AdminSiteFixture.StrictFeed, SharedClient, f.Clock.UtcNow, "onboard check"), default));
        var (r, _, token) = await CreateMine(b, "laptop");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotNull(token);
        Assert.Single(await f.Tokens.ListAsync("acme", default));
    }

    [Fact]
    public async Task The_cap_and_the_hourly_limit_show_fixed_messages()
    {
        await using var f = await StartAsync();
        var b = await Developer(f);
        for (var i = 0; i < 5; i++) Assert.NotNull((await CreateMine(b, "t" + i)).Token);
        var full = (await b.GetTextAsync("/tokens")).Body;
        Assert.Contains("You have 5 active tokens", full);
        Assert.DoesNotContain("action=\"/tokens/create\"", full);
        foreach (var t in await f.Tokens.ListAsync("acme", default)) await f.Tokens.RevokeAsync(t.TokenId, default);   // operator revokes: not counted against her

        for (var i = 5; i < 10; i++) Assert.NotNull((await CreateMine(b, "t" + i)).Token);
        foreach (var t in await f.Tokens.ListAsync("acme", default)) await f.Tokens.RevokeAsync(t.TokenId, default);
        var (r, body, token) = await CreateMine(b, "eleventh");
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        Assert.Contains("Too many token changes from your account in the last hour.", body);
        Assert.Null(token);
    }

    [Fact]
    public async Task Labels_are_encoded_on_the_page_and_quoted_last_in_the_trail()
    {
        await using var f = await StartAsync();
        var b = await Developer(f);
        const string label = "<b>x</b>\"; kind=Automation; owner=operator";
        var (_, created, token) = await CreateMine(b, label);
        Assert.NotNull(token);
        Assert.DoesNotContain("<b>x</b>", created);
        var list = (await b.GetTextAsync("/tokens")).Body;
        Assert.DoesNotContain("<b>x</b>", list);
        Assert.Contains("&lt;b&gt;x&lt;/b&gt;", list);
        var issue = (await f.Actions.LatestAsync(10, default)).Single(x => x.Action == "token.issue").Detail;
        Assert.EndsWith("; label=\"<b>x</b>\\\"; kind=Automation; owner=operator\"", issue);
        Assert.StartsWith("kind=Developer; expires=", issue);

        var (bad, badBody, _) = await CreateMine(b, "two\nlines");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("Check the name", badBody);
    }

    [Fact]
    public async Task Sign_in_records_the_person_and_unlinking_the_directory_revokes_their_tokens()
    {
        await using var f = await StartAsync();
        var b = await Developer(f);
        var m = await f.Members.GetAsync(DirA, Ada, default);
        Assert.Equal(("acme", "Ada Lovelace", f.Clock.UtcNow), (m!.TenantId, m.DisplayName, m.LastSeenAt));
        var (_, _, token) = await CreateMine(b, "laptop");
        var operatorIssued = await f.Tokens.IssueAsync("acme", "op", default);

        var commands = new DirectoryBindingCommands(f.Tenants, f.Directories, f.Clock);
        Assert.Equal(1, await commands.UnbindAsync(ActorScope.OperatorAs("founder"), "acme", DirA, default));
        Assert.Null(await f.Tokens.AuthenticateAsync(token!, default));
        Assert.NotNull(await f.Tokens.AuthenticateAsync(operatorIssued.Plaintext, default));
        Assert.Equal("operator founder (directory unlinked)", (await f.Tokens.ListAsync("acme", default)).Single(t => t.OwnerOid == Ada).RevokedBy);
    }

    [Fact]
    public async Task The_admin_sites_tenant_page_shows_owner_expiry_and_issued_via()
    {
        await using var f = await StartAsync();
        await CreateMine(await Developer(f), "ada laptop");
        await f.Tokens.IssueAsync("acme", "legacy", default);
        var admin = f.Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await admin.SignInAsync()).StatusCode);
        var page = (await admin.GetTextAsync("/tenants/acme")).Body;
        Assert.Contains("<th>Owner</th>", page.Replace(" scope=\"col\"", ""));
        Assert.Contains($"Ada Lovelace ({Ada})", page);
        Assert.Contains("self (customer site)", page);
        Assert.Contains("2027-01-08 12:00:00Z", page);   // 90 days after the fixture clock
        Assert.Contains("never", page);
        Assert.Contains("operator", page);
    }

    // ---- the pages stay self-contained, and no token reaches a log

    private static readonly string[] AllowedOtherOrigin = { TestOperator.MarketingUrl, TestOperator.PrivacyUrl, TestOperator.EarlyAccessUrl, "mailto:" + TestOperator.SupportEmail, FeedUrl };

    private static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Security-Policy", "X-Content-Type-Options", "Referrer-Policy", "Cache-Control", "Pragma", "Strict-Transport-Security",
        "X-Frame-Options", "X-Robots-Tag", "Content-Type", "Content-Length", "Transfer-Encoding", "Date", "Set-Cookie", "Retry-After", "Location",
        "Expires",   // sent with a sliding session renewal (the cookie middleware), as on every site page after half the window
    };

    private static void AssertSelfContained(string name, string html)
    {
        Assert.True(html.StartsWith("<!doctype html><html lang=\"en\">", StringComparison.Ordinal), name);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"(?i)\sstyle\s*=", html);
        Assert.DoesNotMatch(@"(?i)\son[a-z]+\s*=", html);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(html, @"(?i)\b(?:href|src|srcset|action|formaction|xlink:href|data|poster)\s*=\s*""([^""]*)"""))
        {
            var url = m.Groups[1].Value;
            Assert.True((url.StartsWith('/') && !url.StartsWith("//")) || url.StartsWith('#') || AllowedOtherOrigin.Contains(url), $"{name}: unexpected URL {url}");
        }
        foreach (Match m in Regex.Matches(html, @"(?i)(?:[a-z][a-z0-9+.-]*:)?//[^\s""'<>]+")) Assert.True(AllowedOtherOrigin.Contains(m.Value), $"{name}: {m.Value}");
        Assert.Contains("<link rel=\"stylesheet\" href=\"/app.css\">", html);
        Assert.Contains("<footer>", html);
    }

    [Fact]
    public async Task Every_token_page_keeps_the_csp_and_headers_and_loads_nothing_from_elsewhere_and_no_token_is_logged()
    {
        var logs = new CapturingLoggerProvider();
        await using var f = await StartAsync(s => s.AddSingleton<ILoggerProvider>(logs));
        var dev = await Developer(f);
        var pages = new List<(string Name, HttpResponseMessage R, string Csp)>();
        var tokens = new List<string>();

        pages.Add(("tokens", await dev.GetAsync("/tokens"), SignedInSite.ContentSecurityPolicy));
        var (created, _, t1) = await CreateMine(dev, "laptop");
        tokens.Add(t1!);
        pages.Add(("created", created, SignedInSite.ContentSecurityPolicy));
        var id = Assert.Single(await f.Tokens.ListAsync("acme", default)).TokenId;
        pages.Add(("confirm", await dev.GetAsync($"/tokens/revoke?token={id}"), SignedInSite.ContentSecurityPolicy));
        pages.Add(("bad label", (await CreateMine(dev, "")).Response, SignedInSite.ContentSecurityPolicy));
        pages.Add(("team as developer", await dev.GetAsync("/team"), SignedInSite.ContentSecurityPolicy));

        var admin = await Admin(f);
        pages.Add(("team", await admin.GetAsync("/team"), SignedInSite.ContentSecurityPolicy));
        var (auto, _, t2) = await CreateAutomation(admin, "ci", "30");
        tokens.Add(t2!);
        pages.Add(("automation created", auto, SignedInSite.ContentSecurityPolicy));
        pages.Add(("team confirm", await admin.GetAsync($"/team/revoke?token={id}"), SignedInSite.ContentSecurityPolicy));
        f.Clock.Advance(TimeSpan.FromMinutes(16));
        pages.Add(("team stale", await admin.GetAsync("/team"), SignedInSite.LandingContentSecurityPolicy));
        pages.Add(("fresh needed", await Post(admin, "/tokens", "/team/create", ("label", "x"), ("days", "3")), SignedInSite.LandingContentSecurityPolicy));
        await f.Tenants.SetActiveAsync("acme", false, default);
        pages.Add(("suspended", await dev.GetAsync("/tokens"), SignedInSite.ContentSecurityPolicy));

        var expected = new Dictionary<string, (int, string)>
        {
            ["tokens"] = (200, "Create my token"),
            ["created"] = (200, "Your token is ready"),
            ["confirm"] = (200, "Revoke laptop?"),
            ["bad label"] = (400, "Check the name"),
            ["team as developer"] = (403, "Admins only"),
            ["team"] = (200, "Create automation token"),
            ["automation created"] = (200, "Your token is ready"),
            ["team confirm"] = (200, "This token belongs to Ada Lovelace"),
            ["team stale"] = (200, "Sign in again to make changes."),
            ["fresh needed"] = (403, "Sign in again first"),
            ["suspended"] = (403, TestOperator.Name + " has paused this account, so feeds and tokens are off."),
        };
        foreach (var (name, r, csp) in pages)
        {
            var body = await r.Content.ReadAsStringAsync();
            Assert.True(expected[name].Item1 == (int)r.StatusCode, $"{name}: status {(int)r.StatusCode}");
            Assert.True(body.Contains(expected[name].Item2, StringComparison.Ordinal), $"{name}: missing {expected[name].Item2}");
            AssertSelfContained(name, body);
            Assert.Equal(csp, Header(r, "Content-Security-Policy"));
            Assert.Contains("no-store", Header(r, "Cache-Control"));
            foreach (var h in r.Headers.Select(x => x.Key).Concat(r.Content.Headers.Select(x => x.Key))) Assert.True(AllowedHeaders.Contains(h), $"{name}: header {h}");
        }

        string all;
        lock (logs.Lines) all = string.Join("\n", logs.Lines);
        foreach (var t in tokens) Assert.DoesNotContain(t, all);
        Assert.DoesNotContain("pmt_", all);
    }

    [Fact]
    public async Task The_stylesheet_still_loads_nothing()
    {
        await using var f = await StartAsync();
        var css = (await f.Browser(AppHost).GetTextAsync("/app.css")).Body;
        foreach (var banned in new[] { "url(", "@import", "//", "@font-face" }) Assert.DoesNotContain(banned, css, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("table.tokens", css);
    }
}
