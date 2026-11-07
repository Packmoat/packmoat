using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Auth;

namespace Packmoat.Host.Web;

/// <summary>Slows repeated failed sign-ins from one address. Each site has its own, separate from the proxy's token limiter, so none can lock out another.</summary>
public class SignInLimiter
{
    private readonly FailedAuthLimiter _inner;
    public SignInLimiter(IClock clock, int maxFailures = 5, TimeSpan? window = null, ILogger? log = null) =>
        _inner = new FailedAuthLimiter(clock, maxFailures, window ?? TimeSpan.FromMinutes(10), log: log);
    public int? RetryAfterSeconds(HttpContext c) => _inner.RetryAfterSeconds(Address(c));
    public void RecordFailure(HttpContext c) => _inner.RecordFailure(Address(c));
    private static string Address(HttpContext c) => ClientAddress.Key(c);
}

/// <summary>Raised when something tries to start a session for a person the site does not let in. Mapped to 403 and counted as a failed sign-in; no cookie is written.</summary>
public class SignInDeniedException : Exception
{
    public SignInDeniedException(string message) : base(message) { }
    /// <summary>The heading of the 403 page. Fixed text: it never echoes a claim the person did not need to see.</summary>
    public virtual string Title => "Not allowed";
    /// <summary>The sentence on the 403 page.</summary>
    public virtual string UserMessage => "You are not allowed to do this.";
    /// <summary>What the person can do about it, for sites that show a next step. Fixed text. Null: the site's generic advice.</summary>
    public virtual string? NextStep => null;
}

internal sealed class ClockTimeProvider : TimeProvider
{
    private readonly IClock _clock;
    public ClockTimeProvider(IClock clock) => _clock = clock;
    public override DateTimeOffset GetUtcNow() => _clock.UtcNow;
}

/// <summary>What one signed-in site is: its hostname, its own scheme names, cookie and callback path, its limiter and how its pages look.</summary>
public sealed class SignedInSiteSpec
{
    /// <summary>Short name used for the log category (Packmoat.{Name}).</summary>
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required string CookieScheme { get; init; }
    public required string RemoteScheme { get; init; }
    public required string CookieName { get; init; }
    public required string CallbackPath { get; init; }
    /// <summary>An optional second remote scheme (the customer site's Azure DevOps check) with its own callback, handled and limited like the first.</summary>
    public string? ExtraRemoteScheme { get; init; }
    public string? ExtraCallbackPath { get; init; }
    public string SignInPath { get; init; } = "/signin";
    public long MaxRequestBodyBytes { get; init; } = 4096;
    /// <summary>This site's failed sign-in limiter.</summary>
    public required Func<IServiceProvider, SignInLimiter> Limiter { get; init; }
    /// <summary>Wraps a body in the site's page chrome: (title, body html) -> full page.</summary>
    public required Func<string, string, string> Page { get; init; }
    /// <summary>The body of an error page: (title, message, next step or null) -> body html. All three are plain text; the function encodes them. Null: a plain box.</summary>
    public Func<string, string, string?, string>? ErrorBody { get; init; }
    /// <summary>The body of the 403 page for a refused sign-in, built from the refusal's fixed text. Null: the error body above.</summary>
    public Func<SignInDeniedException, string>? DeniedBody { get; init; }
    public string LogCategory => "Packmoat." + Name;
}

/// <summary>
/// The parts every signed-in site shares: security headers, error mapping, request limits, per-site authentication,
/// the antiforgery gate, cookie settings and the Microsoft sign-in plumbing. Each site is a separate pipeline branch chosen by the Host
/// header, authenticates only with its own named schemes and runs only its own sign-in callback handler.
/// </summary>
public static class SignedInSite
{
    /// <summary>
    /// One antiforgery cookie name for every site. The __Host- prefix makes the browser keep it per hostname (Secure, Path=/, no Domain),
    /// so the admin host's cookie is never sent to another site's host and the other way round.
    /// </summary>
    public const string AntiforgeryCookieName = "__Host-packmoat-af";
    public const string AntiforgeryField = "__pm_af";
    public static readonly TimeSpan SlidingWindow = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan AbsoluteCap = TimeSpan.FromHours(8);
    internal const string StartedItem = "packmoat.started";
    private const string CodeReceivedItem = "packmoat.code-received";

