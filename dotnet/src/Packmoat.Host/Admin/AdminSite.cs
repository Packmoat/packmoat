using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Modules;
using Packmoat.Host.Web;
using Packmoat.Infrastructure;
using static Packmoat.Host.Admin.AdminHtml;

namespace Packmoat.Host.Admin;

/// <summary>What the admin site was configured with. Built once at start. Never printed: it holds the client secret.</summary>
public sealed class AdminSettings
{
    public required string Host { get; init; }
    public Guid? TenantId { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public required AdminAllowlist Allow { get; init; }
    /// <summary>The only hosts a tenant created on the site may use as its upstream (lower-case).</summary>
    public IReadOnlySet<string> UpstreamHosts { get; init; } = new HashSet<string> { "pkgs.dev.azure.com" };
    public string? KeysDir { get; init; }
    /// <summary>True when Microsoft sign-in is fully configured (or a test supplied a fake provider). Otherwise every page answers 503.</summary>
    public bool SignInAvailable { get; init; }
    public string Authority => $"https://login.microsoftonline.com/{TenantId:D}/v2.0";
    public override string ToString() => $"AdminSettings({Host}, <redacted>)";
}

/// <summary>Slows repeated failed admin sign-ins from one address. Separate from the proxy's token limiter and from any other site's limiter.</summary>
public sealed class AdminSignInLimiter : SignInLimiter
{
    public AdminSignInLimiter(IClock clock, int maxFailures = 5, TimeSpan? window = null, ILogger? log = null) : base(clock, maxFailures, window, log) { }
}

/// <summary>
/// The operator admin site: pages and safe write actions on their own hostname, signed in with Microsoft (Entra ID) and limited to an allowlist.
/// It is a separate pipeline branch chosen by the Host header (the shared plumbing is <see cref="SignedInSite"/>). The proxy pipeline never sees
/// the admin cookie and the admin pipeline never reads a Packmoat token.
/// </summary>
public static class AdminSite
{
    public const string CookieScheme = "PackmoatAdminCookie";
    public const string RemoteScheme = "PackmoatAdminEntra";
    public const string CookieName = "__Host-packmoat-admin";
    public const string AntiforgeryCookieName = SignedInSite.AntiforgeryCookieName;
    public const string AntiforgeryField = SignedInSite.AntiforgeryField;
    /// <summary>Registered in Entra as https://admin.&lt;host&gt;/signin-oidc. Do not change it without changing the app registration.</summary>
    public const string CallbackPath = "/signin-oidc";
    public const int AuditLimit = 100;
    public const int ActiveDays = 30;
    /// <summary>The most decision record rows the tenant page shows at once (the panel says when more match).</summary>
    public const int DecisionsLimit = 100;
    public static readonly TimeSpan SlidingWindow = SignedInSite.SlidingWindow;
    public static readonly TimeSpan AbsoluteCap = SignedInSite.AbsoluteCap;
    public const string ContentSecurityPolicy = SignedInSite.ContentSecurityPolicy;

    // ---------------------------------------------------------------- configuration

