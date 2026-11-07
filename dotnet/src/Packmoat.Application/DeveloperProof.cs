using System.Globalization;
using System.Text.RegularExpressions;

namespace Packmoat.Application;

/// <summary>What the developer probe found, for the page's sentence. The recorded result keeps the status and detail only.</summary>
public enum DeveloperFinding
{
    /// <summary>The developer was refused on the feed (401 or 403) and both controls passed: PASS.</summary>
    Refused,
    /// <summary>The developer can read the feed (200 on the index, or the feed is in their feed list): FAIL.</summary>
    CanRead,
    /// <summary>The developer's account could not list the organisation's feeds: not verified.</summary>
    NotMember,
    /// <summary>Packmoat's own identity could not read the feed, so a refusal would prove nothing: not verified.</summary>
    ControlFailed,
    /// <summary>Anything else (404, 429, 5xx, a timeout): not verified.</summary>
    Unclear,
}

/// <summary>One step's verdict: what was found, the status and the detail recorded.</summary>
public sealed record DeveloperProbeDecision(DeveloperFinding Finding, CheckStatus Status, string Detail);

/// <summary>
/// The developer check's verdict, pure, shared by the CLI's <c>onboard check</c> (a developer PAT) and the wizard's developer proof (a
/// developer's own sign-in, #64 I2). Two controls come first, so a refusal means something: (a) Packmoat's own identity reads the
/// public-only feed index (200), and (b) the developer's credential lists the organisation's feeds (200). Only then does the developer's
/// GET of the feed index decide: 401 or 403 PASS, 200 FAIL, anything else (404 included, per the I0 spike) NOT VERIFIED.
/// </summary>
public static class DeveloperProbeVerdict
{
    public const string Id = ConnectRules.DeveloperCheck;
    /// <summary>The site's title (#119; the CLI prints its own).</summary>
    public const string Title = "A developer account is refused on Packmoat's feed";

    // The site's detail for each finding (#119 item 8). The CLI keeps the verdict's own text below.
    public const string SiteRefused = "Your account could sign in to Azure DevOps, and Azure DevOps refused it on Packmoat's feed.";
    public const string SiteCanRead = "Your account can read Packmoat's feed directly.";
    public const string SiteNotMember = "Azure DevOps didn't list this organisation's feeds for your account.";
    public const string SiteControlFailed = "Packmoat's own feed access couldn't read the feed, so a refusal wouldn't prove anything. Ask your Admin to check the feed's settings again.";
    public const string SiteUnclear = "Azure DevOps didn't give a clear answer. Check again in a few minutes.";

    /// <summary>The developer proof's detail on the site: one fixed sentence per finding, never a status code or an error.</summary>
    public static string SiteDetail(DeveloperFinding finding) => finding switch
    {
        DeveloperFinding.Refused => SiteRefused,
        DeveloperFinding.CanRead => SiteCanRead,
        DeveloperFinding.NotMember => SiteNotMember,
        DeveloperFinding.ControlFailed => SiteControlFailed,
        _ => SiteUnclear,
    };

    /// <summary>
    /// Control (a): null when Packmoat's own identity got 200 on the public-only feed index, otherwise why the control failed
    /// (<paramref name="packmoatStatus"/> null: no answer at all, or no token).
    /// </summary>
    public static string? ControlFailure(int? packmoatStatus) => packmoatStatus == 200
        ? null
        : $"control failed: Packmoat's own identity got {(packmoatStatus is { } s ? "HTTP " + s.ToString(CultureInfo.InvariantCulture) : "no answer")} on the public-only feed index, so a refusal elsewhere would prove nothing. Fix Packmoat's access to the public-only feed first.";

    /// <summary>Control (a) failed: not verified.</summary>
    public static DeveloperProbeDecision ControlNotMet(string controlFailure) => new(DeveloperFinding.ControlFailed, CheckStatus.Skip, "NOT VERIFIED: " + controlFailure);