    public const string ContentSecurityPolicy = "default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
    // Chrome applies form-action to the redirect that follows a form post, so a page with a sign-in form must allow Microsoft's login host.
    public const string LandingContentSecurityPolicy = "default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self' https://login.microsoftonline.com; base-uri 'none'; frame-ancestors 'none'";
    private const string MicrosoftSignInOrigin = "https://login.microsoftonline.com";
    private const string CspItem = "packmoat.csp";
    private const string SiteItem = "packmoat.site";

    private static string E(string? value) => HtmlEncoder.Default.Encode(value ?? "");

    // ---------------------------------------------------------------- services

    private sealed class SharedMarker { }

    /// <summary>The default key folder: PACKMOAT_ADMIN_KEYS_DIR, else "admin-keys" next to the database file. Null only in tests without a database.</summary>
    internal static string? KeysDir(PackmoatOptions options) =>
        options.AdminKeysDir ?? (options.DatabasePath is { } db ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db))!, "admin-keys") : null);

    /// <summary>
    /// Registers what all signed-in sites share, once: the cookie encryption keys, antiforgery and authentication with NO default scheme
    /// (every site names its own schemes, so one site's cookie can never be read by another branch by accident).
    /// </summary>
    internal static void AddShared(IServiceCollection services, string? keysDir, CookieKeyStorage storage = CookieKeyStorage.Folder)
    {
        if (services.Any(d => d.ServiceType == typeof(SharedMarker))) return;
        services.AddSingleton<SharedMarker>();

        // The application name is kept from when only the admin site existed, so existing keys and sessions stay valid.
        var dp = services.AddDataProtection().SetApplicationName("packmoat-admin");
        // PACKMOAT_COOKIE_KEYS=database (#232 B1): the keys live in the database (the store PackmoatApp registers); the folder is only the
        // start's one-time import source, so it is never prepared, created or changed here.
        if (storage == CookieKeyStorage.Database)
            services.AddOptions<KeyManagementOptions>().Configure<ICookieKeyStore>((o, store) => o.XmlRepository = new DatabaseXmlRepository(store));
        else if (keysDir is { } dir) dp.PersistKeysToFileSystem(PrepareKeysDirectory(dir));
        else dp.UseEphemeralDataProtectionProvider();   // tests only: production always has a database path, hence a keys folder

        services.AddAntiforgery(o =>
        {
            o.Cookie.Name = AntiforgeryCookieName;
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.Path = "/";
            o.FormFieldName = AntiforgeryField;
            o.SuppressXFrameOptionsHeader = true;   // X-Frame-Options: DENY is set for every site response instead
        });
        services.AddAuthentication();
    }

    private static DirectoryInfo PrepareKeysDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists)
        {
            if (OperatingSystem.IsWindows()) info.Create();
            else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (!OperatingSystem.IsWindows()) EnsureOwnerOnly(path, File.SetUnixFileMode, File.GetUnixFileMode);
        return info;
    }

    /// <summary>Tries to make the key folder owner-only, then reads the mode back. Any group or other bit left set stops the start: the keys can forge sessions.</summary>
    internal static void EnsureOwnerOnly(string path, Action<string, UnixFileMode> chmod, Func<string, UnixFileMode> read)
    {
        try { chmod(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch (Exception) { /* judged by the read-back below */ }
        var mode = read(path);
        const UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & others) != 0)
            throw new InvalidOperationException($"The admin key folder {path} is accessible to other users (mode {Convert.ToString((int)mode, 8)}) and could not be restricted. Fix its permissions (chmod 700) and start again.");
    }

    // ---------------------------------------------------------------- cookie

    /// <summary>
    /// The session cookie: __Host- name, HttpOnly, Secure, SameSite=Lax, sliding <see cref="SlidingWindow"/>, hard cap <see cref="AbsoluteCap"/>.
    /// <paramref name="isAllowed"/> is asked when the session starts and on every request; a "no" means no cookie (start) or sign-out (later).
    /// </summary>
    internal static void ConfigureCookie(CookieAuthenticationOptions o, IClock clock, string cookieName, string cookieScheme,
        Func<HttpContext, ClaimsPrincipal?, bool> isAllowed, Func<Exception> denied, Func<CookieSigningInContext, Task>? onSigningIn = null)
    {
        o.Cookie.Name = cookieName;                    // the __Host- prefix: browsers refuse it unless Secure, Path=/ and no Domain
        o.Cookie.Path = "/";
        o.Cookie.Domain = null;
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.IsEssential = true;
        o.ExpireTimeSpan = SlidingWindow;
        o.SlidingExpiration = true;
        o.TimeProvider = new ClockTimeProvider(clock);
        o.Events = new CookieAuthenticationEvents
        {
            // The one door into a session. Whatever signs a person in (Microsoft, or a fake in tests), a person who is not let in gets no cookie.
            // onSigningIn (optional) runs first: a site may decide there and replace the principal with the minimal session one.
            OnSigningIn = async ctx =>
            {
                if (onSigningIn is not null) await onSigningIn(ctx);
                if (!isAllowed(ctx.HttpContext, ctx.Principal)) throw denied();
                ctx.Properties.Items[StartedItem] = clock.UtcNow.ToString("O");   // sliding renewal moves IssuedUtc; this does not move, so the cap holds
            },
            OnValidatePrincipal = async ctx =>
            {
                var started = ctx.Properties.Items.TryGetValue(StartedItem, out var s) && DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : (DateTimeOffset?)null;
                if (started is null || clock.UtcNow - started.Value >= AbsoluteCap || !isAllowed(ctx.HttpContext, ctx.Principal))
                {
                    ctx.RejectPrincipal();
                    await ctx.HttpContext.SignOutAsync(cookieScheme);
                }
            },
            OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; },
            OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; },
        };
    }

    // ---------------------------------------------------------------- Microsoft sign-in

    /// <summary>
    /// The sign-in settings every site shares: authorization-code flow with PKCE, response mode query, scopes openid and profile only,
    /// claims read as sent, no tokens kept, and a redirect URI built from the configured host (never the request's Host header or port).
    /// The caller sets the authority, client, token validation and the token-validated and failure events. Returns the redirect URI.
    /// </summary>
    internal static string ApplyCommonOidc(OpenIdConnectOptions o, string host, string callbackPath, string cookieScheme)
    {
        o.ResponseType = OpenIdConnectResponseType.Code;
        o.ResponseMode = OpenIdConnectResponseMode.Query;   // a plain GET back: no cross-site POST, so the correlation cookie can be SameSite=Lax
        o.UsePkce = true;
        o.CallbackPath = callbackPath;
        o.SignInScheme = cookieScheme;
        o.RequireHttpsMetadata = true;
        o.SaveTokens = false;
        o.GetClaimsFromUserInfoEndpoint = false;
        o.MapInboundClaims = false;
        o.Scope.Clear();
        o.Scope.Add("openid");
        o.Scope.Add("profile");
        o.CorrelationCookie.SameSite = SameSiteMode.Lax;
        o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        o.CorrelationCookie.HttpOnly = true;
        o.NonceCookie.SameSite = SameSiteMode.Lax;
        o.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
        o.NonceCookie.HttpOnly = true;
        o.BackchannelTimeout = TimeSpan.FromSeconds(20);
        // Built from the configured host, never from the request's Host header or port. Both the sign-in request and the code redemption use it.
        var redirectUri = $"https://{host}{callbackPath}";
        o.Events = new OpenIdConnectEvents
        {
            // Only the ID token redeemed from the token endpoint (with PKCE and our credential) counts. One that arrives directly in the
            // callback was not asked for (response type is code) and could have been injected, so the whole sign-in fails.
            OnMessageReceived = ctx =>
            {
                if (!string.IsNullOrEmpty(ctx.ProtocolMessage?.IdToken)) ctx.Fail("An ID token arrived in the callback; only the token endpoint's is accepted.");
                return Task.CompletedTask;
            },
            OnRedirectToIdentityProvider = ctx => { ctx.ProtocolMessage.RedirectUri = redirectUri; return Task.CompletedTask; },
            OnAuthorizationCodeReceived = ctx =>
            {
                MarkCodeReceived(ctx.HttpContext);   // from here on a failure counts against the limiter
                if (ctx.TokenEndpointRequest is { } r) r.RedirectUri = redirectUri;
                if (ctx.Properties is { } p) p.Items[OpenIdConnectDefaults.RedirectUriForCodePropertiesKey] = redirectUri;
                return Task.CompletedTask;
            },
        };
        return redirectUri;
    }

    /// <summary>
    /// Marks this request as one where Microsoft issued an authorization code (the handler is about to redeem it). Set by every site's
    /// code-received event (<see cref="ApplyCommonOidc"/>), read by <see cref="CountIfCodeReceived"/>.
    /// </summary>
    internal static void MarkCodeReceived(HttpContext ctx) => ctx.Items[CodeReceivedItem] = true;

    /// <summary>
    /// The failed sign-in limiter for a sign-in's failure event (#145): counts only a failure after Microsoft issued a code (a redemption
    /// that failed, a token refused, a person refused). A bare, forged or replayed callback, an error reply or a decline is not a guess:
    /// the state is data-protected and PKCE, the nonce and the correlation cookie bind the code to this browser, so counting those
    /// would only let a hostile page lock a person out for free.
    /// </summary>
    internal static void CountIfCodeReceived(HttpContext ctx, SignInLimiter limiter)
    {
        if (CodeWasReceived(ctx)) limiter.RecordFailure(ctx);
    }

    /// <summary>True when Microsoft issued an authorization code on this request (see <see cref="MarkCodeReceived"/>).</summary>
    internal static bool CodeWasReceived(HttpContext ctx) => ctx.Items.ContainsKey(CodeReceivedItem);

    /// <summary>Only what the pages show: the directory id, the object id, a display name (cut to 100 characters) and any extra claims the site adds.</summary>
    internal static ClaimsPrincipal Minimal(ClaimsPrincipal p, params Claim[] extra)
    {
        var claims = new List<Claim>
        {
            new("tid", p.FindFirst("tid")!.Value),
            new("oid", p.FindFirst("oid")!.Value),
        };
        if (p.FindFirst("name")?.Value is { Length: > 0 } n) claims.Add(new Claim("name", n.Length > 100 ? n[..100] : n));
        claims.AddRange(extra);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Entra", "name", ClaimsIdentity.DefaultRoleClaimType));
    }

    // ---------------------------------------------------------------- pipeline

    /// <summary>
    /// WebApplication inserts UseAuthentication at the START of the whole pipeline whenever authentication services exist, unless it
    /// finds this flag. That would let the sign-in handlers (and the site cookies) act on the proxy host. Mark it as done: each site branch
    /// authenticates for itself and the proxy never does. Tests pin this (the proxy host answers every site's callback path with 401).
    /// The key is internal to ASP.NET Core; if a future version stops honouring it, those tests fail loudly.
    /// </summary>
    internal static void SuppressGlobalAuthentication(WebApplication app) => ((IApplicationBuilder)app).Properties["__AuthenticationMiddlewareSet"] = true;

    /// <summary>Maps one site as a branch chosen by its Host header: guard, limits, its own authentication, antiforgery gate, then <paramref name="route"/>.</summary>
    internal static void Map(WebApplication app, SignedInSiteSpec site, RequestDelegate route)
    {
        SuppressGlobalAuthentication(app);
        app.MapWhen(ctx => string.Equals(ctx.Request.Host.Host, site.Host, StringComparison.OrdinalIgnoreCase), branch =>
        {
            branch.Use((ctx, next) => Guard(ctx, next, site));
            branch.Use((ctx, next) => Limits(ctx, next, site));
            branch.Use((ctx, next) => Authenticate(ctx, next, site));
            branch.Use((ctx, next) => AntiforgeryGate(ctx, next, site));
            branch.Run(route);
        });
    }

    /// <summary>The site whose branch is handling this request.</summary>
    internal static SignedInSiteSpec Site(HttpContext ctx) =>
        ctx.Items[SiteItem] as SignedInSiteSpec ?? throw new InvalidOperationException("Not inside a signed-in site branch.");

    /// <summary>Lets a page with a sign-in form (the admin landing page, the customer site's "Sign in again" forms) post to Microsoft's login host.</summary>
    internal static void UseLandingCsp(HttpContext ctx) => ctx.Items[CspItem] = LandingContentSecurityPolicy;

    /// <summary>
    /// A page whose forms may lead to <paramref name="origins"/> (in the order given), to this site and to Microsoft's sign-in, nowhere else:
    /// a customer-site module's page (#192), for example one whose form redirects to a payment provider's page. Each origin must be exactly
    /// https://host (see <see cref="FormTargetsContentSecurityPolicy"/>).
    /// </summary>
    internal static void UseFormTargetsCsp(HttpContext ctx, IReadOnlyList<string> origins) => ctx.Items[CspItem] = FormTargetsContentSecurityPolicy(origins);

    /// <summary>
    /// The policy for <see cref="UseFormTargetsCsp"/>: form-action 'self', the origins, then Microsoft's sign-in. With no origins it is
    /// exactly <see cref="LandingContentSecurityPolicy"/>. Throws for anything that is not a bare https origin: a scheme other than https,
    /// a port, a path (even "/"), a query, a fragment, user info, or any character a policy could be split or widened with.
    /// </summary>
    internal static string FormTargetsContentSecurityPolicy(IReadOnlyList<string> origins)
    {
        ArgumentNullException.ThrowIfNull(origins);
        foreach (var origin in origins)
            if (!IsBareHttpsOrigin(origin)) throw new ArgumentException("A form target must be a bare https origin, such as https://pay.example.com.", nameof(origins));
        var targets = string.Concat(origins.Select(o => " " + o));
        return $"default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self'{targets} {MicrosoftSignInOrigin}; base-uri 'none'; frame-ancestors 'none'";
    }

    private static bool IsBareHttpsOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin) || !origin.StartsWith("https://", StringComparison.Ordinal)) return false;
        var host = origin["https://".Length..];
        // A DNS name only: letters, digits, dots and hyphens, starting and ending with a letter or digit, lower-case (as a policy is compared).
        if (host.Length is 0 or > 253 || !host.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-')) return false;
        if (!char.IsAsciiLetterOrDigit(host[0]) || !char.IsAsciiLetterOrDigit(host[^1]) || host.Contains("..", StringComparison.Ordinal)) return false;
        return Uri.TryCreate(origin, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && u.Host == host && u.AbsolutePath == "/" && u.UserInfo.Length == 0;
    }

    /// <summary>Outermost: security headers on every response, and the error mapping (403 for a refused person or actor, 500 with no detail otherwise).</summary>
    private static async Task Guard(HttpContext ctx, RequestDelegate next, SignedInSiteSpec site)
    {
        ctx.Items[SiteItem] = site;
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h.ContentSecurityPolicy = ctx.Items[CspItem] as string ?? ContentSecurityPolicy;
            h.XContentTypeOptions = "nosniff";
            // Not "no-referrer": with that policy browsers send "Origin: null" on a form POST, and the write actions
            // require the real Origin. "same-origin" still sends nothing to any other site.
            h["Referrer-Policy"] = "same-origin";
            h.CacheControl = "no-store";
            h.Pragma = "no-cache";
            h.StrictTransportSecurity = "max-age=31536000";
            h.XFrameOptions = "DENY";
            h["X-Robots-Tag"] = "noindex, nofollow";
            return Task.CompletedTask;
        });
        try { await next(ctx); }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException bad)
        {
            await ErrorPage(ctx, bad.StatusCode, "Request refused", "The request was malformed or too large.");   // for example a body over the site limit
        }
        catch (Exception ex) when (ex is SignInDeniedException or AccessDeniedException)
        {
            if (ex is SignInDeniedException denied)
            {
                site.Limiter(ctx.RequestServices).RecordFailure(ctx);
                if (site.DeniedBody is { } body) await Page(ctx, 403, denied.Title, body(denied));
                else await ErrorPage(ctx, 403, denied.Title, denied.UserMessage, next: denied.NextStep);
            }
            else await ErrorPage(ctx, 403, "Not allowed", "You are not allowed to do this.");
        }
        catch (Exception ex)
        {
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(site.LogCategory).LogError("Site request failed ({Kind})", ex.GetType().Name);
            await ErrorPage(ctx, 500, "Something went wrong", "The page could not be produced.");
        }
    }

    /// <summary>Small bodies, GET and POST only, no body on a GET, and the failed sign-in limiter in front of the callback.</summary>
    private static async Task Limits(HttpContext ctx, RequestDelegate next, SignedInSiteSpec site)
    {
        // Site-only limits: forms are small but not 1 KB small (the antiforgery field alone is a few hundred bytes).
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } f) f.MaxRequestBodySize = site.MaxRequestBodyBytes;
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsPost(ctx.Request.Method))
        {
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            ctx.Response.Headers.Allow = "GET, POST";
            return;
        }
        if (HttpMethods.IsGet(ctx.Request.Method) && (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding")))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if ((ctx.Request.Path == site.CallbackPath || (site.ExtraCallbackPath is { } extra && ctx.Request.Path == extra)) && site.Limiter(ctx.RequestServices).RetryAfterSeconds(ctx) is { } wait)
        {
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            ctx.Response.Headers.RetryAfter = wait.ToString();
            return;
        }
        await next(ctx);
    }

    /// <summary>
    /// This site's authentication only. In place of UseAuthentication, which would run EVERY registered callback handler (matched by path, not
    /// host) and the app-wide default scheme: here only this site's remote handler may answer its callback, and only this site's cookie
    /// scheme sets the user.
    /// </summary>
    private static async Task Authenticate(HttpContext ctx, RequestDelegate next, SignedInSiteSpec site)
    {
        ctx.Features.Set<IAuthenticationFeature>(new AuthenticationFeature { OriginalPath = ctx.Request.Path, OriginalPathBase = ctx.Request.PathBase });
        var handlers = ctx.RequestServices.GetRequiredService<IAuthenticationHandlerProvider>();
        if (await handlers.GetHandlerAsync(ctx, site.RemoteScheme) is IAuthenticationRequestHandler remote && await remote.HandleRequestAsync()) return;
        if (site.ExtraRemoteScheme is { } second && await handlers.GetHandlerAsync(ctx, second) is IAuthenticationRequestHandler other && await other.HandleRequestAsync()) return;
        var result = await ctx.AuthenticateAsync(site.CookieScheme);
        if (result.Succeeded && result.Principal is { } user) ctx.User = user;
        await next(ctx);
    }

    /// <summary>Every POST must carry a valid antiforgery token (cookie and form field). Nothing is routed before this passes.</summary>
    private static async Task AntiforgeryGate(HttpContext ctx, RequestDelegate next, SignedInSiteSpec site)
    {
        if (HttpMethods.IsPost(ctx.Request.Method))
        {
            try { await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException)
            {
                // Not counted against the sign-in limiter (#145): any other site's form can send this POST, so counting it would let a
                // hostile page lock a person out.
                await ErrorPage(ctx, 400, "Request refused", "The form was missing its security token. Go back and try again.");
                return;
            }
        }
        await next(ctx);
    }

    /// <summary>
    /// Origin must be exactly https://<paramref name="host"/> (default port, no trailing slash). Browsers send it on every POST; a missing,
    /// "null" or foreign one is refused, and so is a Sec-Fetch-Site other than same-origin. Every write on every site checks this.
    /// </summary>
    internal static bool OriginOk(HttpContext ctx, string host)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length == 0 || origin.EndsWith('/') || !Uri.TryCreate(origin, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps || !string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase) || !u.IsDefaultPort) return false;
        var site = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
        return site.Length == 0 || site == "same-origin";
    }

    // ---------------------------------------------------------------- responses

    internal static Task Write(HttpContext ctx, int status, string html)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        return ctx.Response.WriteAsync(html, ctx.RequestAborted);
    }

    /// <summary>An error page in the current site's chrome. Never carries a new session cookie.</summary>
    internal static async Task ErrorPage(HttpContext ctx, int status, string title, string message, int? retryAfterSeconds = null, string? next = null)
    {
        var site = Site(ctx);
        var body = site.ErrorBody is { } custom ? custom(title, message, next) : $"<div class=\"box\"><h1>{E(title)}</h1><p>{E(message)}</p></div>";
        await Page(ctx, status, title, body, retryAfterSeconds);
    }

    /// <summary>An error page with a body the site built (trusted markup), in the current site's chrome. Never carries a new session cookie.</summary>
    private static async Task Page(HttpContext ctx, int status, string title, string body, int? retryAfterSeconds = null)
    {
        if (ctx.Response.HasStarted) { ctx.Abort(); return; }
        ctx.Response.Clear();
        ctx.Response.Headers.Remove("Set-Cookie");   // an error never carries a new session
        if (retryAfterSeconds is { } wait) ctx.Response.Headers.RetryAfter = wait.ToString();
        await Write(ctx, status, Site(ctx).Page(title, body));
    }

    /// <summary>A POST form to /signout carrying the antiforgery token.</summary>
    internal static string SignOutForm(HttpContext ctx)
    {
        var t = ctx.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(ctx);
        return $"<form method=\"post\" action=\"/signout\"><input type=\"hidden\" name=\"{E(t.FormFieldName)}\" value=\"{E(t.RequestToken!)}\"><button type=\"submit\">Sign out</button></form>";
    }

    internal static string AntiforgeryToken(HttpContext ctx) => ctx.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(ctx).RequestToken!;
}
