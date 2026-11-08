using System.Text.RegularExpressions;
using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>
/// How a Connect ended. <see cref="Connected"/> is the only outcome that wrote anything; every other value is a refusal, and nothing was
/// written for it.
/// </summary>
public enum ConnectResult
{
    Connected,
    /// <summary>The switch does not allow the person's directory.</summary>
    Disabled,
    NotAdmin,
    /// <summary>The session's sign-in is older than <see cref="CustomerTokenRules.FreshSignIn"/> (or unknown).</summary>
    FreshSignInRequired,
    /// <summary>The person's directory is no longer linked to their tenant.</summary>
    NotLinked,
    /// <summary>The tenant's feed is not its customer's to set up (operator-set, #207), or it is suspended or gone: only an active tenant
    /// that is self-made or made for its customer to connect connects itself. (The name is kept from before #207.)</summary>
    NotSelfMade,
    /// <summary>The tenant uses its own app registration: Connect never overwrites one.</summary>
    OwnApp,
    /// <summary>Another tenant already uses the shared app in this directory (or this tenant is bound to it in another directory).</summary>
    DirectoryTaken,
    /// <summary>The active shared client id is not the one the page showed (or there is none).</summary>
    ClientChanged,
    /// <summary>The feed the page showed is not the one the settings check was about.</summary>
    FeedChanged,
    /// <summary>A check id names no row of this tenant of the right kind.</summary>
    CheckNotFound,
    /// <summary>A newer settings check exists, or a newer developer check of the same feed: only the latest of each can be connected.</summary>
    NotLatest,
    /// <summary>The two checks are not about the same directory, feed and shared client id (or not this directory's).</summary>
    ChecksDisagree,
    /// <summary>A required result is not PASS (or the outcome is not passed).</summary>
    NotPassed,
    SettingsTooOld,
    DeveloperTooOld,
    /// <summary>The developer proof was made by the person who checked the settings.</summary>
    SamePerson,
    /// <summary>The checked feed URL is not one the wizard could have built.</summary>
    BadFeedUrl,
}

/// <summary>
/// Pure: whether a settings check and a developer check together allow a self-made tenant to connect its feed (#64). The windows are
/// named constants that may change.
/// </summary>
public static class ConnectRules
{
    /// <summary>The settings check must be at most this old when Connect is pressed.</summary>
    public static readonly TimeSpan SettingsWindow = TimeSpan.FromHours(24);
    /// <summary>The developer proof must be at most this old when Connect is pressed.</summary>
    public static readonly TimeSpan DeveloperWindow = TimeSpan.FromDays(7);
    /// <summary>A check dated this far in the future still counts (clock skew); anything later never does.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    /// <summary>The settings results that must all PASS. 'project' must PASS too whenever it ran (a feed in a project).</summary>
    public static readonly string[] RequiredSettings = { "feed", "identity", "upstreams", "permissions", "views", "private" };
    public const string ProjectCheck = "project";
    /// <summary>The developer row's one required result.</summary>
    public const string DeveloperCheck = "developer";

    public const string ConnectAction = "feed.connect";
    public const string WithdrawnAction = "feed.withdrawn";

    /// <summary>
    /// Null when the pair qualifies at <paramref name="now"/>:
    /// <list type="bullet">
    /// <item>the right kinds; both about the same directory (the settings checker's own), feed URL and shared client id, and the developer
    /// signed in from that directory;</item>
    /// <item>both outcomes passed and nothing FAILED; settings: every <see cref="RequiredSettings"/> (and 'project' when present) PASS;
    /// developer: 'developer' PASS;</item>
    /// <item>the settings check at most <see cref="SettingsWindow"/> old and the developer check at most <see cref="DeveloperWindow"/>
    /// old (both edges included; never more than <see cref="ClockSkew"/> in the future);</item>
    /// <item>the developer's object id differs from the settings checker's;</item>
    /// <item>the feed URL has the shape the wizard builds from GUIDs.</item>
    /// </list>
    /// It does not know which tenant the rows belong to or whether the settings row is the latest: the store checks those.
    /// </summary>
    public static ConnectResult? Check(FeedCheck settings, FeedCheck developer, DateTimeOffset now)
    {
        if (settings.Kind != FeedCheckKind.Settings || developer.Kind != FeedCheckKind.Developer) return ConnectResult.CheckNotFound;
        if (!SameSubject(settings, developer)) return ConnectResult.ChecksDisagree;
        if (!SettingsPassed(settings) || !DeveloperPassed(developer)) return ConnectResult.NotPassed;
        if (!Within(settings.CheckedAt, SettingsWindow, now)) return ConnectResult.SettingsTooOld;
        if (!Within(developer.CheckedAt, DeveloperWindow, now)) return ConnectResult.DeveloperTooOld;
        if (!DifferentPeople(settings.CheckedByOid, developer.CheckedByOid)) return ConnectResult.SamePerson;
        if (UrlsFor(settings.FeedIndexUrl) is null) return ConnectResult.BadFeedUrl;
        return null;
    }

