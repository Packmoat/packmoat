using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host;
using Packmoat.Host.Customer;
using Packmoat.Host.Web;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>What the pretend Microsoft says at the end of the Azure DevOps step-up sign-in: the validated ID token's principal and the access token.</summary>
public sealed class FakeAzureDevOpsSignIn
{
    public ClaimsPrincipal? Next { get; set; }
    public string? AccessToken { get; set; } = FakeAzureDevOps.Token;
    public AuthenticationProperties? LastChallenge { get; set; }
    public int Challenges;
}

/// <summary>
/// Stands in for the OpenID Connect handler of the Azure DevOps check, around the REAL event: the callback builds the token-validated
/// context the real handler would (principal, protected properties from the challenge, token endpoint response) and runs the real
/// <see cref="AppAzureSetup.ConfigureOidc"/> options' OnTokenValidated. The challenge records its properties and sends the browser here.
/// </summary>
public sealed class FakeAzureDevOpsSchemeHandler : AuthenticationHandler<AuthenticationSchemeOptions>, IAuthenticationRequestHandler
{
    public FakeAzureDevOpsSchemeHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : base(options, logger, encoder) { }

    public async Task<bool> HandleRequestAsync()
    {
        if (Request.Path != AppAzureSetup.CallbackPath) return false;
        var signIn = Context.RequestServices.GetRequiredService<FakeAzureDevOpsSignIn>();
        if (signIn.Next is null || signIn.LastChallenge is null) { Response.StatusCode = 400; return true; }
        var o = new OpenIdConnectOptions();
        AppAzureSetup.ConfigureOidc(o, Context.RequestServices.GetRequiredService<AppSiteSettings>(), Context.RequestServices.GetRequiredService<IClock>());
        var properties = new AuthenticationProperties(new Dictionary<string, string?>(signIn.LastChallenge.Items));
        var ctx = new TokenValidatedContext(Context, Scheme, o, signIn.Next, properties)
        {
            TokenEndpointResponse = new OpenIdConnectMessage { AccessToken = signIn.AccessToken, IdToken = "id-token" },
        };
        await o.Events.TokenValidated(ctx);
        if (ctx.Result?.Handled != true) Response.StatusCode = 500;   // the real event always handles the response
        return true;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var signIn = Context.RequestServices.GetRequiredService<FakeAzureDevOpsSignIn>();
        signIn.LastChallenge = properties;
        Interlocked.Increment(ref signIn.Challenges);
        Response.Redirect(AppAzureSetup.CallbackPath + "?fake=1");
        return Task.CompletedTask;
    }
}

/// <summary>
/// The Azure setup wizard's test kit (#197): the site, the people and the steps. The core's tests and the hosted tests (which compile this
/// file in, for the wizard's parts that need the hosted module) share it.
/// </summary>
public partial class AzureSetupSiteTests
{
    private const string AppHost = CustomerSiteTests.AppHost;
    private const string AppClient = CustomerSiteTests.AppClient;
    private const string DirA = CustomerSiteTests.DirA;   // acme: operator-made
    private const string DirS = "5e1f5e1f-5e1f-4e1f-8e1f-5e1f5e1f5e1f";   // selfco: self-made, no feed yet
    private const string Ada = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    private const string Origin = "https://" + AppHost;

