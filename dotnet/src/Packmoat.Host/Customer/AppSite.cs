using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Modules;
using Packmoat.Host.Web;
using Packmoat.Infrastructure;
using static Packmoat.Host.Customer.AppHtml;

namespace Packmoat.Host.Customer;

/// <summary>What the customer site was configured with. Built once at start. Never printed: it holds the certificate with its private key.</summary>
public sealed class AppSiteSettings
{
    public const string Authority = "https://login.microsoftonline.com/organizations/v2.0";
    public const string TokenEndpoint = "https://login.microsoftonline.com/organizations/oauth2/v2.0/token";

    public required string Host { get; init; }
    /// <summary>The customer sign-in app's client id, lower-case canonical GUID, or null when not set.</summary>
    public string? ClientId { get; init; }
    public X509Certificate2? Certificate { get; init; }
    /// <summary>True when Microsoft sign-in is fully configured (or a test supplied a fake provider). Otherwise every page answers 503.</summary>
    public bool SignInAvailable { get; init; }
    /// <summary>Why sign-in is not available, for one startup log line. Fixed text, never a value.</summary>
    public string? UnavailableReason { get; init; }
    /// <summary>The Azure setup wizard (PACKMOAT_AZURE_SETUP). Off: none of its pages, its sign-in callback or the home card exist.</summary>
    public AzureSetupSettings AzureSetup { get; init; } = AzureSetupSettings.Off;
    /// <summary>Who runs this server (#186): the footer's links, the support address and the operator's name in the pages' sentences.</summary>
    public OperatorLinks Operator { get; init; } = OperatorLinks.None;
    /// <summary>
    /// The feed address customers add as a NuGet source: PACKMOAT_PUBLIC_BASE_URL, else the first of PACKMOAT_HOSTS, then /v3/index.json.
    /// </summary>
    public string FeedIndexUrl { get; init; } = "/v3/index.json";
    public override string ToString() => $"AppSiteSettings({Host}, <redacted>)";
}

/// <summary>Slows repeated failed customer sign-ins from one address. Separate from the admin site's and the proxy's limiters.</summary>
public sealed class AppSignInLimiter : SignInLimiter
{
    public AppSignInLimiter(IClock clock, int maxFailures = 5, TimeSpan? window = null, ILogger? log = null) : base(clock, maxFailures, window, log) { }
}

/// <summary>A customer sign-in was refused. 403, no cookie. The page names the reason in fixed words; only a not-linked directory shows its own id.</summary>
public sealed class CustomerSignInDeniedException : SignInDeniedException
{
    public SignInDenial Reason { get; }
    public string? DirectoryId { get; }
    /// <summary>A not-linked directory's card, from the customer-site module (#192). Null: the core's (<see cref="NoCustomerSiteModule"/>).</summary>
    public NotLinkedCard? NotLinked { get; }

    public CustomerSignInDeniedException(SignInDenial reason, string? directoryId = null, NotLinkedCard? notLinked = null) : base("Customer sign-in refused: " + reason)
    {
        Reason = reason;
        DirectoryId = directoryId;
        NotLinked = reason == SignInDenial.DirectoryNotLinked ? notLinked : null;
    }

    public override string Title => Reason switch
    {
        SignInDenial.DirectoryNotLinked => NotLinked?.Title ?? NoCustomerSiteModule.NotLinkedTitle,
        SignInDenial.NoRole or SignInDenial.GuestNoRole or SignInDenial.MemberStatusUnknown => "No Packmoat role",
        SignInDenial.ConsumerDirectory => "Use your work account",
        _ => "Sign-in failed",
    };

    /// <summary>The sentence on the 403 page without this server's support words: the site's card uses <see cref="MessageFor"/>.</summary>
    public override string UserMessage => MessageFor(SupportContact.None);

    /// <summary>The sentence on the 403 page, telling a person whom to ask as this server's operator is configured (#186).</summary>
    public string MessageFor(SupportContact support) => Reason switch
    {
        SignInDenial.DirectoryNotLinked => NoCustomerSiteModule.NotLinkedSentence,
        // The wording of #80. NoRole: the organisation has auto-join off. GuestNoRole: auto-join is on, but not for this account.
        SignInDenial.NoRole => "Your organisation only lets in people with a Packmoat role. Ask your administrator to give you the Packmoat Developer role.",
        SignInDenial.GuestNoRole => "Guest accounts need a Packmoat role to sign in. Ask the organisation's administrator to give you the Packmoat Developer role.",
        // Member or guest unknown (no usable acct claim): the no-role wording of #79.
        SignInDenial.MemberStatusUnknown => "Your account has no Packmoat role. Ask your administrator to assign Packmoat.Admin or Packmoat.Developer.",
        SignInDenial.ConsumerDirectory => "Personal Microsoft accounts can't sign in. Choose your work or school account.",
        _ => AppSite.FailedSentence(support),
    };

    /// <summary>
    /// The card for this refusal. A directory that is not on this server gets the module's card (its own id and a way forward), or the
    /// core's. Every other refusal offers "Sign in again", where Microsoft lets the person pick another account.
    /// </summary>
    internal string Card(SupportContact support) => Reason == SignInDenial.DirectoryNotLinked
        ? (NotLinked ?? NoCustomerSiteModule.Card(DirectoryId)).Html
        : AppHtml.SignInCard(Title, MessageFor(support));
}

/// <summary>
/// The customer site: read-only pages on their own hostname (app.&lt;host&gt;), signed in with Microsoft through a separate multitenant app
/// registration. The tenant comes only from an operator-made directory binding looked up by the verified token's tid; roles come from the
/// Entra app roles. A separate pipeline branch chosen by the Host header, with its own cookie, schemes and callback path.
/// </summary>
public static class AppSite
{
    public const string CookieScheme = "PackmoatAppCookie";
    public const string RemoteScheme = "PackmoatAppEntra";
    public const string CookieName = "__Host-packmoat-app";
    /// <summary>Register in Entra as https://app.&lt;host&gt;/signin-microsoft. Not the admin site's /signin-oidc.</summary>
    public const string CallbackPath = "/signin-microsoft";
    public const string RoleClaim = "packmoat_role";
    private const string LogCategory = "Packmoat.App";

    /// <summary>
    /// The loop guard: set whenever this site sends someone to Microsoft, holding the time (Unix seconds, from <see cref="IClock"/>). An
    /// automatic start within <see cref="AutomaticStartGuard"/> of the last one shows the signed-out card instead of going to Microsoft
    /// again. Cleared only by sign-out, never by a successful sign-in, so a browser that loses its session cannot loop.
    /// </summary>
    public const string StartCookieName = "__Host-packmoat-app-start";
    public static readonly TimeSpan AutomaticStartGuard = TimeSpan.FromMinutes(2);

    // The sign-in outcome sentences (#79).
    internal const string SignedOutSentence = "You're signed out of Packmoat.";
    internal const string CancelledSentence = "Sign-in was cancelled, or your organisation hasn't approved Packmoat yet.";
    /// <summary>#118 moment 2, followed by the "organizations" admin-consent link: no directory is known here.</summary>
    internal const string CancelledNeedAdminSentence = "If Microsoft said \"Need admin approval\", send this link to your IT administrator:";
    internal static string FailedSentence(SupportContact support) => "Sign-in couldn't be completed. If it keeps happening, " + support.Phrase + ".";

