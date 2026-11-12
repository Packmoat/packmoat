using System.Globalization;
using System.Text.RegularExpressions;
using Packmoat.Domain;

namespace Packmoat.Application;

public enum AzureSetupMode { Off, On, Allowlist }

/// <summary>
/// The Azure setup wizard's switch (PACKMOAT_AZURE_SETUP: off, on, or a comma-separated list of directory GUIDs), parsed like the
/// checkout switch. Off: none of the wizard's pages, callback or home card exist.
/// </summary>
public sealed class AzureSetupSettings
{
    public static readonly AzureSetupSettings Off = new(AzureSetupMode.Off, new HashSet<string>(StringComparer.Ordinal));

    private AzureSetupSettings(AzureSetupMode mode, IReadOnlySet<string> directories)
    {
        Mode = mode;
        Directories = directories;
    }

    public AzureSetupMode Mode { get; }
    /// <summary>Allowlist mode only: the lower-case canonical directory ids allowed to use the wizard.</summary>
    public IReadOnlySet<string> Directories { get; }

    /// <summary>True when people from <paramref name="directoryId"/> may use the wizard: never when off, never for a value that is not a directory GUID or is the consumer directory.</summary>
    public bool Allows(string? directoryId)
    {
        if (Mode == AzureSetupMode.Off) return false;
        var d = CustomerSignIn.CanonicalDirectory(directoryId);
        if (d is null || Guid.Parse(d) == CustomerSignIn.ConsumerDirectory) return false;
        return Mode == AzureSetupMode.On || Directories.Contains(d);
    }

    /// <summary>Parses the switch. Throws <see cref="InvalidOperationException"/> (naming the setting, never echoing it) when it is invalid.</summary>
    public static AzureSetupSettings Parse(string? flag)
    {
        var text = flag?.Trim() ?? "";
        if (text.Length == 0 || text == "off") return Off;
        if (text == "on") return new AzureSetupSettings(AzureSetupMode.On, Off.Directories);
        var list = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in text.Split(','))
        {
            if (!Guid.TryParseExact(part.Trim(), "D", out var g) || g == Guid.Empty || g == CustomerSignIn.ConsumerDirectory)
                throw new InvalidOperationException("PACKMOAT_AZURE_SETUP must be off, on, or a comma-separated list of directory (tenant) GUIDs.");
            list.Add(g.ToString("D"));
        }
        return new AzureSetupSettings(AzureSetupMode.Allowlist, list);
    }

    public override string ToString() => $"AzureSetupSettings({Mode}, {Directories.Count} directories)";
}

/// <summary>What a person typed in step 1: the organisation, the project (null for an organisation-scoped feed) and the feed.</summary>
public sealed record FeedNames(string Organization, string? Project, string Feed);

/// <summary>
/// Step 1 as it was when the person opened Microsoft's approval page: only the names that passed their own rule (any other
/// is null), and when. Names only: never anything else from the form.
/// </summary>
public sealed record FeedNamesDraft(DateTimeOffset At, string? Organization, string? Project, string? Feed)
{
    /// <summary>Each typed name kept only if it passes its rule (<see cref="AzureFeedNames"/>); null when none does.</summary>
    public static FeedNamesDraft? From(DateTimeOffset at, string? organization, string? project, string? feed)
    {
        var o = AzureFeedNames.IsOrganization(organization) ? organization : null;
        var p = !string.IsNullOrWhiteSpace(project) && AzureFeedNames.IsProject(project) ? project : null;
        var f = AzureFeedNames.IsFeed(feed) ? feed : null;
        return o is null && p is null && f is null ? null : new FeedNamesDraft(at, o, p, f);
    }
}

/// <summary>Which typed name was refused. The page shows a fixed sentence for each; nothing typed is echoed.</summary>
public enum FeedNameProblem { Organization, Project, Feed }

/// <summary>
/// The name rules. No URL is ever accepted as input: the organisation must match the Azure DevOps organisation pattern, the feed must be a
/// plain path segment of at most 64 ASCII characters, and the project (optional; it may hold spaces) is escaped once, only for the
/// Get Feed call that resolves it. Every URL Packmoat keeps is then built from the GUIDs that call returns.
/// </summary>
public static class AzureFeedNames
{
    public const int MaxFeedLength = 64;
    public const int MaxProjectLength = 64;

    // \A and \z, not ^ and $: "$" also matches before a final line break.
    private static readonly Regex Org = new(@"\A[A-Za-z0-9]([A-Za-z0-9-]{0,48}[A-Za-z0-9])?\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// A name that goes into a URL path segment as is: no separator, no escape, no query or fragment, no dot segment, no space or control
    /// character. (The onboarding CLI's rule, used for every name it puts in a URL.)
    /// </summary>
    public static bool IsPlainName(string? name) =>
        !string.IsNullOrEmpty(name) && name != "." && !name.Contains("..")
        && !name.Any(c => c is '/' or '\\' or '%' or '?' or '#' || char.IsWhiteSpace(c) || char.IsControl(c));

    public static bool IsOrganization(string? name) => name is not null && Org.IsMatch(name);

    /// <summary>A plain name of at most 64 characters, ASCII only (a full-width '／' or '．' is never a feed name).</summary>
    public static bool IsFeed(string? name) => IsPlainName(name) && name!.Length <= MaxFeedLength && name.All(c => c is > ' ' and < (char)127);

    // Characters Azure DevOps does not allow in a project name, plus every separator, escape and query character.
    private static readonly char[] ProjectForbidden = { '/', '\\', '%', '?', '#', ':', '*', '"', '\'', '<', '>', ';', '$', '{', '}', ',', '+', '=', '[', ']', '|', '&', '~', '@' };

    /// <summary>
    /// 1 to 64 characters with no leading or trailing space, no dot segment, none of the characters Azure DevOps forbids, no control or
    /// invisible character and nothing from the full-width forms block. Spaces inside and other letters are allowed: the project is escaped
    /// once for the resolve call and never put in a stored URL.
    /// </summary>
    public static bool IsProject(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxProjectLength || name.Trim() != name) return false;
        if (name.StartsWith('.') || name.StartsWith('_') || name.EndsWith('.') || name.Contains("..")) return false;
        foreach (var c in name)
        {
            if (Array.IndexOf(ProjectForbidden, c) >= 0 || char.IsControl(c) || c is >= '＀' and <= '￯') return false;
            if (char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned) return false;
            if (char.IsWhiteSpace(c) && c != ' ') return false;
        }
        return true;
    }

    /// <summary>The names, or the problems. An empty or blank project means an organisation-scoped feed.</summary>
    public static (FeedNames? Names, IReadOnlyList<FeedNameProblem> Problems) Validate(string? organization, string? project, string? feed)
    {
        var problems = new List<FeedNameProblem>();
        if (!IsOrganization(organization)) problems.Add(FeedNameProblem.Organization);
        var p = string.IsNullOrWhiteSpace(project) ? null : project;
        if (p is not null && !IsProject(p)) problems.Add(FeedNameProblem.Project);
        if (!IsFeed(feed)) problems.Add(FeedNameProblem.Feed);
        return problems.Count > 0 ? (null, problems) : (new FeedNames(organization!, p, feed!), problems);
    }
}

/// <summary>
/// A feed as Azure DevOps identified it: the organisation (as typed; compared ignoring case everywhere) and the GUIDs Get Feed returned.
/// Every URL is built from these, never from the response's own <c>url</c>. The path prefix is tight: only this feed's own
/// <c>{org}/{projectId}/_packaging/{feedId}/</c> (every NuGet resource of the feed sits under it).
/// </summary>
public sealed record ResolvedFeed(string Organization, Guid? ProjectId, Guid FeedId)
{
    public const string PackagesBase = "https://pkgs.dev.azure.com/";