    /// <summary>
    /// Control (b): null when the developer's credential listed feeds (exactly 200, with a feed list when <paramref name="readable"/> is
    /// checked) and the public-only feed is not among them. The feed in their own list means they can read it: FAIL.
    /// </summary>
    public static DeveloperProbeDecision? ListControl(int listStatus, bool readable = true, bool listsPublicFeed = false)
    {
        if (listStatus != 200)
            return new(DeveloperFinding.NotMember, CheckStatus.Skip,
                $"NOT VERIFIED: control failed: the developer credential got HTTP {listStatus} even where it should work (listing feeds), so it is expired or wrong. Get a fresh one and rerun.");
        if (!readable)
            return new(DeveloperFinding.NotMember, CheckStatus.Skip,
                "NOT VERIFIED: control failed: listing feeds with the developer credential answered HTTP 200 but not with a list of feeds, so the account may not be a member of the organisation.");
        if (listsPublicFeed)
            return new(DeveloperFinding.CanRead, CheckStatus.Fail,
                "the developer account lists the public-only feed among the feeds it can see, so developers can read it directly and bypass Packmoat. Remove their groups (Contributors, Build Service) from the feed and set views to specific people.");
        return null;
    }

    /// <summary>The developer's GET of the public-only feed index, once both controls passed.</summary>
    public static DeveloperProbeDecision Index(int status) => status switch
    {
        401 or 403 => new(DeveloperFinding.Refused, CheckStatus.Pass, $"the developer credential is valid (it can list feeds) and got HTTP {status} on the public-only feed (refused)"),
        200 => new(DeveloperFinding.CanRead, CheckStatus.Fail, "developer account got HTTP 200: developers can use the public-only feed directly and bypass Packmoat. Remove their groups (Contributors, Build Service) from the feed and set views to specific people."),
        _ => new(DeveloperFinding.Unclear, CheckStatus.Skip, $"NOT VERIFIED: developer account got HTTP {status}, which does not prove a refusal (404 can mean a wrong name; 429 or 5xx mean we were throttled or the service failed)."),
    };

    /// <summary>A developer row's outcome: passed only when 'developer' PASSED, failed when anything FAILED, otherwise incomplete. The bypass advisory never decides it.</summary>
    public static FeedCheckOutcome Outcome(IReadOnlyList<CheckResult> results)
    {
        if (results.Any(r => r.Status == CheckStatus.Fail)) return FeedCheckOutcome.Failed;
        return results.FirstOrDefault(r => r.Id == Id)?.Status == CheckStatus.Pass ? FeedCheckOutcome.Passed : FeedCheckOutcome.Incomplete;
    }
}

/// <summary>One upstream source of a feed, as Get Feeds returns it (<c>protocol</c>, <c>upstreamSourceType</c>, <c>internalUpstreamFeedId</c>).</summary>
public sealed record FeedUpstream(string? Protocol, string? SourceType, string? InternalFeedId);

/// <summary>A feed in a Get Feeds list. <c>Upstreams</c> is null when the list did not include the feed's <c>upstreamSources</c>.</summary>
public sealed record VisibleFeed(string Id, string Name, IReadOnlyList<FeedUpstream>? Upstreams);

/// <summary>
/// The bypass advisory (#64 I2), pure: from the developer's own feed list, the other feeds that let developers skip Packmoat's cooldown:
/// a public upstream with protocol nuget (any case), or an internal upstream pointing at the public-only feed. Advisory only: a WARN
/// (found) or NOT VERIFIED (a feed's upstream sources were not in the list: never a silent pass), never a FAIL, so it never blocks Connect.
/// </summary>
public static class BypassAdvisory
{
    public const string Id = "bypass";
    public const string Title = "No other feed lets developers skip Packmoat";
    /// <summary>The developer's sentence (#119 item 9): {0} is the feed names.</summary>
    public const string FoundSentence = "Feeds that let you skip Packmoat's cooldown: {0}. Ask your Admin to remove nuget.org, and any upstream that points to Packmoat's feed, from them.";
    /// <summary>The same finding on the Admin's Connect card (#119 item 9): {0} is the feed names.</summary>
    public const string AdminFoundSentence = "Feeds that let developers skip Packmoat's cooldown: {0}. Remove nuget.org, and any upstream that points to Packmoat's feed, from them.";
    /// <summary>The Admin's sentence for a row recorded before the names were kept (#119).</summary>
    public const string AdminFoundOlderSentence = "A developer's check found feeds that skip Packmoat's cooldown. Remove nuget.org, and any upstream that points to Packmoat's feed, from them.";
    /// <summary>Not checked, on the Admin's Connect card (#119): about the developer's account, not the reader's.</summary>
    public const string AdminNotCheckedSentence = "Bypass not checked: Azure DevOps didn't show the developer's account the upstream sources of every feed, so Packmoat couldn't tell whether any of them get packages from nuget.org.";
    public const string NotCheckedSentence = "Bypass not checked: Azure DevOps didn't show the upstream sources of {0} of your feeds to your account, so Packmoat couldn't tell whether they get packages from nuget.org.";
    public const string NoneSentence = "None of the {0} other feeds your account can see gets packages from nuget.org or from Packmoat's feed.";
    /// <summary>The list was not read, so nothing was checked.</summary>
    public static CheckResult NotChecked() => new(Id, Title, CheckStatus.Skip, "Bypass not checked: your feeds couldn't be listed.");

