using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Customer;
using Packmoat.Host.Web;

namespace Packmoat.Host.Tests;

/// <summary>Auto-join on the customer site, end to end through the fake Microsoft sign-in, and the real OIDC event.</summary>
[Collection("sqlite-pools")]
public class AutoJoinSiteTests
{
    private const string AppHost = CustomerSiteTests.AppHost;
    private const string AppClient = CustomerSiteTests.AppClient;
    private const string DirA = CustomerSiteTests.DirA;
    private const string DirX = CustomerSiteTests.DirX;
    private const string Ada = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    private const string Bob = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
    private const string Grace = "ffffffff-ffff-4fff-8fff-ffffffffffff";
    private const string Origin = "https://" + AppHost;

    // The wording of the auto-join pages, as the pages encode it.
    private static readonly string NoRoleText = AppHtml.E("Your organisation only lets in people with a Packmoat role. Ask your administrator to give you the Packmoat Developer role.");
    private static readonly string GuestText = AppHtml.E("Guest accounts need a Packmoat role to sign in. Ask the organisation's administrator to give you the Packmoat Developer role.");
    /// <summary>No usable acct claim (MemberStatusUnknown): the no-role wording.</summary>
    private static readonly string UnknownText = AppHtml.E("Your account has no Packmoat role. Ask your administrator to assign Packmoat.Admin or Packmoat.Developer.");
    private static readonly string OnText = AppHtml.E("Anyone in your organisation can sign in as a Developer without being given a role, and can create feed tokens that read your packages. Guests still need a Packmoat role.");
    private static readonly string OffText = AppHtml.E("Only people with a Packmoat role can sign in. Give people the Packmoat Developer role in Microsoft Entra.");
    private static readonly string BothText = AppHtml.E("Turning this off signs out people who joined this way. Tokens they made keep working until you revoke them below.");

    private static async Task<AdminSiteFixture> StartAsync(Action<IServiceCollection>? services = null)
    {
        var f = await AdminSiteFixture.StartAsync(o =>
        {
            o.AppHost = AppHost;
            o.AppClientId = AppClient;
            o.AppRemoteSchemeOverride = auth => auth.AddScheme<AuthenticationSchemeOptions, FakeAppEntraHandler>(AppSite.RemoteScheme, null);
        }, services: services);
        await f.Directories.BindAsync(new DirectoryBinding(DirA, "acme", f.Clock.UtcNow, "op"), default);
        return f;
    }

    /// <summary>A person as Microsoft describes them. <paramref name="acct"/>: the acct optional claim (null: not sent). No role unless given.</summary>
    private static ClaimsPrincipal Person(string oid, string name, string? acct = "0", string? role = null, string tid = DirA, DateTimeOffset? authTime = null, string? idp = null)
    {
        var claims = new List<Claim>
        {
            new("iss", $"https://login.microsoftonline.com/{tid}/v2.0"), new("aud", AppClient), new("tid", tid), new("oid", oid), new("name", name),
        };
        if (role is not null) claims.Add(new Claim("roles", role));
        if (acct is not null) claims.Add(new Claim("acct", acct, ClaimValueTypes.Integer32));
        if (idp is not null) claims.Add(new Claim("idp", idp));
        if (authTime is { } at) claims.Add(new Claim("auth_time", at.ToUnixTimeSeconds().ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "FakeEntra", "name", "roles"));
    }

    private static async Task<(AdminBrowser Browser, HttpResponseMessage Callback)> Callback(AdminSiteFixture f, ClaimsPrincipal person)
    {
        f.AppIdentity.Next = person;
        var b = f.Browser(AppHost);
        var start = await b.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        return (b, await b.GetAsync(start.Headers.Location!.OriginalString));
    }

    private static async Task<AdminBrowser> SignIn(AdminSiteFixture f, ClaimsPrincipal person)
    {
        var (b, callback) = await Callback(f, person);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.True(b.HasCookie(AppSite.CookieName));
        return b;
    }

    private static Task<AdminBrowser> Admin(AdminSiteFixture f) => SignIn(f, Person(Grace, "Grace Hopper", role: CustomerSignIn.AdminRole, authTime: f.Clock.UtcNow));

