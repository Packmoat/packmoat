using Packmoat.Host.Modules;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host;
using Packmoat.Host.Admin;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>What the pretend Microsoft says about whoever "signs in" next. Tests set <see cref="Next"/>; nothing here talks to Microsoft.</summary>
public sealed class FakeIdentityProvider
{
    public ClaimsPrincipal? Next { get; set; }
    /// <summary>Makes the pretend callback fail AFTER it has signed the cookie in, to show an error response never carries the new session.</summary>
    public bool ThrowAfterSignIn { get; set; }
    /// <summary>The properties of the last challenge (what the real handler would send to Microsoft as prompt and max_age).</summary>
    public AuthenticationProperties? LastChallenge { get; set; }

    public static ClaimsPrincipal Person(string? tid, string? oid, string? email = null, string name = "Test Founder")
    {
        var claims = new List<Claim> { new("name", name) };
        if (tid is not null) claims.Add(new Claim("tid", tid));
        if (oid is not null) claims.Add(new Claim("oid", oid));
        if (email is not null) { claims.Add(new Claim("email", email)); claims.Add(new Claim("preferred_username", email)); }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "FakeEntra", "name", ClaimsIdentity.DefaultRoleClaimType));
    }
}

/// <summary>
/// Stands in for the Microsoft (OpenID Connect) handler. The challenge sends the browser straight to our own callback path, and the callback
/// signs the cookie scheme in with whatever <see cref="FakeIdentityProvider.Next"/> says: the same call the real handler makes after validating a token.
/// </summary>
public sealed class FakeEntraHandler : AuthenticationHandler<AuthenticationSchemeOptions>, IAuthenticationRequestHandler
{
    public FakeEntraHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, Microsoft.Extensions.Logging.ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : base(options, logger, encoder) { }

    public async Task<bool> HandleRequestAsync()
    {
        if (Request.Path != AdminSite.CallbackPath) return false;
        var person = Context.RequestServices.GetRequiredService<FakeIdentityProvider>().Next;
        if (person is null) { Response.StatusCode = 400; return true; }
        await Context.SignInAsync(AdminSite.CookieScheme, person);
        if (Context.RequestServices.GetRequiredService<FakeIdentityProvider>().ThrowAfterSignIn) throw new InvalidOperationException("boom after sign-in");
        Response.Redirect("/tenants");
        return true;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Redirect(AdminSite.CallbackPath + "?fake=1");
        return Task.CompletedTask;
    }
}

/// <summary>A tiny browser: keeps cookies by hand (the real ones are Secure and this test server speaks plain http).</summary>
public sealed class AdminBrowser
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    private readonly Dictionary<string, string> _cookies = new();
    public string BaseUrl { get; }
    public string Host { get; set; }
    public List<string> SetCookieLog { get; } = new();

    public AdminBrowser(string baseUrl, string host) { BaseUrl = baseUrl; Host = host; }

    public bool HasCookie(string name) => _cookies.ContainsKey(name);
    public string? Cookie(string name) => _cookies.TryGetValue(name, out var v) ? v : null;
    public void SetCookie(string name, string value) => _cookies[name] = value;
    public IReadOnlyDictionary<string, string> Cookies => _cookies;

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, IEnumerable<KeyValuePair<string, string>>? form = null,
        Action<HttpRequestMessage>? tweak = null, bool sendCookies = true, string? host = null)
    {
        var req = new HttpRequestMessage(method, BaseUrl + path);
        req.Headers.Host = host ?? Host;
        req.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");   // what Caddy sends; the antiforgery system refuses to run on a request it thinks is plain http
        // A page load, as a browser sends it (the customer site starts a sign-in only for one). Tests of icon fetches change it in tweak.
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*;q=0.8");
        if (form is not null) req.Content = new FormUrlEncodedContent(form);
        if (sendCookies && _cookies.Count > 0) req.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", _cookies.Select(c => c.Key + "=" + c.Value)));
        tweak?.Invoke(req);
        var res = await _http.SendAsync(req);
        if (res.Headers.TryGetValues("Set-Cookie", out var sets))
            foreach (var sc in sets)
            {
                SetCookieLog.Add(sc);
                var first = sc.Split(';')[0];
                var eq = first.IndexOf('=');
                var name = first[..eq];
                var value = first[(eq + 1)..];
                if (value.Length == 0 || sc.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)) _cookies.Remove(name);
                else _cookies[name] = value;
            }
        return res;
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? host = null) => SendAsync(HttpMethod.Get, path, host: host);
    public async Task<(int Status, string Body, HttpResponseMessage Response)> GetTextAsync(string path)
    {
        var r = await GetAsync(path);
        return ((int)r.StatusCode, await r.Content.ReadAsStringAsync(), r);
    }

    public static string? Field(string html) => Regex.Match(html, "name=\"" + AdminSite.AntiforgeryField + "\" value=\"([^\"]+)\"") is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    /// <summary>Loads the landing page for its antiforgery token, posts the sign-in, follows to the fake provider's callback. Returns the callback response.</summary>
    public async Task<HttpResponseMessage> SignInAsync()
    {
        var landing = await GetAsync("/");
        var field = Field(await landing.Content.ReadAsStringAsync()) ?? throw new InvalidOperationException("no antiforgery field on the landing page");
        var start = await SendAsync(HttpMethod.Post, "/signin", new[] { new KeyValuePair<string, string>(AdminSite.AntiforgeryField, field) });
        if (start.StatusCode != HttpStatusCode.Redirect) return start;
        return await GetAsync(start.Headers.Location!.OriginalString);
    }

    /// <summary>Fetches a page that carries the sign-out form and returns its antiforgery token.</summary>
    public async Task<string> TokenFromAsync(string page)
    {
        var html = await (await GetAsync(page)).Content.ReadAsStringAsync();
        return Field(html) ?? throw new InvalidOperationException("no antiforgery field on " + page);
    }
}

