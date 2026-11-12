using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// The delegated sign-in check against a fake Azure DevOps: Get Feed resolves the typed names to GUIDs (its "url" ignored, the
/// name checked), the URLs are built from the GUIDs under the tight prefix, and the settings checks run with the person's bearer token,
/// which goes only to Azure DevOps' API hosts. The 'private' check lists feeds and views with that token, then probes them with
/// Packmoat's own identity, which goes only to the package host.
/// </summary>
public partial class DelegatedFeedCheckTests
{
    private const string Dir = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";

    /// <summary>Packmoat's identity as production builds it: the real probe, its hosts-only handler outside the transport (here the fake).</summary>
    internal static SharedAppFeedProbe SharedProbe(FakeAzureDevOps ado, FakePackmoatCredential? credential = null, OneSharedCredential? store = null) =>
        new(() => new HttpClient(new SharedAppProbeHostsHandler { InnerHandler = ado }), () => store ?? new OneSharedCredential(), (credential ?? new FakePackmoatCredential()).Factory);

    private static (DelegatedFeedCheck Check, FakeAzureDevOps Ado) Make(FakePackmoatCredential? credential = null, TimeSpan? privateDeadline = null)
    {
        var ado = new FakeAzureDevOps(Dir);
        // As production: the hosts-only handler outside the transport (here the fake).
        return (new DelegatedFeedCheck(() => new HttpClient(new AzureDevOpsHostsOnlyHandler { InnerHandler = ado }), SharedProbe(ado, credential), privateDeadline), ado);
    }

    private static DelegatedCheckRequest Request(params string[] confirmed) => new(Dir, FakeAzureDevOps.SharedApp, confirmed);
    private static FeedNames Names(string? project = FakeAzureDevOps.ProjectName, string feed = FakeAzureDevOps.FeedName, string org = FakeAzureDevOps.Org) => new(org, project, feed);
    private static CheckResult R(DelegatedCheckRun run, string id) => run.Results.Single(r => r.Id == id);

    [Fact]
    public async Task A_good_project_feed_passes_every_settings_check_on_GUID_urls_under_the_tight_prefix()
    {
        var (check, ado) = Make();
        var run = await check.CheckNewFeedAsync(Names(), Request(), FakeAzureDevOps.Token, default);

        var expected = $"https://pkgs.dev.azure.com/contoso/{FakeAzureDevOps.ProjectId:D}/_packaging/{FakeAzureDevOps.FeedId:D}/nuget/v3/index.json";
        Assert.Equal(expected, run.FeedIndexUrl);
        Assert.Equal(new[] { "feed", "identity", "upstreams", "permissions", "views", "project", "private" }, run.Results.Select(r => r.Id));
        foreach (var r in run.Results) Assert.True(r.Status == CheckStatus.Pass, $"{r.Id}: {r.Status}: {r.Detail}");
        Assert.Equal(FeedCheckOutcome.Passed, AzureSetupRules.Outcome(run.Results));
        Assert.Equal("Packmoat feed access is in your organisation once.", R(run, "identity").Detail);   // found on the service principals' second page
        Assert.Equal("Packmoat feed access has the Collaborator role. No one else has a role on the feed. Built-in administrator groups that Azure DevOps adds to every feed: [contoso]\\Project Collection Administrators.", R(run, "permissions").Detail);   // recognised by the identities API
        Assert.Empty(run.NamedAdmins);
        // The response's own "url" is never followed; the person's token goes only to the three API hosts, never to the package host;
        // only Packmoat's own identity goes to the package host.
        Assert.DoesNotContain(ado.Seen, s => s.Url.Host.Contains("evil", StringComparison.Ordinal));
        Assert.All(ado.Seen.Where(s => s.Url.Host != "pkgs.dev.azure.com"), s => Assert.Contains(s.Url.Host, AzureDevOpsHostsOnlyHandler.Hosts));
        Assert.All(ado.Seen.Where(s => s.Url.Host != "pkgs.dev.azure.com"), s => Assert.Equal("Bearer " + FakeAzureDevOps.Token, s.Authorization));
        Assert.All(ado.Seen.Where(s => s.Url.Host == "pkgs.dev.azure.com"), s => Assert.Equal(FakeAzureDevOps.PackmoatAuthorization, s.Authorization));
        Assert.All(run.Results, r => Assert.False(r.Detail.Contains(FakeAzureDevOps.Token, StringComparison.Ordinal)));
        // After the resolve, every settings call uses the GUIDs, never the typed names.
        Assert.All(ado.Seen.Skip(1).Where(s => s.Url.Host == "feeds.dev.azure.com" && !s.Url.AbsolutePath.EndsWith("/_apis/packaging/feeds", StringComparison.Ordinal)
                && !ado.OtherFeeds.Any(f => s.Url.AbsolutePath.Contains(f.Id.ToString("D"), StringComparison.Ordinal))),
            s => Assert.Contains(FakeAzureDevOps.FeedId.ToString("D"), s.Url.AbsolutePath));
    }