    /// <summary>The live service index writes the organisation in lower case; the URLs here do too (Azure DevOps names ignore case).</summary>
    private string Org => Organization.ToLowerInvariant();

    public string PathPrefix => ProjectId is { } p ? $"{Org}/{p:D}/_packaging/{FeedId:D}/" : $"{Org}/_packaging/{FeedId:D}/";
    public string FeedIndexUrl => PackagesBase + PathPrefix + "nuget/v3/index.json";
}

/// <summary>Why the typed names did not resolve to a feed Packmoat can use.</summary>
public enum FeedResolveProblem { NotFound, SignInRefused, SignInPage, Unreadable, BadIds, NameMismatch, ProjectMismatch, UrlRules }

/// <summary>Pure: checks what Get Feed returned against what was typed, and builds the GUID URLs.</summary>
public static class FeedResolution
{
    /// <summary>
    /// <list type="bullet">
    /// <item>The feed id must be a GUID, and the returned name must be the typed name (ignoring case, as Azure DevOps does): a typed GUID
    /// or a name Azure DevOps resolved to some other feed is refused.</item>
    /// <item>A project was typed: the response must name a project, with a GUID id and the typed name. None was typed: the feed must be
    /// organisation-scoped (no project in the response).</item>
    /// <item>The built URLs must pass the strict URL rules (https and a path prefix).</item>
    /// </list>
    /// </summary>
    public static (ResolvedFeed? Feed, FeedResolveProblem? Problem) Validate(FeedNames typed, string? id, string? name, bool hasProject, string? projectId, string? projectName)
    {
        if (!Guid.TryParseExact(id, "D", out var feedId) || feedId == Guid.Empty) return (null, FeedResolveProblem.BadIds);
        if (!string.Equals(name, typed.Feed, StringComparison.OrdinalIgnoreCase)) return (null, FeedResolveProblem.NameMismatch);
        Guid? project = null;
        if (typed.Project is null)
        {
            if (hasProject) return (null, FeedResolveProblem.ProjectMismatch);
        }
        else
        {
            if (!hasProject) return (null, FeedResolveProblem.ProjectMismatch);
            if (!Guid.TryParseExact(projectId, "D", out var p) || p == Guid.Empty) return (null, FeedResolveProblem.BadIds);
            if (!string.Equals(projectName, typed.Project, StringComparison.OrdinalIgnoreCase)) return (null, FeedResolveProblem.ProjectMismatch);
            project = p;
        }
        var feed = new ResolvedFeed(typed.Organization, project, feedId);
        if (TenantUrlRules.Check(ResolvedFeed.PackagesBase, feed.PathPrefix, feed.FeedIndexUrl, requireHttps: true, requirePrefix: true) is not null)
            return (null, FeedResolveProblem.UrlRules);
        return (feed, null);
    }
}

/// <summary>
/// Why the step-up sign-in that comes back to the check is not good enough. Nothing is checked or recorded. <see cref="NotDeveloper"/>:
/// the developer proof's sign-in is an Admin (or holds both roles), or the session is not a Developer. <see cref="SignInRefused"/>: the
/// developer proof's sign-in would not be let in at all (<see cref="CustomerSignIn.Decide"/> refused it).
/// </summary>
public enum StepUpRefusal { DifferentPerson, NotAdmin, NotFresh, NotDeveloper, SignInRefused }

/// <summary>A named administrator of the feed (not Packmoat and not a built-in admin group): what is shown, the name and the descriptor.</summary>
public sealed record NamedFeedAdmin(string Label, string Name, string Descriptor);

public static class AzureSetupRules
{
    /// <summary>
    /// The step-up sign-in's ID token against the current session: exactly one tid and one oid, the same directory and person as the
    /// session; the Packmoat.Admin role in the new token and an Admin session; and the new token's auth_time within
    /// <see cref="CustomerTokenRules.FreshSignIn"/> (unknown is never fresh). Null when all hold.
    /// </summary>
    public static StepUpRefusal? CheckStepUp(CustomerActor session, IReadOnlyList<string> tids, IReadOnlyList<string> oids, IReadOnlyList<string> roles,
        DateTimeOffset? authTime, DateTimeOffset now)
    {
        if (tids.Count != 1 || oids.Count != 1) return StepUpRefusal.DifferentPerson;
        var tid = CustomerSignIn.CanonicalDirectory(tids[0]);
        if (tid is null || tid != CustomerSignIn.CanonicalDirectory(session.DirectoryId)) return StepUpRefusal.DifferentPerson;
        if (!Guid.TryParse(oids[0], out var oid) || oid == Guid.Empty || oid != session.ObjectId) return StepUpRefusal.DifferentPerson;
        if (session.Role != CustomerRole.Admin || !roles.Contains(CustomerSignIn.AdminRole, StringComparer.Ordinal)) return StepUpRefusal.NotAdmin;
        if (!CustomerTokenRules.IsFresh(authTime, now)) return StepUpRefusal.NotFresh;
        return null;
    }

    /// <summary>
    /// The developer proof's step-up sign-in against the current session. <paramref name="decision"/> is
    /// <see cref="CustomerSignIn.Decide"/> on the step-up's own claims, with the same binding and the tenant's auto-join setting as at
    /// sign-in. Null when all hold: exactly one tid and one oid, the session's; the session is a Developer; the decision let the person in
    /// as THIS session's person and tenant, with the Developer role (an Admin, or both roles, is refused: Admins can often read the feed
    /// directly); and the step-up's auth_time is fresh (unknown is never fresh).
    /// </summary>
    public static StepUpRefusal? CheckDeveloperStepUp(CustomerActor session, IReadOnlyList<string> tids, IReadOnlyList<string> oids, SignInDecision decision,
        DateTimeOffset? authTime, DateTimeOffset now)
    {
        if (tids.Count != 1 || oids.Count != 1) return StepUpRefusal.DifferentPerson;
        var tid = CustomerSignIn.CanonicalDirectory(tids[0]);
        if (tid is null || tid != CustomerSignIn.CanonicalDirectory(session.DirectoryId)) return StepUpRefusal.DifferentPerson;
        if (!Guid.TryParse(oids[0], out var oid) || oid == Guid.Empty || oid != session.ObjectId) return StepUpRefusal.DifferentPerson;
        if (session.Role != CustomerRole.Developer) return StepUpRefusal.NotDeveloper;
        if (decision is not SignInDecision.Allowed allowed) return StepUpRefusal.SignInRefused;
        if (allowed.DirectoryId.ToString("D") != tid || allowed.ObjectId != session.ObjectId || !string.Equals(allowed.TenantId, session.TenantId, StringComparison.Ordinal))
            return StepUpRefusal.DifferentPerson;
        if (allowed.Role != CustomerRole.Developer) return StepUpRefusal.NotDeveloper;
        if (!CustomerTokenRules.IsFresh(authTime, now)) return StepUpRefusal.NotFresh;
        return null;
    }

    /// <summary>
    /// The checks the wizard runs: the feed's settings, and 'private' (Packmoat's own identity is refused on the other feeds the
    /// Admin can see). The developer proof and the proxy are separate steps.
    /// </summary>
    public static readonly string[] SettingsChecks = { "feed", "identity", "upstreams", "permissions", "views", "private" };

    /// <summary>The developer proof is offered only while the latest settings check passed at most this long ago).</summary>
    public static readonly TimeSpan DeveloperStepWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// Failed when anything failed. Passed only when every settings check (and 'project', when it ran) PASSED: a warning or a check that
    /// could not run is Incomplete, never good enough.
    /// </summary>
    public static FeedCheckOutcome Outcome(IReadOnlyList<CheckResult> results)
    {
        if (results.Any(r => r.Status == CheckStatus.Fail)) return FeedCheckOutcome.Failed;
        var required = SettingsChecks.Concat(results.Any(r => r.Id == "project") ? new[] { "project" } : Array.Empty<string>());
        return required.All(id => results.FirstOrDefault(r => r.Id == id)?.Status == CheckStatus.Pass) ? FeedCheckOutcome.Passed : FeedCheckOutcome.Incomplete;
    }