    public static CheckResult Check(IReadOnlyList<VisibleFeed> feeds, Guid publicFeedId)
    {
        static bool Is(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        var others = feeds.Where(f => !(Guid.TryParse(f.Id, out var id) && id == publicFeedId)).ToList();
        var bypass = others.Where(f => f.Upstreams is { } ups && ups.Any(u =>
                (Is(u.SourceType, "public") && Is(u.Protocol, "nuget"))
                || (Is(u.SourceType, "internal") && Guid.TryParse(u.InternalFeedId, out var target) && target == publicFeedId)))
            .Select(f => f.Name).ToList();
        if (bypass.Count > 0) return new(Id, Title, CheckStatus.Warn, string.Format(CultureInfo.InvariantCulture, FoundSentence, string.Join(", ", bypass)), bypass);
        var unknown = others.Count(f => f.Upstreams is null);
        if (unknown > 0) return new(Id, Title, CheckStatus.Skip, string.Format(CultureInfo.InvariantCulture, NotCheckedSentence, unknown));
        return new(Id, Title, CheckStatus.Pass, string.Format(CultureInfo.InvariantCulture, NoneSentence, others.Count));
    }

    /// <summary>
    /// The advisory on the Admin's Connect card: a bypass found is addressed to the Admin, with the names the row kept (or, for a row
    /// recorded before they were kept, without names: never parsed from the developer's sentence); not checked keeps its own sentence;
    /// null when nothing was found.
    /// </summary>
    public static string? AdminSentence(CheckResult bypass) => bypass.Status switch
    {
        CheckStatus.Warn => bypass.Names is { Count: > 0 } names
            ? string.Format(CultureInfo.InvariantCulture, AdminFoundSentence, string.Join(", ", names))
            : AdminFoundOlderSentence,
        CheckStatus.Skip => AdminNotCheckedSentence,
        _ => null,
    };
}

/// <summary>
/// One probe of another feed (or view) with Packmoat's identity: its name and the HTTP status (null: no answer).
/// <paramref name="OrganisationDefault"/>: the probe is of Azure DevOps' default feed itself (<see cref="PrivateFeedVerdict.IsOrganisationDefault"/>), not one of its views.
/// </summary>
public sealed record PrivateProbe(string Name, int? Status, bool OrganisationDefault = false);

/// <summary>
/// The 'private' result (#64 I2), pure: Packmoat's own identity must be refused on every other feed and non-private view the Admin's
/// account can see. It limits the blast radius if the shared certificate leaks. FAIL on any 200 or 3xx (with the names); PASS only with
/// the identity check PASS, the control (Packmoat reads the public-only feed: 200) and every probe 401 or 403; anything else, or more than
/// <see cref="MaxFeeds"/> feeds or <see cref="MaxProbes"/> probes, NOT VERIFIED. Only feeds the Admin can see were checked, and it says so.
/// </summary>
public static class PrivateFeedVerdict
{
    public const string Id = "private";
    public const string Title = "Packmoat feed access is refused on your other feeds.";
    public const int MaxFeeds = 50;
    public const int MaxProbes = 100;
    /// <summary>The plan's wording: {0} the number of other feeds.</summary>
    public const string PassSentence = "Packmoat feed access can read Packmoat's feed and is refused on the {0} other feeds your account can see. Feeds your account can't see weren't checked.";
    /// <summary>#119 item 4: {0} the feed's name.</summary>
    public const string FailOneSentence = "Packmoat feed access can read 1 other feed: {0}.";
    /// <summary>#119 item 4: {0} how many, {1} their names.</summary>
    public const string FailManySentence = "Packmoat feed access can read {0} other feeds: {1}.";
    public const string FailHint = "Remove its access there (often through the project's Readers group, or a view shared with your organisation), then check again.";
    /// <summary>#119 item 4, when Azure DevOps' default feed is one of them: {0} its name.</summary>
    public const string DefaultFeedHint = "Azure DevOps made a feed called {0} that everyone in your organisation can read. In its Permissions, remove Project Collection Valid Users, or delete the feed if you don't use it.";

