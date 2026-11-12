using System.Net;
using System.Text.Json.Nodes;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure;
using Xunit.Abstractions;

namespace Packmoat.Host.Tests;

/// <summary>
/// Runs the onboarding check engine against a real Azure DevOps feed with a delegated (user) bearer token, as the setup
/// wizard will. Runs only when pointed at one; nothing identifying a real organisation lives in the repository:
///   PACKMOAT_LIVE_ADO_BEARER_FILE  a file (outside the repository, mode 600) holding an Azure DevOps access token, e.g. from
///                                  `az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798`
///   PACKMOAT_LIVE_ADO_FEED_INDEX   the feed's NuGet index, https://pkgs.dev.azure.com/{org}/{project}/_packaging/{feed}/nuget/v3/index.json
///   PACKMOAT_LIVE_ADO_SHARED_APP   optional: the shared app's client id; with PACKMOAT_LIVE_ADO_DIRECTORY, the 'identity' check runs
///   PACKMOAT_LIVE_ADO_DIRECTORY    optional: the directory (Entra tenant id) the feed's organisation and the app belong to
///   PACKMOAT_LIVE_ADO_CONFIRM_ADMINS optional: named feed administrators to confirm, separated by ';'
/// The token is read from the file and handed to the engine only; it is never printed or logged.
/// </summary>
public sealed class LiveDelegatedFactAttribute : FactAttribute
{
    public static readonly string[] Required = { "PACKMOAT_LIVE_ADO_BEARER_FILE", "PACKMOAT_LIVE_ADO_FEED_INDEX" };

    public LiveDelegatedFactAttribute()
    {
        var missing = Required.Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))).ToArray();
        if (missing.Length > 0) Skip = "Live delegated-check test skipped. Set: " + string.Join(", ", missing);
        else if (!File.Exists(Environment.GetEnvironmentVariable("PACKMOAT_LIVE_ADO_BEARER_FILE"))) Skip = "Live delegated-check test skipped: PACKMOAT_LIVE_ADO_BEARER_FILE not found.";
    }
}

public class LiveDelegatedCheckTests
{
    private readonly ITestOutputHelper _out;
    public LiveDelegatedCheckTests(ITestOutputHelper output) => _out = output;

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    /// <summary>{org}, {project or null}, {feed} from a pkgs.dev.azure.com feed index URL.</summary>
    private static (string Org, string? Project, string Feed) Parse(Uri index)
    {
        var s = index.AbsolutePath.Trim('/').Split('/');
        var p = Array.IndexOf(s, "_packaging");
        Assert.True(p is 1 or 2 && s.Length > p + 1, "PACKMOAT_LIVE_ADO_FEED_INDEX is not a feed index URL of the form {org}[/{project}]/_packaging/{feed}/nuget/v3/index.json");
        return (s[0], p == 2 ? s[1] : null, s[p + 1]);
    }