    /// <summary>The personal data kept in feed_checks (confirmed admins, the checker's display name, result details) is blanked after this long.</summary>
    public static readonly TimeSpan ConfirmedAdminsRetention = TimeSpan.FromDays(30);
    public const int MaxChecksPerWindow = 10;
    public static readonly TimeSpan RateWindow = TimeSpan.FromHours(1);
    /// <summary>The whole check, Azure DevOps calls included, must finish within this.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    /// <summary>
    /// How long a check's lease runs: the <see cref="Deadline"/> bounds only the Azure DevOps calls, and the lease is taken before them and
    /// ended after the result is blanked, recorded, withdrawn and kept, so it lasts a minute more than the deadline. Only a copy stopped
    /// mid-check leaves it to run out.
    /// </summary>
    public static readonly TimeSpan CheckLease = Deadline + TimeSpan.FromSeconds(60);
    /// <summary>A throttled Azure DevOps answer is retried at most once, waiting at most this long.</summary>
    public const int MaxRetries = 1;
    public static readonly TimeSpan RetryWaitCap = TimeSpan.FromSeconds(5);
    /// <summary>At most this many named administrators can be confirmed in one check (they travel in the sign-in state).</summary>
    public const int MaxConfirmableAdmins = 10;
}

/// <summary>What the check needs besides the token: the directory the shared app must come from, its client id and the confirmed admins (descriptors).</summary>
/// <para>TenantId: the Packmoat tenant, for the check's log lines only.</para>
public sealed record DelegatedCheckRequest(string DirectoryId, string SharedClientId, IReadOnlyList<string> ConfirmedAdmins, string? TenantId = null);

/// <summary>One run's results: the feed URL it was about (built from GUIDs; empty when the names did not resolve), the results and the named admins found.</summary>
public sealed record DelegatedCheckRun(string FeedIndexUrl, IReadOnlyList<CheckResult> Results, IReadOnlyList<NamedFeedAdmin> NamedAdmins);

/// <summary>
/// The feed setup check run with a person's own short-lived Azure DevOps token (the delegated sign-in check). The token is used only for
/// this call and sent only to Azure DevOps' API hosts; it is never stored, logged or put in a result.
/// </summary>
public interface IDelegatedFeedCheck
{
    /// <summary>Resolves the typed names with Get Feed, builds the GUID URLs and runs the settings checks on that feed.</summary>
    Task<DelegatedCheckRun> CheckNewFeedAsync(FeedNames names, DelegatedCheckRequest request, string accessToken, CancellationToken cancellationToken);
    /// <summary>Runs the settings checks on the tenant's stored feed URL, as it is (an operator-made shared tenant: check only).</summary>
    Task<DelegatedCheckRun> CheckStoredFeedAsync(Tenant tenant, DelegatedCheckRequest request, string accessToken, CancellationToken cancellationToken);
}

/// <summary>
/// What the wizard offers a tenant (<see cref="FeedSetupRules.Kind"/>). <see cref="Wizard"/>: a tenant whose customer sets up the feed
/// (self-made, or operator-made for the customer to connect) with no upstream or a shared binding in the person's own directory;
/// its checks are recorded in feed_checks. <see cref="CheckOnly"/>: an operator-made shared tenant (like e2e-shared) in the
/// person's directory: "Check again" runs on the stored feed and nothing is recorded. <see cref="ReadOnly"/>: everything else (own app,
/// operator-made without the shared app): a status card only.
/// </summary>
public enum AzureSetupKind { Wizard, CheckOnly, ReadOnly }

/// <summary>A check held for the person who ran it (in the shared scratch, 30 minutes), so the page can show it (and the named admins to confirm) after the redirect.</summary>
public sealed record HeldFeedCheck(DateTimeOffset At, FeedNames? Names, string FeedIndexUrl, FeedCheckOutcome Outcome, IReadOnlyList<CheckResult> Results,
    IReadOnlyList<NamedFeedAdmin> NamedAdmins, bool Recorded)
{
    /// <summary>A developer proof only: what the probe found, for the page's sentence.</summary>
    public DeveloperFinding? Finding { get; init; }
    /// <summary>This check withdrew the connected feed's verification (<see cref="ConnectRules.ShouldWithdraw"/>): Packmoat stopped serving it.</summary>
    public bool Withdrawn { get; init; }
    /// <summary>This check should have withdrawn the connected feed, but the withdrawal failed twice: the feed may still be served.</summary>
    public bool WithdrawFailed { get; init; }
}

/// <summary>How a re-check's withdrawal went.</summary>
public enum WithdrawOutcome { NotNeeded, Withdrawn, Failed }

/// <summary>The level of a line the setup commands log (the host maps it to its logger). A line never holds a token or a person's name.</summary>
public enum SetupLogLevel { Warning, Error }

/// <summary>The two checks the Connect button would connect (both qualify by <see cref="ConnectRules.Check"/> now).</summary>
public sealed record ConnectOffer(FeedCheck Settings, FeedCheck Developer);

/// <summary>What the setup page shows.</summary>
public sealed record AzureSetupView(Tenant Tenant, AzureSetupKind Kind, UpstreamBinding? Upstream, UpstreamReadiness Readiness, string? SharedClientId,
    FeedCheck? Latest, HeldFeedCheck? Held)
{
    /// <summary>The wizard can run a check: the shared app is configured and a check engine is available.</summary>
    public bool CanCheck { get; init; }
    /// <summary>Set when the latest settings check and the latest developer check qualify and the feed is not connected yet: show Connect.</summary>
    public ConnectOffer? Connect { get; init; }
    /// <summary>The latest settings check passed (within <see cref="AzureSetupRules.DeveloperStepWindow"/>), nothing is connected and there is no Connect yet: a developer's proof is next.</summary>
    public bool WaitingForDeveloper { get; init; }
    /// <summary>The tenant's auto-join setting, read for the waiting card (null: unknown).</summary>
    public bool? AutoJoinOn { get; init; }
    /// <summary>This person's step 1 as kept when they opened Microsoft's approval page (30 minutes), or null.</summary>
    public FeedNamesDraft? Draft { get; init; }
}

/// <summary>What the developer proof page shows: the tenant, the settings check it is about (its feed URL is the target) and this person's last proof.</summary>
public sealed record DeveloperSetupView(Tenant Tenant, FeedCheck Settings, HeldFeedCheck? Held);

/// <summary>What a check is asked to do, as carried through the sign-in: the typed names (wizard) and the admins being confirmed (descriptors).</summary>
public sealed record AzureSetupRequest(FeedNames? Names, IReadOnlyList<string> ConfirmedAdmins);

/// <summary><see cref="DevelopersOnly"/>: the developer proof, asked for by an Admin. <see cref="NotReady"/>: no settings check qualifies for a developer proof yet.</summary>
public enum AzureSetupRefusal { Disabled, NotAdmin, NotLinked, ReadOnly, NotAvailable, BadNames, TooManyRequests, CheckRunning, DevelopersOnly, NotReady }

public sealed class AzureSetupRefusedException : InvalidOperationException
{
    public AzureSetupRefusedException(AzureSetupRefusal reason) : base($"Refused: {reason}.") => Reason = reason;
    public AzureSetupRefusal Reason { get; }
}