    /// <summary>Returns null when the admin site is disabled (no PACKMOAT_ADMIN_HOST): nothing is registered and nothing is mapped.</summary>
    internal static AdminSettings? Configure(WebApplicationBuilder builder, PackmoatOptions options, DatabaseEngine engine)
    {
        var host = options.AdminHost?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(host)) return null;
        if (host.IndexOfAny(new[] { '/', ':', '*', ' ', ',', ';', '@', '?', '#' }) >= 0)
            throw new InvalidOperationException("PACKMOAT_ADMIN_HOST must be a bare hostname such as admin.example.com.");
        var proxyHosts = (options.AllowedHosts ?? "").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (proxyHosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("PACKMOAT_ADMIN_HOST must not also be listed in PACKMOAT_HOSTS: one hostname cannot be both the feed and the admin site.");

        Guid? tenant = Guid.TryParse(options.AdminTenantId, out var t) && t != Guid.Empty ? t : null;
        var allow = AdminAllowlist.Parse(options.AdminAllow, tenant, out _);
        var fake = options.AdminRemoteSchemeOverride;
        var configured = tenant is not null && !string.IsNullOrWhiteSpace(options.AdminClientId) && !string.IsNullOrWhiteSpace(options.AdminClientSecret);
        var settings = new AdminSettings
        {
            Host = host,
            TenantId = tenant,
            ClientId = options.AdminClientId?.Trim(),
            ClientSecret = options.AdminClientSecret,
            Allow = allow,
            UpstreamHosts = ParseHosts(options.AdminUpstreamHosts),
            KeysDir = SignedInSite.KeysDir(options, engine),
            SignInAvailable = configured || fake is not null,
        };

        var services = builder.Services;
        services.AddSingleton(settings);
        services.TryAddSingleton(allow);   // TryAdd: a test may register its own
        services.AddSingleton<AdminSignInLimiter>(sp => new AdminSignInLimiter(sp.GetRequiredService<IClock>(), log: sp.GetRequiredService<ILogger<AdminSignInLimiter>>()));
        services.TryAddSingleton(sp => new AdminCommands(sp.GetRequiredService<ITenantStore>(), sp.GetRequiredService<ITokenService>(), sp.GetRequiredService<IClock>(),
            alerts: sp.GetService<IOpsAlerts>()));
        services.TryAddSingleton(sp => new AdminFormNonces(sp.GetRequiredService<IClock>()));
        services.TryAddSingleton(sp => new AdminWriteLimiter(sp.GetRequiredService<IClock>()));
        services.TryAddSingleton(sp => new AdminQueries(sp.GetRequiredService<ITenantStore>(), sp.GetRequiredService<IAuditQuery>(),
            sp.GetRequiredService<ITokenService>(), sp.GetRequiredService<IActiveDeveloperQuery>(), decisions: sp.GetService<IDecisionRecordQuery>()));

        SignedInSite.AddShared(services, settings.KeysDir, CookieKeyStorages.Parse(options.CookieKeys));
        var auth = services.AddAuthentication();   // no default scheme: this site names its own schemes everywhere
        auth.AddCookie(CookieScheme, _ => { });
        services.AddOptions<CookieAuthenticationOptions>(CookieScheme).Configure<IClock>(ConfigureCookie);
        if (fake is not null) fake(auth);
        else if (configured) auth.AddOpenIdConnect(RemoteScheme, o => ConfigureOidc(o, settings));
        return settings;
    }