    /// <summary>The suspended page (#186): the operator paused the account, by name or in neutral words.</summary>
    internal static string SuspendedSentence(SupportContact support) => support.OperatorSubject + " has paused this account, so feeds and tokens are off.";
    internal static string SuspendedNext(SupportContact support) => support.CapitalPhrase + " to restore access.";

    /// <summary>The suspended page's "what to do next": the core's sentence, then the customer-site module's note, if it has one (#197 C3).</summary>
    internal static string SuspendedNext(SupportContact support, ICustomerSiteModule module) =>
        module.SuspendedNote is { Length: > 0 } note ? SuspendedNext(support) + " " + note : SuspendedNext(support);

    /// <summary>
    /// The suspended page's button: mail the support address ("Email Packmoat support", the operator's name encoded), or none when no
    /// address is set. The address was checked at start to characters that need no escaping.
    /// </summary>
    internal static string? SupportButton(OperatorLinks op) => op.SupportEmail is { } email
        ? $"<a class=\"btn\" href=\"mailto:{email}\">{E(op.Name is { } name ? "Email " + name + " support" : "Email support")}</a>"
        : null;

    // ---------------------------------------------------------------- configuration

    /// <summary>Returns null when the customer site is disabled (no PACKMOAT_APP_HOST): nothing is registered and nothing is mapped.</summary>
    internal static AppSiteSettings? Configure(WebApplicationBuilder builder, PackmoatOptions options, DatabaseEngine engine, string? adminHost)
    {
        var host = options.AppHost?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(host)) return null;
        if (host.IndexOfAny(new[] { '/', ':', '*', ' ', ',', ';', '@', '?', '#' }) >= 0)
            throw new InvalidOperationException("PACKMOAT_APP_HOST must be a bare hostname such as app.example.com.");
        var proxyHosts = (options.AllowedHosts ?? "").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (proxyHosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("PACKMOAT_APP_HOST must not also be listed in PACKMOAT_HOSTS: one hostname cannot be both the feed and the customer site.");
        if (string.Equals(host, adminHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PACKMOAT_APP_HOST must differ from PACKMOAT_ADMIN_HOST.");

        var clientId = CustomerSignIn.CanonicalDirectory(options.AppClientId);   // same GUID canonicalisation
        X509Certificate2? cert = null;
        string? reason = null;
        if (clientId is null) reason = "PACKMOAT_APP_CLIENT_ID is not set (or not a GUID)";
        else if (string.IsNullOrWhiteSpace(options.AppCertPath)) reason = "PACKMOAT_APP_CERT is not set";
        else
        {
            try
            {
                cert = X509Certificate2.CreateFromPemFile(options.AppCertPath);
                if (!cert.HasPrivateKey) { cert.Dispose(); cert = null; reason = "the PACKMOAT_APP_CERT file holds no private key"; }
            }
            catch (Exception) { cert = null; reason = "the PACKMOAT_APP_CERT file could not be read as a certificate and key PEM"; }   // never the exception text
        }
        var fake = options.AppRemoteSchemeOverride;
        var configured = clientId is not null && cert is not null;
        var settings = new AppSiteSettings
        {
            Host = host,
            ClientId = clientId,
            Certificate = cert,
            SignInAvailable = configured || fake is not null,
            UnavailableReason = configured || fake is not null ? null : reason,
            AzureSetup = options.AzureSetup,
            Operator = options.Operator,
            FeedIndexUrl = FeedIndexUrl(options.PublicBaseUrl, proxyHosts),
        };

        var services = builder.Services;
        services.AddSingleton(settings);
        services.AddSingleton<AppSignInLimiter>(sp => new AppSignInLimiter(sp.GetRequiredService<IClock>(), log: sp.GetRequiredService<ILogger<AppSignInLimiter>>()));
        services.TryAddSingleton(sp => new AdminQueries(sp.GetRequiredService<ITenantStore>(), sp.GetRequiredService<IAuditQuery>(),
            sp.GetRequiredService<ITokenService>(), sp.GetRequiredService<IActiveDeveloperQuery>()));
        services.TryAddSingleton(sp => new CustomerQueries(sp.GetRequiredService<AdminQueries>(), sp.GetRequiredService<IServingGate>()));
        // Production registers these with the database (PackmoatApp); TryAdd lets a test host supply its own stores.
        services.TryAddSingleton(sp => new CustomerTokenCommands(sp.GetRequiredService<ITenantStore>(), sp.GetRequiredService<ITokenService>(),
            sp.GetRequiredService<IMemberStore>(), sp.GetRequiredService<ISignInDirectoryStore>(), sp.GetRequiredService<IAdminActionLog>(),
            sp.GetRequiredService<IClock>(), sp.GetService<ISharedAppCredentialStore>(), sp.GetRequiredService<IServingGate>()));
        services.TryAddSingleton(sp => new CustomerTokenQueries(sp.GetRequiredService<ITenantStore>(), sp.GetRequiredService<ITokenService>(),
            sp.GetRequiredService<ISignInDirectoryStore>(), sp.GetService<ISharedAppCredentialStore>(), sp.GetRequiredService<IServingGate>()));
        services.TryAddSingleton(sp => new AutoJoinCommands(sp.GetRequiredService<ITenantStore>(), sp.GetRequiredService<ISignInDirectoryStore>(),
            sp.GetRequiredService<IAutoJoinSettingStore>(), sp.GetRequiredService<IAdminActionLog>(), sp.GetRequiredService<IClock>()));
        services.TryAddSingleton(sp => new AppFormNonces(sp.GetRequiredService<IClock>()));   // the customer site's own: never the admin site's store
        // Waivers (#157): the customer limits and the cap alert live in the core, which CustomerWaiverCommands calls as the actor's tenant.
        services.TryAddSingleton(sp => new CustomerWaiverCommands(sp.GetRequiredService<ITenantStore>(), sp.GetRequiredService<ITokenService>(),
            sp.GetRequiredService<ISignInDirectoryStore>(), sp.GetRequiredService<IAdminActionLog>(), sp.GetRequiredService<IClock>(), sp.GetService<IOpsAlerts>()));
        services.TryAddSingleton(sp => new CustomerWaiverQueries(sp.GetRequiredService<ITenantStore>(), sp.GetRequiredService<ISignInDirectoryStore>(), sp.GetRequiredService<IMemberStore>(),
            sp.GetRequiredService<IClock>()));
        SignedInSite.AddShared(services, SignedInSite.KeysDir(options, engine), CookieKeyStorages.Parse(options.CookieKeys));
        var auth = services.AddAuthentication();   // no default scheme: this site names its own schemes everywhere
        auth.AddCookie(CookieScheme, _ => { });
        services.AddOptions<CookieAuthenticationOptions>(CookieScheme).Configure<IClock>(ConfigureCookie);
        if (fake is not null) fake(auth);
        else if (configured)
        {
            auth.AddOpenIdConnect(RemoteScheme, _ => { });
            services.AddOptions<OpenIdConnectOptions>(RemoteScheme).Configure<IClock>((o, clock) => ConfigureOidc(o, settings, clock));
        }
        // The Azure setup wizard (off by default): its delegated sign-in scheme and check exist only while the switch is not off.
        if (settings.AzureSetup.Mode != AzureSetupMode.Off)
        {
            AppAzureSetup.AddServices(services, options, settings);
            if (options.AppAzureDevOpsSchemeOverride is { } fakeAdo) fakeAdo(auth);
            else if (configured)
            {
                auth.AddOpenIdConnect(AppAzureSetup.Scheme, _ => { });
                services.AddOptions<OpenIdConnectOptions>(AppAzureSetup.Scheme).Configure<IClock>((o, clock) => AppAzureSetup.ConfigureOidc(o, settings, clock));
            }
        }
        return settings;
    }

    /// <summary>
    /// The proxy's own address for customers (#186): the public base URL when set, else https:// and the first proxy hostname. Never a
    /// fixed hostname.
    /// </summary>
    internal static string FeedIndexUrl(string? publicBaseUrl, IReadOnlyList<string> proxyHosts) =>
        (string.IsNullOrWhiteSpace(publicBaseUrl) ? "https://" + proxyHosts.FirstOrDefault()?.ToLowerInvariant() : publicBaseUrl.Trim().TrimEnd('/')) + "/v3/index.json";

    // ---------------------------------------------------------------- session

    /// <summary>The session claim holding when the person signed in to Microsoft (Unix seconds): the fifth session value.</summary>
    public const string AuthTimeClaim = "auth_time";

    /// <summary>
    /// The session cookie. The decision is made in the cookie's sign-in event, so whatever signed the person in (Microsoft, or a fake in
    /// tests), <see cref="CustomerSignIn.Decide"/> runs and a refused person gets no cookie. The session then holds only the directory id,
    /// the object id, the display name, the Packmoat role decided here and the sign-in time.
    /// </summary>
    internal static void ConfigureCookie(CookieAuthenticationOptions o, IClock clock) =>
        SignedInSite.ConfigureCookie(o, clock, CookieName, CookieScheme,
            // A member session, or a prospect session while the customer-site module still offers its directory a setup.
            (ctx, p) => ReadSession(p) is not null || ReadProspect(p, ctx.RequestServices.GetRequiredService<ICustomerSiteModule>()) is not null,
            () => new CustomerSignInDeniedException(SignInDenial.MissingIdentity), DecideAtSignIn);

    /// <summary>The session claim that marks a session that is NOT a member session. Its only value is <see cref="ProspectSessionKind"/>.</summary>
    public const string SessionKindClaim = "packmoat_session";
    public const string ProspectSessionKind = "prospect";
    /// <summary>The session claim of a person let in by auto-join (#80), with no Packmoat role. Its only value is <see cref="AutoJoin"/>.</summary>
    public const string JoinClaim = "packmoat_join";
    public const string AutoJoin = "auto";
    /// <summary>
    /// Set on the sign-in properties only by a prospect page's confirmed sign-in (<see cref="CustomerPage{T}.Reauth"/> with confirm: the
    /// hosted "Set up your organisation", prompt=login), and read once at sign-in. A
    /// prospect session keeps Microsoft's auth_time only when this was set, so any other sign-in (an automatic start above all) is never fresh.
    /// </summary>
    internal const string ConfirmItem = "packmoat.confirm";

    private static async Task DecideAtSignIn(CookieSigningInContext ctx)
    {
        var services = ctx.HttpContext.RequestServices;
        var settings = services.GetRequiredService<AppSiteSettings>();
        var confirmed = ctx.Properties.Items.TryGetValue(ConfirmItem, out var mark) && mark == "1";
        ctx.Properties.Items.Remove(ConfirmItem);   // read once; never kept in the cookie
        var claims = ToVerifiedClaims(ctx.Principal);
        var directory = claims.DirectoryIds.Count == 1 ? CustomerSignIn.CanonicalDirectory(claims.DirectoryIds[0]) : null;
        var binding = directory is null ? null : await services.GetRequiredService<ISignInDirectoryStore>().GetAsync(directory, ctx.HttpContext.RequestAborted);
        var decision = CustomerSignIn.Decide(claims, settings.ClientId ?? "", binding, autoJoin: false);
        // Auto-join is the bound tenant's setting, read now (no cache), and ONLY for a person who passed every other check but has no
        // role: people with a role never depend on it. A read that fails throws, and this person gets no cookie.
        if (decision is SignInDecision.Denied { Reason: SignInDenial.NoRole } && binding is not null
            && await services.GetRequiredService<IAutoJoinSettingStore>().IsOnAsync(binding.TenantId, ctx.HttpContext.RequestAborted))
            decision = CustomerSignIn.Decide(claims, settings.ClientId ?? "", binding, autoJoin: true);
        if (decision is SignInDecision.Denied denied)
        {
            var log = services.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);
            // A prospect: only for a directory that is not linked and that the customer-site module offers a setup (none does by default),
            // and only when the module has prospect pages: a prospect session with none to land on would send the person round the sign-in (#251).
            var module = services.GetRequiredService<ICustomerSiteModule>();
            var prospect = denied is { Reason: SignInDenial.DirectoryNotLinked, DirectoryId: { } notLinked } && module.ProspectRoutes.Count > 0 && module.OffersSetup(notLinked)
                ? ProspectDecision.For(claims, decision) : null;
            if (prospect is ProspectDecision.Eligible eligible)
            {
                // A prospect session: no role, no member row (RecordSignInAsync is NOT called), and it reaches only the sign-up pages.
                log.LogInformation("Customer sign-in: self sign-up prospect");   // never a claim
                var prospectSession = new List<Claim>
                {
                    new Claim("tid", eligible.DirectoryId),
                    new Claim("oid", eligible.ObjectId.ToString("D")),
                    new Claim("name", eligible.DisplayName),
                    new Claim(SessionKindClaim, ProspectSessionKind),
                };
                if (confirmed && SignInTime(ctx.Principal, services.GetRequiredService<IClock>().UtcNow) is { } confirmedAt)
                    prospectSession.Add(new Claim(AuthTimeClaim, confirmedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
                ctx.Principal = new ClaimsPrincipal(new ClaimsIdentity(prospectSession, "Entra", "name", RoleClaim));
                return;
            }
            log.LogWarning("Customer sign-in refused ({Reason})", denied.Reason.ToString());   // the reason only, never a claim
            throw new CustomerSignInDeniedException(denied.Reason, denied.DirectoryId,
                denied is { Reason: SignInDenial.DirectoryNotLinked, DirectoryId: { } directoryId } ? module.NotLinkedCard(directoryId, needsAdmin: prospect is ProspectDecision.NeedsAdmin) : null);
        }
        var allowed = (SignInDecision.Allowed)decision;
        var now = services.GetRequiredService<IClock>().UtcNow;
        var signedInAt = SignInTime(ctx.Principal, now);
        var session = new List<Claim>
        {
            new Claim("tid", allowed.DirectoryId.ToString("D")),
            new Claim("oid", allowed.ObjectId.ToString("D")),
            new Claim("name", allowed.DisplayName),
            new Claim(RoleClaim, allowed.Role.ToString()),
        };
        if (allowed.AutoJoined)
        {
            // Marks the session so every click checks the setting again; turning auto-join off ends it.
            session.Add(new Claim(JoinClaim, AutoJoin));
            services.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory).LogInformation("Customer sign-in: auto-joined as Developer");   // never a claim
        }
        // Fail closed: no auth_time from Microsoft, no sign-in time in the session, so this session is never fresh for an admin-only write.
        if (signedInAt is { } at) session.Add(new Claim(AuthTimeClaim, at.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        ctx.Principal = new ClaimsPrincipal(new ClaimsIdentity(session, "Entra", "name", RoleClaim));

        // Remember the person (directory, oid, tenant, name, seen times) for token ownership. Never blocks the sign-in.
        try
        {
            var actor = new CustomerActor(allowed.TenantId, allowed.ObjectId, allowed.Role, allowed.DisplayName, allowed.DirectoryId.ToString("D"), signedInAt);
            await services.GetRequiredService<CustomerTokenCommands>().RecordSignInAsync(actor, ctx.HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory).LogWarning("Recording the customer sign-in failed ({Kind})", ex.GetType().Name);
        }
    }

    /// <summary>
    /// When the person last proved who they are to Microsoft: the validated ID token's own auth_time (an optional claim the operator must
    /// turn on for the sign-in app), when there is exactly one, it is a plausible Unix time and it is not in the future. Otherwise null:
    /// the completion time of this sign-in is NOT used, because Microsoft can complete a sign-in silently from an old single sign-on
    /// session, and that must never count as a fresh sign-in.
    /// </summary>
    internal static DateTimeOffset? SignInTime(ClaimsPrincipal? p, DateTimeOffset now)
    {
        var values = p?.FindAll(AuthTimeClaim).Select(c => c.Value).ToArray() ?? Array.Empty<string>();
        if (values.Length == 1 && long.TryParse(values[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var secs)
            && secs is > 0 and < 253402300799 && DateTimeOffset.FromUnixTimeSeconds(secs) is var t && t <= now + TimeSpan.FromMinutes(1))
            return t < now ? t : now;
        return null;
    }

    /// <summary>The claims of a signed-in principal (verified by the OIDC handler, or a test's fake) as plain values for the decision.</summary>
    internal static VerifiedClaims ToVerifiedClaims(ClaimsPrincipal? p)
    {
        string[] All(string type) => p?.FindAll(type).Select(c => c.Value).ToArray() ?? Array.Empty<string>();
        return new VerifiedClaims(All("iss"), All("aud"), All("tid"), All("oid"), p?.FindFirst("name")?.Value, All("roles"), All("acct"), All("idp"));
    }

    internal sealed record Session(string DirectoryId, Guid ObjectId, string DisplayName, CustomerRole Role, DateTimeOffset? SignedInAt, bool AutoJoined = false);

    /// <summary>
    /// The prospect session's values, or null: not exactly the shape <see cref="DecideAtSignIn"/> writes for a prospect (one
    /// <see cref="SessionKindClaim"/> of "prospect", no role), or the customer-site module no longer offers its directory a setup (with no
    /// module, never). Only an eligible Admin ever gets this session, so <c>IsAdmin</c> is true.
    /// </summary>
    internal static Prospect? ReadProspect(ClaimsPrincipal? p, ICustomerSiteModule module)
    {
        if (p?.Identity?.IsAuthenticated != true) return null;
        string? One(string type) => p.FindAll(type).Select(c => c.Value).ToArray() is { Length: 1 } v ? v[0] : null;
        if (One(SessionKindClaim) != ProspectSessionKind || p.HasClaim(c => c.Type == RoleClaim)) return null;
        var tid = CustomerSignIn.CanonicalDirectory(One("tid"));
        if (tid is null || !module.OffersSetup(tid) || !Guid.TryParse(One("oid"), out var oid) || oid == Guid.Empty) return null;
        DateTimeOffset? at = long.TryParse(One(AuthTimeClaim), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var secs)
            && secs is > 0 and < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(secs) : null;
        return new Prospect(tid, oid, One("name") ?? "Signed-in user", IsAdmin: true, at);
    }

    /// <summary>The prospect signed in on this request, or null (always null when the module has no prospect pages, as with none).</summary>
    internal static async Task<Prospect?> ResolveProspectAsync(HttpContext ctx, ICustomerSiteModule module)
    {
        if (module.ProspectRoutes.Count == 0) return null;
        var result = await ctx.AuthenticateAsync(CookieScheme);
        return result.Succeeded ? ReadProspect(result.Principal, module) : null;
    }

    /// <summary>
    /// The session's values, or null when the principal is not the shape <see cref="DecideAtSignIn"/> writes. A session from before the
    /// sign-in time was stored has none: it stays valid, but is never fresh enough for an admin-only write.
    /// </summary>
    internal static Session? ReadSession(ClaimsPrincipal? p)
    {
        if (p?.Identity?.IsAuthenticated != true) return null;
        if (p.HasClaim(c => c.Type == SessionKindClaim)) return null;   // a prospect (or any other non-member) session is never a member session
        string? One(string type) => p.FindAll(type).Select(c => c.Value).ToArray() is { Length: 1 } v ? v[0] : null;
        var tid = CustomerSignIn.CanonicalDirectory(One("tid"));
        if (tid is null || !Guid.TryParse(One("oid"), out var oid) || oid == Guid.Empty) return null;
        if (!Enum.TryParse<CustomerRole>(One(RoleClaim), ignoreCase: false, out var role) || !Enum.IsDefined(role)) return null;
        // Auto-joined: exactly one join claim of "auto", and only ever a Developer. Any other join claim is not a session the sign-in wrote.
        var auto = false;
        if (p.HasClaim(c => c.Type == JoinClaim))
        {
            if (One(JoinClaim) != AutoJoin || role != CustomerRole.Developer) return null;
            auto = true;
        }
        DateTimeOffset? at = long.TryParse(One(AuthTimeClaim), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var secs)
            && secs is > 0 and < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(secs) : null;
        return new Session(tid, oid, One("name") ?? "Signed-in user", role, at, auto);
    }

    /// <summary>
    /// The signed-in customer, from the session cookie and the directory binding read again now. No binding any more (the operator unbound
    /// it): the session is ended. An auto-joined session also reads its tenant's auto-join setting again (no cache), and ends when it is
    /// off. Never built from a request field.
    /// </summary>
    internal static async Task<CustomerActor?> ResolveActorAsync(HttpContext ctx)
    {
        var result = await ctx.AuthenticateAsync(CookieScheme);
        if (!result.Succeeded || ReadSession(result.Principal) is not { } s) return null;
        var binding = await ctx.RequestServices.GetRequiredService<ISignInDirectoryStore>().GetAsync(s.DirectoryId, ctx.RequestAborted);
        if (binding is null || binding.AzureTenantId != s.DirectoryId
            || (s.AutoJoined && !await ctx.RequestServices.GetRequiredService<IAutoJoinSettingStore>().IsOnAsync(binding.TenantId, ctx.RequestAborted)))
        {
            await ctx.SignOutAsync(CookieScheme);
            return null;
        }
        return new CustomerActor(binding.TenantId, s.ObjectId, s.Role, s.DisplayName, binding.AzureTenantId, s.SignedInAt);
    }

    /// <summary>
    /// The signed-in member as <see cref="ResolveActorAsync"/> finds them, but changing nothing: a session that no longer resolves is
    /// simply not an actor here (no sign-out). For the Azure DevOps check's callback, which writes no cookie of its own. An auto-joined
    /// session is an actor only when <paramref name="allowAutoJoined"/> (the developer proof: auto-joined people are Developers) and the
    /// tenant's auto-join setting is still on, read now.
    /// </summary>
    internal static async Task<CustomerActor?> ReadActorAsync(HttpContext ctx, bool allowAutoJoined = false)
    {
        var result = await ctx.AuthenticateAsync(CookieScheme);
        if (!result.Succeeded || ReadSession(result.Principal) is not { } s || (s.AutoJoined && !allowAutoJoined)) return null;
        var binding = await ctx.RequestServices.GetRequiredService<ISignInDirectoryStore>().GetAsync(s.DirectoryId, ctx.RequestAborted);
        if (binding is null || binding.AzureTenantId != s.DirectoryId) return null;
        if (s.AutoJoined && !await ctx.RequestServices.GetRequiredService<IAutoJoinSettingStore>().IsOnAsync(binding.TenantId, ctx.RequestAborted)) return null;
        return new CustomerActor(binding.TenantId, s.ObjectId, s.Role, s.DisplayName, binding.AzureTenantId, s.SignedInAt);
    }

    // ---------------------------------------------------------------- Microsoft sign-in

    /// <summary>
    /// Authorization-code flow with PKCE against the "organizations" endpoint (work and school accounts of any directory), certificate
    /// credential (a signed client assertion, no secret), issuer accepted only when it is exactly the token's own directory's issuer.
    /// </summary>
    /// <summary>
    /// How an ID token is checked: issuer exactly the token's own directory's (<see cref="ValidateIssuer"/>), audience this app, lifetime, and
    /// the signing key. Microsoft's signing-key issuer check is on as well: a key whose published "issuer" is a different directory than the
    /// token's cannot sign for it (the multitenant hardening Microsoft recommends).
    /// </summary>
    internal static TokenValidationParameters TokenValidation(string clientId)
    {
        var p = new TokenValidationParameters
        {
            ValidateIssuer = true,
            IssuerValidator = ValidateIssuer,
            ValidateAudience = true,
            ValidAudience = clientId,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            NameClaimType = "name",
        };
        p.EnableAadSigningKeyIssuerValidation();
        return p;
    }

    /// <summary>
    /// The customer sign-in's Microsoft settings: the customer app, its certificate credential and token checks, and the events that decide
    /// the session. Public like the admin site's, so a test can run the real handler's events (#197).
    /// </summary>
    public static void ConfigureOidc(OpenIdConnectOptions o, AppSiteSettings s, IClock clock)
    {
        SignedInSite.ApplyCommonOidc(o, s.Host, CallbackPath, CookieScheme);
        o.Authority = AppSiteSettings.Authority;
        o.ClientId = s.ClientId;
        o.ClientSecret = null;
        o.TokenValidationParameters = TokenValidation(s.ClientId!);
        // The handler runs ClaimActions on the principal AFTER OnTokenValidated, and the defaults delete iss and aud (among others), which the
        // sign-in decision needs. OnTokenValidated below already builds a minimal identity, so no claim actions are wanted at all.
        o.ClaimActions.Clear();
        UseCertificateCredential(o, s, clock);
        o.Events.OnTokenValidated = ctx =>
        {
            // Hand the cookie's sign-in event only what the decision reads. Issuer and audience come from the validated token itself.
            var p = ctx.Principal!;
            var claims = new List<Claim>();
            void Copy(string type) => claims.AddRange(p.FindAll(type).Select(c => new Claim(type, c.Value)));
            Copy("tid"); Copy("oid"); Copy("name"); Copy("roles"); Copy(AuthTimeClaim);
            // Auto-join (#80): acct (0 member, 1 guest; an optional claim) and idp, read only by the decision, never kept in the session.
            Copy("acct"); Copy("idp");
            // Issuer and audience: the principal's own iss/aud claims, which come from the token the handler just validated; the token
            // itself is a fallback. (The first live sign-in failed because the default ClaimActions deleted iss/aud after this event.)
            Copy("iss"); Copy("aud");
            if (ctx.SecurityToken is { } token && !claims.Any(c => c.Type == "iss"))
            {
                claims.Add(new Claim("iss", token.Issuer));
                foreach (var aud in Audiences(token)) claims.Add(new Claim("aud", aud));
            }
            ctx.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Entra", "name", "roles"));
            return Task.CompletedTask;
        };
        // Both outcome pages are fixed text: nothing from the callback (error, error_description, the exception) is ever shown, and a log
        // line names only an allowlisted error code. Microsoft's error code alone picks the page (#143): a decline (access_denied,
        // consent_required) shows the cancelled card even when the handler could not match the reply to a sign-in (state or correlation
        // lost). That is safe: the card signs nobody in, and its approval link is the fixed "organizations" one.
        // Only a failure after Microsoft issued a code counts against the limiter (#145); a decline or a bare callback never does.
        o.Events.OnRemoteFailure = async ctx =>
        {
            SignedInSite.CountIfCodeReceived(ctx.HttpContext, ctx.HttpContext.RequestServices.GetRequiredService<AppSignInLimiter>());
            var error = await SignInReply.ErrorAsync(ctx.HttpContext.Request);
            var kind = ctx.Failure?.GetType().Name ?? "unknown";   // never the exception text: it can echo protocol values
            ctx.HandleResponse();
            if (SignInReply.IsDeclined(error))
            {
                Log(ctx.HttpContext).LogWarning("Customer sign-in cancelled or not approved ({Kind}, error {Error})", kind, SignInReply.LogCode(error));
                await CancelledPage(ctx.HttpContext, s);
                return;
            }
            Log(ctx.HttpContext).LogWarning("Customer sign-in failed ({Kind}, error {Error}, token {TokenError})", kind, SignInReply.LogCode(error),
                SignInReply.TokenLogCode(ctx.HttpContext, ctx.Failure));
            await SignedInSite.Write(ctx.HttpContext, 400, Page(ctx.HttpContext, "Sign-in failed", SignInCard("Sign-in failed", FailedSentence(s.Operator.Support))));
        };
        // Microsoft answered error=access_denied to a reply the handler matched to this browser's sign-in: the person cancelled, or their
        // organisation has not consented to the app. Not counted against the limiter (#145): a decline is not a guess.
        o.Events.OnAccessDenied = async ctx =>
        {
            Log(ctx.HttpContext).LogWarning("Customer sign-in cancelled or not approved");
            ctx.HandleResponse();
            await CancelledPage(ctx.HttpContext, s);
        };
    }

    private static ILogger Log(HttpContext ctx) => ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory);

    /// <summary>
    /// The sign-in cancelled card (403): fixed text and Microsoft's admin-consent link. Nothing from the reply is trusted for the link
    /// either: the directory is not known, so it is Microsoft's "organizations".
    /// </summary>
    internal static Task CancelledPage(HttpContext ctx, AppSiteSettings s)
    {
        var approve = $"<p>{E(CancelledNeedAdminSentence)}</p><pre class=\"snippet\">{E(AppAzureSetup.OrganisationConsentUrl(s, null))}</pre>";
        return SignedInSite.Write(ctx, 403, Page(ctx, "Sign-in cancelled", SignInCard("Sign-in cancelled", CancelledSentence, moreHtml: approve)));
    }

    /// <summary>The code redemption carries a signed client assertion made with the app's certificate, never a secret.</summary>
    internal static void UseCertificateCredential(OpenIdConnectOptions o, AppSiteSettings s, IClock clock)
    {
        var fixRedirect = o.Events.OnAuthorizationCodeReceived;
        o.Events.OnAuthorizationCodeReceived = async ctx =>
        {
            await fixRedirect(ctx);
            if (ctx.TokenEndpointRequest is { } r && s.Certificate is { } cert && s.ClientId is { } id)
            {
                r.ClientSecret = null;
                r.ClientAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
                r.ClientAssertion = ClientAssertion(cert, id, AppSiteSettings.TokenEndpoint, clock.UtcNow);
            }
        };
    }

    internal static IEnumerable<string> Audiences(SecurityToken token) => token switch
    {
        JwtSecurityToken j => j.Audiences,
        JsonWebToken j => j.Audiences,
        _ => Array.Empty<string>(),
    };

    /// <summary>
    /// Multitenant issuer check: accepted only when the issuer is exactly https://login.microsoftonline.com/{tid}/v2.0 for the token's own
    /// tid claim, and that tid is not the consumer directory. Anything else throws (the sign-in fails; nothing is logged but the type).
    /// </summary>
    internal static string ValidateIssuer(string issuer, SecurityToken token, TokenValidationParameters _)
    {
        var tid = token switch
        {
            JsonWebToken j => j.TryGetPayloadValue<string>("tid", out var t) ? t : null,
            JwtSecurityToken j => j.Payload.TryGetValue("tid", out var t) ? t as string : null,
            _ => null,
        };
        if (!CustomerSignIn.IssuerMatches(issuer, tid)) throw new SecurityTokenInvalidIssuerException("The token's issuer is not its own directory's issuer.");
        return issuer;
    }

    /// <summary>The certificate credential: a short-lived JWT signed with the app's certificate, as Microsoft's token endpoint expects (RS256, x5t header).</summary>
    internal static string ClientAssertion(X509Certificate2 cert, string clientId, string audience, DateTimeOffset now)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = clientId,
            Audience = audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddMinutes(5).UtcDateTime,
            Claims = new Dictionary<string, object> { ["sub"] = clientId, ["jti"] = Guid.NewGuid().ToString("D") },
            SigningCredentials = new X509SigningCredentials(cert, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    // ---------------------------------------------------------------- pipeline

    internal static SignedInSiteSpec Spec(AppSiteSettings settings, ICustomerSiteModule module) => new()
    {
        Name = "App",
        Host = settings.Host,
        CookieScheme = CookieScheme,
        RemoteScheme = RemoteScheme,
        CookieName = CookieName,
        CallbackPath = CallbackPath,
        // The Azure DevOps check's sign-in: a second callback handled (and limited) the same way, only while its switch is not off.
        ExtraRemoteScheme = settings.AzureSetup.Mode != AzureSetupMode.Off ? AppAzureSetup.Scheme : null,
        ExtraCallbackPath = settings.AzureSetup.Mode != AzureSetupMode.Off ? AppAzureSetup.CallbackPath : null,
        Limiter = sp => sp.GetRequiredService<AppSignInLimiter>(),
        Page = (title, body) => Page(settings.Operator, module, title, body),
        ErrorBody = (title, message, next) => ErrorCard(settings.Operator, title, message, next),
        DeniedBody = denied => denied is CustomerSignInDeniedException c ? c.Card(settings.Operator.Support) : SignInCard(denied.Title, FailedSentence(settings.Operator.Support)),
    };

    public const int CertificateWarningDays = 30;

    /// <summary>At start: an error when the sign-in certificate has expired, a warning when it expires within 30 days. Only the date is logged.</summary>
    internal static void CheckCertificateExpiry(ILogger log, DateTime notAfterUtc, DateTimeOffset now)
    {
        var left = new DateTimeOffset(notAfterUtc, TimeSpan.Zero) - now;
        if (left <= TimeSpan.Zero)
            log.LogError("Customer site sign-in certificate EXPIRED on {Expiry:yyyy-MM-dd}: customer sign-in will fail. Make a new one and upload it to the app registration (docs/runbooks/customer-site.md).", notAfterUtc);
        else if (left < TimeSpan.FromDays(CertificateWarningDays))
            log.LogWarning("Customer site sign-in certificate expires on {Expiry:yyyy-MM-dd} ({Days} days left). Replace it before then (docs/runbooks/customer-site.md).", notAfterUtc, (int)left.TotalDays);
    }

    internal static void MapBranch(WebApplication app, AppSiteSettings settings)
    {
        if (settings.UnavailableReason is { } why) app.Logger.LogWarning("Customer site sign-in is not configured ({Reason}); every customer site page answers 503.", why);
        if (settings.Certificate is { } cert) CheckCertificateExpiry(app.Logger, cert.NotAfter.ToUniversalTime(), app.Services.GetRequiredService<IClock>().UtcNow);
        SignedInSite.Map(app, Spec(settings, app.Services.GetRequiredService<ICustomerSiteModule>()), ctx => Route(ctx, settings));
    }

    // ---------------------------------------------------------------- routes

    private static async Task Route(HttpContext ctx, AppSiteSettings s)
    {
        var path = ctx.Request.Path.Value ?? "/";
        var get = HttpMethods.IsGet(ctx.Request.Method);
        // The two static files are served even when sign-in is not configured, so the 503 page below is styled. They hold no data.
        if (get && path == "/app.css") { await Static(ctx, "text/css; charset=utf-8", Stylesheet); return; }
        if (get && path == "/favicon.svg") { await Static(ctx, "image/svg+xml", FaviconSvg); return; }
        if (!s.SignInAvailable)
        {
            // The way out is the operator's website when one is set (#186); otherwise no button, since every page here answers this.
            await SignedInSite.Write(ctx, 503, Page(ctx, "Not configured", ErrorCard(ctx, "Not configured", "Sign-in is not configured on this server yet.",
                "Try again later. If you expected to sign in today, " + s.Operator.Support.Phrase + ".",
                s.Operator.SiteUrl is { } site ? $"<a class=\"btn ghost\" href=\"{site}\">{E("Go to " + s.Operator.SiteName)}</a>" : "")));
            return;
        }
        // There is no sign-in page: a signed-out GET goes straight to Microsoft (see StartSignInAsync for the loop guard).
        if (get && path == "/signed-out") { await SignedOutPage(ctx); return; }   // never redirects, signed in or not
        if (get && path == "/signin")
        {
            // "Sign in again": Microsoft offers its account picker. A click on this site, a typed address, or a browser that sends no
            // Sec-Fetch-Site (Safari before 16.4, Firefox before 90) skips the loop guard; a navigation another site's page says it made is
            // held to it, so a hostile page cannot loop /signin to pile up sign-in cookies. The pending sign-in cap bounds the rest.
            if (!IsNavigation(ctx)) { await NotFound(ctx); return; }
            await StartSignInAsync(ctx, "/home", automatic: !NotFromAnotherSite(ctx), prompt: "select_account");
            return;
        }
        if (!get && path == "/signout") { await SignOut(ctx); return; }
        // The consent return pages (the Packmoat app's always; the feed-access app's only while the wizard's switch is not off): no
        // sign-in, no state, nothing from the reply shown.
        if (await AppAzureSetup.RoutePublicAsync(ctx, s, path, get)) return;

        // A prospect session (only a module that offers a setup makes one) reaches only the module's prospect pages. Any other page
        // sends it to the first of them, before the member check, so a prospect never loops through a sign-in.
        var module = ctx.RequestServices.GetRequiredService<ICustomerSiteModule>();
        if (await ResolveProspectAsync(ctx, module) is { } prospect)
        {
            if (await RouteModuleAsync(ctx, s, module.ProspectRoutes, prospect, path, get, isProspect: true)) return;
            if (module.ProspectRoutes.FirstOrDefault(r => !r.IsPost) is not { } landing) { await NotFound(ctx); return; }
            ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
            ctx.Response.Headers.Location = landing.Path;
            return;
        }

        // Everything below needs a signed-in person whose directory is still linked.
        var actor = await ResolveActorAsync(ctx);
        if (actor is null)
        {
            // A deep link survives the trip to Microsoft only for the fixed pages in ReturnPath. A signed-out POST starts nothing.
            // Only a page load starts one: an icon or other subresource fetch (Safari's /favicon.ico, iOS's /apple-touch-icon.png) gets 404.
            if (get && IsNavigation(ctx)) await StartSignInAsync(ctx, ReturnPath(path, s.AzureSetup.Mode != AzureSetupMode.Off), automatic: true);
            else if (get) await NotFound(ctx);
            else ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (get && path == "/") { ctx.Response.Redirect("/home"); return; }
        if (get && path == "/home") { await Home(ctx, actor, s); return; }
        if (await AppTokens.RouteAsync(ctx, actor, s, path, get)) return;
        if (await AppWaivers.RouteAsync(ctx, actor, s, path, get)) return;
        // The customer-site module's pages (none without one).
        if (await RouteModuleAsync(ctx, s, module.MemberRoutes, actor, path, get, isProspect: false)) return;
        // The Azure setup wizard (off by default): the pages exist only while the switch allows this person's directory.
        if (s.AzureSetup.Allows(actor.DirectoryId) && await AppAzureSetup.RouteAsync(ctx, actor, s, path, get)) return;
        await SignedInNotFound(ctx);
    }

    /// <summary>The 404 for a signed-in person: the way home and the sign-out button.</summary>
    private static Task SignedInNotFound(HttpContext ctx) =>
        SignedInSite.Write(ctx, 404, Page(ctx, "Not found", ErrorCard(ctx, "Not found", "There is no page at this address.", "Go to your home page.",
            "<a class=\"btn\" href=\"/home\">Go to your home page</a>"), SignedInSite.SignOutForm(ctx)));

    /// <summary>
    /// A module's page, with every check the core owns (#192), in this order: the route by exact path and method; then
    /// <see cref="CustomerRoute{T}.Exists"/> (false: the signed-in 404, so a page that does not exist for this person cannot be told from no
    /// page, whatever the Origin); then, for a POST, the Origin (403; the antiforgery token was checked before any routing); then the
    /// handler. True when the path was one of the module's pages.
    /// </summary>
    internal static async Task<bool> RouteModuleAsync<T>(HttpContext ctx, AppSiteSettings s, IReadOnlyList<CustomerRoute<T>> routes, T who, string path, bool get,
        bool isProspect) where T : class
    {
        var route = routes.FirstOrDefault(r => r.Path == path && r.IsPost == !get);
        if (route is null) return false;
        if (!route.Exists(who)) { await SignedInNotFound(ctx); return true; }
        if (!get && !SignedInSite.OriginOk(ctx, s.Host))
        {
            await SignedInSite.ErrorPage(ctx, 403, "Request refused", "The request did not come from this site.", next: "Go back to the page and try again.");
            return true;
        }
        var returnPaths = routes.Where(r => !r.IsPost).Select(r => r.Path).ToHashSet(StringComparer.Ordinal);
        await route.Handle(new CustomerPage<T>(ctx, who, returnPaths, isProspect));
        return true;
    }

    /// <summary>
    /// At start (#251): refuses a customer-site module whose pages could not work. Every page's path is a path on this site (one "/" and
    /// then not "/" or "\", and only printable ASCII with no space, since a browser drops a tab or a newline and reads a look-alike slash
    /// as "/": so the landing redirect and a fresh sign-in's return never lead to another site), and a module with prospect pages has a GET
    /// one for a prospect to land on.
    /// </summary>
    internal static void Validate(ICustomerSiteModule module)
    {
        foreach (var path in module.MemberRoutes.Select(r => r.Path).Concat(module.ProspectRoutes.Select(r => r.Path)))
            if (path is not ['/', ..] || path is ['/', '/' or '\\', ..] || !path.All(c => c is > ' ' and < '\x7f'))
                throw new InvalidOperationException($"The customer-site module's page \"{Shown(path)}\" is not a path on this site. Refusing to start: a page's path starts with one \"/\".");
        if (module.ProspectRoutes.Count > 0 && !module.ProspectRoutes.Any(r => !r.IsPost))
            throw new InvalidOperationException("The customer-site module has prospect pages but no GET page among them. Refusing to start: a prospect lands on the first GET page.");
    }

    /// <summary>A path for the start's one error line: any character outside printable ASCII as \uXXXX, so a newline cannot split the line.</summary>
    internal static string Shown(string path) => string.Concat(path.Select(c => c is >= ' ' and < '\x7f' ? c.ToString() : $"\\u{(int)c:x4}"));

    private static Task Static(HttpContext ctx, string contentType, string content)
    {
        ctx.Response.ContentType = contentType;
        return ctx.Response.WriteAsync(content, ctx.RequestAborted);
    }

    /// <summary>
    /// Where a signed-out deep link may return after Microsoft: exactly /tokens, /team or /waivers, or /setup/azure or /setup/developer (the link an
    /// Admin sends a developer) while the Azure setup switch is not off (case-sensitive, any query dropped), otherwise /home. A fixed
    /// list, never the request's own path or a parameter, so it can never be an open redirect.
    /// </summary>
    internal static string ReturnPath(string path, bool azureSetup = false) =>
        path is "/tokens" or "/team" or AppWaivers.Path || (azureSetup && path is AppAzureSetup.Path or AppAzureSetup.DeveloperPath) ? path : "/home";

    /// <summary>
    /// The one way this site sends someone to Microsoft.
    /// 1. The failed sign-in limiter first: when it is tripped, a 429 page and no redirect.
    /// 2. An automatic start (a signed-out visit) within <see cref="AutomaticStartGuard"/> of the last start in this browser shows the
    ///    signed-out card instead (200, no redirect): this is what stops a redirect loop when the browser comes back without a session.
    /// 3. Otherwise the start marker is (re)set and the challenge sent. An automatic start sends no prompt and no max_age, so it is a
    ///    silent single sign-on when Microsoft can: it never counts as a fresh sign-in (freshness comes only from auth_time).
    /// </summary>
    internal static async Task StartSignInAsync(HttpContext ctx, string returnPath, bool automatic, string? prompt = null, TimeSpan? maxAge = null, bool confirm = false)
    {
        if (confirm && automatic) throw new ArgumentException("An automatic start is never a confirmed sign-in.", nameof(confirm));
        if (ctx.RequestServices.GetRequiredService<AppSignInLimiter>().RetryAfterSeconds(ctx) is { } wait)
        {
            ctx.Response.Headers.RetryAfter = wait.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await SignedInSite.Write(ctx, 429, Page(ctx, "Too many attempts", ErrorCard(ctx, "Too many attempts", "Too many failed sign-ins. Wait a few minutes.",
                "Wait a few minutes, then sign in again.", SignInAgainLink)));
            return;
        }
        var now = ctx.RequestServices.GetRequiredService<IClock>().UtcNow;
        if (automatic && StartedRecently(ctx, now)) { await SignedOutPage(ctx); return; }
        // Each start adds a correlation and a nonce cookie that live for 15 minutes. Past a handful, stop adding them: the request header
        // would otherwise grow until the browser is refused (431) on this site.
        if (PendingSignIns(ctx) > MaxPendingSignIns) { await SignedOutPage(ctx); return; }
        ctx.Response.Cookies.Append(StartCookieName, now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), StartCookieOptions());
        var properties = new OpenIdConnectChallengeProperties { RedirectUri = returnPath };
        if (prompt is not null) properties.Prompt = prompt;
        if (maxAge is not null) properties.MaxAge = maxAge;
        if (confirm) properties.Items[ConfirmItem] = "1";   // protected in the OIDC state, so only this site can set it
        await ctx.ChallengeAsync(RemoteScheme, properties);
    }

    /// <summary>At most this many unfinished sign-ins (correlation cookies) in one browser before a start shows the card instead.</summary>
    internal const int MaxPendingSignIns = 5;

    internal static int PendingSignIns(HttpContext ctx) => ctx.Request.Cookies.Keys.Count(k => k.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal));

    /// <summary>
    /// A page load, not a fetch for an icon or other resource: Sec-Fetch-Mode is navigate, or (a browser that does not send it) the
    /// request accepts HTML.
    /// </summary>
    internal static bool IsNavigation(HttpContext ctx)
    {
        var mode = ctx.Request.Headers["Sec-Fetch-Mode"].ToString();
        if (mode.Length > 0) return string.Equals(mode, "navigate", StringComparison.OrdinalIgnoreCase);
        return ctx.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sec-Fetch-Site same-origin (a link on this site), none (typed, a bookmark), or absent (an older browser that does not send it).
    /// same-site, cross-site or anything else: from another site.
    /// </summary>
    private static bool NotFromAnotherSite(HttpContext ctx) =>
        ctx.Request.Headers["Sec-Fetch-Site"].ToString() is var site
        && (site.Length == 0 || string.Equals(site, "same-origin", StringComparison.OrdinalIgnoreCase) || string.Equals(site, "none", StringComparison.OrdinalIgnoreCase));

    /// <summary>404 for a signed-out request that is not a page load. No challenge, no cookie.</summary>
    private static Task NotFound(HttpContext ctx) =>
        SignedInSite.Write(ctx, 404, Page(ctx, "Not found", ErrorCard(ctx, "Not found", "There is no page at this address.", null)));

    /// <summary>True when this browser was sent to Microsoft less than two minutes ago. A marker that is not a number counts as recent.</summary>
    private static bool StartedRecently(HttpContext ctx, DateTimeOffset now)
    {
        if (!ctx.Request.Cookies.TryGetValue(StartCookieName, out var value)) return false;
        if (!long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var secs) || secs > 253402300799) return true;
        return now - DateTimeOffset.FromUnixTimeSeconds(secs) < AutomaticStartGuard;
    }

    /// <summary>The marker: __Host- name (Secure, Path=/, no Domain), HttpOnly, SameSite=Lax so it comes back with Microsoft's redirect, two minutes.</summary>
    private static CookieOptions StartCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = AutomaticStartGuard,
        IsEssential = true,
    };

    /// <summary>The signed-out card: after sign-out, and when the loop guard stops an automatic start. 200, and it never redirects.</summary>
    private static Task SignedOutPage(HttpContext ctx) =>
        SignedInSite.Write(ctx, 200, Page(ctx, "Signed out", SignInCard("Signed out", SignedOutSentence, notice: false)));

    /// <summary>
    /// The fresh sign-in for admin-only writes: Microsoft is asked to make the person sign in again (prompt=login; max_age too, which Entra
    /// does not document). Freshness comes ONLY from the new ID token's auth_time claim: every admin-only write checks that it is within
    /// 15 minutes of now, so an auth_time more than 15 minutes before this callback (a silent single sign-on) never counts, and a token
    /// without auth_time leaves the session with no sign-in time at all. The new token goes through the same decision as any sign-in, so
    /// the role is decided again from it. A POST (antiforgery and Origin checked) from a page that uses the sign-in-form CSP.
    /// </summary>
    internal static Task ReauthAsync(HttpContext ctx, string returnPath, bool confirm = false) =>
        StartSignInAsync(ctx, returnPath + "?m=reauth", automatic: false, prompt: "login", maxAge: CustomerTokenRules.FreshSignIn, confirm: confirm);   // m=reauth lets the page say so when Microsoft did not confirm a recent sign-in
    /// <summary>Ends the session here (not at Microsoft) and clears the start marker, then sends the browser to the signed-out card, which never redirects.</summary>
    private static async Task SignOut(HttpContext ctx)
    {
        await ctx.SignOutAsync(CookieScheme);
        ctx.Response.Cookies.Delete(StartCookieName, StartCookieOptions());
        ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
        ctx.Response.Headers.Location = "/signed-out";
    }

    private static async Task Home(HttpContext ctx, CustomerActor actor, AppSiteSettings s)
    {
        var home = await ctx.RequestServices.GetRequiredService<CustomerQueries>().HomeAsync(actor, ctx.RequestAborted);
        if (home is null)
        {
            // Cannot normally happen (a binding refers to an existing tenant), but never show an empty page: end the session. A card, not a
            // redirect: a redirect to / would start a sign-in that lands here again.
            await ctx.SignOutAsync(CookieScheme);
            await SignedInSite.Write(ctx, 404, Page(ctx, "Account not found", SignInCard("Account not found", "Your Packmoat account could not be found.")));
            return;
        }
        if (home.Suspended)
        {
            await SignedInSite.Write(ctx, 403, Page(ctx, "Suspended",
                ErrorCard(ctx, "Suspended", SuspendedSentence(s.Operator.Support),
                    SuspendedNext(s.Operator.Support, ctx.RequestServices.GetRequiredService<ICustomerSiteModule>()), SupportButton(s.Operator)), SignedInSite.SignOutForm(ctx)));
            return;
        }
        var admin = home.Role == CustomerRole.Admin;
        var module = ctx.RequestServices.GetRequiredService<ICustomerSiteModule>();
        var connect = await AppAzureSetup.HomeCardAsync(ctx, actor, s);   // "" while the Azure setup switch does not allow the directory
        await SignedInSite.Write(ctx, 200, Page(ctx, "Home",
            "<section class=\"card home\">" + module.HomeBanner(actor, home) + connect + "<p class=\"eyebrow\">Organisation</p>" +
            $"<h1>{E(home.TenantName)}</h1>" +
            "<dl class=\"facts\">" +
            $"<div><dt>Signed in as</dt><dd>{E(home.DisplayName)}</dd></div>" +
            $"<div><dt>Role</dt><dd><span class=\"role{(admin ? " admin" : "")}\">{(admin ? "Admin" : "Developer")}</span></dd></div>" +
            "</dl>" +
            "<div class=\"stats\">" +
            $"<dl class=\"stat\"><dt>Cooldown: new versions wait this long</dt><dd>{home.CooldownDays}<span class=\"unit\">{(home.CooldownDays == 1 ? "day" : "days")}</span></dd></dl>" +
            $"<dl class=\"stat\"><dt>Active developers, last {home.Days} days</dt><dd>{home.ActiveDevelopers}</dd></dl>" +
            "</div>" +
            "<nav class=\"links\" aria-label=\"Your pages\"><a class=\"btn\" href=\"/tokens\">Your feed tokens</a>" +
            (admin ? "<a class=\"btn ghost\" href=\"/team\">Team tokens</a>" : "") +
            "<a class=\"btn ghost\" href=\"/waivers\">Waivers</a>" +
            module.HomeLinks(actor, home) + "</nav>" +
            "<p class=\"fine\">Create a token for your machine on Your feed tokens. " +
            (admin ? E("To change the cooldown or other settings, " + s.Operator.Support.Phrase + ".") : "To change the cooldown, ask your Admin.") + "</p>" +
            $"<div class=\"actions\">{SignedInSite.SignOutForm(ctx)}</div></section>"));
    }
}