/// <summary>
/// The wizard's state between pages, kept where every copy of the server sees it: the last check each person ran and their last developer
/// proof (30 minutes each), each person's step 1 draft (30 minutes, names only), all in <see cref="IFeedSetupScratch"/>; and one check
/// at a time per tenant, under the job lease <c>feed-check:{tenant}</c> (<see cref="LeaseJob"/>), which runs out by itself
/// <see cref="AzureSetupRules.CheckLease"/> after it was taken if a copy stops mid-check. A sign-in round trip that comes back to another
/// copy, or a restart, finds what the person left. Every method throws when the store cannot be reached: the commands decide what that
/// means, and never take it as a result.
/// </summary>
public sealed class AzureSetupState
{
    public static readonly TimeSpan HoldFor = TimeSpan.FromMinutes(30);
    private readonly IFeedSetupScratch _scratch;
    private readonly IJobLease _lease;
    private readonly IClock _clock;

    /// <param name="scratch">Where each person's last check, last developer proof and draft are kept (production: the database's).</param>
    /// <param name="lease">The job lease that lets one check run at a time per tenant (production: the database's).</param>
    public AzureSetupState(IFeedSetupScratch scratch, IJobLease lease, IClock clock)
    {
        _scratch = scratch ?? throw new ArgumentNullException(nameof(scratch));
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>The job lease a tenant's check runs under: the settings check and the developer proof share it.</summary>
    public static string LeaseJob(string tenantId) => "feed-check:" + tenantId;

    private static string KindOf(FeedCheckKind kind) => kind == FeedCheckKind.Developer ? FeedSetupScratchKinds.Developer : FeedSetupScratchKinds.Settings;

    /// <summary>Keeps (or replaces) this person's last check of <paramref name="kind"/> for this tenant, for <see cref="HoldFor"/>.</summary>
    public Task HoldAsync(string tenantId, Guid oid, HeldFeedCheck check, FeedCheckKind kind = FeedCheckKind.Settings, CancellationToken ct = default) =>
        _scratch.PutAsync(tenantId, oid, KindOf(kind), FeedSetupPayload.Write(check), HoldFor, ct);

    /// <summary>This person's last check of <paramref name="kind"/> for this tenant, or null when there is none or it is not between 0 and 30 minutes old.</summary>
    public async Task<HeldFeedCheck?> GetAsync(string tenantId, Guid oid, FeedCheckKind kind = FeedCheckKind.Settings, CancellationToken ct = default) =>
        FeedSetupPayload.ReadCheck(await _scratch.GetAsync(tenantId, oid, KindOf(kind), ct)) is { } c && Fresh(c.At) ? c : null;

    /// <summary>Forgets this person's last check of <paramref name="kind"/> for this tenant.</summary>
    public Task ForgetAsync(string tenantId, Guid oid, FeedCheckKind kind = FeedCheckKind.Settings, CancellationToken ct = default) =>
        _scratch.DeleteAsync(tenantId, oid, KindOf(kind), ct);

    /// <summary>Keeps (or replaces) this person's step 1 draft for this tenant.</summary>
    public Task KeepDraftAsync(string tenantId, Guid oid, FeedNamesDraft draft, CancellationToken ct = default) =>
        _scratch.PutAsync(tenantId, oid, FeedSetupScratchKinds.Draft, FeedSetupPayload.Write(draft), HoldFor, ct);

    /// <summary>Forgets this person's step 1 draft for this tenant.</summary>
    public Task ClearDraftAsync(string tenantId, Guid oid, CancellationToken ct = default) =>
        _scratch.DeleteAsync(tenantId, oid, FeedSetupScratchKinds.Draft, ct);

    /// <summary>This person's step 1 draft for this tenant, or null when there is none or it is not between 0 and 30 minutes old.</summary>
    public async Task<FeedNamesDraft?> GetDraftAsync(string tenantId, Guid oid, CancellationToken ct = default) =>
        FeedSetupPayload.ReadDraft(await _scratch.GetAsync(tenantId, oid, FeedSetupScratchKinds.Draft, ct)) is { } d && Fresh(d.At) ? d : null;

    /// <summary>The rule the in-memory state had: kept from its own time for less than <see cref="HoldFor"/>, and never from the future.</summary>
    private bool Fresh(DateTimeOffset at) => _clock.UtcNow - at is var age && age >= TimeSpan.Zero && age < HoldFor;

    /// <summary>
    /// Takes the tenant's check lease for a holder made for this one run, so a second check (on this copy or another) is refused while it
    /// runs. Returns that holder, to give to <see cref="EndAsync"/>, or null when another check holds the lease.
    /// </summary>
    public async Task<string?> TryBeginAsync(string tenantId, CancellationToken ct = default)
    {
        var holder = JobLeases.ProcessHolder + "-" + Guid.NewGuid().ToString("N");
        return await _lease.TryAcquireAsync(LeaseJob(tenantId), holder, AzureSetupRules.CheckLease, ct) ? holder : null;
    }

    /// <summary>Ends the run's lease (whatever the caller's token says: a check that has ended must free the tenant).</summary>
    public Task EndAsync(string tenantId, string holder) => _lease.ReleaseAsync(LeaseJob(tenantId), holder, CancellationToken.None);
}

/// <summary>
/// What the wizard writes into the scratch: a held check or a draft as JSON, enums by name. A payload that does not read back (a row
/// written by a different build) reads as nothing kept, as after a restart before.
/// </summary>
public static class FeedSetupPayload
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static string Write(HeldFeedCheck check) => System.Text.Json.JsonSerializer.Serialize(check, Options);
    public static string Write(FeedNamesDraft draft) => System.Text.Json.JsonSerializer.Serialize(draft, Options);

    public static HeldFeedCheck? ReadCheck(string? payload) =>
        Read<HeldFeedCheck>(payload) is { FeedIndexUrl: not null, Results: not null, NamedAdmins: not null } c
        && c.Results.All(r => r is { Id: not null, Title: not null, Detail: not null }) && c.NamedAdmins.All(a => a is { Label: not null, Name: not null, Descriptor: not null })
            ? c : null;
    public static FeedNamesDraft? ReadDraft(string? payload) => Read<FeedNamesDraft>(payload);

    private static T? Read<T>(string? payload) where T : class
    {
        if (payload is null) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(payload, Options); }
        catch (System.Text.Json.JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }
}

/// <summary>
/// The setup wizard's rules. The tenant comes only from the actor; its directory binding is read again on every call. Only an Admin of
/// a directory the switch allows gets anything. The checks of a tenant whose customer sets up the feed (self-made, or operator-made for
/// the customer to connect: <see cref="FeedSetupRules.CustomerSetsUpFeed"/>, read from the tenant store, never from a module) are recorded
/// in feed_checks (tenant URLs and verification records are written only by Connect, <see cref="ConnectFeedCommands"/>); any other
/// tenant's never are. A failed re-check of such a connected tenant may withdraw its verification (<see cref="ConnectRules.ShouldWithdraw"/>).
/// </summary>
public sealed class AzureSetupCommands
{
    private readonly ITenantStore _tenants;
    private readonly ISignInDirectoryStore _directories;
    private readonly ISharedAppCredentialStore? _shared;
    private readonly IFeedCheckStore? _checks;
    private readonly IDelegatedFeedCheck? _check;
    private readonly AzureSetupState _state;
    private readonly AzureSetupSettings _settings;
    private readonly IClock _clock;
    private readonly RateLimit _limiter;
    private readonly TimeSpan _deadline;
    private readonly ISelfServiceFeedStore? _feeds;
    private readonly Action<SetupLogLevel, string> _log;
    private readonly IDeveloperFeedProbe? _developerProbe;
    private readonly IAutoJoinSettingStore? _autoJoin;

