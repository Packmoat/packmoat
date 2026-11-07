using System.Text.Json.Nodes;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// The 'private' check (#64 I2) against a fake Azure DevOps: the Admin's token lists the organisation's feeds and their views; Packmoat's
/// OWN identity (never the Admin's token) is the control on the public-only feed and probes every other feed and non-private view, by
/// GUID URLs on the package host only.
/// </summary>
public partial class DelegatedFeedCheckTests
{
    private static FeedNames GoodNames => Names();

    private static async Task<(CheckResult Private, FakeAzureDevOps Ado, DelegatedCheckRun Run)> Private(Action<FakeAzureDevOps>? arrange = null, FakePackmoatCredential? credential = null)
    {
        var (check, ado) = Make(credential);
        arrange?.Invoke(ado);
        var run = await check.CheckNewFeedAsync(GoodNames, Request(), FakeAzureDevOps.Token, default);
        return (R(run, "private"), ado, run);
    }

    private static string Index(Guid feed, Guid? project = null, Guid? view = null) =>
        GuidIndexUrls.Build(FakeAzureDevOps.Org, project ?? FakeAzureDevOps.ProjectId, feed, view)!;

    private static List<(Uri Url, string? Authorization)> PackageHost(FakeAzureDevOps ado) => ado.Seen.Where(s => s.Url.Host == "pkgs.dev.azure.com").ToList();