    internal static IReadOnlySet<string> ParseHosts(string? text)
    {
        var hosts = (text ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(h => h.Trim().ToLowerInvariant()).ToHashSet();
        return hosts.Count == 0 ? new HashSet<string> { "pkgs.dev.azure.com" } : hosts;
    }

    // Every check reads the list from the container at request time, so each of the four places that ask can be tested on its own.
    private static AdminAllowlist Allow(HttpContext ctx) => ctx.RequestServices.GetRequiredService<AdminAllowlist>();

    internal static void ConfigureCookie(CookieAuthenticationOptions o, IClock clock) =>
        SignedInSite.ConfigureCookie(o, clock, CookieName, CookieScheme, (ctx, p) => Allow(ctx).IsAllowed(p), () => new AdminSignInDeniedException());

    /// <summary>The Microsoft sign-in settings: authorization-code flow with PKCE, this one directory only, claims read as sent (tid and oid stay tid and oid).</summary>
    public static void ConfigureOidc(OpenIdConnectOptions o, AdminSettings s)
    {
        SignedInSite.ApplyCommonOidc(o, s.Host, CallbackPath, CookieScheme);
        o.Authority = s.Authority;
        o.ClientId = s.ClientId;
        o.ClientSecret = s.ClientSecret;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = s.Authority,           // this directory only; no multi-tenant "common" issuer pattern
            ValidateAudience = true,
            ValidAudience = s.ClientId,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            NameClaimType = "name",
        };
        o.Events.OnTokenValidated = ctx =>
        {
            var allow = ctx.HttpContext.RequestServices.GetRequiredService<AdminAllowlist>();
            if (!allow.IsAllowed(ctx.Principal)) { ctx.Fail(new AdminSignInDeniedException()); return Task.CompletedTask; }
            ctx.Principal = SignedInSite.Minimal(ctx.Principal!);
            return Task.CompletedTask;
        };
        o.Events.OnRemoteFailure = async ctx =>
        {
            SignedInSite.CountIfCodeReceived(ctx.HttpContext, ctx.HttpContext.RequestServices.GetRequiredService<AdminSignInLimiter>());
            var denied = ctx.Failure is AdminSignInDeniedException;
            ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Packmoat.Admin")
                .LogWarning("Admin sign-in refused ({Kind}, token {TokenError})", denied ? "not on the allowlist" : ctx.Failure?.GetType().Name ?? "unknown",
                    SignInReply.TokenLogCode(ctx.HttpContext, ctx.Failure));   // never the exception text or error_description: they can echo protocol values
            ctx.HandleResponse();
            await Write(ctx.HttpContext, denied ? 403 : 400, Page(denied ? "Not allowed" : "Sign-in failed",
                denied ? "<div class=\"box\"><h1>Not allowed</h1><p>This account is not allowed to use the admin site.</p></div>"
                       : "<div class=\"box\"><h1>Sign-in failed</h1><p>The sign-in could not be completed. <a href=\"/\">Try again</a>.</p></div>"));
        };
    }

    // ---------------------------------------------------------------- pipeline

    internal static SignedInSiteSpec Spec(AdminSettings settings) => new()
    {
        Name = "Admin",
        Host = settings.Host,
        CookieScheme = CookieScheme,
        RemoteScheme = RemoteScheme,
        CookieName = CookieName,
        CallbackPath = CallbackPath,
        Limiter = sp => sp.GetRequiredService<AdminSignInLimiter>(),
        Page = (title, body) => Page(title, body),
    };

    internal static void MapBranch(WebApplication app, AdminSettings settings) =>
        SignedInSite.Map(app, Spec(settings), ctx => Route(ctx, settings));

    internal static Task ErrorPage(HttpContext ctx, int status, string title, string message, int? retryAfterSeconds = null) =>
        SignedInSite.ErrorPage(ctx, status, title, message, retryAfterSeconds);

    internal static Task Write(HttpContext ctx, int status, string html) => SignedInSite.Write(ctx, status, html);

    internal static string SignOutForm(HttpContext ctx) => SignedInSite.SignOutForm(ctx);

    private static string AntiforgeryToken(HttpContext ctx) => SignedInSite.AntiforgeryToken(ctx);

    // ---------------------------------------------------------------- routes