    /// <param name="deadline">A test seam: production always uses <see cref="AzureSetupRules.Deadline"/>.</param>
    /// <param name="feeds">Where a failed re-check withdraws a connected feed. Without it nothing is ever withdrawn.</param>
    /// <param name="log">Where a failed withdrawal is reported (the host's logger).</param>
    /// <param name="developerProbe">The developer proof's probe. Without it the developer proof is not available.</param>
    /// <param name="autoJoin">The auto-join setting, read only for the Admin's waiting card.</param>
    /// <param name="rates">The shared rate counter: each person's checks and developer proofs are counted there.</param>
    public AzureSetupCommands(ITenantStore tenants, ISignInDirectoryStore directories, ISharedAppCredentialStore? shared,
        IFeedCheckStore? checks, IDelegatedFeedCheck? check, AzureSetupState state, AzureSetupSettings settings, IClock clock, IRateCounter rates, TimeSpan? deadline = null,
        ISelfServiceFeedStore? feeds = null, Action<SetupLogLevel, string>? log = null, IDeveloperFeedProbe? developerProbe = null,
        IAutoJoinSettingStore? autoJoin = null)
    {
        _developerProbe = developerProbe;
        _autoJoin = autoJoin;
        _feeds = feeds;
        _log = log ?? ((_, _) => { });
        _deadline = deadline ?? AzureSetupRules.Deadline;
        _tenants = tenants;
        _directories = directories;
        _shared = shared;
        _checks = checks;
        _check = check;
        _state = state;
        _settings = settings;
        _clock = clock;
        _limiter = new RateLimit(rates, RateScopes.FeedCheck, AzureSetupRules.MaxChecksPerWindow, AzureSetupRules.RateWindow);
    }

    public AzureSetupSettings Settings => _settings;

    private sealed record Context(string Tid, Tenant Tenant, AzureSetupKind Kind, UpstreamBinding? Upstream, string? SharedClientId);

    /// <summary>Switch, Admin, directory still bound to the actor's tenant, then what the tenant may do.</summary>
    private async Task<Context> GuardAsync(CustomerActor actor, CancellationToken ct)
    {
        if (!_settings.Allows(actor.DirectoryId)) throw new AzureSetupRefusedException(AzureSetupRefusal.Disabled);
        if (actor.Role != CustomerRole.Admin) throw new AzureSetupRefusedException(AzureSetupRefusal.NotAdmin);
        return await ContextAsync(actor, ct);
    }

    /// <summary>The directory still bound to the actor's tenant, then what the tenant may do (the caller has checked the switch and the role).</summary>
    private async Task<Context> ContextAsync(CustomerActor actor, CancellationToken ct)
    {
        var tid = CustomerSignIn.CanonicalDirectory(actor.DirectoryId)!;
        var binding = await _directories.GetAsync(tid, ct);
        if (binding is null || binding.AzureTenantId != tid || !string.Equals(binding.TenantId, actor.TenantId, StringComparison.Ordinal))
            throw new AzureSetupRefusedException(AzureSetupRefusal.NotLinked);
        var tenant = await _tenants.GetAsync(actor.TenantId, ct) ?? throw new AzureSetupRefusedException(AzureSetupRefusal.NotLinked);
        var customer = await CustomerSetsUpFeedAsync(tenant.Id, ct);
        var upstream = await _tenants.GetUpstreamBindingAsync(tenant.Id, ct);
        var sameDirectory = upstream is not null && CustomerSignIn.CanonicalDirectory(upstream.AzureTenantId) == tid;
        var kind = FeedSetupRules.Kind(tenant.Active, customer, upstream, sameDirectory);
        var client = _shared is null ? null : (await _shared.GetAsync(SharedAppSlot.Active, ct))?.ClientId;
        return new Context(tid, tenant, kind, upstream, client);
    }

    /// <summary>The rule, on the tenant row's origin and feed_setup. No row (a tenant gone meanwhile) is treated as operator-set: never written.</summary>
    private async Task<bool> CustomerSetsUpFeedAsync(string tenantId, CancellationToken ct) =>
        await _tenants.GetSetupAsync(tenantId, ct) is { } setup && FeedSetupRules.CustomerSetsUpFeed(setup.Origin, setup.FeedSetup);

    private bool CanCheck(Context c) => c.Kind != AzureSetupKind.ReadOnly && c.SharedClientId is not null && _check is not null
                                        && (c.Kind != AzureSetupKind.Wizard || _checks is not null);