    /// <summary>
    /// Azure DevOps' default feed: outside any project, named after the organisation (ignoring case). Project Collection Valid Users can
    /// read it, so Packmoat feed access can too. A project feed of the same name is not it.
    /// </summary>
    public static bool IsOrganisationDefault(string feedName, bool hasProject, string organization) =>
        !hasProject && string.Equals(feedName, organization, StringComparison.OrdinalIgnoreCase);

    /// <summary>The FAIL detail: how many and which, then the default feed's hint when it is one of them, and the general hint for any other.</summary>
    private static string FailDetail(IReadOnlyList<PrivateProbe> leaking)
    {
        var names = leaking.Select(p => p.Name).Distinct(StringComparer.Ordinal).ToList();
        var sb = new System.Text.StringBuilder(names.Count == 1
            ? string.Format(CultureInfo.InvariantCulture, FailOneSentence, names[0])
            : string.Format(CultureInfo.InvariantCulture, FailManySentence, names.Count, string.Join(", ", names)));
        if (leaking.FirstOrDefault(p => p.OrganisationDefault) is { } defaultFeed)
            sb.Append(' ').Append(string.Format(CultureInfo.InvariantCulture, DefaultFeedHint, defaultFeed.Name));
        if (leaking.Any(p => !p.OrganisationDefault)) sb.Append(' ').Append(FailHint);
        return sb.ToString();
    }

    /// <summary>More feeds than one check tests. <paramref name="support"/>: who to ask (#186), the operator's address or neutral words.</summary>
    public static CheckResult TooMany(int feeds, int probes, SupportContact support) => new(Id, Title, CheckStatus.Skip,
        $"NOT VERIFIED: your account can see {feeds} other feeds ({probes} feeds and views to test), more than Packmoat tests in one check ({MaxFeeds} feeds, {MaxProbes} tests). {support.CapitalPhrase}.");

    public static CheckResult NotVerified(string why) => new(Id, Title, CheckStatus.Skip, "NOT VERIFIED: " + why);

    /// <summary>The private check used up its own time budget: it alone is not verified, the other results stand.</summary>
    public static CheckResult OutOfTime(SupportContact support) => NotVerified("testing Packmoat feed access on your other feeds took too long. Check again in a few minutes. If it keeps happening, " + support.Phrase + ".");

