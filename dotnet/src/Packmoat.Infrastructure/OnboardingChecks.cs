using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure;

public sealed class OnboardingOptions
{
    public string Organization { get; set; } = "";
    public string? Project { get; set; }
    public string Feed { get; set; } = "";
    /// <summary>Part of the Packmoat principal's descriptor or display name (its service-principal object id works).</summary>
    public string PackmoatPrincipal { get; set; } = "";
    public string FeedsApiBase { get; set; } = "https://feeds.dev.azure.com";
    /// <summary>Azure DevOps Graph (identities) API host.</summary>
    public string GraphApiBase { get; set; } = "https://vssps.dev.azure.com";
    /// <summary>
    /// Set when the tenant uses Packmoat's shared app: its application (client) id. The 'identity' check then finds that app's service
    /// principal in the organisation and uses it as Packmoat's principal, so no --principal is needed.
    /// </summary>
    public string? SharedAppClientId { get; set; }
    /// <summary>The customer's directory (Entra tenant) id the tenant is bound to, in shared mode. The service principal found must be from it.</summary>
    public string? SharedAppDirectoryId { get; set; }
    public string PackagesBase { get; set; } = "https://pkgs.dev.azure.com";
    /// <summary>Azure DevOps core API host (projects).</summary>
    public string ProjectsApiBase { get; set; } = "https://dev.azure.com";
    /// <summary>
    /// Runs the 'project' check (the feed's project is private) when the feed is in a project. Off by default, so the CLI's `onboard check`
    /// is unchanged; the setup wizard turns it on (its token has Project read).
    /// </summary>
    public bool CheckProject { get; set; }
    /// <summary>
    /// The customer site's wording: a failed read is one fixed sentence (never the error), and the permissions check speaks of
    /// "you" confirming, not the operator's --confirm-admin. Off by default, so the CLI's `onboard check` prints exactly what it did; the
    /// setup wizard's delegated check turns it on.
    /// </summary>
    public bool CustomerWording { get; set; }
    /// <summary>
    /// Who the customer site's wording tells a person to ask: the operator's name and address, or neutral words when unset. Never
    /// read by the CLI's wording.
    /// </summary>
    public SupportContact Support { get; set; } = SupportContact.None;
    /// <summary>A short-lived credential from a customer admin who is owner of the feed. Used only for reading settings.</summary>
    public string? AdminToken { get; set; }
    public bool AdminTokenIsBearer { get; set; }
    /// <summary>A normal developer's credential (PAT, Packaging read). Used to prove developers cannot reach the public-only feed.</summary>
    public string? DeveloperToken { get; set; }
    public bool DeveloperTokenIsBearer { get; set; }
    /// <summary>
    /// Named administrators of the public-only feed the operator has confirmed with the customer (display name or descriptor). The
    /// organisation's built-in admin groups need no confirmation. Not stored: a later run must confirm them again.
    /// </summary>
    public List<string> ConfirmedAdmins { get; } = new();
    /// <summary>URLs of the customer's PRIVATE feeds. Packmoat's own identity must be refused on each.</summary>
    public List<string> PrivateFeedProbeUrls { get; } = new();
    public string? ProxyUrl { get; set; }
    public string? ProxyToken { get; set; }
    /// <summary>A throttled (429/503) answer is retried this many times, waiting as long as Retry-After asks, but never longer than the cap.</summary>
    public int MaxRetries { get; set; } = 3;
    public TimeSpan RetryWaitCap { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Shared-app phase 2: how long the 'proxy' check is retried until it passes (running servers re-read the stored identity every 5 s; older builds every 2 minutes).</summary>
    public TimeSpan ProxyRetryFor { get; set; } = TimeSpan.Zero;
    public TimeSpan ProxyRetryInterval { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// The onboarding verification: reads the customer's feed settings and probes access, then reports PASS, FAIL, WARN or SKIP
/// for each item of the customer isolation checklist. It prints names, statuses and HTTP codes, never a credential.
/// </summary>
public sealed class OnboardingChecks : IFeedSetupCheck
{
    private static readonly string[] AllowedUpstreamHosts = { "api.nuget.org", "registry.npmjs.org" };

    private readonly HttpClient _http;
    private readonly IUpstreamCredentialSource _credentials;
    private readonly OnboardingOptions _o;
    private readonly Tenant _tenant;
    private (bool Ok, string Detail)? _packmoatControl;
    /// <summary>The ways Packmoat's principal can appear in the feed's permission list: the --principal given, or what the identity check found.</summary>
    private readonly List<string> _principals = new();
    private readonly List<NamedFeedAdmin> _namedAdmins = new();

    /// <summary>The feed's named administrators found by the last run's 'permissions' check (not Packmoat, not a built-in admin group).</summary>
    public IReadOnlyList<NamedFeedAdmin> NamedAdmins => _namedAdmins;

    public const string FeedTitle = "The feed checked is the feed Packmoat serves";
    public const string ProjectTitle = "The feed's project is private";

    // The customer site's wording, used only with OnboardingOptions.CustomerWording.
    /// <summary>A read the person's sign-in could not make: never the error itself.</summary>
    public static string CustomerReadFailed(SupportContact support) => "Packmoat couldn't read this from Azure DevOps. Check again; if it keeps happening, " + support.Phrase + ".";
    public const string CustomerConfirmed = " Confirmed by you: {0}.";
    public const string CustomerUnconfirmed = "Administrators not confirmed yet: {0}.";
    public const string CustomerNotPresent = "No longer an administrator of the feed: {0}. Check again.";
    /// <summary>{0}: the names. Followed by whom to ask (<see cref="OnboardingOptions.Support"/>), added after formatting.</summary>
    public const string CustomerAmbiguous = "More than one administrator has the name {0}.";
    public const string CustomerIdentityTitle = "Packmoat feed access is in your organisation exactly once";
    public const string CustomerPermissionsTitle = "Only Packmoat feed access and the administrators you confirm have a role on Packmoat's feed";
    public const string CustomerCollaborator = "Packmoat feed access has the Collaborator role.";
    public const string CustomerBuiltInGroups = " Built-in administrator groups that Azure DevOps adds to every feed: {0}.";

    /// <summary>The CLI's text, or the site's <see cref="CustomerReadFailed"/>.</summary>
    private string Or(string cli) => _o.CustomerWording ? CustomerReadFailed(_o.Support) : cli;

    /// <summary>The CLI's text, or the site's own.</summary>
    private string Site(string cli, string site) => _o.CustomerWording ? site : cli;

    private string PermissionsTitle => Site("Only Packmoat (and chosen admins) hold a role on the public-only feed", CustomerPermissionsTitle);

    public OnboardingChecks(HttpClient http, IUpstreamCredentialSource credentials, Tenant tenant, OnboardingOptions options)
    {
        _http = http;
        _credentials = credentials;
        _tenant = tenant;
        _o = options;
    }

    /// <summary>Every check, once (own-app tenants).</summary>
    public async Task<IReadOnlyList<CheckResult>> RunAsync(CancellationToken ct)
    {
        ValidateNames(_o);   // before any URL is built from them
        return await RunCoreAsync(includeProxy: true, ct);
    }

    /// <summary>
    /// Phase 1 for a shared-app tenant: every check except 'proxy'. The proxy cannot pass yet: the server refuses a shared tenant
    /// until a pass is recorded, and the pass is recorded only when this phase passes. Phase 2 is <see cref="RunProxyAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<CheckResult>> RunWithoutProxyAsync(CancellationToken ct)
    {
        ValidateNames(_o);
        return await RunCoreAsync(includeProxy: false, ct);
    }

    /// <summary>
    /// Phase 2: the 'proxy' check, retried every <see cref="OnboardingOptions.ProxyRetryInterval"/> until it passes or
    /// <see cref="OnboardingOptions.ProxyRetryFor"/> has passed (running servers pick up a new record within their identity cache
    /// lifetime). A check that cannot run (no proxy URL or token) is returned at once.
    /// </summary>
    public async Task<CheckResult> RunProxyAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _o.ProxyRetryFor;
        for (var attempt = 1; ; attempt++)
        {
            var r = await Guard("proxy", ProxyTitle, () => Proxy(ct));
            if (r.Status is CheckStatus.Pass or CheckStatus.Skip || DateTimeOffset.UtcNow + _o.ProxyRetryInterval > deadline)
                return attempt == 1 ? r : r with { Detail = r.Detail + $" (after {attempt} attempts)" };
            await Task.Delay(_o.ProxyRetryInterval, ct);
        }
    }

    private const string ProxyTitle = "Packmoat answers: 401 without a token, 200 with one";

    private async Task<IReadOnlyList<CheckResult>> RunCoreAsync(bool includeProxy, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        results.Add(await Guard("feed", FeedTitle, () => Task.FromResult(SameFeed())));
        var feed = FeedsApiUrl("");
        var admin = _o.AdminToken is null ? null : _o.AdminTokenIsBearer ? "Bearer " + _o.AdminToken : Basic(_o.AdminToken);
        _principals.Clear();
        _namedAdmins.Clear();
        if (_o.PackmoatPrincipal.Length > 0) _principals.Add(_o.PackmoatPrincipal);

        if (_o.SharedAppClientId is not null)
        {
            var title = Site("Packmoat's shared app is in the organisation exactly once", CustomerIdentityTitle);
            results.Add(admin is null
                ? new CheckResult("identity", title, CheckStatus.Skip, Or("needs an admin credential that can read the organisation's identities (Graph read)"))
                : await Guard("identity", title, () => SharedIdentity(admin, ct)));
        }

        if (admin is null)
        {
            foreach (var (id, title) in new[] { ("upstreams", "Upstream sources limited to nuget.org and npmjs"), ("permissions", PermissionsTitle), ("views", "No view is open to the organisation") })
                results.Add(new CheckResult(id, title, CheckStatus.Skip, Or("needs an admin credential for the feed (a short-lived PAT with Packaging read & manage)")));
        }
        else
        {
            results.Add(await Guard("upstreams", "Upstream sources limited to nuget.org and npmjs", () => Upstreams(feed, admin, ct)));
            results.Add(await Guard("permissions", PermissionsTitle, () => Permissions(admin, ct)));
            results.Add(await Guard("views", "No view is open to the organisation", () => Views(admin, ct)));
        }
        // An organisation-scoped feed has no project, so there is nothing to check (and no result).
        if (_o.CheckProject && !string.IsNullOrEmpty(_o.Project))
            results.Add(admin is null
                ? new CheckResult("project", ProjectTitle, CheckStatus.Skip, Or("needs an admin credential that can read the project (Project read)"))
                : await Guard("project", ProjectTitle, () => ProjectVisibility(admin, ct)));

        _packmoatControl = null;
        results.Add(await Guard("developer", "A normal developer account is refused on the public-only feed (checklist 3b)", () => Developer(ct)));
        results.Add(await Guard("private", "Packmoat's own identity is refused on the customer's private feeds", () => PrivateFeeds(ct)));
        if (includeProxy) results.Add(await Guard("proxy", ProxyTitle, () => Proxy(ct)));
        return results;
    }

    private async Task<CheckResult> Guard(string id, string title, Func<Task<(CheckStatus, string)>> body)
    {
        try
        {
            var (status, detail) = await body();
            return new CheckResult(id, title, status, detail);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or KeyNotFoundException or FormatException or UriFormatException or JsonException)
        {
            // The same text on the site: ConnectRules.IsDefiniteFail and the timeout rule read this prefix (an error is never a definite FAIL).
            return new CheckResult(id, title, CheckStatus.Fail, "could not complete: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>The fixed refusal for a bad --org/--project/--feed. It never echoes the value.</summary>
    public const string BadNameMessage = "--org, --project and --feed must each be a plain name: not empty, and without '/', '\\', '%', '?', '#', '..' or spaces.";

    /// <summary>
    /// A name that goes into a URL path segment as is: no separator, no escape, no query or fragment, no dot segment, no space or
    /// control character. Checked before any URL is built, so the URL is exactly the names joined by '/'.
    /// </summary>
    public static bool IsPlainName(string? name) => AzureFeedNames.IsPlainName(name);

    private static void ValidateNames(OnboardingOptions o)
    {
        if (!IsPlainName(o.Organization) || !IsPlainName(o.Feed) || (o.Project is not null && !IsPlainName(o.Project)))
            throw new ArgumentException(BadNameMessage);
    }

    /// <summary>
    /// Every other check probes the feed named by --org/--project/--feed. That only means something if it is the feed the proxy
    /// actually serves for this tenant, so the two are compared first. The names were checked to be plain (no '%', '?', '#'), so the
    /// URL built from them has no escapes, query or fragment; a stored URL that has any of those (or user info) can therefore never
    /// be the same feed and is a FAIL without any decoding. Scheme and host are compared as URIs do; the path ignores case
    /// (Azure DevOps names are case-insensitive) and a trailing slash. Neither user info nor query is ever printed.
    /// </summary>
    private (CheckStatus, string) SameFeed()
    {
        var stored = _tenant.UpstreamFeedIndexUrl;
        if (!Uri.TryCreate(stored, UriKind.Absolute, out var storedUri))
            return (CheckStatus.Fail, "the tenant's stored feed index URL is not a valid absolute URL; fix it with `tenant` before relying on any other check.");
        var checkedUri = new Uri(PublicIndexUrl());
        if (SameFeedUrl(stored, checkedUri))
            return (CheckStatus.Pass, Site($"the options match the tenant's feed: {Display(storedUri)}", $"This is the feed Packmoat serves: {Display(storedUri)}"));
        return (CheckStatus.Fail,
            $"the options point at {Display(checkedUri)} but Packmoat serves {Display(storedUri)} for this tenant, so every other result would be about the wrong feed. " +
            "Rerun with the --org/--project/--feed of the tenant's feed (or fix the tenant's feed URL). A stored URL with user info, a query, a fragment or a percent-escape never matches. " +
            "If the tenant stores the feed or project by id (a GUID) rather than by name, pass the same form here.");
    }

    /// <summary>
    /// True only when <paramref name="stored"/> has no user info, query, fragment, backslash or percent-escape, and has the same scheme,
    /// host, port and path as <paramref name="checkedUri"/> (path compared ignoring case and one trailing slash). Nothing is decoded.
    /// </summary>
    public static bool SameFeedUrl(string stored, Uri checkedUri)
    {
        if (stored.IndexOfAny(new[] { '%', '?', '#', '\\', '@' }) >= 0) return false;
        if (!Uri.TryCreate(stored, UriKind.Absolute, out var s) || s.UserInfo.Length > 0) return false;
        return string.Equals(s.Scheme, checkedUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.IdnHost, checkedUri.IdnHost, StringComparison.OrdinalIgnoreCase)
            && s.Port == checkedUri.Port
            && string.Equals(s.AbsolutePath.TrimEnd('/'), checkedUri.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static string Display(Uri u) => u.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);

    private async Task<(CheckStatus, string)> Upstreams(string feedUrl, string admin, CancellationToken ct)
    {
        var doc = await GetJson(feedUrl, admin, ct);
        var sources = (doc["upstreamSources"] as JsonArray)?.Select(x => x?["location"]?.GetValue<string>() ?? "?").ToArray() ?? Array.Empty<string>();
        if (sources.Length == 0)
            return (CheckStatus.Fail, "the feed reports no upstream sources: it cannot serve nuget.org packages, or the settings could not be read. Add nuget.org as the only NuGet upstream and " + Site("rerun.", "check again."));
        var extras = sources.Where(x => !IsAllowedUpstream(x)).ToArray();
        if (_o.CustomerWording)
            return extras.Length == 0
                ? (CheckStatus.Pass, $"Upstream sources: {string.Join(", ", sources)}.")
                : (CheckStatus.Fail, $"Remove these upstream sources from the feed's settings, keeping only nuget.org and npmjs: {string.Join(", ", extras)}.");
        return extras.Length == 0
            ? (CheckStatus.Pass, $"{sources.Length} upstream source(s): {string.Join(", ", sources)}")
            : (CheckStatus.Fail, $"remove {extras.Length} extra upstream source(s): {string.Join(", ", extras)}");
    }

    /// <summary>A feed role as Azure DevOps shows it: "reader" is Reader.</summary>
    public static string RoleName(string role) => role.Length == 0 ? role : char.ToUpperInvariant(role[0]) + role[1..];

    /// <summary>A view's visibility in plain words, never the API's value.</summary>
    public static string VisibilityName(string? visibility) => visibility?.ToLowerInvariant() switch
    {
        "organization" or "collection" => "your organisation",
        "aadtenant" => "your Microsoft directory",
        _ => "not set to Specific people",
    };

    /// <summary>Exact host match over https. "nuget.org.evil.example" and "evil.example/api.nuget.org" are not nuget.org.</summary>
    public static bool IsAllowedUpstream(string location) =>
        Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && AllowedUpstreamHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    // Groups that mean "many people". Holding any role on the public-only feed through one of these defeats the isolation;
    // holding administrator through one is the worst case, so it is a failure rather than something to confirm.
    private static readonly string[] BroadGroups = { "Valid Users", "Contributors", "Readers", "Collaborators", "Build Service", "Everyone" };

    private async Task<(CheckStatus, string)> Permissions(string admin, CancellationToken ct)
    {
        if (_principals.Count == 0)
            return (CheckStatus.Skip, _o.SharedAppClientId is null
                ? "NOT VERIFIED: no --principal (the service principal's object id) given, so Packmoat's own role cannot be identified."
                : Site("NOT VERIFIED: Packmoat's service principal was not identified (see the identity check), so its role cannot be checked.",
                    "NOT VERIFIED: Packmoat feed access wasn't found in your organisation (see the check above), so its role on the feed can't be checked."));

        var entries = await GetAllValues(FeedsApiUrl("/permissions?includeIds=true"), admin, ct);
        _identitiesUnavailable = null;
        var problems = new List<string>();
        var admins = new List<(string Label, string Name, string Descriptor)>();
        var orgAdmins = new List<string>();
        var packmoatRole = "none";

        foreach (var e in entries)
        {
            var role = e["role"]?.GetValue<string>() ?? "none";
            var name = e["displayName"]?.GetValue<string>() ?? "";
            var descriptor = e["identityDescriptor"]?.GetValue<string>() ?? "";
            var label = name.Length > 0 ? name : Shorten(descriptor);
            if (role == "none") continue;

            if (IsPackmoat(descriptor, name))
            {
                packmoatRole = role;
                continue;
            }
            if (role != "administrator" || BroadGroups.Any(g => label.Contains(g, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"{label} has {Site(role, RoleName(role))}");
                continue;
            }
            // Azure DevOps puts the organisation's own admin groups on every new feed as administrators: expected, not a finding.
            // Only the identities API can say which group is which (both have SIDs ending -0-0-0-0-1), so every Team Foundation
            // group administrator is looked up; anything not confirmed there is a named admin.
            if (descriptor.StartsWith(TeamFoundationIdentity, StringComparison.OrdinalIgnoreCase))
            {
                var (builtIn, how) = await IdentifyOrgAdminGroup(descriptor, admin, ct);
                if (builtIn is not null)
                {
                    orgAdmins.Add(_o.CustomerWording ? label : $"{label} ({builtIn}, confirmed by the identities API)");
                    continue;
                }
                if (how is not null) label += $" ({how})";
            }
            admins.Add((label, name, descriptor));
        }

        _namedAdmins.AddRange(admins.Select(a => new NamedFeedAdmin(a.Label, a.Name, a.Descriptor)));
        if (_o.CustomerWording && (packmoatRole != "collaborator" || problems.Count > 0))
        {
            var fix = new List<string>();
            if (packmoatRole != "collaborator")
                fix.Add(packmoatRole == "none" ? "Packmoat feed access has no role on the feed; it needs Collaborator." : $"Packmoat feed access has the {RoleName(packmoatRole)} role on the feed; it needs Collaborator.");
            if (problems.Count > 0) fix.Add($"Change these roles in the feed's Permissions: {string.Join("; ", problems)}.");
            return (CheckStatus.Fail, string.Join(" ", fix));
        }
        if (packmoatRole != "collaborator") problems.Insert(0, $"Packmoat's principal has '{packmoatRole}', it needs exactly 'collaborator'");
        if (problems.Count > 0) return (CheckStatus.Fail, "fix: " + string.Join("; ", problems));

        var expected = orgAdmins.Count == 0 ? ""
            : _o.CustomerWording ? string.Format(CustomerBuiltInGroups, string.Join("; ", orgAdmins))
            : $" Expected organisation admin groups: {string.Join("; ", orgAdmins)}.";
        var confirmations = _o.ConfirmedAdmins.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (admins.Count == 0 && confirmations.Count == 0)
            return (CheckStatus.Pass, Site("Packmoat is collaborator and no other identity holds a role.", CustomerCollaborator + " No one else has a role on the feed.") + expected);

        // PASS only when the remaining named administrators are exactly the confirmed set: each matched by a confirmation, each
        // confirmation matching exactly one administrator (by display name or full descriptor).
        static bool Matches(string confirmation, (string Label, string Name, string Descriptor) a) =>
            confirmation.Equals(a.Name, StringComparison.OrdinalIgnoreCase) || confirmation.Equals(a.Descriptor, StringComparison.OrdinalIgnoreCase);
        var unconfirmed = admins.Where(a => !confirmations.Any(c => Matches(c, a))).Select(a => a.Label).ToList();
        var notPresent = confirmations.Where(c => !admins.Any(a => Matches(c, a))).ToList();
        var ambiguous = confirmations.Where(c => admins.Count(a => Matches(c, a)) > 1).ToList();
        var confirmed = confirmations.Except(notPresent, StringComparer.OrdinalIgnoreCase).ToList();
        var byOperator = confirmed.Count == 0 ? ""
            : _o.CustomerWording ? string.Format(CustomerConfirmed, string.Join(", ", confirmed))
            : $" Confirmed by the operator (--confirm-admin): {string.Join(", ", confirmed)}.";

        if (unconfirmed.Count == 0 && notPresent.Count == 0 && ambiguous.Count == 0)
            return (CheckStatus.Pass, Site("Packmoat is collaborator; every other administrator is expected or confirmed.",
                CustomerCollaborator + " Every other administrator is a built-in group or one you confirmed.") + expected + byOperator);

        if (_o.CustomerWording)
        {
            var sentences = new List<string>();
            if (unconfirmed.Count > 0) sentences.Add(string.Format(CustomerUnconfirmed, string.Join(", ", unconfirmed)));
            if (notPresent.Count > 0) sentences.Add(string.Format(CustomerNotPresent, string.Join(", ", notPresent)));
            if (ambiguous.Count > 0) sentences.Add(string.Format(CustomerAmbiguous, string.Join(", ", ambiguous)) + " " + _o.Support.CapitalPhrase + ".");
            return (CheckStatus.Warn, CustomerCollaborator + " " + string.Join(" ", sentences) + expected + byOperator);
        }

        var differences = new List<string>();
        if (unconfirmed.Count > 0) differences.Add($"administrators not confirmed (ask the customer who they are, then rerun with --confirm-admin \"<name>\" for each): {string.Join(", ", unconfirmed)}");
        if (notPresent.Count > 0) differences.Add($"confirmed but not a named administrator on the feed (drop that --confirm-admin, or ask the customer what changed): {string.Join(", ", notPresent)}");
        if (ambiguous.Count > 0) differences.Add($"a confirmation matches more than one administrator (confirm each by its descriptor instead): {string.Join(", ", ambiguous)}");
        return (CheckStatus.Warn, "Packmoat is collaborator. " + string.Join("; ", differences) + "." + expected + byOperator);
    }

    private const string TeamFoundationIdentity = "Microsoft.TeamFoundation.Identity;";

    private const string CollectionDomain = "vstfs:///Framework/IdentityDomain/";
    private const string ProjectDomain = "vstfs:///Classification/TeamProject/";
    private const string AdministrativeGroup = "AdministrativeApplicationGroup";
    /// <summary>Set once in a run when the identities API could not be read, so it is not asked again for every group.</summary>
    private string? _identitiesUnavailable;

    /// <summary>
    /// The identities API's description of one identity (Identities - Read Identities, by descriptor). Properties come as
    /// {"$type":"System.String","$value":...} pairs, as seen live: SpecialType, ScopeName, Domain, Account, SchemaClassName.
    /// </summary>
    public sealed record IdentityFacts(bool IsContainer, string? SpecialType, string? ScopeName, string? Domain, string? Account);

    /// <summary>
    /// Which of the organisation's built-in admin groups this is, from the identities API only (the SID cannot tell them apart:
    /// Project Collection Administrators and every project's Project Administrators both end -0-0-0-0-1):
    /// <list type="bullet">
    /// <item>Project Collection Administrators: a group, SpecialType AdministrativeApplicationGroup, Domain vstfs:///Framework/IdentityDomain/...,
    /// ScopeName = the organisation checked, Account "Project Collection Administrators".</item>
    /// <item>Project Administrators of the checked project: a group, SpecialType AdministrativeApplicationGroup, Domain
    /// vstfs:///Classification/TeamProject/{id}, ScopeName = the project checked (or that id = the project checked, when given as a GUID),
    /// Account "Project Administrators".</item>
    /// </list>
    /// Null for anything else, including another project's Project Administrators.
    /// </summary>
    public static string? OrgAdminGroup(IdentityFacts f, string organization, string? project)
    {
        static bool Is(string? a, string? b) => a is not null && b is not null && a.Equals(b, StringComparison.OrdinalIgnoreCase);
        if (!f.IsContainer || !Is(f.SpecialType, AdministrativeGroup) || f.Domain is null) return null;

        if (f.Domain.StartsWith(CollectionDomain, StringComparison.OrdinalIgnoreCase)
            && Is(f.ScopeName, organization) && Is(f.Account, "Project Collection Administrators"))
            return "the organisation's built-in Project Collection Administrators";

        if (!string.IsNullOrEmpty(project) && f.Domain.StartsWith(ProjectDomain, StringComparison.OrdinalIgnoreCase)
            && Is(f.Account, "Project Administrators")
            && (Is(f.ScopeName, project)
                || (Guid.TryParse(f.Domain[ProjectDomain.Length..], out var id) && Guid.TryParse(project, out var want) && id == want)))
            return "the project's built-in Project Administrators";
        return null;
    }

    /// <summary>
    /// Looks the group up and returns which built-in admin group it is (null if none), and otherwise a note for the operator.
    /// When the API cannot be read, nothing is recognised: the group is a named admin, and the note says the PAT needs Identity (read).
    /// </summary>
    private async Task<(string? BuiltIn, string? Note)> IdentifyOrgAdminGroup(string descriptor, string admin, CancellationToken ct)
    {
        const string needsIdentityRead = "not recognised as a built-in admin group: the identities lookup failed ({0}); the admin PAT needs Identity (read) for automatic recognition";
        const string couldNotRead = "Packmoat couldn't read what this group is";
        if (_identitiesUnavailable is not null) return (null, Site(string.Format(needsIdentityRead, _identitiesUnavailable), couldNotRead));

        JsonObject? identity;
        try
        {
            var body = await GetJson($"{_o.GraphApiBase.TrimEnd('/')}/{_o.Organization}/_apis/identities?descriptors={Uri.EscapeDataString(descriptor)}&queryMembership=None&api-version=7.1", admin, ct);
            var found = (body["value"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            identity = found.Count == 1 ? found[0] : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException or JsonException)
        {
            _identitiesUnavailable = ex.Message;
            return (null, Site(string.Format(needsIdentityRead, ex.Message), couldNotRead));
        }
        if (identity is null) return (null, Site("not recognised as a built-in admin group: the identities API did not return it", "Azure DevOps didn't describe this group"));

        static string? Prop(JsonObject? props, string name) => props?[name] switch
        {
            JsonObject o when o["$value"] is JsonValue v && v.TryGetValue<string>(out var s) => s,
            JsonValue plain when plain.TryGetValue<string>(out var p) => p,
            _ => null,
        };
        var props = identity["properties"] as JsonObject;
        var facts = new IdentityFacts(
            identity["isContainer"] is JsonValue c && c.TryGetValue<bool>(out var container) && container,
            Prop(props, "SpecialType"), Prop(props, "ScopeName"), Prop(props, "Domain"), Prop(props, "Account"));
        return OrgAdminGroup(facts, _o.Organization, _o.Project) is { } builtIn
            ? (builtIn, null)
            : (null, Site($"not a built-in admin group of this organisation or project: the identities API describes '{facts.Account}' of type '{facts.SpecialType}' in scope '{facts.ScopeName}'",
                "not one of Azure DevOps' built-in administrator groups"));
    }

    /// <summary>The identity IS the configured principal: the whole descriptor, its last segment, or the display name, compared exactly (never "contains").</summary>
    private bool IsPackmoat(string descriptor, string displayName)
    {
        var last = descriptor[(descriptor.LastIndexOfAny(new[] { '\\', ';', '/' }) + 1)..];
        return _principals.Any(principal =>
            descriptor.Equals(principal, StringComparison.OrdinalIgnoreCase)
            || last.Equals(principal, StringComparison.OrdinalIgnoreCase)
            || displayName.Equals(principal, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Shared mode: lists the organisation's service principals (Azure DevOps Graph API) and finds the one whose application id is
    /// Packmoat's shared app. Exactly one must exist; it becomes Packmoat's principal for the permissions check.
    /// API shape per Microsoft's REST reference (Service Principals - List, 7.1-preview.1): a 'value' array of GraphServicePrincipal
    /// with applicationId, originId, descriptor, domain, isDeletedInOrigin; more pages are signalled by the X-MS-ContinuationToken header.
    /// NOT YET PROVEN against a live organisation.
    /// </summary>
    private async Task<(CheckStatus, string)> SharedIdentity(string admin, CancellationToken ct)
    {
        List<JsonObject> all;
        try
        {
            all = await GetAllValues($"{_o.GraphApiBase.TrimEnd('/')}/{_o.Organization}/_apis/graph/serviceprincipals?api-version=7.1-preview.1", admin, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException or JsonException)
        {
            return (CheckStatus.Skip, Or("NOT VERIFIED: could not list the organisation's service principals (" + ex.Message + "). The admin credential needs Graph (read) as well as Packaging."));
        }

        static string Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        static bool SameGuid(string a, string b) => Guid.TryParse(a, out var x) && Guid.TryParse(b, out var y) && x == y;

        var matches = all.Where(sp => SameGuid(Text(sp, "applicationId"), _o.SharedAppClientId!)
                                      && !(sp["isDeletedInOrigin"] is JsonValue d && d.TryGetValue<bool>(out var deleted) && deleted)).ToList();
        if (matches.Count == 0)
            return (CheckStatus.Fail, Site($"Packmoat's app (application id {_o.SharedAppClientId}) is not a member of the organisation ({all.Count} service principal(s) listed). " +
                "The customer adds it in Organization settings > Users after their directory administrator has added the app to the directory.",
                "Packmoat feed access isn't in your organisation yet. Add it in Organization settings > Users (step 3), after a directory administrator has approved it (step 2)."));
        if (matches.Count > 1)
            return (CheckStatus.Fail, Site($"{matches.Count} service principals in the organisation have Packmoat's application id; expected exactly one. Ask the customer to remove the extra ones.",
                $"{matches.Count} users in your organisation are Packmoat feed access; there should be one. Remove the extra ones in Organization settings > Users."));

        var sp = matches[0];
        var originId = Text(sp, "originId");
        var descriptor = Text(sp, "descriptor");
        var domain = Text(sp, "domain");
        if (originId.Length == 0)
            return (CheckStatus.Fail, Site("the service principal for Packmoat's app has no originId (object id), so its feed role cannot be identified.",
                "Azure DevOps didn't give Packmoat feed access's id, so its role on the feed can't be checked. " + _o.Support.CapitalPhrase + "."));
        if (domain.Length == 0 || string.IsNullOrEmpty(_o.SharedAppDirectoryId))
            return (CheckStatus.Skip, Site($"NOT VERIFIED: found Packmoat's app once (object id {originId}), but " + (domain.Length == 0
                ? "the API did not say which directory it comes from (no 'domain'), so it cannot be matched to the tenant's directory."
                : "the tenant's bound directory is not known here."),
                "NOT VERIFIED: Packmoat feed access is in your organisation once, but Azure DevOps didn't say which Microsoft directory it comes from. " + _o.Support.CapitalPhrase + "."));
        if (!SameGuid(domain, _o.SharedAppDirectoryId!))
            return (CheckStatus.Fail, Site($"Packmoat's app in this organisation comes from directory {domain}, but the tenant is bound to directory {_o.SharedAppDirectoryId}. Rebind the tenant (identity bind) to the organisation's directory.",
                "Packmoat feed access in this organisation comes from a different Microsoft directory from the one your organisation uses with Packmoat. " + _o.Support.CapitalPhrase + "."));
        if (_o.PackmoatPrincipal.Length > 0 && !_o.PackmoatPrincipal.Equals(originId, StringComparison.OrdinalIgnoreCase) && !_o.PackmoatPrincipal.Equals(descriptor, StringComparison.OrdinalIgnoreCase))
            return (CheckStatus.Fail, $"--principal {_o.PackmoatPrincipal} is not the service principal of Packmoat's app (object id {originId}). Leave --principal out in shared mode.");

        _principals.Add(originId);
        if (descriptor.Length > 0) _principals.Add(descriptor);
        return (CheckStatus.Pass, Site($"found Packmoat's app once: object id {originId}{(domain.Length > 0 ? ", directory " + domain : "")}. It is used as Packmoat's principal for the role check.",
            "Packmoat feed access is in your organisation once."));
    }

    /// <summary>
    /// A feed in a public project can be made readable by anyone, without signing in, which skips Packmoat. Projects - Get answers
    /// visibility "private" or "public".
    /// </summary>
    private async Task<(CheckStatus, string)> ProjectVisibility(string admin, CancellationToken ct)
    {
        var doc = await GetJson($"{_o.ProjectsApiBase.TrimEnd('/')}/{_o.Organization}/_apis/projects/{_o.Project}?api-version=7.1", admin, ct);
        var visibility = doc["visibility"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
        if (string.Equals(visibility, "private", StringComparison.OrdinalIgnoreCase)) return (CheckStatus.Pass, "the project is private");
        if (string.Equals(visibility, "public", StringComparison.OrdinalIgnoreCase))
            return (CheckStatus.Fail, "the project is public: a feed in a public project can be read by anyone without signing in, which skips Packmoat. Make the project private, or use an organisation-scoped feed.");
        return (CheckStatus.Skip, "NOT VERIFIED: Azure DevOps did not say whether the project is private.");
    }

    private async Task<(CheckStatus, string)> Views(string admin, CancellationToken ct)
    {
        var doc = await GetJson(FeedsApiUrl("/views"), admin, ct);
        var views = (doc["value"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? Array.Empty<JsonObject>();
        var openViews = views.Where(v => (v["visibility"]?.GetValue<string>() ?? "private") is not "private").ToArray();
        if (_o.CustomerWording && openViews.Length > 0)
            return (CheckStatus.Fail, "These views can be read by more people than the feed: "
                + string.Join(", ", openViews.Select(v => $"{v["name"]} ({VisibilityName(v["visibility"]?.GetValue<string>())})"))
                + ". In the feed's settings under Views, set each to Specific people.");
        var open = openViews.Select(v => $"{v["name"]} is '{v["visibility"]}'").ToArray();
        return open.Length == 0
            ? (CheckStatus.Pass, $"{views.Length} view(s), all private")
            : (CheckStatus.Fail, "set to specific people: " + string.Join("; ", open));
    }

    /// <summary>
    /// Positive control: Packmoat's own identity must be able to read the public-only feed. Without it, a refusal below could just mean
    /// a wrong URL, a typo in the feed name or a broken credential, and "refused" would be proven of nothing.
    /// </summary>
    private async Task<(bool Ok, string Detail)> PackmoatControl(CancellationToken ct)
    {
        if (_packmoatControl is { } cached) return cached;
        var credential = await _credentials.GetAsync(_tenant, ct);
        using var response = await Send(() => Get(PublicIndexUrl(), $"{credential.Scheme} {credential.Parameter}"), ct);
        var failure = DeveloperProbeVerdict.ControlFailure((int)response.StatusCode);
        _packmoatControl = failure is null ? (true, "") : (false, failure);
        return _packmoatControl.Value;
    }

    /// <summary>The developer check, with the verdict the wizard's developer proof shares (<see cref="DeveloperProbeVerdict"/>).</summary>
    private async Task<(CheckStatus, string)> Developer(CancellationToken ct)
    {
        if (_o.DeveloperToken is null)
            return (CheckStatus.Skip, "NOT VERIFIED: no developer credential given. Ask one developer for a short-lived Packaging (read) PAT and rerun.");

        var control = await PackmoatControl(ct);
        if (!control.Ok) return Pair(DeveloperProbeVerdict.ControlNotMet(control.Detail));

        var developer = _o.DeveloperTokenIsBearer ? "Bearer " + _o.DeveloperToken : Basic(_o.DeveloperToken);
        // Second control: the developer's credential must itself be valid (not expired or mistyped), shown by it working on something it may read.
        using (var valid = await Send(() => Get(FeedsApiUrl("", feedName: false), developer), ct))
            if (DeveloperProbeVerdict.ListControl((int)valid.StatusCode) is { } refused) return Pair(refused);

        using var response = await Send(() => Get(PublicIndexUrl(), developer), ct);
        return Pair(DeveloperProbeVerdict.Index((int)response.StatusCode));
    }

    private static (CheckStatus, string) Pair(DeveloperProbeDecision d) => (d.Status, d.Detail);

    private async Task<(CheckStatus, string)> PrivateFeeds(CancellationToken ct)
    {
        if (_o.PrivateFeedProbeUrls.Count == 0)
            return (CheckStatus.Skip, "NOT VERIFIED: no private feed URLs given (--probe-url, once per private feed).");

        var control = await PackmoatControl(ct);
        if (!control.Ok) return (CheckStatus.Skip, "NOT VERIFIED: " + control.Detail);

        var credential = await _credentials.GetAsync(_tenant, ct);
        var leaks = new List<string>();
        var unclear = new List<string>();
        foreach (var url in _o.PrivateFeedProbeUrls)
        {
            using var response = await Send(() => Get(url, $"{credential.Scheme} {credential.Parameter}"), ct);
            var code = (int)response.StatusCode;
            if (code is 401 or 403) continue;                      // refused, and the control above shows the credential itself is good
            if (code is 200 or (>= 300 and < 400)) leaks.Add($"{Redact(url)} answered HTTP {code}");
            else unclear.Add($"{Redact(url)} answered HTTP {code}");
        }
        if (leaks.Count > 0) return (CheckStatus.Fail, "Packmoat's identity can read: " + string.Join("; ", leaks) + ". Remove its group memberships or roles before going live.");
        if (unclear.Count > 0) return (CheckStatus.Skip, "NOT VERIFIED: " + string.Join("; ", unclear) + " (404 can mean a typo in the URL, not a refusal).");
        return (CheckStatus.Pass, $"Packmoat's identity can read the public-only feed but is refused on all {_o.PrivateFeedProbeUrls.Count} private feed(s)");
    }

    private async Task<(CheckStatus, string)> Proxy(CancellationToken ct)
    {
        if (_o.ProxyUrl is null || _o.ProxyToken is null)
            return (CheckStatus.Skip, "NOT VERIFIED: no proxy URL and token given.");

        using var anon = await Send(() => new HttpRequestMessage(HttpMethod.Get, _o.ProxyUrl), ct);
        using var authed = await Send(() => Get(_o.ProxyUrl, "Bearer " + _o.ProxyToken), ct);
        return (int)anon.StatusCode == 401 && authed.StatusCode == HttpStatusCode.OK
            ? (CheckStatus.Pass, "401 without a token, 200 with the customer's token")
            : (CheckStatus.Fail, $"without token HTTP {(int)anon.StatusCode} (want 401), with token HTTP {(int)authed.StatusCode} (want 200)");
    }

    // ---- helpers
    private string Scope() => string.IsNullOrEmpty(_o.Project) ? $"{_o.Organization}/" : $"{_o.Organization}/{_o.Project}/";
    private string FeedsApiUrl(string tail, bool feedName = true) => $"{_o.FeedsApiBase.TrimEnd('/')}/{Scope()}_apis/packaging/feeds{(feedName ? "/" + _o.Feed : "")}{tail}{(tail.Contains('?') ? "&" : "?")}api-version=7.1";
    private string PublicIndexUrl() => $"{_o.PackagesBase.TrimEnd('/')}/{Scope()}_packaging/{_o.Feed}/nuget/v3/index.json";
    private static HttpRequestMessage Get(string url, string authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }
    private static string Basic(string pat) => "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat));
    private static string Shorten(string descriptor) => descriptor.Length > 60 ? descriptor[..57] + "..." : descriptor;
    private static string Redact(string url) => new Uri(url).GetLeftPart(UriPartial.Path);

    /// <summary>Sends a fresh request each time; a throttled answer (429, or 503 with Retry-After) is retried, waiting as asked up to a cap.</summary>
    private async Task<HttpResponseMessage> Send(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = make();
            var response = await _http.SendAsync(request, ct);
            if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) || attempt >= _o.MaxRetries) return response;

            var wait = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1 << attempt));
            wait = wait < TimeSpan.Zero ? TimeSpan.Zero : wait > _o.RetryWaitCap ? _o.RetryWaitCap : wait;
            response.Dispose();
            await Task.Delay(wait, ct);
        }
    }

    private async Task<JsonObject> GetJson(string url, string authorization, CancellationToken ct) => (await GetPage(url, authorization, ct)).Body;

    private async Task<(JsonObject Body, string? Continuation)> GetPage(string url, string authorization, CancellationToken ct)
    {
        using var response = await Send(() => Get(url, authorization), ct);
        // Exactly 200. Azure DevOps answers a credential it does not accept with 203 and an HTML sign-in page, and a redirect (the
        // client never follows one) is a sign-in too: neither is a settings answer, whatever IsSuccessStatusCode says.
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} from the Azure DevOps API, not 200 (203 or a redirect means a sign-in page: is the admin credential valid and an owner of the feed?)");
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)); }
        catch (JsonException)
        {
            // The body is never echoed: it is not ours to print and may be a whole HTML page.
            throw new FormatException($"the Azure DevOps API answered HTTP 200 but the body is not JSON (Content-Type {response.Content.Headers.ContentType?.MediaType ?? "none"}); is the admin credential valid?");
        }
        var body = parsed as JsonObject ?? throw new FormatException("the Azure DevOps API answered HTTP 200 but the body is not a JSON object");
        return (body, response.Headers.TryGetValues("x-ms-continuationtoken", out var t) ? t.FirstOrDefault() : null);
    }

    /// <summary>All entries of "value", following the continuation token so nobody hides on page two.</summary>
    private async Task<List<JsonObject>> GetAllValues(string url, string authorization, CancellationToken ct)
    {
        var all = new List<JsonObject>();
        string? token = null;
        for (var page = 0; page < 100; page++)
        {
            var (body, next) = await GetPage(token is null ? url : $"{url}&continuationToken={Uri.EscapeDataString(token)}", authorization, ct);
            all.AddRange((body["value"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>());
            if (string.IsNullOrEmpty(next)) return all;
            token = next;
        }
        throw new InvalidOperationException("the list has more than 100 pages; stopping rather than guessing");
    }
}
