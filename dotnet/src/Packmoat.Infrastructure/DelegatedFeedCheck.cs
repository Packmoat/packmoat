using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure;

/// <summary>
/// Refuses any request that is not https to one of Azure DevOps' API hosts on the default port, before it leaves the process. The
/// delegated check's token can therefore go nowhere else, whatever URL a check builds. (pkgs.dev.azure.com is not on the list: the token is
/// never sent to the package host.)
/// </summary>
public sealed class AzureDevOpsHostsOnlyHandler : DelegatingHandler
{
    public static readonly string[] Hosts = { "feeds.dev.azure.com", "vssps.dev.azure.com", "dev.azure.com" };

    public static bool Allowed(Uri? uri) =>
        uri is { IsAbsoluteUri: true } u && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && u.UserInfo.Length == 0
        && Hosts.Contains(u.IdnHost, StringComparer.OrdinalIgnoreCase);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Allowed(request.RequestUri)
            ? base.SendAsync(request, cancellationToken)
            : throw new HttpRequestException("refused: not an Azure DevOps API host");   // never the URL
}

/// <summary>
/// <see cref="IDelegatedFeedCheck"/>: Get Feed to resolve the typed names, then the onboarding check engine with the person's token as
/// the admin credential, on the GUID-built feed. The HttpClient comes from the named client (redirects never followed, Azure DevOps API
/// hosts only). The token lives only in this call's locals and the engine's options.
/// </summary>
public sealed class DelegatedFeedCheck : IDelegatedFeedCheck
{
    /// <summary>The named HttpClient: <see cref="CreatePrimaryHandler"/> under <see cref="AzureDevOpsHostsOnlyHandler"/>.</summary>
    public const string ClientName = "packmoat-azure-devops-delegated";
    public const string FeedsApiBase = "https://feeds.dev.azure.com";
    public const string GraphApiBase = "https://vssps.dev.azure.com";
    public const string ProjectsApiBase = "https://dev.azure.com";

    /// <summary>The production transport: a redirect is never followed (it would be a sign-in page, or somewhere the token must not go).</summary>
    public static SocketsHttpHandler CreatePrimaryHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    private readonly Func<HttpClient> _client;
    private readonly ISharedAppFeedProbe? _shared;
    private readonly TimeSpan _privateDeadline;

    /// <summary>The 'private' check's own budget at most. It also ends <see cref="PrivateMargin"/> before the whole check's deadline.</summary>
    public static readonly TimeSpan PrivateDeadline = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PrivateMargin = TimeSpan.FromSeconds(5);
    /// <summary>At most this many views listings at a time (the shared app probe also probes at most this many indexes at a time).</summary>
    public const int MaxConcurrency = 8;

    /// <param name="shared">Packmoat's own identity, for the 'private' check (#64 I2). Without it 'private' is not verified.</param>
    /// <param name="privateDeadline">A test seam: production always uses <see cref="PrivateDeadline"/>.</param>
    /// <param name="log">Where status codes and error types go (#119): never into a result the customer sees.</param>
    /// <param name="support">Who the results tell a person to ask (#186): the operator's name and address, or neutral words when unset.</param>
    public DelegatedFeedCheck(Func<HttpClient> client, ISharedAppFeedProbe? shared = null, TimeSpan? privateDeadline = null, Action<string>? log = null,
        SupportContact? support = null)
    {
        _client = client;
        _support = support ?? SupportContact.None;
        _shared = shared;
        _privateDeadline = privateDeadline ?? PrivateDeadline;
        _log = log ?? (_ => { });
    }

    private readonly Action<string> _log;
    private readonly SupportContact _support;

    /// <summary>This check's log: each line names the Packmoat tenant, and nothing else that identifies anyone (#119).</summary>
    private Action<string> Log(DelegatedCheckRequest request) => message => _log($"{message} (tenant {request.TenantId ?? "unknown"})");

    /// <summary>The engine's checks that are not its to run here: 'private' is run below (#64 I2), the developer proof and the proxy are other steps.</summary>
    private static readonly string[] NotRunHere = { "developer", "private", "proxy" };