    [Fact]
    public async Task The_project_is_escaped_once_for_the_resolve_call_only()
    {
        var (check, ado) = Make();
        await check.CheckNewFeedAsync(Names(), Request(), FakeAzureDevOps.Token, default);
        var resolve = ado.Seen.First().Url;
        Assert.Equal($"/Contoso/Platform%20Team/_apis/packaging/feeds/{FakeAzureDevOps.FeedName}", resolve.AbsolutePath);
        Assert.DoesNotContain("%25", resolve.OriginalString);   // not escaped twice
        Assert.DoesNotContain(ado.Seen.Skip(1), s => s.Url.OriginalString.Contains("Platform", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_organisation_scoped_feed_has_no_project_check_and_passes()
    {
        var (check, ado) = Make();
        ado.OrgScoped = true;
        var run = await check.CheckNewFeedAsync(Names(project: null), Request(), FakeAzureDevOps.Token, default);
        Assert.Equal($"https://pkgs.dev.azure.com/contoso/_packaging/{FakeAzureDevOps.FeedId:D}/nuget/v3/index.json", run.FeedIndexUrl);
        Assert.DoesNotContain(run.Results, r => r.Id == "project");
        Assert.Equal(FeedCheckOutcome.Passed, AzureSetupRules.Outcome(run.Results));
        Assert.DoesNotContain(ado.Seen, s => s.Url.Host == "dev.azure.com");
    }

    [Fact]
    public async Task A_feed_in_a_public_project_fails_the_project_check()
    {
        var (check, ado) = Make();
        ado.ProjectVisibility = "public";
        var run = await check.CheckNewFeedAsync(Names(), Request(), FakeAzureDevOps.Token, default);
        Assert.Equal(CheckStatus.Fail, R(run, "project").Status);
        Assert.Contains("public", R(run, "project").Detail);
        Assert.Equal(FeedCheckOutcome.Failed, AzureSetupRules.Outcome(run.Results));
    }

    [Fact]
    public async Task A_feed_name_typed_in_another_case_resolves_to_the_same_feed()
    {
        var (check, _) = Make();
        var run = await check.CheckNewFeedAsync(Names(feed: "Public-Packages", project: "platform team", org: "CONTOSO"), Request(), FakeAzureDevOps.Token, default);
        Assert.Equal(FeedCheckOutcome.Passed, AzureSetupRules.Outcome(run.Results));
        Assert.StartsWith("https://pkgs.dev.azure.com/contoso/", run.FeedIndexUrl);
    }

    [Theory]
    [InlineData("some-other-feed")]                                   // Azure DevOps resolved the name to a different feed
    [InlineData("0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f")]               // its id, not its name
    public async Task A_resolve_that_returns_a_different_feed_name_is_refused(string returned)
    {
        var (check, ado) = Make();
        ado.ReturnedFeedName = returned;
        var run = await check.CheckNewFeedAsync(Names(), Request(), FakeAzureDevOps.Token, default);
        var feed = Assert.Single(run.Results);
        Assert.Equal(("feed", CheckStatus.Fail), (feed.Id, feed.Status));
        Assert.Equal(DelegatedFeedCheck.Describe(FeedResolveProblem.NameMismatch), feed.Detail);
        Assert.Equal("", run.FeedIndexUrl);
        Assert.Single(ado.Seen);   // nothing else is asked
    }

    [Fact]
    public async Task A_feed_typed_by_its_id_is_refused_by_the_name_check()
    {
        var (check, ado) = Make();
        var run = await check.CheckNewFeedAsync(Names(feed: FakeAzureDevOps.FeedId.ToString("D")), Request(), FakeAzureDevOps.Token, default);
        Assert.Equal(DelegatedFeedCheck.Describe(FeedResolveProblem.NameMismatch), Assert.Single(run.Results).Detail);
        Assert.Single(ado.Seen);
    }

    [Fact]
    public async Task A_project_feed_named_without_its_project_and_the_reverse_are_refused()
    {
        var (check, ado) = Make();
        ado.ReturnedProjectName = "Another Project";
        var run = await check.CheckNewFeedAsync(Names(), Request(), FakeAzureDevOps.Token, default);
        Assert.Equal(DelegatedFeedCheck.Describe(FeedResolveProblem.ProjectMismatch), Assert.Single(run.Results).Detail);
    }

    [Theory]
    [InlineData(404, FeedResolveProblem.NotFound)]
    [InlineData(401, FeedResolveProblem.SignInRefused)]
    [InlineData(203, FeedResolveProblem.SignInPage)]   // Azure DevOps' HTML sign-in page: a 2xx that is not the feed
    [InlineData(302, FeedResolveProblem.SignInPage)]   // a redirect is never followed
    [InlineData(204, FeedResolveProblem.Unreadable)]   // any 2xx but 200 is not an answer
    public async Task A_resolve_that_is_not_exactly_200_json_is_refused_and_nothing_else_is_asked(int status, FeedResolveProblem problem)
    {
        var (check, ado) = Make();
        ado.GetFeedStatus = status;
        var run = await check.CheckNewFeedAsync(Names(), Request(), FakeAzureDevOps.Token, default);
        var feed = Assert.Single(run.Results);
        Assert.Equal(CheckStatus.Fail, feed.Status);
        Assert.Equal(DelegatedFeedCheck.Describe(problem), feed.Detail);
        Assert.DoesNotContain("Sign in to Azure DevOps", feed.Detail);   // the body is never echoed
        Assert.Single(ado.Seen);
        Assert.Equal(FeedCheckOutcome.Failed, AzureSetupRules.Outcome(run.Results));
    }

    /// <summary>The resolve call's own timeout (inside the check's deadline) is not verified, never a failure: slowness says nothing about the feed.</summary>
    [Fact]
    public async Task A_resolve_that_times_out_is_not_verified_rather_than_failed()
    {
        var (check, ado) = Make();
        ado.TimeoutOn = "/_apis/packaging/feeds/";
        var run = await check.CheckNewFeedAsync(Names(), Request(), FakeAzureDevOps.Token, default);
        var feed = Assert.Single(run.Results);
        Assert.Equal(("feed", CheckStatus.Skip, AzureSetupCommands.NoAnswerInTime), (feed.Id, feed.Status, feed.Detail));
        Assert.Equal(FeedCheckOutcome.Incomplete, AzureSetupRules.Outcome(run.Results));
        Assert.Equal("", run.FeedIndexUrl);
    }

    [Fact]   // mutation: the resolve's catch of TaskCanceledException without its "not the caller's token" filter
    public async Task A_cancel_during_the_resolve_is_a_cancel_not_a_timed_out_check()
    {
        var (check, ado) = Make();
        using var cts = new CancellationTokenSource();
        (ado.CancelOn, ado.CancelOnPath) = (cts, "/_apis/packaging/feeds/");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.CheckNewFeedAsync(Names(), Request(), FakeAzureDevOps.Token, cts.Token));
    }

    [Fact]
    public async Task A_wrong_token_is_a_refused_sign_in()
    {
        var (check, _) = Make();
        var run = await check.CheckNewFeedAsync(Names(), Request(), "not-the-token", default);
        Assert.Equal(DelegatedFeedCheck.Describe(FeedResolveProblem.SignInRefused), Assert.Single(run.Results).Detail);
    }

    [Fact]
    public async Task A_named_admin_on_the_second_permissions_page_is_a_warning_until_confirmed_by_descriptor()
    {
        var (check, ado) = Make();
        ado.NamedAdmin = true;
        var first = await check.CheckNewFeedAsync(Names(), Request(), FakeAzureDevOps.Token, default);
        Assert.Equal(CheckStatus.Warn, R(first, "permissions").Status);
        Assert.Equal(FeedCheckOutcome.Incomplete, AzureSetupRules.Outcome(first.Results));
        var ada = Assert.Single(first.NamedAdmins);
        Assert.Equal(("Ada Lovelace", FakeAzureDevOps.AdaDescriptor), (ada.Name, ada.Descriptor));

        var confirmed = await check.CheckNewFeedAsync(Names(), Request(ada.Descriptor), FakeAzureDevOps.Token, default);
        Assert.Equal(CheckStatus.Pass, R(confirmed, "permissions").Status);
        Assert.Equal(FeedCheckOutcome.Passed, AzureSetupRules.Outcome(confirmed.Results));
        // a confirmation of someone who is not an administrator is not a match either
        var wrong = await check.CheckNewFeedAsync(Names(), Request(ada.Descriptor, "Microsoft.IdentityModel.Claims.ClaimsIdentity;contoso.example\\eve@contoso.example"), FakeAzureDevOps.Token, default);
        Assert.Equal(CheckStatus.Warn, R(wrong, "permissions").Status);
    }

    [Fact]
    public async Task The_service_principal_from_another_directory_fails_identity()
    {
        var (check, ado) = Make();
        ado.Directory = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";
        var run = await check.CheckNewFeedAsync(Names(), Request(), FakeAzureDevOps.Token, default);
        Assert.Equal(CheckStatus.Fail, R(run, "identity").Status);
        Assert.Equal(FeedCheckOutcome.Failed, AzureSetupRules.Outcome(run.Results));
    }

    [Fact]
    public async Task A_stored_feed_is_checked_as_stored_and_an_unusable_one_fails()
    {
        var (check, _) = Make();
        var url = $"https://pkgs.dev.azure.com/Contoso/{FakeAzureDevOps.ProjectId:D}/_packaging/{FakeAzureDevOps.FeedName}/nuget/v3/index.json";
        var tenant = new Tenant("e2e-shared", "E2E shared", "https://pkgs.dev.azure.com/", "Contoso/", url, new TenantPolicy(14));
        var run = await check.CheckStoredFeedAsync(tenant, Request(), FakeAzureDevOps.Token, default);
        Assert.Equal(url, run.FeedIndexUrl);
        Assert.Equal(FeedCheckOutcome.Passed, AzureSetupRules.Outcome(run.Results));

        var bad = await check.CheckStoredFeedAsync(tenant with { UpstreamFeedIndexUrl = "https://evil.example/Contoso/_packaging/f/nuget/v3/index.json" }, Request(), FakeAzureDevOps.Token, default);
        Assert.Equal(CheckStatus.Fail, Assert.Single(bad.Results).Status);
    }

    [Theory]
    [InlineData("https://feeds.dev.azure.com/org/_apis/packaging/feeds/f", true)]
    [InlineData("https://vssps.dev.azure.com/org/_apis/graph/serviceprincipals", true)]
    [InlineData("https://dev.azure.com/org/_apis/projects/p", true)]
    [InlineData("https://FEEDS.dev.azure.com/org/x", true)]
    [InlineData("https://pkgs.dev.azure.com/org/_packaging/f/nuget/v3/index.json", false)]   // the package host never gets the token
    [InlineData("http://feeds.dev.azure.com/org/x", false)]
    [InlineData("https://feeds.dev.azure.com:8443/org/x", false)]
    [InlineData("https://feeds.dev.azure.com.evil.example/org/x", false)]
    [InlineData("https://evil.example/feeds.dev.azure.com", false)]
    [InlineData("https://user@feeds.dev.azure.com/org/x", false)]
    [InlineData("https://login.microsoftonline.com/x", false)]
    public async Task Only_the_three_Azure_DevOps_API_hosts_are_reachable(string url, bool allowed)
    {
        var ado = new FakeAzureDevOps(Dir);
        using var http = new HttpClient(new AzureDevOpsHostsOnlyHandler { InnerHandler = ado });
        Assert.Equal(allowed, AzureDevOpsHostsOnlyHandler.Allowed(new Uri(url)));
        if (allowed) await http.GetAsync(url);
        else
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(url));
            Assert.DoesNotContain("evil", ex.Message);   // the refusal never echoes the URL
        }
        Assert.Equal(allowed ? 1 : 0, ado.Seen.Count);
    }