    /// <summary>The real shared app probe, recording the URL lists it is asked for (in the order given: the probes themselves run in parallel).</summary>
    private sealed class RecordingProbe(ISharedAppFeedProbe inner) : ISharedAppFeedProbe
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();
        public Task<IReadOnlyList<int?>> ProbeAsync(string directoryId, IReadOnlyList<string> indexUrls, CancellationToken ct)
        {
            lock (Calls) Calls.Add(indexUrls.ToList());
            return inner.ProbeAsync(directoryId, indexUrls, ct);
        }
    }

    private static (DelegatedFeedCheck Check, FakeAzureDevOps Ado, RecordingProbe Probe) MakeRecording(FakePackmoatCredential? credential = null)
    {
        var ado = new FakeAzureDevOps(Dir);
        var probe = new RecordingProbe(SharedProbe(ado, credential));
        return (new DelegatedFeedCheck(() => new HttpClient(new AzureDevOpsHostsOnlyHandler { InnerHandler = ado }), probe), ado, probe);
    }

    [Fact]
    public async Task Private_passes_when_packmoats_identity_reads_the_feed_and_is_refused_on_every_other_feed()
    {
        var credential = new FakePackmoatCredential();
        var (check, ado, probe) = MakeRecording(credential);
        var run = await check.CheckNewFeedAsync(GoodNames, Request(), FakeAzureDevOps.Token, default);
        var r = R(run, "private");
        Assert.Equal((PrivateFeedVerdict.Title, CheckStatus.Pass), (r.Title, r.Status));
        Assert.Equal(string.Format(PrivateFeedVerdict.PassSentence, 1), r.Detail);
        Assert.Equal(FeedCheckOutcome.Passed, AzureSetupRules.Outcome(run.Results));
        // one probe call: the control first (its status is the verdict's control), then the other feed; the private view is not probed
        var urls = new[] { Index(FakeAzureDevOps.FeedId), Index(ado.OtherFeeds[0].Id) };
        Assert.Equal(urls, Assert.Single(probe.Calls));
        // what reached the package host, in whatever order the parallel probes arrived: GUID URLs, Packmoat's identity only
        Assert.Equal(urls.Order(StringComparer.Ordinal), PackageHost(ado).Select(s => s.Url.AbsoluteUri).Order(StringComparer.Ordinal));
        Assert.All(PackageHost(ado), s => Assert.Equal(FakeAzureDevOps.PackmoatAuthorization, s.Authorization));
        // the token was asked for in the person's directory, for the active shared app
        var signIn = Assert.Single(credential.SignIns);
        Assert.Equal((Dir, FakeAzureDevOps.SharedApp), (signIn.AuthorityTenantId, signIn.ClientId));
        // the Admin's token listed the feeds at organisation level and the other feed's views by GUIDs
        Assert.Contains(ado.Seen, s => s.Url.AbsoluteUri == "https://feeds.dev.azure.com/contoso/_apis/packaging/feeds?api-version=7.1" && s.Authorization == "Bearer " + FakeAzureDevOps.Token);
        Assert.Contains(ado.Seen, s => s.Url.AbsolutePath == $"/contoso/{FakeAzureDevOps.ProjectId:D}/_apis/packaging/feeds/{ado.OtherFeeds[0].Id:D}/views");
    }

    [Theory]
    [InlineData(200)]
    [InlineData(302)]
    public async Task Another_feed_that_packmoats_identity_can_read_fails_with_its_name(int code)
    {
        var (r, _, run) = await Private(ado =>
        {
            ado.OtherFeeds[0].PackmoatStatus = code;
            ado.OtherFeeds.Add(new FakeAzureDevOps.OtherFeed { Name = "finance", ProjectId = null });   // organisation-scoped: refused
        });
        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Equal("Packmoat feed access can read 1 other feed: team-packages. " + PrivateFeedVerdict.FailHint, r.Detail);
        Assert.Equal(FeedCheckOutcome.Failed, AzureSetupRules.Outcome(run.Results));
    }

    /// <summary>The I0 spike's real case: a feed whose Local view is shared with the organisation. The view is probed by its GUID.</summary>
    [Fact]
    public async Task A_view_shared_with_the_organisation_is_probed_and_fails_when_packmoat_can_read_it()
    {
        var view = Guid.NewGuid();
        var (r, ado, _) = await Private(ado => ado.OtherFeeds[0].Views.Add((view, "Release", "organization", 200)));
        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Equal("Packmoat feed access can read 1 other feed: team-packages@Release. " + PrivateFeedVerdict.FailHint, r.Detail);
        Assert.Contains(PackageHost(ado), s => s.Url.AbsoluteUri == Index(ado.OtherFeeds[0].Id, view: view));

        var (refused, _, _) = await Private(ado => ado.OtherFeeds[0].Views.Add((Guid.NewGuid(), "Release", "Organization", 401)));
        Assert.Equal(CheckStatus.Pass, refused.Status);
    }

    [Fact]
    public async Task A_404_or_no_answer_on_another_feed_is_not_verified()
    {
        var (r, _, run) = await Private(ado => ado.OtherFeeds[0].PackmoatStatus = 404);
        Assert.Equal(CheckStatus.Skip, r.Status);
        Assert.Equal("NOT VERIFIED: Azure DevOps gave an answer that doesn't prove a refusal for team-packages. Check again in a few minutes.", r.Detail);   // the status is logged, not shown (#119)
        Assert.Equal(FeedCheckOutcome.Incomplete, AzureSetupRules.Outcome(run.Results));
    }

    [Fact]
    public async Task Without_the_control_or_the_identity_a_refusal_proves_nothing()
    {
        var (control, _, _) = await Private(ado => ado.PackmoatIndexStatus = 401);
        Assert.Equal(CheckStatus.Skip, control.Status);
        Assert.Equal("NOT VERIFIED: Packmoat feed access couldn't read Packmoat's feed, so a refusal elsewhere would prove nothing. Check that it has the Collaborator role on that feed.", control.Detail);

        var (identity, _, _) = await Private(ado => ado.Directory = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee");
        Assert.Equal(CheckStatus.Skip, identity.Status);
        Assert.Contains("identity check", identity.Detail);

        var (noToken, ado, _) = await Private(credential: new FakePackmoatCredential { Fail = true });
        Assert.Equal(CheckStatus.Skip, noToken.Status);
        Assert.Empty(PackageHost(ado));
    }

    [Fact]
    public async Task More_than_fifty_feeds_or_a_hundred_probes_is_not_verified_and_nothing_is_probed()
    {
        var (feeds, ado, _) = await Private(ado =>
        {
            for (var i = 0; i < PrivateFeedVerdict.MaxFeeds; i++) ado.OtherFeeds.Add(new FakeAzureDevOps.OtherFeed { Name = "f" + i });
        });
        Assert.Equal(CheckStatus.Skip, feeds.Status);
        Assert.Contains("51 other feeds", feeds.Detail);
        Assert.Empty(PackageHost(ado));

        var (probes, ado2, _) = await Private(ado =>
        {
            ado.OtherFeeds.Clear();
            for (var i = 0; i < 34; i++)
            {
                var f = new FakeAzureDevOps.OtherFeed { Name = "f" + i };
                f.Views.Add((Guid.NewGuid(), "Release", "organization", 403));
                f.Views.Add((Guid.NewGuid(), "Prerelease", "organization", 403));
                ado.OtherFeeds.Add(f);
            }
        });
        Assert.Equal(CheckStatus.Skip, probes.Status);
        Assert.Contains("102 feeds and views", probes.Detail);
        Assert.Empty(PackageHost(ado2));

        var (fifty, _, _) = await Private(ado =>
        {
            ado.OtherFeeds.Clear();
            for (var i = 0; i < PrivateFeedVerdict.MaxFeeds; i++) ado.OtherFeeds.Add(new FakeAzureDevOps.OtherFeed { Name = "f" + i });
        });
        Assert.Equal(CheckStatus.Pass, fifty.Status);   // the edge counts
    }

    [Fact]
    public async Task Lists_that_cannot_be_read_are_not_verified()
    {
        var (list, _, _) = await Private(ado => ado.AdminListStatus = 403);
        Assert.Equal(CheckStatus.Skip, list.Status);
        Assert.Equal("NOT VERIFIED: your organisation's feeds couldn't be listed, so Packmoat's access to them wasn't tested.", list.Detail);

        var (views, _, _) = await Private(ado => ado.OtherFeeds[0].ViewsStatus = 401);
        Assert.Equal(CheckStatus.Skip, views.Status);
        Assert.Contains("team-packages (its views couldn't be listed)", views.Detail);
        Assert.DoesNotContain("HTTP", views.Detail);
    }

    [Fact]
    public async Task Without_packmoats_identity_private_is_not_verified_and_the_settings_check_is_incomplete()
    {
        var ado = new FakeAzureDevOps(Dir);
        var check = new DelegatedFeedCheck(() => new HttpClient(new AzureDevOpsHostsOnlyHandler { InnerHandler = ado }));
        var run = await check.CheckNewFeedAsync(GoodNames, Request(), FakeAzureDevOps.Token, default);
        Assert.Equal(CheckStatus.Skip, R(run, "private").Status);
        Assert.Equal(FeedCheckOutcome.Incomplete, AzureSetupRules.Outcome(run.Results));
    }

    [Fact]
    public async Task A_stored_feed_named_by_name_is_found_in_the_list_and_its_control_uses_guids()
    {
        var (check, ado, probe) = MakeRecording();
        var url = $"https://pkgs.dev.azure.com/Contoso/{FakeAzureDevOps.ProjectId:D}/_packaging/{FakeAzureDevOps.FeedName}/nuget/v3/index.json";
        var run = await check.CheckStoredFeedAsync(new Tenant("e2e-shared", "E2E shared", "https://pkgs.dev.azure.com/", "Contoso/", url, new TenantPolicy(14)), Request(), FakeAzureDevOps.Token, default);
        Assert.Equal(CheckStatus.Pass, R(run, "private").Status);
        Assert.Equal(Index(FakeAzureDevOps.FeedId), Assert.Single(probe.Calls)[0]);   // the control, built from GUIDs
        Assert.Contains(PackageHost(ado), s => s.Url.AbsoluteUri == Index(FakeAzureDevOps.FeedId));
    }

    [Theory]
    [InlineData("https://pkgs.dev.azure.com/contoso/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/nuget/v3/index.json", true)]
    [InlineData("https://pkgs.dev.azure.com/contoso/7a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f@11111111-1111-4111-8111-111111111111/nuget/v3/index.json", true)]
    [InlineData("https://pkgs.dev.azure.com/contoso/_packaging/public-packages/nuget/v3/index.json", false)]   // by name: never
    [InlineData("https://pkgs.dev.azure.com/contoso/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/nuget/v3/index.json?x=1", false)]
    [InlineData("https://pkgs.dev.azure.com/contoso/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/nuget/v3/flat2/x/index.json", false)]
    [InlineData("https://feeds.dev.azure.com/contoso/_apis/packaging/feeds", false)]
    [InlineData("https://vssps.dev.azure.com/contoso/_apis/graph/serviceprincipals", false)]
    [InlineData("http://pkgs.dev.azure.com/contoso/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/nuget/v3/index.json", false)]
    [InlineData("https://pkgs.dev.azure.com:8443/contoso/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/nuget/v3/index.json", false)]
    [InlineData("https://evil.example/contoso/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/nuget/v3/index.json", false)]
    public async Task Packmoats_identity_goes_only_to_guid_feed_indexes_on_the_package_host(string url, bool allowed)
    {
        var ado = new FakeAzureDevOps(Dir);
        using var http = new HttpClient(new SharedAppProbeHostsHandler { InnerHandler = ado });
        Assert.Equal(allowed, SharedAppProbeHostsHandler.Allowed(new Uri(url)));
        if (allowed) await http.GetAsync(url);
        else Assert.DoesNotContain("evil", (await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(url))).Message);
        Assert.Equal(allowed ? 1 : 0, ado.Seen.Count);
        // and the probe never even sends a URL that is not built from GUIDs
        var statuses = await SharedProbe(ado).ProbeAsync(Dir, new[] { url }, default);
        Assert.Equal(allowed ? 2 : 0, ado.Seen.Count);
        if (!allowed) Assert.Null(Assert.Single(statuses));
    }
}