    public async Task<DelegatedCheckRun> CheckNewFeedAsync(FeedNames names, DelegatedCheckRequest request, string accessToken, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var http = _client();
        var scope = names.Project is null ? names.Organization : names.Organization + "/" + Uri.EscapeDataString(names.Project);
        var url = $"{FeedsApiBase}/{scope}/_apis/packaging/feeds/{names.Feed}?api-version=7.1";
        var (feed, problem, timedOut) = await ResolveAsync(http, url, names, accessToken, Log(request), ct);
        if (timedOut)
            return new DelegatedCheckRun("", new[] { new CheckResult("feed", OnboardingChecks.FeedTitle, CheckStatus.Skip, AzureSetupCommands.NoAnswerInTime) }, Array.Empty<NamedFeedAdmin>());
        if (feed is null) return new DelegatedCheckRun("", new[] { new CheckResult("feed", OnboardingChecks.FeedTitle, CheckStatus.Fail, problem!) }, Array.Empty<NamedFeedAdmin>());
        var tenant = new Tenant("setup-check", "Setup check", ResolvedFeed.PackagesBase, feed.PathPrefix, feed.FeedIndexUrl, new TenantPolicy(0));
        return await RunAsync(http, tenant, feed.Organization.ToLowerInvariant(), feed.ProjectId?.ToString("D"), feed.FeedId.ToString("D"), request, accessToken,
            f => f.FeedId == feed.FeedId, started, ct);
    }

    public async Task<DelegatedCheckRun> CheckStoredFeedAsync(Tenant tenant, DelegatedCheckRequest request, string accessToken, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        if (!TryParseStored(tenant.UpstreamFeedIndexUrl, out var org, out var project, out var feed))
            return new DelegatedCheckRun(tenant.UpstreamFeedIndexUrl, new[] { new CheckResult("feed", OnboardingChecks.FeedTitle, CheckStatus.Fail,
                "the stored feed address is not an Azure Artifacts NuGet feed index (https://pkgs.dev.azure.com/{organisation}[/{project}]/_packaging/{feed}/nuget/v3/index.json).") },
                Array.Empty<NamedFeedAdmin>());
        // A stored URL may name the feed and project by name or by id: the feed list entry is matched either way.
        static bool Same(string stored, Guid? id, string? name) =>
            Guid.TryParse(stored, out var g) ? id == g : string.Equals(stored, name, StringComparison.OrdinalIgnoreCase);
        return await RunAsync(_client(), tenant, org, project, feed, request, accessToken,
            f => Same(feed, f.FeedId, f.Name) && (project is null ? !f.HasProject : f.HasProject && Same(project, f.ProjectId, f.ProjectName)), started, ct);
    }

    /// <summary>{org}, {project or null} and {feed} from a pkgs.dev.azure.com feed index URL that passes the strict URL rules, as stored.</summary>
    internal static bool TryParseStored(string url, out string org, out string? project, out string feed)
    {
        org = feed = "";
        project = null;
        if (TenantUrlRules.Check(ResolvedFeed.PackagesBase, "", url, requireHttps: true, requirePrefix: false) is not null
            || !Uri.TryCreate(url, UriKind.Absolute, out var u) || !string.Equals(u.IdnHost, "pkgs.dev.azure.com", StringComparison.OrdinalIgnoreCase)) return false;
        var s = u.AbsolutePath.Trim('/').Split('/');
        var p = Array.IndexOf(s, "_packaging");
        if (p is not (1 or 2) || s.Length != p + 5 || s[p + 2] != "nuget" || s[p + 3] != "v3" || s[p + 4] != "index.json") return false;
        org = s[0];
        project = p == 2 ? s[1] : null;
        feed = s[p + 1];
        return AzureFeedNames.IsPlainName(org) && AzureFeedNames.IsPlainName(feed) && (project is null || AzureFeedNames.IsPlainName(project));
    }