    private static async Task Route(HttpContext ctx, AdminSettings s)
    {
        var path = ctx.Request.Path.Value ?? "/";
        var get = HttpMethods.IsGet(ctx.Request.Method);
        if (get && path == "/admin.css") { ctx.Response.ContentType = "text/css; charset=utf-8"; await ctx.Response.WriteAsync(Stylesheet, ctx.RequestAborted); return; }
        if (get && path == "/favicon.svg") { ctx.Response.ContentType = "image/svg+xml"; await ctx.Response.WriteAsync(OperatorFaviconSvg, ctx.RequestAborted); return; }
        if (get && path == "/") { await Landing(ctx, s); return; }
        if (!get && path == "/signin") { await SignIn(ctx, s); return; }
        if (!get && path == "/signout") { await SignOut(ctx); return; }

        // Everything below needs a signed-in, allowlisted person.
        var result = await ctx.AuthenticateAsync(CookieScheme);
        if (!result.Succeeded || !Allow(ctx).IsAllowed(result.Principal))
        {
            if (get) ctx.Response.Redirect("/"); else ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (!get)
        {
            if (!await AdminWrites.HandleAsync(ctx, s, result.Principal!)) await NotFound(ctx);   // a POST to anything that is not a write action
            return;
        }
        var queries = ctx.RequestServices.GetRequiredService<AdminQueries>();
        if (path == "/tenants") { await Tenants(ctx, queries); return; }
        if (path == "/actions") { await Actions(ctx); return; }
        if (path == "/audit") { await Audit(ctx, queries); return; }
        if (path.StartsWith("/tenants/", StringComparison.Ordinal) && path.Length > "/tenants/".Length && path.IndexOf('/', "/tenants/".Length) < 0)
        {
            await TenantPage(ctx, queries, path["/tenants/".Length..], result.Principal!);
            return;
        }
        await NotFound(ctx);
    }

    private static Task NotFound(HttpContext ctx) =>
        Write(ctx, 404, Page("Not found", "<div class=\"box\"><h1>Not found</h1></div>", SignOutForm(ctx)));

    /// <summary>What both 503 pages say when Microsoft sign-in is not configured on this server.</summary>
    private const string NotConfiguredBody = "<div class=\"box\"><h1>Packmoat operator console</h1><p>Sign-in is not configured on this server.</p></div>";

    private static async Task Landing(HttpContext ctx, AdminSettings s)
    {
        var existing = await ctx.AuthenticateAsync(CookieScheme);
        if (existing.Succeeded && Allow(ctx).IsAllowed(existing.Principal)) { ctx.Response.Redirect("/tenants"); return; }
        if (!s.SignInAvailable)
        {
            await Write(ctx, 503, Page("Not configured", NotConfiguredBody));
            return;
        }
        SignedInSite.UseLandingCsp(ctx);
        var t = ctx.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(ctx);
        await Write(ctx, 200, Page("Sign in",
            "<div class=\"signin-stage\"><section class=\"card\" aria-labelledby=\"signin-h\"><h1 id=\"signin-h\">Packmoat operator console</h1>" +
            "<p class=\"lede\">Manage tenants, read the audit log and run operator actions.</p>" +
            "<p>For Packmoat operators only. Sign in with your allowlisted Microsoft account.</p>" +
            $"<form class=\"signin-form\" method=\"post\" action=\"/signin\">{Hidden(t.FormFieldName, t.RequestToken!)}" +
            $"<button type=\"submit\" class=\"ms-signin\">{SiteChrome.MicrosoftLogoSvg}<span>Sign in with Microsoft</span></button></form></section></div>"));
    }

    private static async Task SignIn(HttpContext ctx, AdminSettings s)
    {
        if (!s.SignInAvailable) { await Write(ctx, 503, Page("Not configured", NotConfiguredBody)); return; }
        if (ctx.RequestServices.GetRequiredService<AdminSignInLimiter>().RetryAfterSeconds(ctx) is { } wait)
        {
            ctx.Response.Headers.RetryAfter = wait.ToString();
            await Write(ctx, 429, Page("Too many attempts", "<div class=\"box\"><p>Too many failed sign-ins. Wait a few minutes.</p></div>"));
            return;
        }
        await ctx.ChallengeAsync(RemoteScheme, new AuthenticationProperties { RedirectUri = "/tenants" });
    }

    private static async Task SignOut(HttpContext ctx)
    {
        await ctx.SignOutAsync(CookieScheme);
        ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
        ctx.Response.Headers.Location = "/";
    }

    /// <summary>A token's owner for the tenant page: display name and object id, or "none" for an operator-issued token. Encoded by Cell.</summary>
    internal static string OwnerText(TokenInfo k) => k.OwnerOid is null ? "none" : $"{k.OwnerName ?? "unknown"} ({k.OwnerOid})";

    private static string Time(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'");

    private static async Task Tenants(HttpContext ctx, AdminQueries q)
    {
        var tenants = await q.ListTenantsAsync(ActorScope.Operator, ctx.RequestAborted);
        var rows = string.Concat(tenants.OrderBy(t => t.Id, StringComparer.Ordinal).Select(t =>
            $"<tr><td><a href=\"/tenants/{E(Uri.EscapeDataString(t.Id))}\">{E(t.Id)}</a></td>{Cell(t.Name)}{Cell(t.Active ? "Active" : "Suspended")}{Cell(t.Policy.CooldownDays.ToString())}{Cell(t.Policy.InternalPrefixes.Count.ToString())}{Cell(t.Policy.Waivers.Count.ToString())}</tr>"));
        await Write(ctx, 200, Page("Tenants",
            "<h1>Tenants</h1>" + AdminWrites.Notice(ctx, ctx.Request.Query["r"].ToString()) +
            "<table><tr><th>Id</th><th>Name</th><th>Status</th><th>Cooldown days</th><th>Internal prefixes</th><th>Waivers</th></tr>" + rows + "</table>" +
            (tenants.Count == 0 ? "<p class=\"muted\">No tenants yet.</p>" : "") +
            AdminWrites.CreateTenantForm(AntiforgeryToken(ctx)), SignOutForm(ctx)));
    }

    private static async Task TenantPage(HttpContext ctx, AdminQueries q, string id, ClaimsPrincipal who)
    {
        var op = ActorScope.Operator;
        var ct = ctx.RequestAborted;
        // GetTenantAsync, not ShowTenantAsync: the upstream identity (it holds the customer's private key) is never even read here.
        var t = id.Length > 200 ? null : await q.GetTenantAsync(op, id, ct);
        if (t is null) { await NotFound(ctx); return; }
        var tokens = await q.ListTokensAsync(op, id, ct);
        var active = await q.ActiveDevelopersAsync(op, id, ActiveDays, ct);
        // The binding only: which app and which directory. It reads no key column and decrypts nothing.
        var binding = await q.GetUpstreamBindingAsync(op, id, ct);
        var lastDay = active.Tokens.ToDictionary(a => a.TokenId, a => a.LastActiveDay);
        // The serving gate's answer, for the admin-site module's section (#193): its line for the operator and its own forms. A gate that
        // throws costs the page (the site's error page), as before.
        var serving = await ctx.RequestServices.GetRequiredService<IServingGate>().AskAsync(t.Id, ct);
        var af = AntiforgeryToken(ctx);
        var module = ctx.RequestServices.GetRequiredService<IAdminSiteModule>();
        var section = await module.TenantSectionAsync(new AdminTenantPage(ctx, who, t, null, module, af), serving);

        string Table(string[] heads, IEnumerable<string> rows) =>
            "<table><tr>" + string.Concat(heads.Select(h => $"<th>{E(h)}</th>")) + "</tr>" + string.Concat(rows) + "</table>";

        string Act(string verb) => AdminWrites.TenantAction(t.Id, verb);
        string One(string verb, string label, params (string Name, string Value)[] hidden) =>
            AdminWrites.PostForm(Act(verb), af, string.Concat(hidden.Select(h => Hidden(h.Name, h.Value))), label, "inline");
        string Del(string verb, string label, string field, string value) => $"<td>{One(verb, label, (field, value))}</td>";

        var body = AdminWrites.Notice(ctx, ctx.Request.Query["r"].ToString()) +
            $"<h1>{E(t.Name)} <span class=\"muted\">({E(t.Id)})</span></h1>" +
            $"<p>Status: <strong>{(t.Active ? "Active" : "Suspended")}</strong>. Cooldown: {t.Policy.CooldownDays} days. Policy revision {t.Policy.Revision}. <a href=\"/audit?tenant={E(Uri.EscapeDataString(t.Id))}\">Audit trail</a> <a href=\"#decisions\">Decisions</a></p>" +
            $"<p>Active developers, last {ActiveDays} days: <strong>{active.Count}</strong></p>" +
            $"<p>Upstream: <strong>{E(UpstreamText(t))}</strong></p>" +
            $"<p>Upstream identity: <strong>{E(IdentityText(binding))}</strong></p>" +
            section +
            "<h2>Tenant</h2>" +
            (t.Active ? One("suspend", "Suspend tenant") : One("activate", "Activate tenant")) +
            AdminWrites.PostForm(Act("cooldown"), af, AdminWrites.Field("days", "Cooldown days", 4, value: t.Policy.CooldownDays.ToString()), "Set cooldown", "inline") +
            "<h2>Internal prefixes</h2>" + Table(new[] { "Prefix", "" }, t.Policy.InternalPrefixes.Select(p => $"<tr>{Cell(p)}{Del("prefix-remove", "Remove", "prefix", p)}</tr>")) +
            AdminWrites.PostForm(Act("prefix-add"), af, AdminWrites.Field("prefix", "Prefix", 100, hint: "for example Contoso."), "Add prefix", "inline") +
            "<h2>Package overrides</h2>" + Table(new[] { "Package", "Cooldown days", "" }, t.Policy.PackageOverrides.Select(o => $"<tr>{Cell(o.PackageId)}{Cell(o.CooldownDays.ToString())}{Del("override-remove", "Remove", "package", o.PackageId)}</tr>")) +
            AdminWrites.PostForm(Act("override-set"), af, AdminWrites.Field("package", "Package", 100) + AdminWrites.Field("days", "Cooldown days", 4), "Set override", "inline") +
            "<h2>Waivers</h2>" + Table(new[] { "Id", "Package", "Version", "Expires", "Reason", "Created by", "Source", "" },
                t.Policy.Waivers.Select(w => $"<tr>{Cell(w.Id)}{Cell(w.PackageId)}{Cell(w.Version)}{Cell(Time(w.ExpiresAt))}{Cell(w.Reason)}{Cell(w.CreatedBy)}" +
                    $"{Cell(w.Source == WaiverSource.Customer ? "customer" : "operator")}{Del("waiver-revoke", "Revoke", "waiver", w.Id)}</tr>")) +
            AdminWrites.PostForm(Act("waiver-add"), af,
                AdminWrites.Field("package", "Package", 100) + AdminWrites.Field("version", "Version", 64, hint: "or *") + AdminWrites.Field("days", "Days", 4, hint: "1 to 3650") +
                "<br>" + AdminWrites.Field("reason", "Reason", AdminInput.MaxReason), "Add waiver") +
            "<h2>Tokens</h2>" + Table(new[] { "Id", "Label", "Kind", "Owner", "Issued via", "Created", "Expires", "Revoked", $"Last active day ({ActiveDays} days)", "" },
                tokens.Select(k => $"<tr>{Cell(k.TokenId)}{Cell(k.Label)}{Cell(k.Kind.ToString())}{Cell(OwnerText(k))}{Cell(k.IssuedVia == TokenIssuer.Self ? "self (customer site)" : "operator")}" +
                    $"{Cell(Time(k.CreatedAt))}{Cell(k.ExpiresAt is { } x ? Time(x) : "never")}{Cell(k.RevokedAt is { } r ? Time(r) + (k.RevokedBy is { } by ? " by " + by : "") : "")}{Cell(lastDay.TryGetValue(k.TokenId, out var day) ? day.ToString("yyyy-MM-dd") : "")}" +
                    (k.RevokedAt is null ? Del("token-revoke", "Revoke", "token", k.TokenId) : "<td></td>") + "</tr>")) +
            AdminWrites.PostForm(Act("token-issue"), af,
                Hidden("nonce", ctx.RequestServices.GetRequiredService<AdminFormNonces>().Issue(t.Id)) +
                AdminWrites.Field("label", "Label", AdminInput.MaxLabel, hint: "by role or machine, not a person's name where you can") +
                "<label>Kind <select name=\"kind\"><option value=\"developer\">developer</option><option value=\"automation\">automation</option></select></label>", "Issue token") +
            "<p class=\"muted\">The token is shown once, on the next page. It cannot be shown again. Upstream identity and certificates are set with the command line only.</p>" +
            await DecisionsPanel(ctx, q, t.Id);
        await Write(ctx, 200, Page("Tenant " + t.Id, body, SignOutForm(ctx)));
    }

    /// <summary>
    /// The read-only "Decisions" panel (#123): the tenant's decision record, newest last seen first, at most <see cref="DecisionsLimit"/>
    /// rows, searched by package (exact, or a prefix) and version with a GET form on the tenant page. Read through AdminQueries as the
    /// operator, never from the database here. Package ids and versions are whatever clients asked for: every value is encoded, and a
    /// refused search shows a fixed message, never the error text.
    /// </summary>
    private static async Task<string> DecisionsPanel(HttpContext ctx, AdminQueries q, string tenantId)
    {
        var package = ctx.Request.Query["package"].ToString();
        var match = ctx.Request.Query["match"].ToString();
        var version = ctx.Request.Query["version"].ToString();
        var prefix = match == "prefix";
        var html = "<h2 id=\"decisions\">Decisions</h2>" +
            $"<p class=\"muted\">The decision record: what Packmoat decided for each version this tenant's feed asked about, and why. Newest {DecisionsLimit} rows by last seen. " +
            "The Decisions column counts decisions (listings and downloads alike, not downloads). Package ids and versions are as clients sent them.</p>" +
            $"<form method=\"get\" action=\"/tenants/{E(Uri.EscapeDataString(tenantId))}#decisions\">" +
            $"<label>Package <input name=\"package\" value=\"{E(package)}\" maxlength=\"{RecordingPackageGate.MaxFieldLength}\"></label> " +
            $"<label>Match <select name=\"match\"><option value=\"exact\"{(prefix ? "" : " selected")}>exact</option><option value=\"prefix\"{(prefix ? " selected" : "")}>starts with</option></select></label> " +
            $"<label>Version <input name=\"version\" value=\"{E(version)}\" maxlength=\"{RecordingPackageGate.MaxFieldLength}\"></label> " +
            "<button type=\"submit\">Search</button></form>";
        if (!q.HasDecisionRecord) return html + "<p class=\"muted\">The decision record is not available on this server.</p>";
        if (match is not ("" or "exact" or "prefix")) return html + "<p>That search is not valid. Choose exact or starts with.</p>";

        DecisionRecordPage page;
        try
        {
            page = await q.DecisionsAsync(ActorScope.Operator, new DecisionRecordQuery(tenantId, package, prefix, version, DecisionsLimit), ctx.RequestAborted);
        }
        catch (ArgumentException)
        {
            return html + $"<p>That search is not valid. A version needs a package, a starts-with search needs some text, and each is at most {RecordingPackageGate.MaxFieldLength} characters.</p>";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A storage failure (a locked table, a missing index) costs the panel, never the rest of the tenant page. The type only:
            // a message could carry the SQL or a typed value.
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Packmoat.Admin")
                .LogWarning("Decisions panel could not read the decision record ({Kind})", ex.GetType().Name);
            return html + "<p>The decision record could not be read.</p>";
        }
        var rows = string.Concat(page.Rows.Select(r =>
            $"<tr>{Cell(r.PackageId)}{Cell(r.Version)}{Cell(r.Decision.ToString())}{Cell(r.Reason.ToString())}{Cell(r.CooldownDays?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none")}" +
            $"{Cell(r.PublishedAt is { } p ? Time(p) : "unknown")}{Cell(Time(r.FirstSeenAt))}{Cell(Time(r.LastSeenAt))}{Cell(r.SeenCount.ToString(System.Globalization.CultureInfo.InvariantCulture))}" +
            $"{Cell(r.PolicyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture))}{Cell(r.WaiverId)}</tr>"));
        return html +
            "<table><tr><th>Package</th><th>Version</th><th>Decision</th><th>Reason</th><th>Cooldown days</th><th>Published</th><th>First seen</th><th>Last seen</th><th>Decisions</th><th>Policy revision</th><th>Waiver</th></tr>" +
            rows + "</table>" +
            (page.Rows.Count == 0 ? "<p class=\"muted\">No decisions recorded for this search.</p>" : "") +
            (page.More ? "<p class=\"muted\">More rows match: search for a package to narrow it.</p>" : "");
    }