/// <summary>
/// The developer probe (#64 I2) against a fake Azure DevOps: (a) Packmoat's identity reads the index, (b) the developer lists the
/// organisation's feeds, (c) the developer GETs the GUID index (401/403 PASS, 200 FAIL, anything else NOT VERIFIED), (d) the bypass
/// advisory. The developer's token goes only to feeds.dev.azure.com and the exact index URL.
/// </summary>
public partial class DeveloperFeedProbeTests
{
    private const string Dir = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
    private static readonly string Feed = $"https://pkgs.dev.azure.com/contoso/{FakeAzureDevOps.ProjectId:D}/_packaging/{FakeAzureDevOps.FeedId:D}/nuget/v3/index.json";
    private const string ListUrl = "https://feeds.dev.azure.com/contoso/_apis/packaging/feeds?api-version=7.1";

    private static async Task<(DeveloperProbeRun Run, FakeAzureDevOps Ado)> Probe(Action<FakeAzureDevOps>? arrange = null, FakePackmoatCredential? credential = null, string? feed = null)
    {
        var ado = new FakeAzureDevOps(Dir);
        arrange?.Invoke(ado);
        var probe = new DeveloperFeedProbe(() => new HttpClient(new DeveloperProbeHostsHandler { InnerHandler = ado }), DelegatedFeedCheckTests.SharedProbe(ado, credential));
        return (await probe.ProbeAsync(new DeveloperProbeRequest(Dir, feed ?? Feed), FakeAzureDevOps.DeveloperToken, default), ado);
    }