    private static Task<HttpResponseMessage> PostWith(AdminBrowser b, string html, string action, params (string Name, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>> { new(SignedInSite.AntiforgeryField, AdminBrowser.Field(html) ?? throw new InvalidOperationException("no form on the page")) };
        form.AddRange(fields.Select(x => new KeyValuePair<string, string>(x.Name, x.Value)));
        return b.SendAsync(HttpMethod.Post, action, form, r => r.Headers.TryAddWithoutValidation("Origin", Origin));
    }

    private static async Task<HttpResponseMessage> Toggle(AdminBrowser b, string enabled)
    {
        var page = await (await b.GetAsync("/team")).Content.ReadAsStringAsync();
        return await PostWith(b, page, "/team/auto-join", ("enabled", enabled));
    }

    private static async Task AssertSignedOutAsync(AdminBrowser b)
    {
        var r = await b.GetAsync("/home");
        Assert.DoesNotContain("Signed in as", await r.Content.ReadAsStringAsync());
        Assert.False(b.HasCookie(AppSite.CookieName));
    }

    private sealed class ThrowingAutoJoinStore : IAutoJoinSettingStore
    {
        public int Reads { get; private set; }
        public Task<bool> IsOnAsync(string tenantId, CancellationToken ct) { Reads++; throw new InvalidOperationException("setting unreadable"); }
        public Task SetAsync(string tenantId, bool enabled, DateTimeOffset at, string changedBy, CancellationToken ct) => throw new InvalidOperationException("setting unwritable");
    }

    /// <summary>An in-memory setting (no row: on) that can be made to fail, to show a failed read on a later click is never read as on.</summary>
    private sealed class FlakyAutoJoinStore : IAutoJoinSettingStore
    {
        public bool Fail { get; set; }
        public Task<bool> IsOnAsync(string tenantId, CancellationToken ct) => Fail ? throw new InvalidOperationException("setting unreadable") : Task.FromResult(true);
        public Task SetAsync(string tenantId, bool enabled, DateTimeOffset at, string changedBy, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_setting_that_cannot_be_read_on_a_later_click_shows_nothing_to_an_auto_joined_session()
    {
        var store = new FlakyAutoJoinStore();
        await using var f = await StartAsync(s => s.AddSingleton<IAutoJoinSettingStore>(store));
        var ada = await SignIn(f, Person(Ada, "Ada"));
        var bob = await SignIn(f, Person(Bob, "Bob", role: CustomerSignIn.DeveloperRole));
        store.Fail = true;
        var r = await ada.GetTextAsync("/home");
        Assert.Equal(500, r.Status);
        Assert.DoesNotContain("Signed in as", r.Body);
        Assert.Contains("Signed in as", (await bob.GetTextAsync("/home")).Body);   // a role session never reads it
    }

    // ---- sign-in

    [Fact]
    public async Task A_member_with_no_role_joins_as_developer_and_the_log_names_no_claim()
    {
        var logs = new CapturingLoggerProvider();
        await using var f = await StartAsync(s => s.AddSingleton<ILoggerProvider>(logs));
        var b = await SignIn(f, Person(Ada, "Ada Secretname"));
        var home = (await b.GetTextAsync("/home")).Body;
        Assert.Contains("<span class=\"role\">Developer</span>", home);
        Assert.DoesNotContain("href=\"/team\"", home);
        Assert.Equal(403, (await b.GetTextAsync("/team")).Status);   // never an Admin
        Assert.NotNull(await f.Members.GetAsync(DirA, Ada, default));   // remembered like any member

        string all;
        lock (logs.Lines) all = string.Join("\n", logs.Lines);
        Assert.Contains("Customer sign-in: auto-joined as Developer", all);
        foreach (var leak in new[] { "Secretname", DirA, Ada, "acct" }) Assert.DoesNotContain(leak, all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Without_the_acct_claim_nobody_joins_and_the_no_role_card_is_shown()
    {
        await using var f = await StartAsync();
        foreach (var person in new[] { Person(Ada, "Ada", acct: null), Person(Ada, "Ada", acct: "2") })
        {
            var (b, callback) = await Callback(f, person);
            Assert.Equal(HttpStatusCode.Forbidden, callback.StatusCode);
            var body = await callback.Content.ReadAsStringAsync();
            Assert.Contains("<h1>No Packmoat role</h1><p>" + UnknownText + "</p>", body);   // the no-role wording, not the guest card
            Assert.DoesNotContain(GuestText, body);
            Assert.False(b.HasCookie(AppSite.CookieName));
            Assert.DoesNotContain(b.SetCookieLog, c => c.StartsWith(AppSite.CookieName + "="));
        }
    }

    [Fact]
    public async Task A_missing_acct_and_a_guest_are_logged_with_different_reasons()
    {
        var logs = new CapturingLoggerProvider();
        await using var f = await StartAsync(s => s.AddSingleton<ILoggerProvider>(logs));
        await Callback(f, Person(Ada, "Ada", acct: null));
        string first;
        lock (logs.Lines) first = string.Join("\n", logs.Lines);
        Assert.Contains("Customer sign-in refused (MemberStatusUnknown)", first);
        Assert.DoesNotContain("(GuestNoRole)", first);
        await Callback(f, Person(Bob, "Bob", acct: "1"));
        string all;
        lock (logs.Lines) all = string.Join("\n", logs.Lines);
        Assert.Contains("Customer sign-in refused (GuestNoRole)", all);
    }

    [Theory]
    [InlineData("1", null)]                                                                   // a guest
    [InlineData("0", "https://sts.windows.net/bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb/")]        // an external member
    [InlineData("0", "live.com")]
    public async Task A_guest_or_external_member_with_no_role_is_refused_with_the_guest_card(string acct, string? idp)
    {
        await using var f = await StartAsync();
        var (b, callback) = await Callback(f, Person(Ada, "Ada", acct: acct, idp: idp));
        Assert.Equal(HttpStatusCode.Forbidden, callback.StatusCode);
        Assert.Contains("<h1>No Packmoat role</h1><p>" + GuestText + "</p>", await callback.Content.ReadAsStringAsync());
        Assert.False(b.HasCookie(AppSite.CookieName));
    }

    [Fact]
    public async Task With_auto_join_off_a_member_with_no_role_gets_the_no_role_card()
    {
        await using var f = await StartAsync();
        await f.AutoJoin.SetAsync("acme", false, f.Clock.UtcNow, "test", default);
        var (b, callback) = await Callback(f, Person(Ada, "Ada"));
        Assert.Equal(HttpStatusCode.Forbidden, callback.StatusCode);
        Assert.Contains("<h1>No Packmoat role</h1><p>" + NoRoleText + "</p>", await callback.Content.ReadAsStringAsync());
        Assert.False(b.HasCookie(AppSite.CookieName));
        // An assigned role still gets in.
        await SignIn(f, Person(Bob, "Bob", role: CustomerSignIn.DeveloperRole));
    }

    [Fact]
    public async Task A_setting_that_cannot_be_read_means_no_cookie_for_a_no_role_member_only()
    {
        var store = new ThrowingAutoJoinStore();
        await using var f = await StartAsync(s => s.AddSingleton<IAutoJoinSettingStore>(store));
        var (b, callback) = await Callback(f, Person(Ada, "Ada"));
        Assert.Equal(HttpStatusCode.InternalServerError, callback.StatusCode);
        Assert.False(b.HasCookie(AppSite.CookieName));
        Assert.DoesNotContain(b.SetCookieLog, c => c.StartsWith(AppSite.CookieName + "="));
        Assert.Equal(1, store.Reads);

        // People with a role never depend on the setting: they sign in, and it is never read for them.
        var dev = await SignIn(f, Person(Bob, "Bob", role: CustomerSignIn.DeveloperRole));
        Assert.Contains("Signed in as", (await dev.GetTextAsync("/home")).Body);
        var admin = await SignIn(f, Person(Grace, "Grace", role: CustomerSignIn.AdminRole, authTime: f.Clock.UtcNow));
        Assert.Contains("Signed in as", (await admin.GetTextAsync("/home")).Body);
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task A_real_jwt_with_a_numeric_acct_goes_through_the_handler_event_to_a_developer_decision()
    {
        // A JWT whose payload has "acct": 0 as a JSON number, validated by JsonWebTokenHandler as the OIDC handler does.
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var issuer = $"https://login.microsoftonline.com/{DirA}/v2.0";
        var handler = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler();
        var jwt = handler.CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = AppClient,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["tid"] = DirA, ["oid"] = Ada, ["name"] = "Ada", ["acct"] = 0 },
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256),
        });
        Assert.Contains("\"acct\":0", System.Text.Encoding.UTF8.GetString(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(jwt.Split('.')[1])));
        var validated = await handler.ValidateTokenAsync(jwt, new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidIssuer = issuer, ValidAudience = AppClient, IssuerSigningKey = key, NameClaimType = "name",
        });
        Assert.True(validated.IsValid);

        var o = new OpenIdConnectOptions();
        AppSite.ConfigureOidc(o, new AppSiteSettings { Host = AppHost, ClientId = AppClient, SignInAvailable = true }, new Packmoat.Domain.SystemClock());
        var scheme = new AuthenticationScheme(AppSite.RemoteScheme, null, typeof(OpenIdConnectHandler));
        var ctx = new TokenValidatedContext(new Microsoft.AspNetCore.Http.DefaultHttpContext(), scheme, o, new ClaimsPrincipal(validated.ClaimsIdentity), new AuthenticationProperties());   // the principal is the handler's validated identity, as the real handler passes it
        await o.Events.TokenValidated(ctx);
        using (var empty = System.Text.Json.JsonDocument.Parse("{}"))
            foreach (var action in o.ClaimActions)
                foreach (var identity in ctx.Principal!.Identities) action.Run(empty.RootElement, identity, "https://login.microsoftonline.com/");

        var claims = AppSite.ToVerifiedClaims(ctx.Principal);
        Assert.Equal(new[] { "0" }, claims.AccountStatuses);
        var a = Assert.IsType<SignInDecision.Allowed>(CustomerSignIn.Decide(claims, AppClient, new DirectoryBinding(DirA, "acme", DateTimeOffset.UtcNow, "test"), autoJoin: true));
        Assert.Equal((CustomerRole.Developer, true), (a.Role, a.AutoJoined));
    }

    [Fact]
    public async Task With_no_binding_the_setting_is_not_read_and_the_directory_is_not_linked()
    {
        var store = new ThrowingAutoJoinStore();
        await using var f = await StartAsync(s => s.AddSingleton<IAutoJoinSettingStore>(store));
        var (b, callback) = await Callback(f, Person(Ada, "Ada", tid: DirX));
        Assert.Equal(HttpStatusCode.Forbidden, callback.StatusCode);
        Assert.Contains(CustomerSiteTests.NotLinkedText, await callback.Content.ReadAsStringAsync());
        Assert.Equal(0, store.Reads);
        Assert.False(b.HasCookie(AppSite.CookieName));
    }

    [Fact]
    public async Task The_real_handler_event_carries_a_numeric_acct_and_the_idp_to_the_decision()
    {
        var o = new OpenIdConnectOptions();
        AppSite.ConfigureOidc(o, new AppSiteSettings { Host = AppHost, ClientId = AppClient, SignInAvailable = true }, new Packmoat.Domain.SystemClock());
        var scheme = new AuthenticationScheme(AppSite.RemoteScheme, null, typeof(OpenIdConnectHandler));
        var person = Person(Ada, "Ada", acct: "0", idp: $"https://sts.windows.net/{DirA}/");   // acct as a JSON number becomes an Integer32 claim
        var ctx = new TokenValidatedContext(new Microsoft.AspNetCore.Http.DefaultHttpContext(), scheme, o, person, new AuthenticationProperties());
        await o.Events.TokenValidated(ctx);
        using (var empty = System.Text.Json.JsonDocument.Parse("{}"))
            foreach (var action in o.ClaimActions)
                foreach (var identity in ctx.Principal!.Identities) action.Run(empty.RootElement, identity, "https://login.microsoftonline.com/");

        var claims = AppSite.ToVerifiedClaims(ctx.Principal);
        Assert.Equal(new[] { "0" }, claims.AccountStatuses);
        Assert.Equal(new[] { $"https://sts.windows.net/{DirA}/" }, claims.IdentityProviders);
        var a = Assert.IsType<SignInDecision.Allowed>(CustomerSignIn.Decide(claims, AppClient, new DirectoryBinding(DirA, "acme", DateTimeOffset.UtcNow, "test"), autoJoin: true));
        Assert.True(a.AutoJoined);
        Assert.Equal(CustomerRole.Developer, a.Role);

        // A guest's idp reaches the decision too, and refuses.
        var guest = new TokenValidatedContext(new Microsoft.AspNetCore.Http.DefaultHttpContext(), scheme, o, Person(Ada, "Ada", acct: "0", idp: "live.com"), new AuthenticationProperties());
        await o.Events.TokenValidated(guest);
        Assert.IsType<SignInDecision.Denied>(CustomerSignIn.Decide(AppSite.ToVerifiedClaims(guest.Principal), AppClient, new DirectoryBinding(DirA, "acme", DateTimeOffset.UtcNow, "test"), autoJoin: true));
    }

    // ---- the /team switch

    [Fact]
    public async Task The_team_page_says_who_can_join_and_an_admin_turns_it_off_and_on()
    {
        await using var f = await StartAsync();
        var b = await Admin(f);
        var team = await b.GetTextAsync("/team");
        Assert.Equal(200, team.Status);
        Assert.Contains("<h2>Who can join</h2>", team.Body);
        Assert.Contains(OnText, team.Body);
        Assert.Contains(BothText, team.Body);
        Assert.DoesNotContain(OffText, team.Body);
        Assert.Contains("<input type=\"hidden\" name=\"enabled\" value=\"off\"><button type=\"submit\" class=\"btn\">Turn off</button>", team.Body);

        var off = await Toggle(b, "off");
        Assert.Equal(HttpStatusCode.SeeOther, off.StatusCode);
        Assert.Equal("/team?m=autojoin-off", off.Headers.Location!.OriginalString);
        Assert.False(await f.AutoJoin.IsOnAsync("acme", default));
        var after = (await b.GetTextAsync("/team?m=autojoin-off")).Body;
        Assert.Contains("<p class=\"flash\" role=\"status\">Automatic joining is off.</p>", after);
        Assert.Contains(OffText, after);
        Assert.Contains(BothText, after);
        Assert.DoesNotContain(OnText, after);
        Assert.Contains("<input type=\"hidden\" name=\"enabled\" value=\"on\"><button type=\"submit\" class=\"btn\">Turn on</button>", after);

        var on = await Toggle(b, "on");
        Assert.Equal("/team?m=autojoin-on", on.Headers.Location!.OriginalString);
        Assert.True(await f.AutoJoin.IsOnAsync("acme", default));
        Assert.Contains("<p class=\"flash\" role=\"status\">Automatic joining is on.</p>", (await b.GetTextAsync("/team?m=autojoin-on")).Body);

        var trail = (await f.Actions.LatestAsync(10, default)).Reverse().Where(x => x.Action.StartsWith("team.autojoin", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "enabled=off; was=on", "enabled=on; was=off" }, trail.Select(x => x.Detail));
        Assert.All(trail, x => Assert.Equal(("team.autojoin.set", "acme", "app", Grace, DirA), (x.Action, x.TenantId, x.Site, x.ActorOid, x.ActorTid)));

        // Asking for what is already so changes nothing and writes nothing.
        var again = await Toggle(b, "on");
        Assert.Equal("/team?m=autojoin-on", again.Headers.Location!.OriginalString);
        Assert.Equal(2, (await f.Actions.LatestAsync(10, default)).Count(x => x.Action.StartsWith("team.autojoin", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_switch_needs_the_form_token_the_sites_origin_and_a_plain_on_or_off()
    {
        await using var f = await StartAsync();
        var b = await Admin(f);
        var page = await (await b.GetAsync("/team")).Content.ReadAsStringAsync();
        var form = new[] { new KeyValuePair<string, string>(SignedInSite.AntiforgeryField, AdminBrowser.Field(page)!), new("enabled", "off") };
        Assert.Equal(HttpStatusCode.Forbidden, (await b.SendAsync(HttpMethod.Post, "/team/auto-join", form)).StatusCode);   // no Origin
        Assert.Equal(HttpStatusCode.Forbidden, (await b.SendAsync(HttpMethod.Post, "/team/auto-join", form, r => r.Headers.TryAddWithoutValidation("Origin", "https://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.SendAsync(HttpMethod.Post, "/team/auto-join", form.Skip(1), r => r.Headers.TryAddWithoutValidation("Origin", Origin))).StatusCode);   // no antiforgery
        foreach (var bad in new[] { "", "OFF", "false", "0", "off " })
            Assert.Equal(HttpStatusCode.BadRequest, (await PostWith(b, page, "/team/auto-join", ("enabled", bad))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostWith(b, page, "/team/auto-join")).StatusCode);   // missing
        Assert.Equal(HttpStatusCode.BadRequest, (await PostWith(b, page, "/team/auto-join", ("enabled", "off"), ("enabled", "off"))).StatusCode);   // twice
        Assert.True(await f.AutoJoin.IsOnAsync("acme", default));
        Assert.DoesNotContain(await f.Actions.LatestAsync(50, default), x => x.Action.StartsWith("team.autojoin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_developer_is_refused_and_a_stale_admin_is_asked_to_sign_in_again()
    {
        await using var f = await StartAsync();
        var dev = await SignIn(f, Person(Bob, "Bob", role: CustomerSignIn.DeveloperRole, authTime: f.Clock.UtcNow));
        var home = await (await dev.GetAsync("/home")).Content.ReadAsStringAsync();
        var r = await PostWith(dev, home, "/team/auto-join", ("enabled", "off"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("Only a Packmoat admin in your organisation can do this.", await r.Content.ReadAsStringAsync());

        var admin = await Admin(f);
        var page = await (await admin.GetAsync("/team")).Content.ReadAsStringAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(16));   // the session is still valid, but not fresh
        var stale = await PostWith(admin, page, "/team/auto-join", ("enabled", "off"));
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        var body = await stale.Content.ReadAsStringAsync();
        Assert.Contains("Sign in again first", body);
        Assert.Contains("<form method=\"post\" action=\"/reauth\"", body);
        Assert.True(await f.AutoJoin.IsOnAsync("acme", default));
        Assert.DoesNotContain(await f.Actions.LatestAsync(50, default), x => x.Action.StartsWith("team.autojoin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Turning_it_off_signs_out_people_who_joined_this_way_on_their_next_click_and_no_one_else()
    {
        await using var f = await StartAsync();
        var ada = await SignIn(f, Person(Ada, "Ada"));                                         // joined automatically
        var bob = await SignIn(f, Person(Bob, "Bob", role: CustomerSignIn.DeveloperRole));       // has a role
        Assert.Contains("Signed in as", (await ada.GetTextAsync("/home")).Body);

        var admin = await Admin(f);
        Assert.Equal(HttpStatusCode.SeeOther, (await Toggle(admin, "off")).StatusCode);

        await AssertSignedOutAsync(ada);
        Assert.Contains("Signed in as", (await bob.GetTextAsync("/home")).Body);
        Assert.Contains("Signed in as", (await admin.GetTextAsync("/home")).Body);
    }

    [Fact]
    public async Task The_setting_is_read_again_on_every_click_with_no_cache()
    {
        await using var f = await StartAsync();
        var ada = await SignIn(f, Person(Ada, "Ada"));
        Assert.Contains("Signed in as", (await ada.GetTextAsync("/home")).Body);
        Assert.Equal(200, (await ada.GetTextAsync("/tokens")).Status);
        // Changed behind the site's back (the operator, another server): the next click sees it.
        await f.AutoJoin.SetAsync("acme", false, f.Clock.UtcNow, "elsewhere", default);
        await AssertSignedOutAsync(ada);
        // And the next sign-in too.
        var (b, callback) = await Callback(f, Person(Bob, "Bob"));
        Assert.Equal(HttpStatusCode.Forbidden, callback.StatusCode);
        await f.AutoJoin.SetAsync("acme", true, f.Clock.UtcNow, "elsewhere", default);
        await SignIn(f, Person(Bob, "Bob"));
    }

    [Fact]
    public async Task A_session_that_claims_auto_join_as_an_admin_is_not_a_session()
    {
        // Only the sign-in writes the session. A principal shaped like an auto-joined Admin (which the sign-in never writes) is refused.
        var auto = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tid", DirA), new Claim("oid", Ada), new Claim("name", "Ada"), new Claim(AppSite.RoleClaim, "Admin"), new Claim(AppSite.JoinClaim, AppSite.AutoJoin),
        }, "Entra", "name", AppSite.RoleClaim));
        Assert.Null(AppSite.ReadSession(auto));
        var dev = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tid", DirA), new Claim("oid", Ada), new Claim("name", "Ada"), new Claim(AppSite.RoleClaim, "Developer"), new Claim(AppSite.JoinClaim, AppSite.AutoJoin),
        }, "Entra", "name", AppSite.RoleClaim));
        Assert.True(AppSite.ReadSession(dev)!.AutoJoined);
        var odd = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tid", DirA), new Claim("oid", Ada), new Claim("name", "Ada"), new Claim(AppSite.RoleClaim, "Developer"), new Claim(AppSite.JoinClaim, "manual"),
        }, "Entra", "name", AppSite.RoleClaim));
        Assert.Null(AppSite.ReadSession(odd));
    }
}