    private sealed record Site(AdminSiteFixture F, FakeAzureDevOps Ado, FakeAzureDevOpsSignIn SignIn, CapturingLoggerProvider Logs, FakePackmoatCredential Packmoat) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => F.DisposeAsync();
    }

    private static async Task<Site> StartAsync(string? flag = "on", bool sharedApp = true, Action<PackmoatOptions>? configure = null,
        Func<AdminSiteFixture, ISelfServiceFeedStore>? feeds = null, Action<IServiceCollection>? services = null, bool logEverything = true,
        IReadOnlyList<Packmoat.Host.Modules.IPackmoatModule>? modules = null)
    {
        var ado = new FakeAzureDevOps(DirS);
        var signIn = new FakeAzureDevOpsSignIn();
        var logs = new CapturingLoggerProvider();
        var packmoat = new FakePackmoatCredential();
        var f = await AdminSiteFixture.StartAsync(o =>
        {
            o.AppHost = AppHost;
            o.AppClientId = AppClient;
            o.AppRemoteSchemeOverride = auth => auth.AddScheme<AuthenticationSchemeOptions, FakeAppEntraHandler>(AppSite.RemoteScheme, null);
            o.AppAzureDevOpsSchemeOverride = auth => auth.AddScheme<AuthenticationSchemeOptions, FakeAzureDevOpsSchemeHandler>(AppAzureSetup.Scheme, null);
            o.AzureDevOpsHandlerOverride = () => ado;
            o.SharedAppTokenOverride = packmoat.Factory;
            if (flag is not null) o.AzureSetup = AzureSetupSettings.Parse(flag);
            configure?.Invoke(o);
        }, services: s =>
        {
            s.AddSingleton(signIn);
            s.AddSingleton<ILoggerProvider>(logs);
            // Every level and category reaches the capture, so a token logged even at Trace is seen. (logEverything false: production's
            // own log rules, for tests that check what production would write.)
            if (logEverything) s.PostConfigure<LoggerFilterOptions>(o => { o.Rules.Clear(); o.MinLevel = LogLevel.Trace; });
            services?.Invoke(s);
        }, fixtureServices: (fx, s) =>
        {
            s.AddSingleton<IFeedCheckStore>(new SqliteFeedCheckStore(fx.Db));
            s.AddSingleton<ISelfServiceFeedStore>(feeds?.Invoke(fx) ?? new SqliteSelfServiceFeedStore(fx.Db));
            s.AddSingleton<ISharedAppCredentialStore>(new SqliteSharedAppCredentialStore(fx.Db, fx.Clock, AesGcmSecretProtector.FromBase64(fx.MasterKey)));
        }, modules: modules);
        // A self-made tenant as self sign-up leaves it: no feed URLs at all.
        await f.Tenants.CreateAsync(new NewTenant("selfco", "Selfco", f.Upstream.BaseUrl, "org/", f.Upstream.FeedIndexUrl, 14, TenantOrigin.Self) { CreatedByOid = Ada }, default);
        await using (var c = await f.Db.OpenAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE tenants SET upstream_base='', allowed_path_prefix='', feed_index_url='' WHERE id='selfco'";
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }
        await f.Directories.BindAsync(new DirectoryBinding(DirA, "acme", f.Clock.UtcNow, "op"), default);
        await f.Directories.BindAsync(new DirectoryBinding(DirS, "selfco", f.Clock.UtcNow, "self-signup"), default);
        if (sharedApp)
            await f.App.Services.GetRequiredService<ISharedAppCredentialStore>().SetAsync(SharedAppSlot.Active, new SharedAppCredential(FakeAzureDevOps.SharedApp, "-----BEGIN PRIVATE KEY-----\nTEST\n-----END PRIVATE KEY-----"), default);
        return new Site(f, ado, signIn, logs, packmoat);
    }

    private static ClaimsPrincipal Person(string dir, string oid = Ada, string role = CustomerSignIn.AdminRole, DateTimeOffset? authTime = null, string? tid = null, string? extraOid = null)
    {
        var claims = new List<Claim>
        {
            new("iss", $"https://login.microsoftonline.com/{dir}/v2.0"), new("aud", AppClient), new("tid", tid ?? dir), new("oid", oid), new("name", "Ada Lovelace"),
        };
        if (role.Length > 0) claims.Add(new Claim("roles", role));
        if (extraOid is not null) claims.Add(new Claim("oid", extraOid));
        if (authTime is { } at) claims.Add(new Claim("auth_time", at.ToUnixTimeSeconds().ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "FakeEntra", "name", "roles"));
    }

    private static async Task<AdminBrowser> SignIn(AdminSiteFixture f, string dir = DirS, string role = CustomerSignIn.AdminRole)
    {
        f.AppIdentity.Next = Person(dir, role: role, authTime: f.Clock.UtcNow);
        var b = f.Browser(AppHost);
        var start = await b.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, (await b.GetAsync(start.Headers.Location!.OriginalString)).StatusCode);
        return b;
    }

    private static Task<HttpResponseMessage> Post(AdminBrowser b, string html, string action, bool origin = true, params (string Name, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>> { new(SignedInSite.AntiforgeryField, AdminBrowser.Field(html) ?? throw new InvalidOperationException("no form on the page")) };
        form.AddRange(fields.Select(x => new KeyValuePair<string, string>(x.Name, x.Value)));
        return b.SendAsync(HttpMethod.Post, action, form, r => { if (origin) r.Headers.TryAddWithoutValidation("Origin", Origin); });
    }

    private static readonly (string, string)[] GoodNames = { ("org", FakeAzureDevOps.Org), ("project", FakeAzureDevOps.ProjectName), ("feed", FakeAzureDevOps.FeedName) };

    /// <summary>Loads the page, posts "Sign in and check", and returns the challenge's redirect (to the fake step-up).</summary>
    private static async Task<HttpResponseMessage> StartCheck(AdminBrowser b, params (string, string)[] fields)
    {
        var (_, page, _) = await b.GetTextAsync(AppAzureSetup.Path);
        return await Post(b, page, AppAzureSetup.CheckPath, true, fields.Length == 0 ? GoodNames : fields);
    }

    /// <summary>The whole check: start, then the step-up callback as <paramref name="person"/> (default: the same Admin, signed in just now).</summary>
    private static async Task<HttpResponseMessage> RunCheck(Site site, AdminBrowser b, ClaimsPrincipal? person = null, params (string, string)[] fields)
    {
        var start = await StartCheck(b, fields);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        site.SignIn.Next = person ?? Person(DirS, authTime: site.F.Clock.UtcNow);
        return await b.GetAsync(start.Headers.Location!.OriginalString);
    }

    private static async Task<long> FeedCheckRows(AdminSiteFixture f)
    {
        await using var c = await f.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM feed_checks";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static bool WroteSessionCookie(HttpResponseMessage r) =>
        r.Headers.TryGetValues("Set-Cookie", out var v) && v.Any(c => c.StartsWith(AppSite.CookieName + "=", StringComparison.Ordinal) && !c.StartsWith(AppSite.CookieName + "=;", StringComparison.Ordinal));

    private const string Grace = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";   // a developer of selfco
    private static readonly string CheckedFeed = $"https://pkgs.dev.azure.com/contoso/{FakeAzureDevOps.ProjectId:D}/_packaging/{FakeAzureDevOps.FeedId:D}/nuget/v3/index.json";
    private static readonly Regex TokenValue = new("pmt_[0-9a-f]{48}");

    private static IReadOnlyList<CheckResult> Passing(params string[] ids) => ids.Select(id => new CheckResult(id, id, CheckStatus.Pass, "ok")).ToList();

    /// <summary>A settings check that has every result Connect needs (including 'private', which #64 I2 adds) and a developer proof by someone else.</summary>
    private static async Task<(long Settings, long Developer)> SeedChecksAsync(AdminSiteFixture f, string tenant = "selfco", string dir = DirS)
    {
        var store = f.App.Services.GetRequiredService<IFeedCheckStore>();
        var settings = await store.RecordAsync(new FeedCheck(tenant, f.Clock.UtcNow, dir, Ada, "Ada Lovelace", dir, CheckedFeed, FakeAzureDevOps.SharedApp,
            FeedCheckOutcome.Passed, Passing("feed", "identity", "upstreams", "permissions", "views", "private", "project"), Array.Empty<string>()), default);
        var developer = await store.RecordAsync(new FeedCheck(tenant, f.Clock.UtcNow - TimeSpan.FromHours(2), dir, Grace, "Grace Hopper", dir, CheckedFeed, FakeAzureDevOps.SharedApp,
            FeedCheckOutcome.Passed, Passing("developer"), Array.Empty<string>()) { Kind = FeedCheckKind.Developer }, default);
        return (settings, developer);
    }

    private static (string, string)[] ConnectFields((long Settings, long Developer) ids) =>
        new[] { ("settings", ids.Settings.ToString()), ("developer", ids.Developer.ToString()), ("feed", CheckedFeed), ("client", FakeAzureDevOps.SharedApp) };

    private static async Task<HttpResponseMessage> ConnectFrom(AdminBrowser b, (long, long) ids, bool origin = true)
    {
        var (_, page, _) = await b.GetTextAsync(AppAzureSetup.Path);
        return await Post(b, page, AppAzureSetup.ConnectPath, origin, ConnectFields(ids));
    }

    private static (string, string, string, string, bool, int) Shape(Tenant t) => (t.Id, t.UpstreamBase, t.AllowedPathPrefix, t.UpstreamFeedIndexUrl, t.Active, t.Policy.CooldownDays);

    private static Task<SharedAppVerification?> Verification(AdminSiteFixture f, string tenant = "selfco") =>
        f.App.Services.GetRequiredService<ISharedAppCredentialStore>().GetVerificationAsync(tenant, default);

    private static async Task<object?> Scalar(AdminSiteFixture f, string sql)
    {
        await using var c = await f.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task<(HttpResponseMessage Response, string Body, string? Token)> CreateToken(AdminBrowser b)
    {
        var page = (await b.GetTextAsync("/tokens")).Body;
        var nonce = Regex.Match(page, "name=\"nonce\" value=\"([^\"]+)\"") is { Success: true } m ? m.Groups[1].Value : "";
        var r = await Post(b, page, "/tokens/create", true, ("nonce", nonce), ("label", "laptop"));
        var body = await r.Content.ReadAsStringAsync();
        return (r, body, TokenValue.Match(body) is { Success: true } t ? t.Value : null);
    }

    /// <summary>
    /// For a tenant that would refuse the issue (#200): the page answers 200 with the refusal in the create form's place, and a POST made
    /// anyway (<see cref="HiddenTokenForm"/>, as Ada of <paramref name="dir"/>) is still refused by the POST's own check.
    /// </summary>
    private static async Task<(string Page, HttpResponseMessage Response, string Body, string? Token)> CreateTokenAnyway(AdminSiteFixture f, AdminBrowser b, string dir = DirS)
    {
        var (status, page, _) = await b.GetTextAsync("/tokens");
        Assert.Equal(200, status);
        Assert.True(HiddenTokenForm.IsBlocked(page), "the create form is hidden");
        var r = await Post(b, page, "/tokens/create", true, ("nonce", HiddenTokenForm.StoreNonce(f.App.Services, dir, Ada)), ("label", "laptop"));
        var body = await r.Content.ReadAsStringAsync();
        return (page, r, body, TokenValue.Match(body) is { Success: true } t ? t.Value : null);
    }

    private static async Task<AdminBrowser> Connected(Site site)
    {
        var ids = await SeedChecksAsync(site.F);
        var b = await SignIn(site.F);
        Assert.Equal(HttpStatusCode.SeeOther, (await ConnectFrom(b, ids)).StatusCode);
        Assert.NotNull(await Verification(site.F));
        return b;
    }

    /// <summary>The v2 admin-consent link for the Packmoat app, written out by hand (#118): sign-in plus the four read-only Azure DevOps scopes.</summary>
    internal static string PackmoatConsentUrl(string directory) =>
        $"https://login.microsoftonline.com/{directory}/v2.0/adminconsent?client_id={AppClient}&redirect_uri=https%3A%2F%2F{AppHost}%2Fsetup%2Fazure%2Fconsented" +
        "&scope=openid%20profile%20499b84ac-1321-427f-aa17-267ca6975798%2Fvso.packaging%20499b84ac-1321-427f-aa17-267ca6975798%2Fvso.graph" +
        "%20499b84ac-1321-427f-aa17-267ca6975798%2Fvso.identity%20499b84ac-1321-427f-aa17-267ca6975798%2Fvso.project";
}