    /// <summary>
    /// The tight prefix: the live index lists VssBaseUrl (https://pkgs.dev.azure.com/{org}/, metadata) outside
    /// {org}/{projectId}/_packaging/{feedId}/. The proxy never follows it: the rewriter keeps only PackageBaseAddress and
    /// RegistrationsBaseUrl, and the proxy builds no upstream URL outside the prefix. The live index writes the organisation in lower case.
    /// </summary>
    [Fact]
    public void The_proxy_drops_VssBaseUrl_and_serves_only_under_the_tight_prefix()
    {
        var feed = new ResolvedFeed("Contoso", FakeAzureDevOps.ProjectId, FakeAzureDevOps.FeedId);
        var under = $"https://pkgs.dev.azure.com/contoso/{FakeAzureDevOps.ProjectId:D}/_packaging/{FakeAzureDevOps.FeedId:D}/nuget/v3/";
        var index = new JsonObject
        {
            ["version"] = "3.0.0",
            ["resources"] = new JsonArray(
                new JsonObject { ["@id"] = under + "flat2/", ["@type"] = "PackageBaseAddress/3.0.0" },
                new JsonObject { ["@id"] = under + "registrations2-semver2/", ["@type"] = "RegistrationsBaseUrl/3.6.0" },
                new JsonObject { ["@id"] = "https://pkgs.dev.azure.com/contoso/", ["@type"] = "VssBaseUrl" }),
        }.ToJsonString();
        var rewritten = Packmoat.Protocols.NuGet.ServiceIndexRewriter.Rewrite(index, new Packmoat.Protocols.NuGet.UrlMap(ResolvedFeed.PackagesBase, "https://feeds.contoso.example/v3/u/"));
        Assert.DoesNotContain("VssBaseUrl", rewritten);
        Assert.DoesNotContain("pkgs.dev.azure.com", rewritten);

        var tenant = new Tenant("selfco", "Selfco", ResolvedFeed.PackagesBase, feed.PathPrefix, feed.FeedIndexUrl, new TenantPolicy(14));
        Assert.True(Packmoat.Host.Proxy.NuGetProxy.TryBuildUpstreamUrl(tenant, $"contoso/{FakeAzureDevOps.ProjectId:D}/_packaging/{FakeAzureDevOps.FeedId:D}/nuget/v3/flat2/x/index.json", out _));
        Assert.True(Packmoat.Host.Proxy.NuGetProxy.TryBuildUpstreamUrl(tenant, $"Contoso/{FakeAzureDevOps.ProjectId:D}/_packaging/{FakeAzureDevOps.FeedId:D}/nuget/v3/flat2/x/index.json", out _));
        Assert.False(Packmoat.Host.Proxy.NuGetProxy.TryBuildUpstreamUrl(tenant, "contoso/", out _));                                         // VssBaseUrl itself
        Assert.False(Packmoat.Host.Proxy.NuGetProxy.TryBuildUpstreamUrl(tenant, "contoso/_packaging/private/nuget/v3/index.json", out _));   // another feed the app might read
        Assert.False(Packmoat.Host.Proxy.NuGetProxy.TryBuildUpstreamUrl(tenant, $"contoso/{FakeAzureDevOps.ProjectId:D}/_packaging/{Guid.NewGuid():D}/nuget/v3/index.json", out _));
    }

    /// <summary>The production transport (a real socket) answers a redirect with the redirect itself: it never goes where it points.</summary>
    [Fact]
    public async Task The_production_transport_never_follows_a_redirect()
    {
        var b = WebApplication.CreateBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = b.Build();
        var hits = new ConcurrentQueue<string>();
        app.Run(ctx =>
        {
            hits.Enqueue(ctx.Request.Path + " " + ctx.Request.Headers.Authorization);
            if (ctx.Request.Path == "/start") { ctx.Response.StatusCode = 302; ctx.Response.Headers.Location = "/elsewhere"; }
            return Task.CompletedTask;
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("localhost", "127.0.0.1");

        using var http = new HttpClient(DelegatedFeedCheck.CreatePrimaryHandler());
        using var request = new HttpRequestMessage(HttpMethod.Get, address + "/start");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", FakeAzureDevOps.Token);
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(new[] { "/start Bearer " + FakeAzureDevOps.Token }, hits.ToArray());
        await app.StopAsync();
    }
}
