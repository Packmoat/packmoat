using System.Net;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Customer;
using Packmoat.Host.Modules;
using Packmoat.Host.Web;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// The customer site's sessions and pages as the core decides them, with no module and with a test module that offers setup (moved
/// here from the hosted tests): the not-linked card, when a prospect session is read, the sign-in's roles and auth time, the
/// confirmed sign-in's marker, a module page's form targets, and the refusal pages' neutral words.
/// </summary>
[Collection("sqlite-pools")]
public class CustomerSiteSessionTests
{
    private const string AppHost = CustomerSiteTests.AppHost;
    private const string AppClient = CustomerSiteTests.AppClient;
    private const string Origin = "https://" + AppHost;
    private const string DirA = CustomerSiteTests.DirA;   // linked to acme
    private const string DirX = CustomerSiteTests.DirX;   // not linked
    private const string Ada = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";

    /// <summary>
    /// A module that offers every directory a setup, with two prospect pages: /start shows a form, and /start/confirm asks Microsoft for a
    /// confirmed sign-in that comes back to /start. A member page, /member, asks for an ordinary fresh sign-in.
    /// </summary>
    private sealed class OfferingModule : ICustomerSiteModule
    {
        public OfferingModule()
        {
            ProspectRoutes =
            [
                new("/start", false, _ => true, page => page.Write(200, "Start",
                    $"<form method=\"post\" action=\"/start/confirm\">{page.AntiforgeryField()}<button type=\"submit\">Confirm</button></form>" +
                    (page.Who.SignedInAt is null ? "<p>not confirmed</p>" : "<p>confirmed</p>"))),
                new("/start/confirm", true, _ => true, page => page.Reauth("/start", confirm: true)),
            ];
            MemberRoutes = [new("/member", false, _ => true, page => page.Reauth("/member"))];
        }

        public bool OffersSetup(string directoryId) => true;
        public NotLinkedCard NotLinkedCard(string directoryId, bool needsAdmin) => new("Not here", "<p>not here</p>");
        public IReadOnlyList<CustomerRoute<CustomerActor>> MemberRoutes { get; }
        public IReadOnlyList<CustomerRoute<Prospect>> ProspectRoutes { get; }
        public string HomeBanner(CustomerActor actor, CustomerHome view) => "";
        public string HomeLinks(CustomerActor actor, CustomerHome view) => "";
        public string ConnectSentence(CustomerActor actor) => "";
    }

    private sealed class Site(WebApplication app, TempDir dir, FakeIdentityProvider identity) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;
        public FakeIdentityProvider Identity { get; } = identity;
        public string BaseUrl => App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("localhost", "127.0.0.1").TrimEnd('/');

