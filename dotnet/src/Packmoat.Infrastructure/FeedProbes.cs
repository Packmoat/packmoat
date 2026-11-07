using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Packmoat.Application;

namespace Packmoat.Infrastructure;

/// <summary>
/// The shared app probe's guard (#64 I2): refuses, before it leaves the process, any request that is not https on the default port to
/// <c>pkgs.dev.azure.com</c> for a feed index URL built from GUIDs (<see cref="GuidIndexUrls"/>). Packmoat's own token can go nowhere else.
/// </summary>
public sealed class SharedAppProbeHostsHandler : DelegatingHandler
{
    public static bool Allowed(Uri? uri) =>
        uri is { IsAbsoluteUri: true } u && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && u.UserInfo.Length == 0
        && string.Equals(u.IdnHost, "pkgs.dev.azure.com", StringComparison.Ordinal) && GuidIndexUrls.IsGuidIndexUrl(u.AbsoluteUri);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Allowed(request.RequestUri)
            ? base.SendAsync(request, cancellationToken)
            : throw new HttpRequestException("refused: not a feed index Packmoat's identity may be sent to");   // never the URL
}

/// <summary>
/// The developer probe's guard (#64 I2): only https on the default port to <c>feeds.dev.azure.com</c>, or to <c>pkgs.dev.azure.com</c>
/// for exactly a feed index URL Connect could use (<see cref="ConnectRules.UrlsFor"/>: GUIDs, the tight prefix, no query). The developer's
/// token can go nowhere else (not vssps, not dev.azure.com, not another package path).
/// </summary>
public sealed class DeveloperProbeHostsHandler : DelegatingHandler
{
    public static bool Allowed(Uri? uri) =>
        uri is { IsAbsoluteUri: true } u && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && u.UserInfo.Length == 0
        && (string.Equals(u.IdnHost, "feeds.dev.azure.com", StringComparison.Ordinal)
            || (string.Equals(u.IdnHost, "pkgs.dev.azure.com", StringComparison.Ordinal) && u.Query.Length == 0 && ConnectRules.UrlsFor(u.AbsoluteUri) is not null));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Allowed(request.RequestUri)
            ? base.SendAsync(request, cancellationToken)
            : throw new HttpRequestException("refused: not a host the developer's token may be sent to");   // never the URL
}

/// <summary>A feed in a Get Feeds list, as Packmoat reads it. Ids are kept only when they are GUIDs.</summary>
internal sealed record ListedFeed(string Name, Guid? FeedId, bool HasProject, Guid? ProjectId, string? ProjectName, IReadOnlyList<FeedUpstream>? Upstreams)
{
    public VisibleFeed Visible => new(FeedId?.ToString("D") ?? "", Name, Upstreams);
}

/// <summary>Reads Azure DevOps' Get Feeds answers (organisation level: project-scoped feeds are listed too, per the I0 spike).</summary>
internal static class FeedLists
{
    public const string FeedsApiBase = DelegatedFeedCheck.FeedsApiBase;

    public static string OrganizationFeedsUrl(string organization) => $"{FeedsApiBase}/{organization}/_apis/packaging/feeds?api-version=7.1";

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static Guid? Id(JsonNode? n) => Guid.TryParseExact(Text(n), "D", out var g) && g != Guid.Empty ? g : null;

    public static IReadOnlyList<ListedFeed> Parse(JsonArray value) => value.OfType<JsonObject>().Select(f =>
    {
        var project = f["project"] as JsonObject;
        IReadOnlyList<FeedUpstream>? upstreams = f["upstreamSources"] is JsonArray ups
            ? ups.OfType<JsonObject>().Select(u => new FeedUpstream(Text(u["protocol"]), Text(u["upstreamSourceType"]), Text(u["internalUpstreamFeedId"]))).ToList()
            : null;
        return new ListedFeed(Text(f["name"]) ?? "", Id(f["id"]), project is not null, Id(project?["id"]), Text(project?["name"]), upstreams);
    }).ToList();