    private async Task<DelegatedCheckRun> RunAsync(HttpClient http, Tenant tenant, string org, string? project, string feed, DelegatedCheckRequest request,
        string accessToken, Func<ListedFeed, bool> isCheckedFeed, long started, CancellationToken ct)
    {
        var o = new OnboardingOptions
        {
            Organization = org, Project = project, Feed = feed,
            FeedsApiBase = FeedsApiBase, GraphApiBase = GraphApiBase, ProjectsApiBase = ProjectsApiBase, PackagesBase = ResolvedFeed.PackagesBase.TrimEnd('/'),
            AdminToken = accessToken, AdminTokenIsBearer = true,
            SharedAppClientId = request.SharedClientId, SharedAppDirectoryId = request.DirectoryId,
            CheckProject = true,
            CustomerWording = true,   // the site's own text, never the CLI's operator text (#119)
            Support = _support,
            MaxRetries = AzureSetupRules.MaxRetries, RetryWaitCap = AzureSetupRules.RetryWaitCap,
        };
        o.ConfirmedAdmins.AddRange(request.ConfirmedAdmins);
        // No developer credential, private feed or proxy is given: those checks are later steps, and Packmoat's own credential is never used.
        var engine = new OnboardingChecks(http, NoCredential.Instance, tenant, o);
        var results = (await engine.RunWithoutProxyAsync(ct)).Where(r => !NotRunHere.Contains(r.Id)).ToList();
        var identity = results.FirstOrDefault(r => r.Id == "identity")?.Status ?? CheckStatus.Skip;
        results.Add(await PrivateAsync(http, org, isCheckedFeed, identity, request.DirectoryId, accessToken, started, Log(request), ct));
        return new DelegatedCheckRun(tenant.UpstreamFeedIndexUrl, results, engine.NamedAdmins.ToList());
    }