        public ISecureDataFormat<AuthenticationTicket> Cookies =>
            App.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AppSite.CookieScheme).TicketDataFormat;

        public async ValueTask DisposeAsync()
        {
            try
            {
                try { await App.StopAsync(); }
                finally { await App.DisposeAsync(); }
            }
            finally { dir.Dispose(); }
        }
    }

    /// <summary>The core with no modules (a test's own customer-site module in <paramref name="services"/>), acme linked to directory A.</summary>
    private static async Task<Site> StartAsync(Action<IServiceCollection>? services = null)
    {
        var temp = TempDir.Create("packmoat-customer-session-", clearSqlitePools: true);
        WebApplication? app = null;
        try
        {
            var dir = temp.Path;
            var identity = new FakeIdentityProvider();
            app = PackmoatApp.Build(new[] { "--urls", "http://127.0.0.1:0" }, TestModules.Server, s =>
            {
                s.AddSingleton<IPublishDateSource>(new FakeDateSource());
                s.AddKeyedSingleton("app", identity);
                services?.Invoke(s);
            }, o =>
            {
                o.Database = Path.Combine(dir, "packmoat.db");
                o.MasterKeyBase64 = AesGcmSecretProtector.GenerateKeyBase64();
                o.AllowedHosts = "feeds.example.test";
                o.AdminKeysDir = Path.Combine(dir, "admin-keys");
                o.AppHost = AppHost;
                o.AppClientId = AppClient;
                o.AppRemoteSchemeOverride = auth => auth.AddScheme<AuthenticationSchemeOptions, FakeAppEntraHandler>(AppSite.RemoteScheme, null);
            });
            await app.StartAsync();
            var tenants = app.Services.GetRequiredService<SqliteTenantStore>();
            await tenants.CreateAsync(new NewTenant("acme", "Acme", "https://pkgs.dev.azure.com/", "org/", "https://pkgs.dev.azure.com/org/_packaging/f/nuget/v3/index.json", 14, TenantOrigin.Self), default);
            await app.Services.GetRequiredService<ISignInDirectoryStore>().BindAsync(new DirectoryBinding(DirA, "acme", DateTimeOffset.UtcNow, "op"), default);
            return new Site(app, temp, identity);
        }
        catch
        {
            if (app is not null) await app.DisposeAsync();
            temp.Dispose();   // A failed start leaves no folder behind
            throw;
        }
    }

    private static ClaimsPrincipal Person(string tid, string role, DateTimeOffset? authTime)
    {
        var claims = new List<Claim>
        {
            new("iss", $"https://login.microsoftonline.com/{tid}/v2.0"), new("aud", AppClient), new("tid", tid), new("oid", Ada),
            new("name", "Ada Lovelace"), new("roles", role),
        };
        if (authTime is { } at) claims.Add(new Claim("auth_time", at.ToUnixTimeSeconds().ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "FakeEntra", "name", "roles"));
    }

    private static async Task<(AdminBrowser Browser, HttpResponseMessage Callback)> SignInAsync(Site site, string tid, string role = CustomerSignIn.AdminRole)
    {
        site.Identity.Next = Person(tid, role, DateTimeOffset.UtcNow);
        var b = new AdminBrowser(site.BaseUrl, AppHost);
        var start = await b.GetAsync("/");
        return (b, await b.GetAsync(start.Headers.Location!.OriginalString));
    }

    private static ClaimsPrincipal Prospect(params Claim[] more) => new(new ClaimsIdentity(new[]
    {
        new Claim("tid", DirX), new Claim("oid", Ada), new Claim("name", "Ada"), new Claim(AppSite.SessionKindClaim, AppSite.ProspectSessionKind),
    }.Concat(more), "Entra", "name", AppSite.RoleClaim));

    // ---------------------------------------------------------------- no module

    [Fact]
    public async Task With_no_module_an_unlinked_admin_gets_the_core_card_and_no_session()
    {
        await using var site = await StartAsync();
        var (b, callback) = await SignInAsync(site, DirX, CustomerSignIn.AdminRole);
        Assert.Equal(HttpStatusCode.Forbidden, callback.StatusCode);
        Assert.False(b.HasCookie(AppSite.CookieName));
        var body = await callback.Content.ReadAsStringAsync();
        Assert.Contains("<title>" + AppHtml.E(NoCustomerSiteModule.NotLinkedTitle) + " - Packmoat</title>", body);
        var card = body[body.IndexOf("<main", StringComparison.Ordinal)..body.IndexOf("</main>", StringComparison.Ordinal)];
        // The wording chosen for the not-linked card.
        Assert.Contains($"<h1>{AppHtml.E("Your organisation isn't on Packmoat here yet")}</h1>", card);
        Assert.Contains(AppHtml.E("Ask your IT team, or whoever runs Packmoat for you, to add your organisation. Give them this directory id. Once they've added it, sign in again."), card);
        Assert.Contains("Directory id: " + DirX, card);
        Assert.Contains(AppHtml.SignInAgainLink, card);
        Assert.DoesNotContain("packmoat.com", card);   // nothing links away from this server
        // And no prospect: the next visit is signed out, never a module's page.
        Assert.NotEqual(HttpStatusCode.SeeOther, (await b.GetAsync("/home")).StatusCode);
    }

    [Fact]   // mutation: ReadProspect reads a prospect with no module (drop its OffersSetup check)
    public void With_no_module_a_prospect_session_is_never_read()
    {
        var p = Prospect();
        Assert.Null(AppSite.ReadProspect(p, NoCustomerSiteModule.Instance));
        Assert.NotNull(AppSite.ReadProspect(p, new OfferingModule()));   // the same session, with a module that offers the directory a setup
        Assert.False(NoCustomerSiteModule.Instance.OffersSetup(DirX));
    }

    [Fact]
    public void A_principal_marked_as_a_prospect_is_never_read_as_a_member_session_even_with_a_role()
    {
        var p = Prospect(new Claim(AppSite.RoleClaim, "Admin"));
        Assert.Null(AppSite.ReadSession(p));
        Assert.Null(AppSite.ReadProspect(p, new OfferingModule()));   // and a prospect never carries a role
        var member = new ClaimsPrincipal(new ClaimsIdentity(p.Claims.Where(c => c.Type != AppSite.SessionKindClaim), "Entra", "name", AppSite.RoleClaim));
        Assert.NotNull(AppSite.ReadSession(member));
    }

    // ---------------------------------------------------------------- the sign-in

    [Fact]
    public async Task The_real_handler_event_keeps_roles_and_auth_time_for_a_prospect()
    {
        var o = new OpenIdConnectOptions();
        AppSite.ConfigureOidc(o, new AppSiteSettings { Host = AppHost, ClientId = AppClient, SignInAvailable = true }, new Packmoat.Domain.SystemClock());
        var scheme = new AuthenticationScheme(AppSite.RemoteScheme, null, typeof(OpenIdConnectHandler));
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var ctx = new TokenValidatedContext(new Microsoft.AspNetCore.Http.DefaultHttpContext(), scheme, o, Person(DirX, CustomerSignIn.AdminRole, at), new AuthenticationProperties());
        await o.Events.TokenValidated(ctx);
        using (var empty = System.Text.Json.JsonDocument.Parse("{}"))
            foreach (var action in o.ClaimActions)
                foreach (var identity in ctx.Principal!.Identities) action.Run(empty.RootElement, identity, "https://login.microsoftonline.com/");
        Assert.Equal(new[] { CustomerSignIn.AdminRole }, ctx.Principal!.FindAll("roles").Select(c => c.Value).ToArray());
        Assert.Equal(at.ToUnixTimeSeconds(), AppSite.SignInTime(ctx.Principal, DateTimeOffset.UtcNow)!.Value.ToUnixTimeSeconds());
        var claims = AppSite.ToVerifiedClaims(ctx.Principal);
        Assert.IsType<ProspectDecision.Eligible>(ProspectDecision.For(claims, CustomerSignIn.Decide(claims, AppClient, null)));
    }

    [Fact]   // mutations: the marker kept in the cookie; an automatic sign-in taken as confirmed
    public async Task Only_a_prospect_pages_confirmed_sign_in_carries_the_marker_and_the_cookie_never_keeps_it()
    {
        var module = new OfferingModule();
        await using var site = await StartAsync(s => s.AddSingleton<ICustomerSiteModule>(module));

        // An automatic sign-in: a prospect session with no marker, so never confirmed, even though Microsoft's auth_time says "just now".
        var (b, callback) = await SignInAsync(site, DirX);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.False(site.Identity.LastChallenge!.Items.ContainsKey(AppSite.ConfirmItem));
        var ticket = site.Cookies.Unprotect(b.Cookie(AppSite.CookieName))!;
        Assert.DoesNotContain(ticket.Principal.Claims, c => c.Type == AppSite.AuthTimeClaim);
        Assert.Equal("/start", (await b.GetAsync("/home")).Headers.Location?.OriginalString);
        var page = (await b.GetTextAsync("/start")).Body;
        Assert.Contains("<p>not confirmed</p>", page);

        // The prospect page's confirmed sign-in: the marker goes to Microsoft, is read once at sign-in, and is not kept in the cookie.
        var confirm = await b.SendAsync(HttpMethod.Post, "/start/confirm", [new(SignedInSite.AntiforgeryField, AdminBrowser.Field(page)!)],
            r => r.Headers.TryAddWithoutValidation("Origin", Origin));
        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        Assert.Equal("1", site.Identity.LastChallenge!.Items[AppSite.ConfirmItem]);
        Assert.Equal("/start?m=reauth", site.Identity.LastChallenge.RedirectUri);
        site.Identity.Next = Person(DirX, CustomerSignIn.AdminRole, DateTimeOffset.UtcNow);
        Assert.Equal("/start?m=reauth", (await b.GetAsync(confirm.Headers.Location!.OriginalString)).Headers.Location?.OriginalString);
        ticket = site.Cookies.Unprotect(b.Cookie(AppSite.CookieName))!;
        Assert.False(ticket.Properties.Items.ContainsKey(AppSite.ConfirmItem));
        Assert.Contains(ticket.Principal.Claims, c => c.Type == AppSite.AuthTimeClaim);
        Assert.Contains("<p>confirmed</p>", (await b.GetTextAsync("/start?m=reauth")).Body);

        // A member's fresh sign-in never carries it.
        var (member, _) = await SignInAsync(site, DirA);
        Assert.Equal(HttpStatusCode.Redirect, (await member.GetAsync("/member")).StatusCode);
        Assert.Equal("/member?m=reauth", site.Identity.LastChallenge!.RedirectUri);
        Assert.False(site.Identity.LastChallenge.Items.ContainsKey(AppSite.ConfirmItem));
    }

    // ---------------------------------------------------------------- a module the start refuses

    /// <summary>A module with the pages it is given, offering every directory a setup unless told not to.</summary>
    private sealed class PagesModule(IReadOnlyList<CustomerRoute<CustomerActor>> member, IReadOnlyList<CustomerRoute<Prospect>> prospect) : ICustomerSiteModule
    {
        public bool OffersSetup(string directoryId) => true;
        public NotLinkedCard NotLinkedCard(string directoryId, bool needsAdmin) =>
            new("Not here", needsAdmin ? "<p>module card: needs an admin</p>" : "<p>module card</p>");
        public IReadOnlyList<CustomerRoute<CustomerActor>> MemberRoutes { get; } = member;
        public IReadOnlyList<CustomerRoute<Prospect>> ProspectRoutes { get; } = prospect;
        public string HomeBanner(CustomerActor actor, CustomerHome view) => "";
        public string HomeLinks(CustomerActor actor, CustomerHome view) => "";
        public string ConnectSentence(CustomerActor actor) => "";
    }

    private static CustomerRoute<T> Page<T>(string path, bool post = false) where T : class => new(path, post, _ => true, page => page.Write(200, "Page", "<p>page</p>"));

    /// <summary>What the server's entry point does with <paramref name="module"/> registered: its exit code and its error output.</summary>
    private static (int Code, string Error, Exception? Thrown) Start(ICustomerSiteModule module)
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-customer-module-start-").FullName;
        try
        {
            WebApplication Build() => PackmoatApp.Build(new[] { "--urls", "http://127.0.0.1:0" }, TestModules.Server, s => s.AddSingleton(module), o =>
            {
                o.Database = Path.Combine(dir, "packmoat.db");
                o.MasterKeyBase64 = AesGcmSecretProtector.GenerateKeyBase64();
                o.AllowedHosts = "feeds.example.test";
            });
            Exception? thrown = null;
            try { using var app = Build(); } catch (Exception e) { thrown = e; }
            if (thrown is null) return (0, "", null);
            var error = new StringWriter();
            return (StartupFailure.Run(Build, error), error.ToString(), thrown);
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }

    [Theory]   // mutations: no path check at start; a path checked for its first two characters only (an open redirect: "//host", "/\host", "/<tab>/host")
    [InlineData("plans")]
    [InlineData("")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData(" /plans")]
    [InlineData("https://evil.example/")]
    [InlineData("/\t/evil.example")]   // a browser drops the tab: "//evil.example"
    [InlineData("/\n/evil")]
    [InlineData("/ /evil")]
    [InlineData("/\u2215evil")]   // a look-alike slash
    [InlineData("/\uFF0Fevil")]   // a full-width slash
    public void A_module_page_whose_path_is_not_a_path_on_this_site_refuses_the_start(string path)
    {
        var shown = string.Concat(path.Select(c => c is >= ' ' and < '\x7f' ? c.ToString() : $"\\u{(int)c:x4}"));   // one line, whatever the path holds
        var expected = $"error: The customer-site module's page \"{shown}\" is not a path on this site. Refusing to start: a page's path starts with one \"/\".";
        foreach (var module in new[] { new PagesModule([Page<CustomerActor>("/member"), Page<CustomerActor>(path)], []), new PagesModule([], [Page<Prospect>("/start"), Page<Prospect>(path, post: true)]) })
        {
            var (code, error, thrown) = Start(module);
            Assert.IsType<InvalidOperationException>(thrown);
            Assert.Equal(1, code);
            Assert.Equal(expected + "\n", error.ReplaceLineEndings("\n"));   // one error: line, exit 1
        }
    }

    [Fact]   // mutation: no prospect landing check at start
    public void Prospect_pages_with_no_GET_page_refuse_the_start()
    {
        var (code, error, thrown) = Start(new PagesModule([], [Page<Prospect>("/start", post: true), Page<Prospect>("/start/confirm", post: true)]));
        Assert.IsType<InvalidOperationException>(thrown);
        Assert.Equal(1, code);
        Assert.Equal("error: The customer-site module has prospect pages but no GET page among them. Refusing to start: a prospect lands on the first GET page.\n",
            error.ReplaceLineEndings("\n"));
    }

    [Fact]   // the rules refuse only what they name: these start
    public void Paths_on_this_site_and_a_module_with_no_prospect_pages_or_a_GET_one_start()
    {
        Assert.Null(Start(new PagesModule([Page<CustomerActor>("/"), Page<CustomerActor>("/plans/list"), Page<CustomerActor>("/a//b", post: true)], [])).Thrown);
        Assert.Null(Start(new PagesModule([], [Page<Prospect>("/start/confirm", post: true), Page<Prospect>("/start")])).Thrown);
        Assert.Null(Start(new OfferingModule()).Thrown);
        Assert.Null(Start(NoCustomerSiteModule.Instance).Thrown);
    }

    [Fact]   // mutation: the sign-in makes a prospect session without asking whether the module has prospect pages
    public async Task A_module_that_offers_a_setup_but_has_no_prospect_pages_makes_no_prospect_session()
    {
        // Earlier, the person got a prospect session that no page reads: every visit went back round the sign-in.
        await using var site = await StartAsync(s => s.AddSingleton<ICustomerSiteModule>(new PagesModule([Page<CustomerActor>("/member")], [])));
        var (b, callback) = await SignInAsync(site, DirX, CustomerSignIn.AdminRole);
        Assert.Equal(HttpStatusCode.Forbidden, callback.StatusCode);
        Assert.False(b.HasCookie(AppSite.CookieName));
        Assert.Contains("<p>module card</p>", await callback.Content.ReadAsStringAsync());   // the module's own not-linked card, not "needs an admin"
    }

    // ---------------------------------------------------------------- a module page's form targets

    [Fact]
    public void Form_targets_build_the_policy_byte_for_byte()
    {
        Assert.Equal(SignedInSite.LandingContentSecurityPolicy, SignedInSite.FormTargetsContentSecurityPolicy([]));
        Assert.Equal("default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self' https://pay.example.com https://login.microsoftonline.com; base-uri 'none'; frame-ancestors 'none'",
            SignedInSite.FormTargetsContentSecurityPolicy(["https://pay.example.com"]));
        Assert.Equal("default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self' https://portal.example.com https://pay.example.com https://login.microsoftonline.com; base-uri 'none'; frame-ancestors 'none'",
            SignedInSite.FormTargetsContentSecurityPolicy(["https://portal.example.com", "https://pay.example.com"]));
    }

    [Theory]   // mutation: a form target may end with "/"
    [InlineData("http://pay.example.com")]
    [InlineData("https://pay.example.com/")]
    [InlineData("https://pay.example.com/checkout")]
    [InlineData("https://pay.example.com:8443")]
    [InlineData("https://pay.example.com?x=1")]
    [InlineData("https://pay.example.com#x")]
    [InlineData("https://user@pay.example.com")]
    [InlineData("https://Pay.example.com")]
    [InlineData("https://*.example.com")]
    [InlineData("https://pay.example.com; script-src *")]
    [InlineData("https://pay.example.com https://evil.example")]
    [InlineData("https://pay.example.com'")]
    [InlineData("https://-pay.example.com")]
    [InlineData("https://pay..example.com")]
    [InlineData("https://")]
    [InlineData("pay.example.com")]
    [InlineData("'self'")]
    [InlineData("")]
    public void A_form_target_that_is_not_a_bare_https_origin_is_refused(string origin)
    {
        Assert.Throws<ArgumentException>(() => SignedInSite.FormTargetsContentSecurityPolicy([origin]));
    }

    // ---------------------------------------------------------------- neutral words with no operator set

    /// <summary>An operator's own address: none may reach a page of a server that has not set one.</summary>
    private static void AssertNeutral(string where, string text)
    {
        Assert.False(text.Contains("packmoat.com", StringComparison.OrdinalIgnoreCase), $"{where} names packmoat.com");
        Assert.False(text.Contains("support@", StringComparison.OrdinalIgnoreCase), $"{where} names a support address");
    }

    [Fact]
    public async Task No_refusal_page_names_the_hosted_service()
    {
        await using var f = await AdminSiteFixture.StartAsync(o =>
        {
            o.Operator = OperatorLinks.None;
            o.AppHost = AppHost;
            o.AppClientId = AppClient;
            o.AppRemoteSchemeOverride = auth => auth.AddScheme<AuthenticationSchemeOptions, FakeAppEntraHandler>(AppSite.RemoteScheme, null);
            o.AzureSetup = AzureSetupSettings.Parse("on");
        }, fixtureServices: (fx, s) =>
        {
            s.AddSingleton<IFeedCheckStore>(new SqliteFeedCheckStore(fx.Db));
            s.AddSingleton<ISelfServiceFeedStore>(new SqliteSelfServiceFeedStore(fx.Db));
            s.AddSingleton<ISharedAppCredentialStore>(new SqliteSharedAppCredentialStore(fx.Db, fx.Clock, AesGcmSecretProtector.FromBase64(fx.MasterKey)));
        });
        // Every refusal page the core draws (the token, waiver and Azure setup refusals), for every reason, as a signed-in page would.
        var refusals = typeof(AppSite).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(m => m.Name.EndsWith("Refused", StringComparison.Ordinal) && m.ReturnType == typeof(Task)
                && m.GetParameters() is [{ ParameterType: var c }, { ParameterType: { IsEnum: true } }, .. var rest] && c == typeof(Microsoft.AspNetCore.Http.HttpContext)
                && rest.All(p => p.IsOptional))   // The serving gate's notice, when there is one
            .ToList();
        Assert.Contains(refusals, m => m.DeclaringType == typeof(AppTokens));
        Assert.Contains(refusals, m => m.DeclaringType == typeof(AppAzureSetup));
        Assert.Contains(refusals, m => m.DeclaringType == typeof(AppWaivers));
        foreach (var method in refusals)
            foreach (var reason in Enum.GetValues(method.GetParameters()[1].ParameterType))
            {
                var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = f.App.Services };
                http.Request.Scheme = "https";
                http.Request.Host = new Microsoft.AspNetCore.Http.HostString(AppHost);
                http.Response.Body = new MemoryStream();
                await (Task)method.Invoke(null, [http, reason, .. method.GetParameters().Skip(2).Select(p => p.DefaultValue)])!;
                http.Response.Body.Position = 0;
                AssertNeutral($"{method.DeclaringType!.Name}.{method.Name}({reason})", await new StreamReader(http.Response.Body).ReadToEndAsync());
            }

        // The sign-in outcome pages Microsoft's reply leads to: cancelled, and failed after a code.
        var o = new OpenIdConnectOptions();
        AppSite.ConfigureOidc(o, f.App.Services.GetRequiredService<AppSiteSettings>(), f.Clock);
        var scheme = new AuthenticationScheme(AppSite.RemoteScheme, null, typeof(OpenIdConnectHandler));
        Microsoft.AspNetCore.Http.DefaultHttpContext Http(string error)
        {
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = f.App.Services };
            http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
            http.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?error=" + error);
            http.Response.Body = new MemoryStream();
            return http;
        }
        static string Body(Microsoft.AspNetCore.Http.HttpContext h) { h.Response.Body.Position = 0; return new StreamReader(h.Response.Body).ReadToEnd(); }
        var denied = Http("access_denied");
        await o.Events.AccessDenied(new AccessDeniedContext(denied, scheme, o));
        AssertNeutral("cancelled sign-in", Body(denied));
        var failed = Http("server_error");
        await o.Events.RemoteFailure(new RemoteFailureContext(failed, scheme, o, new InvalidOperationException("x")));
        Assert.Equal(400, failed.Response.StatusCode);
        AssertNeutral("failed sign-in", Body(failed));
    }
}