    /// <summary>True when <paramref name="at"/> is no more than <paramref name="window"/> before <paramref name="now"/> (the edge included).</summary>
    public static bool Within(DateTimeOffset at, TimeSpan window, DateTimeOffset now) => at <= now + ClockSkew && now - at <= window;

    private static bool SameSubject(FeedCheck s, FeedCheck d)
    {
        var dir = CustomerSignIn.CanonicalDirectory(s.AzureTenantId);
        return dir is not null
               && CustomerSignIn.CanonicalDirectory(s.CheckedByTid) == dir
               && CustomerSignIn.CanonicalDirectory(d.AzureTenantId) == dir
               && CustomerSignIn.CanonicalDirectory(d.CheckedByTid) == dir
               && string.Equals(s.FeedIndexUrl, d.FeedIndexUrl, StringComparison.Ordinal)
               && Guid.TryParse(s.ClientId, out var a) && Guid.TryParse(d.ClientId, out var b) && a == b && a != Guid.Empty;
    }

    private static bool Passes(FeedCheck c, IEnumerable<string> required) =>
        c.Outcome == FeedCheckOutcome.Passed
        && c.Results.All(r => r.Status != CheckStatus.Fail)
        && required.All(id => c.Results.FirstOrDefault(r => r.Id == id)?.Status == CheckStatus.Pass);

    private static bool SettingsPassed(FeedCheck s) =>
        Passes(s, RequiredSettings.Concat(s.Results.Any(r => r.Id == ProjectCheck) ? new[] { ProjectCheck } : Array.Empty<string>()));

    private static bool DeveloperPassed(FeedCheck d) => Passes(d, new[] { DeveloperCheck });

    /// <summary>Both must be real object ids (an unreadable one fails closed) and differ.</summary>
    private static bool DifferentPeople(string a, string b) =>
        Guid.TryParse(a, out var x) && Guid.TryParse(b, out var y) && x != Guid.Empty && y != Guid.Empty && x != y;

    private const string IndexSuffix = "nuget/v3/index.json";