    /// <summary>
    /// The 'private' check (#64 I2). The Admin's token lists the organisation's feeds (Get Feeds at organisation level lists the
    /// project-scoped ones too) and, for every other feed, its views; Packmoat's OWN identity (<see cref="ISharedAppFeedProbe"/>, never the
    /// Admin's token) then GETs the public-only feed's index (the control) and every other feed's index and every non-private view's, all
    /// built from GUIDs. The verdict is <see cref="PrivateFeedVerdict"/>; more than <see cref="PrivateFeedVerdict.MaxFeeds"/> other feeds or
    /// <see cref="PrivateFeedVerdict.MaxProbes"/> probes is not verified, and nothing is probed.
    /// </summary>
    private async Task<CheckResult> PrivateAsync(HttpClient http, string org, Func<ListedFeed, bool> isCheckedFeed, CheckStatus identity, string directoryId,
        string accessToken, long started, Action<string> log, CancellationToken outer)
    {
        if (_shared is null) return PrivateFeedVerdict.NotVerified("Packmoat's own feed access can't be tested on this server.");
        // Its own budget: at most PrivateDeadline, and never past what is left of the whole check's deadline (minus a margin), so a large
        // organisation makes 'private' alone not verified instead of turning every other result into "ran out of time".
        var left = AzureSetupRules.Deadline - Stopwatch.GetElapsedTime(started) - PrivateMargin;
        var budget = left < _privateDeadline ? left : _privateDeadline;
        if (budget <= TimeSpan.Zero) return PrivateFeedVerdict.OutOfTime(_support);
        using var sub = CancellationTokenSource.CreateLinkedTokenSource(outer);
        sub.CancelAfter(budget);
        var ct = sub.Token;
        try
        {
            var (status, value) = await FeedLists.GetValuesAsync(http, FeedLists.OrganizationFeedsUrl(org), accessToken, ct);
            if (value is null)
            {
                log($"Private check: listing the organisation's feeds answered HTTP {status}");
                return PrivateFeedVerdict.NotVerified("your organisation's feeds couldn't be listed, so Packmoat's access to them wasn't tested.");
            }
            var feeds = FeedLists.Parse(value);
            var mine = feeds.Where(isCheckedFeed).ToList();
            if (mine.Count != 1 || mine[0].FeedId is null) return PrivateFeedVerdict.NotVerified("the feed checked wasn't found exactly once in your account's list of feeds.");
            var others = feeds.Where(f => !ReferenceEquals(f, mine[0])).ToList();
            if (others.Count > PrivateFeedVerdict.MaxFeeds) return PrivateFeedVerdict.TooMany(others.Count, others.Count, _support);
            if (others.Any(f => f.FeedId is null || (f.HasProject && f.ProjectId is null)))
                return PrivateFeedVerdict.NotVerified("Azure DevOps listed a feed without a usable id.");
            var control = GuidIndexUrls.Build(org, mine[0].ProjectId, mine[0].FeedId!.Value);
            if (control is null) return PrivateFeedVerdict.NotVerified("the feed's address couldn't be built.");

            // Each other feed's views, at most MaxConcurrency at a time; kept in the feeds' order.
            var perFeed = new (List<(string Name, string Url, bool Default)> Targets, List<PrivateProbe> Unclear)[others.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, others.Count), new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = ct }, async (k, token) =>
            {
                var other = others[k];
                // Only the default feed's own index is flagged (#119): its views have their own visibility.
                var targets = new List<(string Name, string Url, bool Default)>
                {
                    (other.Name, GuidIndexUrls.Build(org, other.ProjectId, other.FeedId!.Value)!, PrivateFeedVerdict.IsOrganisationDefault(other.Name, other.HasProject, org)),
                };
                var unclear = new List<PrivateProbe>();
                perFeed[k] = (targets, unclear);
                var scope = other.ProjectId is { } p ? $"{org}/{p:D}" : org;
                var (viewsStatus, views) = await FeedLists.GetValuesAsync(http, $"{FeedsApiBase}/{scope}/_apis/packaging/feeds/{other.FeedId:D}/views?api-version=7.1", accessToken, token);
                // Never the listing's own status as a probe's: a 401 here is not Packmoat being refused.
                if (views is null)
                {
                    log($"Private check: listing a feed's views answered HTTP {viewsStatus}");
                    unclear.Add(new PrivateProbe($"{other.Name} (its views couldn't be listed)", null));
                    return;
                }
                foreach (var view in views.OfType<JsonObject>())
                {
                    // Only an explicit "private" is skipped: a missing or unknown visibility is probed (fail closed).
                    var visibility = view["visibility"] is JsonValue v && v.TryGetValue<string>(out var vis) ? vis : null;
                    if (string.Equals(visibility, "private", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = view["name"] is JsonValue n && n.TryGetValue<string>(out var vn) ? vn : "?";
                    var url = Guid.TryParseExact(view["id"] is JsonValue i && i.TryGetValue<string>(out var vid) ? vid : null, "D", out var viewId) && viewId != Guid.Empty
                        ? GuidIndexUrls.Build(org, other.ProjectId, other.FeedId!.Value, viewId) : null;
                    if (url is null) unclear.Add(new PrivateProbe($"{other.Name}@{name} (no usable view id)", null));
                    else targets.Add(($"{other.Name}@{name}", url, false));
                }
            });
            var allTargets = perFeed.SelectMany(f => f.Targets).ToList();
            var allUnclear = perFeed.SelectMany(f => f.Unclear).ToList();
            if (allTargets.Count > PrivateFeedVerdict.MaxProbes) return PrivateFeedVerdict.TooMany(others.Count, allTargets.Count, _support);

            var statuses = await _shared.ProbeAsync(directoryId, allTargets.Select(t => t.Url).Prepend(control).ToList(), ct);
            var probes = allTargets.Select((t, k) => new PrivateProbe(t.Name, statuses[k + 1], t.Default)).Concat(allUnclear).ToList();
            // The statuses go to the log only (#119): the result names the feeds, never a status code.
            if (statuses[0] != 200) log($"Private check: Packmoat's own feed access got {statuses[0]?.ToString() ?? "no answer"} on Packmoat's feed");
            foreach (var p in probes.Where(p => p.Status is not (null or 401 or 403 or 200) and not (>= 300 and < 400)))
                log($"Private check: an unclear answer ({p.Status}) for one of the organisation's feeds");
            return PrivateFeedVerdict.Decide(identity, statuses[0], others.Count, probes);
        }
        catch (OperationCanceledException) when (sub.IsCancellationRequested && !outer.IsCancellationRequested)
        {
            return PrivateFeedVerdict.OutOfTime(_support);   // 'private' alone: the other results stand
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return PrivateFeedVerdict.NotVerified(AzureSetupCommands.NoAnswerInTime["NOT VERIFIED: ".Length..]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"Private check: Packmoat's own feed access couldn't be tested ({ex.GetType().Name})");
            return PrivateFeedVerdict.NotVerified("Packmoat's own feed access couldn't be tested. Check again in a few minutes.");
        }
    }