    public static CheckResult Decide(CheckStatus identity, int? controlStatus, int otherFeeds, IReadOnlyList<PrivateProbe> probes)
    {
        var leaks = probes.Where(p => p.Status is 200 or (>= 300 and < 400)).ToList();
        if (leaks.Count > 0) return new(Id, Title, CheckStatus.Fail, FailDetail(leaks));
        if (identity != CheckStatus.Pass)
            return NotVerified("Packmoat feed access wasn't identified in your organisation (see the identity check), so its access to your other feeds proves nothing yet.");
        if (controlStatus != 200)
            return NotVerified("Packmoat feed access couldn't read Packmoat's feed, so a refusal elsewhere would prove nothing. Check that it has the Collaborator role on that feed.");
        var unclear = probes.Where(p => p.Status is not (401 or 403)).ToList();
        if (unclear.Count > 0)
            return NotVerified("Azure DevOps gave an answer that doesn't prove a refusal for " +
                string.Join(", ", unclear.Select(p => p.Name)) + ". Check again in a few minutes.");
        return new(Id, Title, CheckStatus.Pass, string.Format(CultureInfo.InvariantCulture, PassSentence, otherFeeds));
    }
}

/// <summary>
/// The only feed index URLs Packmoat's own identity is ever sent to by a check: <c>https://pkgs.dev.azure.com/{org}/[{projectId}/]_packaging/{feedId}[@{viewId}]/nuget/v3/index.json</c>,
/// built from GUIDs, the organisation in lower case, no query.
/// </summary>
public static class GuidIndexUrls
{
    private const string G = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
    private static readonly Regex Shape = new(
        @"\Ahttps://pkgs\.dev\.azure\.com/[a-z0-9]([a-z0-9-]{0,48}[a-z0-9])?/(" + G + "/)?_packaging/" + G + "(@" + G + @")?/nuget/v3/index\.json\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool IsGuidIndexUrl(string? url) => url is not null && Shape.IsMatch(url);

    /// <summary>The index URL of a feed (or one of its views), or null when the organisation is not a valid name.</summary>
    public static string? Build(string organization, Guid? projectId, Guid feedId, Guid? viewId = null)
    {
        if (!AzureFeedNames.IsOrganization(organization) || feedId == Guid.Empty || projectId == Guid.Empty || viewId == Guid.Empty) return null;
        var url = ResolvedFeed.PackagesBase + organization.ToLowerInvariant() + "/" + (projectId is { } p ? p.ToString("D") + "/" : "")
                  + "_packaging/" + feedId.ToString("D") + (viewId is { } v ? "@" + v.ToString("D") : "") + "/nuget/v3/index.json";
        return IsGuidIndexUrl(url) ? url : null;
    }

    /// <summary>The organisation, project id and feed id of a feed index URL Connect could use (<see cref="ConnectRules.UrlsFor"/>), or null.</summary>
    public static (string Organization, Guid? ProjectId, Guid FeedId)? Parse(string? feedIndexUrl)
    {
        if (ConnectRules.UrlsFor(feedIndexUrl) is not { } urls) return null;
        var parts = urls.PathPrefix.TrimEnd('/').Split('/');
        // {org}/_packaging/{feed} or {org}/{project}/_packaging/{feed}
        return parts.Length == 3 ? (parts[0], null, Guid.Parse(parts[2])) : (parts[0], Guid.Parse(parts[1]), Guid.Parse(parts[3]));
    }
}

/// <summary>
/// Packmoat's own identity reading feed indexes (#64 I2): the active shared app's certificate, signed in to the customer's directory, as
/// the proxy would. Used as the control of both the private check and the developer proof, and to probe the customer's other feeds. The
/// HTTP lives in Infrastructure: its own client, <c>pkgs.dev.azure.com</c> only, <see cref="GuidIndexUrls"/> only, no redirects.
/// </summary>
public interface ISharedAppFeedProbe
{
    /// <summary>
    /// The HTTP status of a GET of each URL with Packmoat's identity in <paramref name="directoryId"/>, in order; null for one that got no
    /// answer. Throws when no token can be had (no active shared app, or the sign-in failed).
    /// </summary>
    Task<IReadOnlyList<int?>> ProbeAsync(string directoryId, IReadOnlyList<string> indexUrls, CancellationToken cancellationToken);
}

/// <summary>What the developer probe is about: the person's directory and the feed the latest settings check was about.</summary>
public sealed record DeveloperProbeRequest(string DirectoryId, string FeedIndexUrl);

/// <summary>The developer probe's results ('developer', then 'bypass') and what it found.</summary>
public sealed record DeveloperProbeRun(IReadOnlyList<CheckResult> Results, DeveloperFinding Finding);

/// <summary>
/// The developer proof's probe (#64 I2) with a developer's own short-lived Azure DevOps token: (a) Packmoat's identity reads the index,
/// (b) the developer lists the organisation's feeds, (c) the developer GETs the index, (d) the bypass advisory from (b). The token is used
/// only for this call and sent only to <c>feeds.dev.azure.com</c> and the exact feed index URL; never stored, logged or put in a result.
/// </summary>
public interface IDeveloperFeedProbe
{
    Task<DeveloperProbeRun> ProbeAsync(DeveloperProbeRequest request, string developerToken, CancellationToken cancellationToken);
}