/// <summary>
/// The admin and customer sites through the running host, on a real database (the core's, shared with the hosted tests, which
/// compile it in). It runs <see cref="TestModules.Server"/>, the tracks of <see cref="TestTracks.Modules"/>, the creation participants of
/// <see cref="TestLifecycle.Participants"/>, and the stores and serving gate of <see cref="TestModules"/>: none in the core's tests, the
/// hosted module's in the hosted tests.
/// </summary>
public sealed partial class AdminSiteFixture : IAsyncDisposable
{
    public const string AdminHost = "admin.packmoat.test";
    public const string Tid = "11111111-1111-4111-8111-111111111111";
    public const string Oid = "22222222-2222-4222-8222-222222222222";
    public const string ClientSecret = "client-secret-DO-NOT-LEAK-7f3a";
    public const string PemMarker = "PEM-BODY-DO-NOT-LEAK-91ce";

    public FakeUpstream Upstream { get; private set; } = null!;
    public FakeClock Clock { get; } = new();
    public FakeIdentityProvider Identity { get; } = new();
    public SqliteDatabase Db { get; private set; } = null!;
    public SqliteTenantStore Tenants { get; private set; } = null!;
    public SqliteTokenService Tokens { get; private set; } = null!;
    public SqliteAuditLog Audit { get; private set; } = null!;
    public SqliteTokenActivity Activity { get; private set; } = null!;
    public SqliteAdminActionLog Actions { get; private set; } = null!;
    public SqliteSignInDirectoryStore Directories { get; private set; } = null!;
    public SqliteMemberStore Members { get; private set; } = null!;
    public SqliteAutoJoinSettingStore AutoJoin { get; private set; } = null!;
    /// <summary>Writes decision record rows for the admin panel to read (the site reads them through IDecisionRecordQuery).</summary>
    public SqliteDecisionRecordLog Decisions { get; private set; } = null!;
    /// <summary>What the pretend Microsoft says on the customer site (see CustomerSiteTests).</summary>
    public FakeIdentityProvider AppIdentity { get; } = new();
    public string MasterKey { get; } = AesGcmSecretProtector.GenerateKeyBase64();
    public string BaseUrl { get; private set; } = "";
    private readonly TempDir _temp = TempDir.Create("packmoat-admin-", clearSqlitePools: true);
    public string Dir => _temp.Path;
    public string KeysDir => Path.Combine(Dir, "admin-keys");
    public WebApplication App { get; private set; } = null!;

    public AdminBrowser Browser(string? host = AdminHost) => new(BaseUrl, host ?? AdminHost);