    /// <summary>Get Feed, exactly 200 with a JSON object, then <see cref="FeedResolution.Validate"/>. A detail for each refusal; never the body or the token.</summary>
    private static async Task<(ResolvedFeed? Feed, string? Problem, bool TimedOut)> ResolveAsync(HttpClient http, string url, FeedNames names, string token, Action<string> log, CancellationToken ct)
    {
        int status;
        JsonObject? body = null;
        try
        {
            using var response = await SendAsync(http, url, token, ct);
            status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.OK)
            {
                try { body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject; }
                catch (JsonException) { body = null; }
                if (body is null) return (null, Describe(FeedResolveProblem.Unreadable), false);
            }
            else log($"Get Feed answered HTTP {status}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // One call's own timeout (not the whole check's deadline): Azure DevOps was slow, which proves nothing about the feed.
            return (null, null, true);
        }
        catch (HttpRequestException ex)
        {
            log($"Get Feed couldn't reach Azure DevOps ({ex.GetType().Name})");
            return (null, "Packmoat couldn't reach Azure DevOps to find the feed. Check again in a few minutes.", false);
        }
        if (body is null)
            return (null, Describe(status switch
            {
                404 => FeedResolveProblem.NotFound,
                401 or 403 => FeedResolveProblem.SignInRefused,
                203 or (>= 300 and < 400) => FeedResolveProblem.SignInPage,
                _ => FeedResolveProblem.Unreadable,
            }), false);
        static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var project = body["project"] as JsonObject;
        // The response's own "url" is ignored: every address is built from the GUIDs.
        var (feed, problem) = FeedResolution.Validate(names, Text(body["id"]), Text(body["name"]), project is not null, Text(project?["id"]), Text(project?["name"]));
        return feed is not null ? (feed, null, false) : (null, Describe(problem!.Value), false);
    }

    /// <summary>GET with a bearer token, retried at most once when throttled (429, or 503), waiting at most the cap.</summary>
    internal static async Task<HttpResponseMessage> SendAsync(HttpClient http, string url, string token, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await http.SendAsync(request, ct);
            if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) || attempt >= AzureSetupRules.MaxRetries) return response;
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
            wait = wait < TimeSpan.Zero ? TimeSpan.Zero : wait > AzureSetupRules.RetryWaitCap ? AzureSetupRules.RetryWaitCap : wait;
            response.Dispose();
            await Task.Delay(wait, ct);
        }
    }

    /// <summary>The "feed" result's detail for a resolve that failed. Fixed text only: the status is logged, not shown (#119).</summary>
    public static string Describe(FeedResolveProblem problem) => problem switch
    {
        FeedResolveProblem.NotFound => $"Azure DevOps found no feed with that name in that organisation and project, or your account can't see it.",
        FeedResolveProblem.SignInRefused => $"Azure DevOps refused your sign-in for this organisation. Check the organisation's name and that your account is a member of it.",
        FeedResolveProblem.SignInPage => $"Azure DevOps answered with a sign-in page instead of the feed's settings. Check that your account is a member of the organisation.",
        FeedResolveProblem.NameMismatch => "Azure DevOps returned a different feed from the one named. Type the feed's name, not its id.",
        FeedResolveProblem.ProjectMismatch => "The feed isn't in the project named. Leave the project empty for an organisation-scoped feed.",
        FeedResolveProblem.BadIds => "Azure DevOps' answer had no usable feed or project id.",
        FeedResolveProblem.UrlRules => "The feed's address could not be built from what Azure DevOps returned.",
        _ => "Azure DevOps' answer could not be read.",
    };

    /// <summary>Packmoat's own credential is never used by the delegated check; asking for it is a bug, so it throws.</summary>
    private sealed class NoCredential : IUpstreamCredentialSource
    {
        public static readonly NoCredential Instance = new();
        public ValueTask<UpstreamCredential> GetAsync(Tenant tenant, CancellationToken ct) => throw new InvalidOperationException("The delegated check never uses Packmoat's own credential.");
    }
}