    /// <summary>
    /// GET with a bearer token, retried once when throttled. Exactly 200 with a JSON object holding a "value" array gives the array;
    /// anything else gives the status and null. Never the body.
    /// </summary>
    public static async Task<(int Status, JsonArray? Value)> GetValuesAsync(HttpClient http, string url, string token, CancellationToken ct)
    {
        using var response = await DelegatedFeedCheck.SendAsync(http, url, token, ct);
        var status = (int)response.StatusCode;
        if (response.StatusCode != HttpStatusCode.OK) return (status, null);
        try { return (status, (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject)?["value"] as JsonArray); }
        catch (JsonException) { return (status, null); }
    }
}

/// <summary>
/// <see cref="ISharedAppFeedProbe"/>: Packmoat's own identity, as the proxy has it for a shared-app tenant (the active shared app's
/// certificate, a token requested from the customer's directory, sent as the Basic password), GETting feed indexes. Its own named client
/// (<see cref="ClientName"/>): <see cref="SharedAppProbeHostsHandler"/> in front, redirects never followed, no request logging. A URL that
/// is not built from GUIDs is never sent (its status is null).
/// </summary>
public sealed class SharedAppFeedProbe : ISharedAppFeedProbe
{
    public const string ClientName = "packmoat-shared-app-probe";

    private readonly Func<HttpClient> _client;
    private readonly Func<ISharedAppCredentialStore?> _store;
    private readonly Func<AppSignIn, TokenCredential> _createCredential;

    /// <param name="createCredential">Builds the Entra credential for the sign-in. Tests replace it so no network is needed.</param>
    public SharedAppFeedProbe(Func<HttpClient> client, Func<ISharedAppCredentialStore?> store, Func<AppSignIn, TokenCredential>? createCredential = null)
    {
        _client = client;
        _store = store;
        _createCredential = createCredential ?? EntraCertificateCredentialSource.CreateEntraCredential;
    }

    public async Task<IReadOnlyList<int?>> ProbeAsync(string directoryId, IReadOnlyList<string> indexUrls, CancellationToken ct)
    {
        var directory = CustomerSignIn.CanonicalDirectory(directoryId);
        if (directory is null || Guid.Parse(directory) == CustomerSignIn.ConsumerDirectory) throw new ArgumentException("Not a customer directory.", nameof(directoryId));
        var store = _store() ?? throw new InvalidOperationException("No shared app credential store is configured.");
        var shared = await store.GetAsync(SharedAppSlot.Active, ct) ?? throw new InvalidOperationException("No active shared app credential is set.");
        var credential = _createCredential(new AppSignIn(directory, shared.ClientId, shared.CertificatePem));
        var token = await credential.GetTokenAsync(new TokenRequestContext(new[] { EntraCertificateCredentialSource.AzureDevOpsScope }), ct);
        var authorization = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("packmoat:" + token.Token));
        var http = _client();
        var statuses = new int?[indexUrls.Count];
        // At most MaxConcurrency at a time, each status in its URL's place.
        await Parallel.ForEachAsync(Enumerable.Range(0, indexUrls.Count), new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = ct }, async (k, token) =>
        {
            var url = indexUrls[k];
            if (!GuidIndexUrls.IsGuidIndexUrl(url)) return;   // never sent: null
            try
            {
                using var response = await SendAsync(http, url, authorization, token);
                statuses[k] = (int)response.StatusCode;
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { }   // this call's own timeout: no answer (null)
            catch (HttpRequestException) { }
        });
        return statuses;
    }

    /// <summary>At most this many index probes at a time.</summary>
    public const int MaxConcurrency = DelegatedFeedCheck.MaxConcurrency;

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, string url, string authorization, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            var response = await http.SendAsync(request, ct);
            if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) || attempt >= AzureSetupRules.MaxRetries) return response;
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
            wait = wait < TimeSpan.Zero ? TimeSpan.Zero : wait > AzureSetupRules.RetryWaitCap ? AzureSetupRules.RetryWaitCap : wait;
            response.Dispose();
            await Task.Delay(wait, ct);
        }
    }
}