    /// <summary>Adds the allowlisted operator as the next identity.</summary>
    public void SignInAs(string? tid, string? oid, string? email = null) => Identity.Next = FakeIdentityProvider.Person(tid, oid, email);

    /// <param name="fixtureServices">Like <paramref name="services"/>, with the fixture (its database and stores already made) to build from.</param>
    /// <param name="modules">The modules to run (default: <see cref="TestModules.Server"/>).</param>
    public static async Task<AdminSiteFixture> StartAsync(Action<PackmoatOptions>? configure = null, IAuditQuery? auditQuery = null, bool fakeProvider = true, string? keysDir = null, Action<IServiceCollection>? services = null,
        Action<AdminSiteFixture, IServiceCollection>? fixtureServices = null, IReadOnlyList<IPackmoatModule>? modules = null)
    {
        var f = new AdminSiteFixture();
        try
        {
            await f.BuildAndStartAsync(configure, auditQuery, fakeProvider, keysDir, services, fixtureServices, modules);
            return f;
        }
        catch
        {
            await f.DisposeAsync();   // the folder, and whatever started before the failure
            throw;
        }
    }

    private async Task BuildAndStartAsync(Action<PackmoatOptions>? configure, IAuditQuery? auditQuery, bool fakeProvider, string? keysDir, Action<IServiceCollection>? services,
        Action<AdminSiteFixture, IServiceCollection>? fixtureServices, IReadOnlyList<IPackmoatModule>? modules)
    {
        var f = this;
        f.Upstream = await FakeUpstream.StartAsync();
        f.Db = new SqliteDatabase(Path.Combine(f.Dir, "packmoat.db"));
        await f.Db.MigrateAsync(TestTracks.Modules);
        f.Tenants = new SqliteTenantStore(f.Db, f.Clock, AesGcmSecretProtector.FromBase64(f.MasterKey), TestLifecycle.Participants);
        f.Tokens = new SqliteTokenService(f.Db, f.Tenants, f.Clock, TestModules.Gate(f.Db, f.Clock));
        f.Audit = new SqliteAuditLog(f.Db);
        f.Activity = new SqliteTokenActivity(f.Db, f.Clock);
        f.Actions = new SqliteAdminActionLog(f.Db);
        f.Directories = new SqliteSignInDirectoryStore(f.Db);
        f.Members = new SqliteMemberStore(f.Db);
        f.AutoJoin = new SqliteAutoJoinSettingStore(f.Db);
        f.Decisions = new SqliteDecisionRecordLog(f.Db);
        await f.Tenants.CreateAsync(new NewTenant("acme", "Acme", f.Upstream.BaseUrl, "org/", f.Upstream.FeedIndexUrl, 14, TenantOrigin.Operator), default);
        await f.Tenants.CreateAsync(new NewTenant("globex", "Globex", f.Upstream.BaseUrl, "org/", f.Upstream.FeedIndexUrl, 7, TenantOrigin.Operator), default);
        f.SignInAs(Tid, Oid);

        f._build = () => f.BuildApp(configure, auditQuery, fakeProvider, keysDir, services, fixtureServices, modules);
        f.App = f._build();
        await f.App.StartAsync();
        f.BaseUrl = AddressOf(f.App);
    }

    private Func<WebApplication>? _build;