    // The tight prefix the wizard builds: {org}/{projectId}/_packaging/{feedId}/ or {org}/_packaging/{feedId}/, lower case, GUIDs in "D" form.
    private static readonly Regex TightPrefix = new(
        @"\A[a-z0-9]([a-z0-9-]{0,48}[a-z0-9])?/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/)?_packaging/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The tenant URLs for a checked feed URL: the upstream base and the tight path prefix, or null unless the URL is exactly
    /// <c>https://pkgs.dev.azure.com/{tight prefix}nuget/v3/index.json</c> and passes the strict URL rules.
    /// </summary>
    public static (string UpstreamBase, string PathPrefix)? UrlsFor(string? feedIndexUrl)
    {
        if (feedIndexUrl is null || !feedIndexUrl.StartsWith(ResolvedFeed.PackagesBase, StringComparison.Ordinal) || !feedIndexUrl.EndsWith(IndexSuffix, StringComparison.Ordinal)) return null;
        var prefix = feedIndexUrl[ResolvedFeed.PackagesBase.Length..^IndexSuffix.Length];
        if (!TightPrefix.IsMatch(prefix)) return null;
        if (TenantUrlRules.Check(ResolvedFeed.PackagesBase, prefix, feedIndexUrl, requireHttps: true, requirePrefix: true) is not null) return null;
        return (ResolvedFeed.PackagesBase, prefix);
    }

    /// <summary>
    /// The withdraw rule (the plan's proposed default, #64): a re-check withdraws the connected feed's verification only when its outcome
    /// is Failed, it was about exactly the connected feed URL, and at least one result is a definite FAIL. A result that "could not
    /// complete" (a call that errored) is not verified, never a definite FAIL; Incomplete and Passed change nothing.
    /// </summary>
    public static bool ShouldWithdraw(FeedCheckOutcome outcome, string checkedFeedUrl, IReadOnlyList<CheckResult> results, SharedAppVerification? connected) =>
        connected is not null
        && outcome == FeedCheckOutcome.Failed
        && string.Equals(checkedFeedUrl, connected.FeedIndexUrl, StringComparison.Ordinal)
        && results.Any(IsDefiniteFail);

    /// <summary>A FAIL the check actually observed, not one standing for an error ("could not complete: ...").</summary>
    public static bool IsDefiniteFail(CheckResult r) => r.Status == CheckStatus.Fail && !r.Detail.StartsWith(CouldNotComplete, StringComparison.Ordinal);

    /// <summary>How the check engine words a check that errored (OnboardingChecks.Guard).</summary>
    public const string CouldNotComplete = "could not complete";
}

/// <summary>
/// What Connect asks the store to write, all re-checked inside its transaction. The tenant comes only from the actor and the directory
/// only from the session's tid. <c>Action</c> is the <c>feed.connect</c> row, written in the same transaction.
/// </summary>
public sealed record ConnectFeedRequest(string TenantId, string DirectoryId, long SettingsCheckId, long DeveloperCheckId, string SeenFeedUrl,
    string SeenClientId, DateTimeOffset At, string VerifiedBy, AdminActionRecord Action);

/// <summary>The self-service feed writes (#64): connect a checked feed, and withdraw it after a failed re-check.</summary>
public interface ISelfServiceFeedStore
{
    /// <summary>
    /// All or nothing, and no other writer can change what it reads between the check and the write. Refuses unless, each re-read inside that one atomic step: the tenant is active and its customer sets up the feed (<see cref="FeedSetupRules.CustomerSetsUpFeed"/>); the directory
    /// is still linked to it; the active shared client id is the one seen; the tenant has no own-app identity (never overwritten) and is
    /// not bound in another directory; no other tenant uses the shared app in this directory; both feed_checks rows belong to THIS tenant,
    /// with the right kinds; the settings row is the tenant's latest settings check and the developer row its latest developer check of
    /// that feed URL; the settings row's feed URL is the one seen; and
    /// <see cref="ConnectRules.Check"/> passes. Then writes the tenant URLs (bumping the policy revision), the shared binding, the
    /// verification (<c>verified_via='wizard'</c>, both check ids) and <c>request.Action</c>. Nothing is written on any refusal.
    /// </summary>
    Task<ConnectResult> ConnectSelfServiceFeedAsync(ConnectFeedRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction: deletes the tenant's verification only if it is exactly <paramref name="verification"/> (same values and time)
    /// and was made by the wizard (<c>verified_via='wizard'</c>: an operator's record is never withdrawn, #251), and, only then, writes
    /// <paramref name="action"/>. False (nothing written) when the record is not that one any more.
    /// </summary>
    Task<bool> WithdrawAsync(SharedAppVerification verification, AdminActionRecord action, CancellationToken cancellationToken);
}

/// <summary>
/// Connect (#64 I1): a self-made tenant's Admin makes Packmoat serve the feed both checks were about. The checks here are the person's
/// (switch, Admin, a fresh sign-in); everything about the tenant and the rows is re-checked by the store in one transaction.
/// Connecting needs nothing from the serving gate: tokens still need it to serve the tenant (<see cref="CustomerTokenGuard"/>).
/// </summary>
public sealed class ConnectFeedCommands
{
    private readonly ISelfServiceFeedStore _store;
    private readonly AzureSetupSettings _settings;
    private readonly IClock _clock;

    public ConnectFeedCommands(ISelfServiceFeedStore store, AzureSetupSettings settings, IClock clock)
    {
        _store = store;
        _settings = settings;
        _clock = clock;
    }

    public async Task<ConnectResult> ConnectAsync(CustomerActor actor, long settingsCheckId, long developerCheckId, string? seenFeedUrl, string? seenClientId,
        CancellationToken ct)
    {
        if (!_settings.Allows(actor.DirectoryId)) return ConnectResult.Disabled;
        if (actor.Role != CustomerRole.Admin) return ConnectResult.NotAdmin;
        var now = _clock.UtcNow;
        if (!CustomerTokenRules.IsFresh(actor.SignedInAt, now)) return ConnectResult.FreshSignInRequired;
        if (string.IsNullOrWhiteSpace(actor.TenantId) || actor.ObjectId == Guid.Empty) return ConnectResult.NotLinked;
        if (string.IsNullOrEmpty(seenFeedUrl) || seenFeedUrl.Length > 512) return ConnectResult.FeedChanged;
        if (!Guid.TryParse(seenClientId, out _)) return ConnectResult.ClientChanged;
        if (settingsCheckId <= 0 || developerCheckId <= 0) return ConnectResult.CheckNotFound;
        var tid = CustomerSignIn.CanonicalDirectory(actor.DirectoryId)!;
        var oid = actor.ObjectId.ToString("D");
        var action = new AdminActionRecord(now, tid, oid, actor.DisplayName, ConnectRules.ConnectAction, actor.TenantId,
            $"feed={seenFeedUrl}; settings_check={settingsCheckId}; developer_check={developerCheckId}", CustomerTokenCommands.Site);
        return await _store.ConnectSelfServiceFeedAsync(new ConnectFeedRequest(actor.TenantId, tid, settingsCheckId, developerCheckId, seenFeedUrl,
            seenClientId!, now, $"wizard: {actor.DisplayName} ({oid})", action), ct);
    }
}