    /// <summary>
    /// The upstream host, path and prefix (no scheme: the site's pages carry no URL to elsewhere), or "not set" (a self-made organisation
    /// before setup). Encoded by the caller.
    /// </summary>
    internal static string UpstreamText(Tenant t) =>
        Uri.TryCreate(t.UpstreamBase, UriKind.Absolute, out var u) ? u.Authority + u.AbsolutePath + t.AllowedPathPrefix : "not set";

    internal static string IdentityText(UpstreamBinding? b) => b switch
    {
        null => "not set",
        { Kind: IdentityKind.Shared } => $"Packmoat app, directory {b.AzureTenantId}",
        _ => "own app",   // its ids stay off the site, as before; the command line shows them
    };

    private static async Task Audit(HttpContext ctx, AdminQueries q)
    {
        var tenant = ctx.Request.Query["tenant"].ToString();
        if (tenant.Length > 200) { await NotFound(ctx); return; }
        var events = await q.AuditTailAsync(ActorScope.Operator, string.IsNullOrWhiteSpace(tenant) ? null : tenant, AuditLimit, ctx.RequestAborted);
        var rows = string.Concat(events.Select(e =>
            $"<tr>{Cell(Time(e.Time))}{Cell(e.TenantId)}{Cell(e.Kind.ToString())}{Cell(e.PackageId)}{Cell(e.Version)}{Cell(e.Decision.ToString())}{Cell(e.Reason.ToString())}{Cell(e.WaiverId)}</tr>"));
        await Write(ctx, 200, Page("Audit",
            $"<h1>Audit trail</h1><p class=\"muted\">Newest {AuditLimit} decisions{(string.IsNullOrWhiteSpace(tenant) ? ", all tenants" : ", tenant " + E(tenant))}.</p>" +
            "<table><tr><th>Time</th><th>Tenant</th><th>Kind</th><th>Package</th><th>Version</th><th>Decision</th><th>Reason</th><th>Waiver</th></tr>" + rows + "</table>", SignOutForm(ctx)));
    }

    public const int ActionsLimit = 100;

    private static async Task Actions(HttpContext ctx)
    {
        var rows = await ctx.RequestServices.GetRequiredService<IAdminActionLog>().LatestAsync(ActionsLimit, ctx.RequestAborted);
        var html = string.Concat(rows.Select(a =>
            $"<tr>{Cell(Time(a.Time))}{Cell(a.ActorName)}{Cell(a.ActorTid)}{Cell(a.ActorOid)}{Cell(a.Action)}{Cell(a.TenantId)}{Cell(a.Detail)}{Cell(a.Site)}</tr>"));
        await Write(ctx, 200, Page("Actions",
            $"<h1>Admin actions</h1><p class=\"muted\">The latest {ActionsLimit} changes made from this site, plus customer directory links made with the command line (site \"cli\"). Rows ending in .failed are attempts that did not take effect. Other command-line changes are not listed here.</p>" +
            "<table><tr><th>Time</th><th>Who</th><th>Directory (tid)</th><th>User (oid)</th><th>Action</th><th>Tenant</th><th>Detail</th><th>Site</th></tr>" + html + "</table>", SignOutForm(ctx)));
    }
}