    private static CheckResult Developer(DeveloperProbeRun run) => run.Results.Single(r => r.Id == "developer");
    private static CheckResult Bypass(DeveloperProbeRun run) => run.Results.Single(r => r.Id == "bypass");
    private static List<string> DeveloperRequests(FakeAzureDevOps ado) =>
        ado.Seen.Where(s => s.Authorization == "Bearer " + FakeAzureDevOps.DeveloperToken).Select(s => s.Url.AbsoluteUri).ToList();

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A_refused_developer_passes_after_both_controls(int code)
    {
        var (run, ado) = await Probe(ado => ado.DeveloperIndexStatus = code);
        Assert.Equal((CheckStatus.Pass, DeveloperFinding.Refused), (Developer(run).Status, run.Finding));
        Assert.Equal(DeveloperProbeVerdict.Title, Developer(run).Title);
        Assert.Equal(new[] { "developer", "bypass" }, run.Results.Select(r => r.Id));
        Assert.Equal(CheckStatus.Pass, Bypass(run).Status);
        // (a) Packmoat's identity first, then (b) and (c) with the developer's token, and nothing else with it
        var first = ado.Seen.First();
        Assert.Equal((Feed, FakeAzureDevOps.PackmoatAuthorization), (first.Url.AbsoluteUri, first.Authorization));
        Assert.Equal(new[] { ListUrl, Feed }, DeveloperRequests(ado));
        Assert.Equal(3, ado.Seen.Count);
        Assert.All(run.Results, r => Assert.False(r.Detail.Contains(FakeAzureDevOps.DeveloperToken, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_developer_who_can_read_the_feed_fails()
    {
        var (run, _) = await Probe(ado => ado.DeveloperIndexStatus = 200);
        Assert.Equal((CheckStatus.Fail, DeveloperFinding.CanRead), (Developer(run).Status, run.Finding));
        Assert.Equal(FeedCheckOutcome.Failed, DeveloperProbeVerdict.Outcome(run.Results));
    }

    [Fact]
    public async Task A_developer_whose_own_list_shows_the_public_only_feed_fails_without_reading_it()
    {
        var (run, ado) = await Probe(ado => ado.DeveloperSeesPublicFeed = true);
        Assert.Equal((CheckStatus.Fail, DeveloperFinding.CanRead), (Developer(run).Status, run.Finding));
        Assert.Equal(new[] { ListUrl }, DeveloperRequests(ado));
    }

    [Theory]
    [InlineData(404)]   // the I0 decision: NOT VERIFIED until it is seen live
    [InlineData(203)]
    [InlineData(302)]
    [InlineData(500)]
    public async Task Anything_but_401_403_or_200_on_the_index_is_not_verified(int code)
    {
        var (run, _) = await Probe(ado => ado.DeveloperIndexStatus = code);
        Assert.Equal((CheckStatus.Skip, DeveloperFinding.Unclear), (Developer(run).Status, run.Finding));
        Assert.Equal(FeedCheckOutcome.Incomplete, DeveloperProbeVerdict.Outcome(run.Results));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task Without_control_a_the_developers_token_is_never_used(int packmoat)
    {
        var (run, ado) = await Probe(ado => ado.PackmoatIndexStatus = packmoat);
        Assert.Equal((CheckStatus.Skip, DeveloperFinding.ControlFailed), (Developer(run).Status, run.Finding));
        Assert.Equal(DeveloperProbeVerdict.SiteControlFailed, Developer(run).Detail);
        Assert.Empty(DeveloperRequests(ado));
        Assert.Equal(CheckStatus.Skip, Bypass(run).Status);

        var (noToken, ado2) = await Probe(credential: new FakePackmoatCredential { Fail = true });
        Assert.Equal(DeveloperFinding.ControlFailed, noToken.Finding);
        Assert.Empty(DeveloperRequests(ado2));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(203)]
    [InlineData(404)]
    public async Task Without_control_b_a_refusal_proves_nothing_and_the_index_is_not_asked(int list)
    {
        var (run, ado) = await Probe(ado => ado.DeveloperListStatus = list);
        Assert.Equal((CheckStatus.Skip, DeveloperFinding.NotMember), (Developer(run).Status, run.Finding));
        Assert.Equal(new[] { ListUrl }, DeveloperRequests(ado));
        Assert.StartsWith("Bypass not checked", Bypass(run).Detail);
    }

    [Fact]
    public async Task Another_feed_with_a_public_nuget_upstream_or_one_to_packmoats_feed_is_a_bypass_warning()
    {
        var (run, _) = await Probe(ado =>
        {
            ado.OtherFeeds[0].Upstreams = new JsonArray(new JsonObject { ["protocol"] = "NuGet", ["upstreamSourceType"] = "public", ["location"] = "https://api.nuget.org/v3/index.json" });
            ado.OtherFeeds.Add(new FakeAzureDevOps.OtherFeed
            {
                Name = "chained",
                Upstreams = new JsonArray(new JsonObject { ["protocol"] = "nuget", ["upstreamSourceType"] = "internal", ["internalUpstreamFeedId"] = FakeAzureDevOps.FeedId.ToString("D") }),
            });
            ado.OtherFeeds.Add(new FakeAzureDevOps.OtherFeed { Name = "java", Upstreams = new JsonArray(new JsonObject { ["protocol"] = "Maven", ["upstreamSourceType"] = "public" }) });
        });
        Assert.Equal(CheckStatus.Pass, Developer(run).Status);   // advisory only: the proof still passes
        Assert.Equal((CheckStatus.Warn, string.Format(BypassAdvisory.FoundSentence, "team-packages, chained")), (Bypass(run).Status, Bypass(run).Detail));
        Assert.Equal(FeedCheckOutcome.Passed, DeveloperProbeVerdict.Outcome(run.Results));
    }

    [Fact]
    public async Task Upstream_sources_the_developer_cannot_see_are_reported_as_bypass_not_checked()
    {
        var (run, _) = await Probe(ado => ado.DeveloperSeesUpstreams = false);
        Assert.Equal(CheckStatus.Pass, Developer(run).Status);
        Assert.Equal(CheckStatus.Skip, Bypass(run).Status);
        Assert.Equal(string.Format(BypassAdvisory.NotCheckedSentence, 1), Bypass(run).Detail);
    }

    [Fact]
    public async Task A_call_that_times_out_is_not_verified_and_a_feed_url_not_built_from_guids_is_never_probed()
    {
        var (slow, _) = await Probe(ado => ado.TimeoutOn = "/_apis/packaging/feeds");
        Assert.Equal((CheckStatus.Skip, DeveloperProbeVerdict.SiteUnclear), (Developer(slow).Status, Developer(slow).Detail));

        var (byName, ado) = await Probe(feed: "https://pkgs.dev.azure.com/contoso/_packaging/public-packages/nuget/v3/index.json");
        Assert.Equal(DeveloperFinding.Unclear, byName.Finding);
        Assert.Empty(ado.Seen);
    }

    [Theory]
    [InlineData("https://feeds.dev.azure.com/contoso/_apis/packaging/feeds?api-version=7.1", true)]
    [InlineData("https://pkgs.dev.azure.com/contoso/7a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/nuget/v3/index.json", true)]
    [InlineData("https://pkgs.dev.azure.com/contoso/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/nuget/v3/index.json?x", false)]
    [InlineData("https://pkgs.dev.azure.com/contoso/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/nuget/v3/flat2/x/index.json", false)]
    [InlineData("https://pkgs.dev.azure.com/contoso/_packaging/public-packages/nuget/v3/index.json", false)]
    [InlineData("https://pkgs.dev.azure.com/contoso/", false)]
    [InlineData("https://vssps.dev.azure.com/contoso/_apis/graph/users", false)]
    [InlineData("https://dev.azure.com/contoso/_apis/projects", false)]
    [InlineData("http://feeds.dev.azure.com/contoso/_apis/packaging/feeds", false)]
    [InlineData("https://feeds.dev.azure.com:8443/contoso/_apis/packaging/feeds", false)]
    [InlineData("https://feeds.dev.azure.com.evil.example/contoso", false)]
    [InlineData("https://user@feeds.dev.azure.com/contoso", false)]
    [InlineData("https://login.microsoftonline.com/x", false)]
    public async Task The_developers_token_goes_only_to_feeds_and_the_exact_guid_index(string url, bool allowed)
    {
        var ado = new FakeAzureDevOps(Dir);
        using var http = new HttpClient(new DeveloperProbeHostsHandler { InnerHandler = ado });
        Assert.Equal(allowed, DeveloperProbeHostsHandler.Allowed(new Uri(url)));
        if (allowed) await http.GetAsync(url);
        else Assert.DoesNotContain("evil", (await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(url))).Message);
        Assert.Equal(allowed ? 1 : 0, ado.Seen.Count);
    }
}