    [LiveDelegatedFact]
    public async Task A_delegated_token_passes_the_settings_checks_on_the_live_feed_and_the_index_prefixes_are_recorded()
    {
        var token = File.ReadAllText(Env("PACKMOAT_LIVE_ADO_BEARER_FILE")!).Trim();
        var index = new Uri(Env("PACKMOAT_LIVE_ADO_FEED_INDEX")!);
        var (org, project, feed) = Parse(index);
        var sharedApp = Env("PACKMOAT_LIVE_ADO_SHARED_APP");
        var directory = Env("PACKMOAT_LIVE_ADO_DIRECTORY");

        var o = new OnboardingOptions
        {
            Organization = org, Project = project, Feed = feed,
            PackagesBase = index.GetLeftPart(UriPartial.Authority),
            AdminToken = token, AdminTokenIsBearer = true,
            CheckProject = true,   // as the wizard runs it: the feed's project must be private
            SharedAppClientId = sharedApp is not null && directory is not null ? sharedApp : null,
            SharedAppDirectoryId = sharedApp is not null && directory is not null ? directory : null,
        };
        o.ConfirmedAdmins.AddRange((Env("PACKMOAT_LIVE_ADO_CONFIRM_ADMINS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var tenant = new Tenant("live-delegated", "Live delegated check", o.PackagesBase + "/", org + "/", index.ToString(), new TenantPolicy(14));

        // As the wizard will: the token goes only to Azure DevOps, and a redirect is never followed.
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        // Packmoat's own credential is never used here: no developer token and no private feed are given, so those checks skip first.
        var results = await new OnboardingChecks(http, new StaticBasicCredentialSource("unused", "unused"), tenant, o).RunWithoutProxyAsync(default);

        foreach (var r in results)
        {
            // Never Assert.DoesNotContain(token, ...): its failure message would print the token.
            Assert.False(r.Detail.Contains(token, StringComparison.Ordinal), $"{r.Id}: detail contains the admin token");
            _out.WriteLine($"[{OnboardingSummary.Label(r.Status)}] {r.Id}: {r.Detail}");
        }

        var expectPass = new List<string> { "feed", "upstreams", "permissions", "views" };
        if (o.SharedAppClientId is not null) expectPass.Insert(1, "identity");
        else _out.WriteLine("identity not run: set PACKMOAT_LIVE_ADO_SHARED_APP and PACKMOAT_LIVE_ADO_DIRECTORY.");
        if (results.Any(r => r.Id == "project")) expectPass.Add("project");   // the 'project' check arrives with the delegated check
        foreach (var id in expectPass)
        {
            var r = results.Single(x => x.Id == id);
            Assert.True(r.Status == CheckStatus.Pass, $"{id}: {OnboardingSummary.Label(r.Status)}: {r.Detail}");
        }

        await RecordIndexPrefixes(http, token, index, org, project, feed);
    }

    /// <summary>
    /// The prefix-tightening spike: does every resource in the live service index sit under {org}/{projectId}/_packaging/{feedId}/?
    /// Resource URLs carry no credential; only they, and the ids from Get Feed, are written out.
    /// </summary>
    private async Task RecordIndexPrefixes(HttpClient http, string token, Uri index, string org, string? project, string feed)
    {
        static HttpRequestMessage Get(string url, string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            return request;
        }

        var scope = project is null ? org : $"{org}/{project}";
        using var feedResponse = await http.SendAsync(Get($"https://feeds.dev.azure.com/{scope}/_apis/packaging/feeds/{feed}?api-version=7.1", token));
        Assert.Equal(HttpStatusCode.OK, feedResponse.StatusCode);
        var feedDoc = JsonNode.Parse(await feedResponse.Content.ReadAsStringAsync())!.AsObject();
        var feedId = feedDoc["id"]!.GetValue<string>();
        var projectId = feedDoc["project"]?["id"]?.GetValue<string>();
        _out.WriteLine($"Get Feed: id {feedId}, project id {projectId ?? "(organisation-scoped)"}");

        using var indexResponse = await http.SendAsync(Get(index.ToString(), token));
        Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);
        var resources = (JsonNode.Parse(await indexResponse.Content.ReadAsStringAsync())!["resources"] as JsonArray)!.OfType<JsonObject>()
            .Select(r => (Id: r["@id"]!.GetValue<string>(), Type: r["@type"]?.GetValue<string>() ?? "?")).ToList();

        var tight = $"https://pkgs.dev.azure.com/{org}/{(projectId is null ? "" : projectId + "/")}_packaging/{feedId}/";
        _out.WriteLine($"Tight prefix tested: {tight} (compared ignoring case)");
        foreach (var (id, type) in resources)
            _out.WriteLine($"{(id.StartsWith(tight, StringComparison.OrdinalIgnoreCase) ? "under" : "OUTSIDE")}  {type}  {id}");
        var outside = resources.Where(r => !r.Id.StartsWith(tight, StringComparison.OrdinalIgnoreCase)).ToList();
        _out.WriteLine(outside.Count == 0
            ? $"Every one of the {resources.Count} resources is under the tight prefix."
            : $"{outside.Count} of {resources.Count} resources are NOT under the tight prefix.");
        // Ids that are not http(s) URLs (urn:uuid:..., com.visualstudio.feeds...) are labels, not addresses a client fetches.
        var urlsOutside = outside.Where(r => r.Id.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || r.Id.StartsWith("http://", StringComparison.OrdinalIgnoreCase)).ToList();
        _out.WriteLine($"Of those, http(s) URLs outside the tight prefix: {(urlsOutside.Count == 0 ? "none" : string.Join(", ", urlsOutside.Select(r => $"{r.Type} {r.Id}")))}");
        Assert.NotEmpty(resources);
    }
}