    /// <summary>The setup page (Admins only).</summary>
    public async Task<AzureSetupView> ViewAsync(CustomerActor actor, CancellationToken ct)
    {
        var c = await GuardAsync(actor, ct);
        var verification = c.Upstream is { Kind: IdentityKind.Shared } && _shared is not null ? await _shared.GetVerificationAsync(c.Tenant.Id, ct) : null;
        var readiness = UpstreamReadiness.Of(c.Tenant, c.Upstream, verification, c.SharedClientId);
        var latest = c.Kind == AzureSetupKind.Wizard && _checks is not null ? await _checks.LatestAsync(c.Tenant.Id, FeedCheckKind.Settings, ct) : null;
        ConnectOffer? offer = null;
        if (latest is not null && readiness != UpstreamReadiness.Ready && c.SharedClientId is not null
            && await _checks!.LatestAsync(c.Tenant.Id, FeedCheckKind.Developer, ct) is { } developer
            && ConnectRules.Check(latest, developer, _clock.UtcNow) is null
            && Guid.TryParse(latest.ClientId, out var seen) && Guid.TryParse(c.SharedClientId, out var active) && seen == active)
            offer = new ConnectOffer(latest, developer);   // a hint only: Connect re-checks everything in one transaction
        var waiting = offer is null && readiness != UpstreamReadiness.Ready && latest is not null && QualifiesForDeveloper(c, latest);
        bool? autoJoin = null;
        if (waiting && _autoJoin is not null)
        {
            try { autoJoin = await _autoJoin.IsOnAsync(c.Tenant.Id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { autoJoin = null; }   // only the card's sentence depends on it
        }
        var held = await HeldOrNoneAsync(c.Tenant.Id, actor.ObjectId, FeedCheckKind.Settings, ct);
        return new AzureSetupView(c.Tenant, c.Kind, c.Upstream, readiness, c.SharedClientId, latest, held)
        {
            CanCheck = CanCheck(c), Connect = offer, WaitingForDeveloper = waiting, AutoJoinOn = autoJoin,
            Draft = c.Kind == AzureSetupKind.Wizard ? await DraftOrNoneAsync(c.Tenant.Id, actor.ObjectId, ct) : null,
        };
    }

    /// <summary>
    /// Step 2, "Open Microsoft's approval page": the setup page's rules (switch, Admin, directory still bound), a Wizard
    /// tenant with the shared app configured; then the typed names that pass their rules are kept for this person and tenant for 30
    /// minutes (when none does, any earlier draft is forgotten), so step 1 is still filled in when they come back. Returns the shared app's client id, for the
    /// approval page. Nothing is checked in Azure DevOps and nothing is written.
    /// </summary>
    public async Task<string> KeepDraftAsync(CustomerActor actor, string? organization, string? project, string? feed, CancellationToken ct)
    {
        var c = await GuardAsync(actor, ct);
        if (c.Kind != AzureSetupKind.Wizard) throw new AzureSetupRefusedException(AzureSetupRefusal.ReadOnly);
        if (c.SharedClientId is null) throw new AzureSetupRefusedException(AzureSetupRefusal.NotAvailable);
        try
        {
            if (FeedNamesDraft.From(_clock.UtcNow, organization, project, feed) is { } draft) await _state.KeepDraftAsync(c.Tenant.Id, actor.ObjectId, draft, ct);
            else await _state.ClearDraftAsync(c.Tenant.Id, actor.ObjectId, ct);   // nothing valid typed: an earlier draft is not brought back
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Step 1 is then empty when the person comes back, as after a restart before: they type the names again.
            ScratchFailed("keeping", FeedSetupScratchKinds.Draft, ex, "step 1 is not kept");
        }
        return c.SharedClientId;
    }

    /// <summary>
    /// A settings check a developer may prove against: a Wizard tenant's, passed, at most <see cref="AzureSetupRules.DeveloperStepWindow"/>
    /// old, about this directory and the active shared client, on a feed URL Connect could use.
    /// </summary>
    private bool QualifiesForDeveloper(Context c, FeedCheck latest) =>
        c.Kind == AzureSetupKind.Wizard
        && latest.Kind == FeedCheckKind.Settings
        && latest.Outcome == FeedCheckOutcome.Passed
        && ConnectRules.Within(latest.CheckedAt, AzureSetupRules.DeveloperStepWindow, _clock.UtcNow)
        && CustomerSignIn.CanonicalDirectory(latest.AzureTenantId) == c.Tid
        && Guid.TryParse(latest.ClientId, out var seen) && Guid.TryParse(c.SharedClientId, out var active) && seen == active
        && ConnectRules.UrlsFor(latest.FeedIndexUrl) is not null;

    // ---------------------------------------------------------------- the developer proof

    /// <summary>
    /// The developer proof's rules: the switch allows the directory; the person is a Developer (an Admin is refused: Admins can often read
    /// the feed directly); the directory is still bound; the tenant's customer sets up the feed (Wizard); a check engine is available; and the tenant's
    /// latest settings check qualifies (<see cref="QualifiesForDeveloper"/>). Returns the context and that settings check: its feed URL is
    /// the target, so the developer types nothing.
    /// </summary>
    private async Task<(Context C, FeedCheck Settings)> DeveloperGuardAsync(CustomerActor actor, CancellationToken ct)
    {
        if (!_settings.Allows(actor.DirectoryId)) throw new AzureSetupRefusedException(AzureSetupRefusal.Disabled);
        if (actor.Role != CustomerRole.Developer) throw new AzureSetupRefusedException(AzureSetupRefusal.DevelopersOnly);
        var c = await ContextAsync(actor, ct);
        if (c.Kind != AzureSetupKind.Wizard) throw new AzureSetupRefusedException(AzureSetupRefusal.ReadOnly);
        if (_checks is null || _developerProbe is null || c.SharedClientId is null) throw new AzureSetupRefusedException(AzureSetupRefusal.NotAvailable);
        var latest = await _checks.LatestAsync(c.Tenant.Id, FeedCheckKind.Settings, ct);
        if (latest is null || !QualifiesForDeveloper(c, latest)) throw new AzureSetupRefusedException(AzureSetupRefusal.NotReady);
        return (c, latest);
    }

    /// <summary>The developer proof page (Developers only).</summary>
    public async Task<DeveloperSetupView> DeveloperViewAsync(CustomerActor actor, CancellationToken ct)
    {
        var (c, settings) = await DeveloperGuardAsync(actor, ct);
        return new DeveloperSetupView(c.Tenant, settings, await HeldOrNoneAsync(c.Tenant.Id, actor.ObjectId, FeedCheckKind.Developer, ct));
    }

    /// <summary>Before the developer's step-up sign-in (POST "Sign in and check my access"): the rules, and at most 10 starts per person per hour.</summary>
    public async Task StartDeveloperAsync(CustomerActor actor, CancellationToken ct)
    {
        var (c, _) = await DeveloperGuardAsync(actor, ct);
        if (!await _limiter.TryAcquireAsync($"developer:{c.Tid}/{actor.ObjectId:D}", ct)) throw new AzureSetupRefusedException(AzureSetupRefusal.TooManyRequests);
    }

    /// <summary>
    /// After the developer's step-up sign-in (the caller has checked it with <see cref="AzureSetupRules.CheckDeveloperStepUp"/>): the rules
    /// again, one check at a time per tenant, then the probe with the developer's token on the latest settings check's feed URL, within
    /// <see cref="AzureSetupRules.Deadline"/>. The result is recorded as a <c>kind='developer'</c> row and held for the page. The token is
    /// used only here and is scrubbed from every detail.
    /// </summary>
    public async Task<HeldFeedCheck> RunDeveloperAsync(CustomerActor actor, string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(accessToken)) throw new AzureSetupRefusedException(AzureSetupRefusal.NotAvailable);
        var (c, settings) = await DeveloperGuardAsync(actor, ct);
        var lease = await BeginAsync(c.Tenant.Id, ct);
        try
        {
            await ForgetHeldAsync(c.Tenant.Id, actor.ObjectId, FeedCheckKind.Developer, ct);
            DeveloperProbeRun run;
            var timedOut = false;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(_deadline);
                try { run = await _developerProbe!.ProbeAsync(new DeveloperProbeRequest(c.Tid, settings.FeedIndexUrl), accessToken, deadline.Token); }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    run = new DeveloperProbeRun(new[] { new CheckResult(DeveloperProbeVerdict.Id, DeveloperProbeVerdict.Title, CheckStatus.Skip, OutOfTime), BypassAdvisory.NotChecked() },
                        DeveloperFinding.Unclear);
                }
                timedOut = deadline.IsCancellationRequested && !ct.IsCancellationRequested;
            }
            var results = run.Results.Select(r => Clean(r, accessToken, timedOut)).ToList();
            var outcome = DeveloperProbeVerdict.Outcome(results);
            var now = _clock.UtcNow;
            await _checks!.RecordAsync(new FeedCheck(c.Tenant.Id, now, c.Tid, actor.ObjectId.ToString("D"), actor.DisplayName, c.Tid, settings.FeedIndexUrl,
                c.SharedClientId!, outcome, results, Array.Empty<string>()) { Kind = FeedCheckKind.Developer }, ct);
            var finding = results.FirstOrDefault(r => r.Id == DeveloperProbeVerdict.Id)?.Status switch
            {
                CheckStatus.Pass => DeveloperFinding.Refused,
                CheckStatus.Fail => DeveloperFinding.CanRead,
                _ => run.Finding is DeveloperFinding.Refused or DeveloperFinding.CanRead ? DeveloperFinding.Unclear : run.Finding,
            };
            var held = new HeldFeedCheck(now, null, settings.FeedIndexUrl, outcome, results, Array.Empty<NamedFeedAdmin>(), Recorded: true) { Finding = finding };
            await HoldOrReportAsync(c.Tenant.Id, actor.ObjectId, held, FeedCheckKind.Developer);
            return held;
        }
        finally { await EndAsync(c.Tenant.Id, lease); }
    }

    /// <summary>
    /// The Developer's home card: true when the developer proof is open to this person (<see cref="DeveloperGuardAsync"/>), the feed is not
    /// connected yet, and there is no passing developer proof of that feed newer than the settings check. Never throws: any failure means no card.
    /// </summary>
    public async Task<bool> ShowDeveloperCardAsync(CustomerActor actor, CancellationToken ct)
    {
        try
        {
            if (actor.Role != CustomerRole.Developer || !_settings.Allows(actor.DirectoryId)) return false;
            var (c, settings) = await DeveloperGuardAsync(actor, ct);
            var verification = c.Upstream is { Kind: IdentityKind.Shared } && _shared is not null ? await _shared.GetVerificationAsync(c.Tenant.Id, ct) : null;
            if (UpstreamReadiness.Of(c.Tenant, c.Upstream, verification, c.SharedClientId) == UpstreamReadiness.Ready) return false;   // connected: nothing left to do
            var developer = await _checks!.LatestAsync(c.Tenant.Id, FeedCheckKind.Developer, ct);
            return !(developer is { Outcome: FeedCheckOutcome.Passed } && developer.FeedIndexUrl == settings.FeedIndexUrl && developer.CheckedAt >= settings.CheckedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    /// <summary>
    /// The home card: true when the switch allows the directory and the tenant's customer sets up its feed (<see cref="FeedSetupRules.CustomerSetsUpFeed"/>)
    /// with no upstream set up yet (for Admins and Developers alike; only Admins get the button). Never throws: any failure means no card.
    /// </summary>
    public async Task<bool> ShowConnectCardAsync(CustomerActor actor, CancellationToken ct)
    {
        try
        {
            if (!_settings.Allows(actor.DirectoryId)) return false;
            var tenant = await _tenants.GetAsync(actor.TenantId, ct);
            if (tenant is null || !tenant.Active || !await CustomerSetsUpFeedAsync(tenant.Id, ct)) return false;
            var upstream = await _tenants.GetUpstreamBindingAsync(tenant.Id, ct);
            return UpstreamReadiness.Of(tenant, upstream, null, null) == UpstreamReadiness.NotSetUp;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    /// <summary>
    /// Before the step-up sign-in (POST "Sign in and check"): the rules above, a tenant that can be checked, valid names for the wizard,
    /// and at most <see cref="AzureSetupRules.MaxChecksPerWindow"/> starts per person per hour. Returns the request to carry through the
    /// sign-in: the names (wizard only) and, when <paramref name="confirmAdmins"/>, the named admins of this person's last check.
    /// </summary>
    public async Task<AzureSetupRequest> StartAsync(CustomerActor actor, string? organization, string? project, string? feed, bool confirmAdmins, CancellationToken ct)
    {
        var c = await GuardAsync(actor, ct);
        if (c.Kind == AzureSetupKind.ReadOnly) throw new AzureSetupRefusedException(AzureSetupRefusal.ReadOnly);
        if (!CanCheck(c)) throw new AzureSetupRefusedException(AzureSetupRefusal.NotAvailable);
        FeedNames? names = null;
        if (c.Kind == AzureSetupKind.Wizard)
        {
            var (valid, _) = AzureFeedNames.Validate(organization, project, feed);
            names = valid ?? throw new AzureSetupRefusedException(AzureSetupRefusal.BadNames);
        }
        var confirmed = Array.Empty<string>();
        if (confirmAdmins && await HeldForConfirmingAsync(c.Tenant.Id, actor.ObjectId, ct) is { } held && held.NamedAdmins.Count is > 0 and <= AzureSetupRules.MaxConfirmableAdmins)
            confirmed = held.NamedAdmins.Select(a => a.Descriptor).ToArray();
        if (!await _limiter.TryAcquireAsync($"{c.Tid}/{actor.ObjectId:D}", ct)) throw new AzureSetupRefusedException(AzureSetupRefusal.TooManyRequests);
        return new AzureSetupRequest(names, confirmed);
    }

    /// <summary>
    /// After the step-up sign-in (the caller has checked it with <see cref="AzureSetupRules.CheckStepUp"/>): the rules again, one check at
    /// a time per tenant, then the check with the person's token, within <see cref="AzureSetupRules.Deadline"/>. A self-made tenant's
    /// result is recorded in feed_checks (and personal data older than 30 days is blanked first); an operator-made tenant's is only held
    /// for the page. The token is used only here and is scrubbed from every detail, whatever the check engine wrote.
    /// </summary>
    public async Task<HeldFeedCheck> RunAsync(CustomerActor actor, AzureSetupRequest request, string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(accessToken)) throw new AzureSetupRefusedException(AzureSetupRefusal.NotAvailable);
        var c = await GuardAsync(actor, ct);
        if (c.Kind == AzureSetupKind.ReadOnly) throw new AzureSetupRefusedException(AzureSetupRefusal.ReadOnly);
        if (!CanCheck(c)) throw new AzureSetupRefusedException(AzureSetupRefusal.NotAvailable);
        FeedNames? names = null;
        if (c.Kind == AzureSetupKind.Wizard)
        {
            // Carried in the protected sign-in state, so only this site could have set them; checked again all the same.
            names = request.Names is { } n ? AzureFeedNames.Validate(n.Organization, n.Project, n.Feed).Names : null;
            if (names is null) throw new AzureSetupRefusedException(AzureSetupRefusal.BadNames);
        }
        var confirmed = request.ConfirmedAdmins.Where(d => !string.IsNullOrWhiteSpace(d) && d.Length <= 512).Distinct(StringComparer.Ordinal)
            .Take(AzureSetupRules.MaxConfirmableAdmins).ToList();
        var lease = await BeginAsync(c.Tenant.Id, ct);
        try
        {
            await ForgetHeldAsync(c.Tenant.Id, actor.ObjectId, FeedCheckKind.Settings, ct);
            var checkRequest = new DelegatedCheckRequest(c.Tid, c.SharedClientId!, confirmed, c.Tenant.Id);
            DelegatedCheckRun run;
            var timedOut = false;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(_deadline);
                try
                {
                    run = names is not null
                        ? await _check!.CheckNewFeedAsync(names, checkRequest, accessToken, deadline.Token)
                        : await _check!.CheckStoredFeedAsync(c.Tenant, checkRequest, accessToken, deadline.Token);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    run = new DelegatedCheckRun("", new[] { new CheckResult("feed", "The check finished in time", CheckStatus.Skip, OutOfTime) }, Array.Empty<NamedFeedAdmin>());
                }
                timedOut = deadline.IsCancellationRequested && !ct.IsCancellationRequested;
            }
            var results = run.Results.Select(r => Clean(r, accessToken, timedOut)).ToList();
            var admins = run.NamedAdmins.Select(a => new NamedFeedAdmin(Scrub(a.Label, accessToken), Scrub(a.Name, accessToken), a.Descriptor))
                .Where(a => !a.Descriptor.Contains(accessToken, StringComparison.Ordinal)).ToList();
            var outcome = AzureSetupRules.Outcome(results);
            var feedUrl = Scrub(run.FeedIndexUrl, accessToken);
            var now = _clock.UtcNow;
            var recorded = false;
            var withdrawn = WithdrawOutcome.NotNeeded;
            if (c.Kind == AzureSetupKind.Wizard)
            {
                var confirmedNames = admins.Where(a => confirmed.Contains(a.Descriptor, StringComparer.Ordinal)).Select(a => a.Name.Length > 0 ? a.Name : a.Label).ToList();
                // Best effort: a blanking failure must never stop the check being recorded, nor a failed check withdrawing the feed. The
                // start-up and daily passes try again.
                try { await _checks!.BlankPersonalDataAsync(now - AzureSetupRules.ConfirmedAdminsRetention, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log(SetupLogLevel.Warning, $"Blanking feed checks' personal data failed ({ex.GetType().Name}); recording the check anyway");
                }
                var checkId = await _checks!.RecordAsync(new FeedCheck(c.Tenant.Id, now, c.Tid, actor.ObjectId.ToString("D"), actor.DisplayName, c.Tid, feedUrl,
                    c.SharedClientId!, outcome, results, confirmedNames) { Names = names }, ct);
                recorded = true;
                // Not the request's token: once a failed check is recorded, a browser that goes away must not skip the withdrawal.
                withdrawn = await WithdrawIfFailedAsync(c, actor, checkId, outcome, feedUrl, results, now, CancellationToken.None);
            }
            var held = new HeldFeedCheck(now, names, feedUrl, outcome, results, admins, recorded)
            { Withdrawn = withdrawn == WithdrawOutcome.Withdrawn, WithdrawFailed = withdrawn == WithdrawOutcome.Failed };
            await HoldOrReportAsync(c.Tenant.Id, actor.ObjectId, held, FeedCheckKind.Settings);
            return held;
        }
        finally { await EndAsync(c.Tenant.Id, lease); }
    }

    // ---------------------------------------------------------------- the shared state, failing closed

    /// <summary>
    /// Takes the tenant's check lease, or refuses: <see cref="AzureSetupRefusal.CheckRunning"/> while another check (on any copy) holds it;
    /// <see cref="AzureSetupRefusal.NotAvailable"/> when the lease cannot be read (logged), as nothing then says no other check runs.
    /// </summary>
    private async Task<string> BeginAsync(string tenantId, CancellationToken ct)
    {
        string? holder;
        try { holder = await _state.TryBeginAsync(tenantId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log(SetupLogLevel.Warning, $"Feed setup: taking the check lease failed ({ex.GetType().Name}); the check is refused");
            throw new AzureSetupRefusedException(AzureSetupRefusal.NotAvailable);
        }
        return holder ?? throw new AzureSetupRefusedException(AzureSetupRefusal.CheckRunning);
    }

    /// <summary>Ends the run's lease. A failure is logged and changes nothing else: the lease then runs out by itself within <see cref="AzureSetupRules.CheckLease"/>.</summary>
    private async Task EndAsync(string tenantId, string holder)
    {
        try { await _state.EndAsync(tenantId, holder); }
        catch (Exception ex)
        {
            _log(SetupLogLevel.Warning, $"Feed setup: ending the check lease failed ({ex.GetType().Name}); it runs out by itself within two minutes");
        }
    }

    /// <summary>
    /// Before a check runs: forgets the person's last result of this kind, so a result kept by an earlier run can never stand in for this
    /// one if keeping this one fails (its named admins could otherwise be confirmed). When that fails, the check is refused (logged).
    /// </summary>
    private async Task ForgetHeldAsync(string tenantId, Guid oid, FeedCheckKind kind, CancellationToken ct)
    {
        try { await _state.ForgetAsync(tenantId, oid, kind, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ScratchFailed("clearing", kind == FeedCheckKind.Developer ? FeedSetupScratchKinds.Developer : FeedSetupScratchKinds.Settings, ex, "the check is refused");
            throw new AzureSetupRefusedException(AzureSetupRefusal.NotAvailable);
        }
    }

    /// <summary>
    /// Keeps the result for the person's page. A failure is logged and changes nothing else: the check is recorded (or not) as it was, the
    /// page shows the recorded result, and nothing is confirmed from it (the earlier result was forgotten before the check ran).
    /// </summary>
    private async Task HoldOrReportAsync(string tenantId, Guid oid, HeldFeedCheck held, FeedCheckKind kind)
    {
        // Not the request's token: once the check has run, a browser that goes away must not lose its result.
        try { await _state.HoldAsync(tenantId, oid, held, kind, CancellationToken.None); }
        catch (Exception ex)
        {
            ScratchFailed("keeping", kind == FeedCheckKind.Developer ? FeedSetupScratchKinds.Developer : FeedSetupScratchKinds.Settings, ex,
                "the page shows the recorded result");
        }
    }

    /// <summary>The person's last check, or null when there is none or the store failed (logged: the page then shows the recorded result, as after a restart before).</summary>
    private async Task<HeldFeedCheck?> HeldOrNoneAsync(string tenantId, Guid oid, FeedCheckKind kind, CancellationToken ct)
    {
        try { return await _state.GetAsync(tenantId, oid, kind, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ScratchFailed("reading", kind == FeedCheckKind.Developer ? FeedSetupScratchKinds.Developer : FeedSetupScratchKinds.Settings, ex,
                "the page shows the recorded result");
            return null;
        }
    }

    /// <summary>The person's step 1 draft, or null when there is none or the store failed (logged: step 1 is then empty).</summary>
    private async Task<FeedNamesDraft?> DraftOrNoneAsync(string tenantId, Guid oid, CancellationToken ct)
    {
        try { return await _state.GetDraftAsync(tenantId, oid, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ScratchFailed("reading", FeedSetupScratchKinds.Draft, ex, "step 1 is empty");
            return null;
        }
    }

    /// <summary>
    /// The last check whose named admins the person is confirming. When the store fails, the start is refused (logged): the person asked to
    /// confirm, and a check run without them would confirm nobody while looking like it did.
    /// </summary>
    private async Task<HeldFeedCheck?> HeldForConfirmingAsync(string tenantId, Guid oid, CancellationToken ct)
    {
        try { return await _state.GetAsync(tenantId, oid, FeedCheckKind.Settings, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ScratchFailed("reading", FeedSetupScratchKinds.Settings, ex, "no admin is confirmed and the check is refused");
            throw new AzureSetupRefusedException(AzureSetupRefusal.NotAvailable);
        }
    }

    /// <summary>One warning: what failed, the kind and the error's type. Never the tenant, the person, or anything kept.</summary>
    private void ScratchFailed(string action, string kind, Exception error, string consequence) =>
        _log(SetupLogLevel.Warning, $"Feed setup: {action} the {kind} scratch failed ({error.GetType().Name}); {consequence}");

    /// <summary>
    /// A tenant whose customer sets up the feed (Wizard kind included): when the check just recorded meets <see cref="ConnectRules.ShouldWithdraw"/> against the
    /// verification as it is NOW (read after recording, not the binding read when the check started, so a Connect made while the check ran
    /// is withdrawn too), that exact verification is deleted and <c>feed.withdrawn</c> written, in one transaction. The proxy then fails
    /// closed for the tenant once its caches expire. A store error is logged and the whole step tried once more (reading the verification
    /// again); if that fails too, an Error is logged and the page says the feed may still be served.
    /// </summary>
    private async Task<WithdrawOutcome> WithdrawIfFailedAsync(Context c, CustomerActor actor, long checkId, FeedCheckOutcome outcome, string feedUrl,
        IReadOnlyList<CheckResult> results, DateTimeOffset now, CancellationToken ct)
    {
        if (_feeds is null || _shared is null || c.Kind != AzureSetupKind.Wizard) return WithdrawOutcome.NotNeeded;
        var failed = string.Join(",", results.Where(ConnectRules.IsDefiniteFail).Select(r => r.Id));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var connected = await _shared.GetVerificationAsync(c.Tenant.Id, ct);
                if (!ConnectRules.ShouldWithdraw(outcome, feedUrl, results, connected)) return WithdrawOutcome.NotNeeded;
                var action = new AdminActionRecord(now, c.Tid, actor.ObjectId.ToString("D"), actor.DisplayName, ConnectRules.WithdrawnAction, c.Tenant.Id,
                    $"feed={connected!.FeedIndexUrl}; settings_check={checkId}; failed={failed}", CustomerTokenCommands.Site);
                return await _feeds.WithdrawAsync(connected, action, ct) ? WithdrawOutcome.Withdrawn : WithdrawOutcome.NotNeeded;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= 2)
                {
                    _log(SetupLogLevel.Error, $"Withdrawing the feed of tenant {c.Tenant.Id} failed twice ({ex.GetType().Name}) after check {checkId} failed; it may still be served");
                    return WithdrawOutcome.Failed;
                }
                _log(SetupLogLevel.Warning, $"Withdrawing the feed of tenant {c.Tenant.Id} failed ({ex.GetType().Name}); trying once more");
            }
        }
    }

    public const string OutOfTime = "NOT VERIFIED: the check ran out of time. Check again in a few minutes.";
    /// <summary>One Azure DevOps call timed out (its own HttpClient timeout, inside the deadline): slowness proves nothing, so not verified.</summary>
    public const string NoAnswerInTime = "NOT VERIFIED: Azure DevOps didn't answer in time. Check again in a few minutes.";

    private static string Scrub(string text, string token) => text.Replace(token, "[redacted]", StringComparison.Ordinal);

    /// <summary>
    /// The token scrubbed out. A failure that was only a cancellation (the whole check's deadline, or one call's own timeout) reads as
    /// not verified, never as failed: a timeout says nothing about the feed's settings.
    /// </summary>
    private static CheckResult Clean(CheckResult r, string token, bool timedOut)
    {
        var cleaned = r with { Title = Scrub(r.Title, token), Detail = Scrub(r.Detail, token) };
        if (cleaned.Status == CheckStatus.Fail
            && (cleaned.Detail.StartsWith("could not complete: TaskCanceledException", StringComparison.Ordinal)
                || cleaned.Detail.StartsWith("could not complete: OperationCanceledException", StringComparison.Ordinal)))
            return cleaned with { Status = CheckStatus.Skip, Detail = timedOut ? OutOfTime : NoAnswerInTime };
        return cleaned;
    }
}