/// <summary>
/// <see cref="IDeveloperFeedProbe"/> (#64 I2). (a) Packmoat's identity GETs the index (<see cref="ISharedAppFeedProbe"/>): 200 needed.
/// (b) The developer's token lists the organisation's feeds (<c>feeds.dev.azure.com/{org}/_apis/packaging/feeds</c>): 200 with a feed
/// list needed, and the public-only feed in it is a FAIL. (c) The developer's token GETs the GUID index, redirects never followed: the
/// verdict is <see cref="DeveloperProbeVerdict"/>. (d) The bypass advisory from (b)'s list. The token lives only in this call's locals and
/// goes through the named client <see cref="ClientName"/> (<see cref="DeveloperProbeHostsHandler"/> in front, no request logging).
/// </summary>
public sealed class DeveloperFeedProbe : IDeveloperFeedProbe
{
    public const string ClientName = "packmoat-azure-devops-developer";

    private readonly Func<HttpClient> _client;
    private readonly ISharedAppFeedProbe _shared;

    public DeveloperFeedProbe(Func<HttpClient> client, ISharedAppFeedProbe shared)
    {
        _client = client;
        _shared = shared;
    }

    /// <summary>The site's result: the verdict's status, with the site's own sentence for the finding (#119; the CLI keeps the verdict's text).</summary>
    private static DeveloperProbeRun Run(DeveloperProbeDecision d, CheckResult bypass) =>
        new(new[] { new CheckResult(DeveloperProbeVerdict.Id, DeveloperProbeVerdict.Title, d.Status, DeveloperProbeVerdict.SiteDetail(d.Finding)), bypass }, d.Finding);

    /// <summary>No clear answer (a feed address Packmoat's setup does not build, a timeout, Azure DevOps unreachable): not verified.</summary>
    private static DeveloperProbeRun Unclear() => Run(new DeveloperProbeDecision(DeveloperFinding.Unclear, CheckStatus.Skip, DeveloperProbeVerdict.SiteUnclear), BypassAdvisory.NotChecked());

    public async Task<DeveloperProbeRun> ProbeAsync(DeveloperProbeRequest request, string developerToken, CancellationToken ct)
    {
        if (GuidIndexUrls.Parse(request.FeedIndexUrl) is not { } feed)
            return Unclear();   // the feed's address isn't one Packmoat's setup builds

        // (a) Packmoat's own identity reads the feed: otherwise a refusal below proves nothing. The developer's token is not used yet.
        int? control;
        try { control = (await _shared.ProbeAsync(request.DirectoryId, new[] { request.FeedIndexUrl }, ct)).Single(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { control = null; }
        if (DeveloperProbeVerdict.ControlFailure(control) is { } failure)
            return Run(DeveloperProbeVerdict.ControlNotMet(failure), BypassAdvisory.NotChecked());

        var http = _client();
        try
        {
            // (b) The developer lists the organisation's feeds (project-scoped ones included).
            var (status, value) = await FeedLists.GetValuesAsync(http, FeedLists.OrganizationFeedsUrl(feed.Organization), developerToken, ct);
            var feeds = value is null ? Array.Empty<ListedFeed>() : FeedLists.Parse(value);
            var bypass = value is null ? BypassAdvisory.NotChecked() : BypassAdvisory.Check(feeds.Select(f => f.Visible).ToList(), feed.FeedId);
            if (DeveloperProbeVerdict.ListControl(status, readable: value is not null, listsPublicFeed: feeds.Any(f => f.FeedId == feed.FeedId)) is { } refused)
                return Run(refused, bypass);

            // (c) The developer GETs the feed's index.
            using var index = await DelegatedFeedCheck.SendAsync(http, request.FeedIndexUrl, developerToken, ct);
            return Run(DeveloperProbeVerdict.Index((int)index.StatusCode), bypass);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Unclear();   // one call's own timeout: slowness proves nothing
        }
        catch (HttpRequestException)
        {
            return Unclear();   // Azure DevOps couldn't be reached
        }
    }
}