    private static string AddressOf(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("localhost", "127.0.0.1").TrimEnd('/');

    /// <summary>
    /// Another copy of the server, built exactly as <see cref="App"/> was (the same database, the same stores and fakes, the same cookie
    /// keys folder), on a port of its own: for what one copy keeps and another must find.
    /// </summary>
    public async Task<ServerCopy> StartCopyAsync()
    {
        var app = (_build ?? throw new InvalidOperationException("The fixture has not started."))();
        try
        {
            await app.StartAsync();
            return new ServerCopy(app, AddressOf(app));
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    /// <summary>A second copy of the server (<see cref="StartCopyAsync"/>): its app and the address it answers on.</summary>
    public sealed record ServerCopy(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public AdminBrowser Browser(string host) => new(BaseUrl, host);

        public async ValueTask DisposeAsync()
        {
            try { await App.StopAsync(); }
            finally { await App.DisposeAsync(); }
        }
    }

    private WebApplication BuildApp(Action<PackmoatOptions>? configure, IAuditQuery? auditQuery, bool fakeProvider, string? keysDir, Action<IServiceCollection>? services,
        Action<AdminSiteFixture, IServiceCollection>? fixtureServices, IReadOnlyList<IPackmoatModule>? modules)
    {
        var f = this;
        return PackmoatApp.Build(new[] { "--urls", "http://127.0.0.1:0" }, modules ?? TestModules.Server, s =>
        {
            s.AddSingleton<IClock>(f.Clock);
            s.AddSingleton<IPublishDateSource>(new FakeDateSource());
            s.AddSingleton<ITenantStore>(f.Tenants);
            s.AddSingleton<ITokenService>(f.Tokens);
            s.AddSingleton<IActiveDeveloperQuery>(f.Activity);
            s.AddSingleton<IAuditQuery>(auditQuery ?? f.Audit);
            s.AddSingleton<ITenantAuthenticator>(new CachedTenantAuthenticator(f.Tokens, f.Clock));
            s.AddSingleton<IAuditLog>(f.Audit);
            s.AddSingleton<IAdminActionLog>(f.Actions);
            s.AddSingleton<IUpstreamCredentialSource>(new StaticBasicCredentialSource(FakeUpstream.User, FakeUpstream.Secret));
            s.AddSingleton(f.Identity);
            s.AddSingleton<ISignInDirectoryStore>(f.Directories);
            s.AddKeyedSingleton("app", f.AppIdentity);
            s.AddSingleton<IMemberStore>(f.Members);
            TestModules.AddStores(f, s);
            s.AddSingleton<IAutoJoinSettingStore>(f.AutoJoin);
            s.AddSingleton<IDecisionRecordQuery>(new SqliteDecisionRecordQuery(f.Db));
            services?.Invoke(s);
            fixtureServices?.Invoke(f, s);
        }, o =>
        {
            o.AdminHost = AdminHost;
            o.AdminTenantId = Tid;
            o.AdminClientId = "33333333-3333-4333-8333-333333333333";
            o.AdminClientSecret = ClientSecret;
            o.AdminAllow = Tid + ":" + Oid;
            o.AdminKeysDir = keysDir ?? f.KeysDir;
            if (fakeProvider) o.AdminRemoteSchemeOverride = auth => auth.AddScheme<AuthenticationSchemeOptions, FakeEntraHandler>(AdminSite.RemoteScheme, null);
            TestOperator.Apply(o);   // the hosted wording; a test of the unset settings sets o.Operator itself
            configure?.Invoke(o);
        });
    }

    /// <summary>An Azure Artifacts feed URL that passes the strict rules (https, a path prefix): what a set-up tenant has.</summary>
    public const string StrictFeed = "https://pkgs.dev.azure.com/org/proj/_packaging/feed/nuget/v3/index.json";

    /// <summary>
    /// Gives a tenant strict https feed URLs (org/) instead of the fake upstream's http ones. The customer site issues tokens only for a
    /// tenant whose upstream is set up; proxied requests of this tenant no longer reach the fake upstream.
    /// </summary>
    public async Task UseStrictFeedUrlsAsync(string tenantId)
    {
        await using var c = await Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE tenants SET upstream_base='https://pkgs.dev.azure.com/', allowed_path_prefix='org/', feed_index_url=@f WHERE id=@id";
        cmd.Parameters.AddWithValue("@f", StrictFeed);
        cmd.Parameters.AddWithValue("@id", tenantId);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
    }

    /// <summary>A set-up own-app tenant, like e2e-test: strict feed URLs and its own app registration in <paramref name="directory"/>.</summary>
    public async Task SetUpOwnAppAsync(string tenantId, string directory)
    {
        await UseStrictFeedUrlsAsync(tenantId);
        await Tenants.SetUpstreamIdentityAsync(tenantId, new UpstreamIdentity(directory, "99999999-9999-4999-8999-999999999999", "-----BEGIN PRIVATE KEY-----\nTEST\n-----END PRIVATE KEY-----"), default);
    }

    /// <summary>Stops what <see cref="StartAsync"/> started (a failed start may have got only part way) and deletes the folder.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (App is not null)
            {
                try { await App.StopAsync(); }
                finally { await App.DisposeAsync(); }
            }
        }
        finally
        {
            try { if (Upstream is not null) await Upstream.DisposeAsync(); }
            finally { _temp.Dispose(); }
        }
    }
}
