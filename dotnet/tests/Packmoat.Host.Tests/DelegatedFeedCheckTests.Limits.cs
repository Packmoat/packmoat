using System.Diagnostics;
using Packmoat.Application;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// The 'private' check's limits (#64 I2 review): its own time budget, bounded concurrency, the probe limit's edge, views with no
/// visibility, and the "url" field Azure DevOps returns, which is never followed nor used for the organisation.
/// </summary>
public partial class DelegatedFeedCheckTests
{
    private static async Task<(DelegatedCheckRun Run, FakeAzureDevOps Ado, TimeSpan Took)> Timed(Action<FakeAzureDevOps> arrange, TimeSpan privateDeadline)
    {
        var (check, ado) = Make(privateDeadline: privateDeadline);
        arrange(ado);
        var clock = Stopwatch.StartNew();
        var run = await check.CheckNewFeedAsync(GoodNames, Request(), FakeAzureDevOps.Token, default);
        return (run, ado, clock.Elapsed);
    }

    /// <summary>
    /// 50 other feeds on a slow Azure DevOps (200 ms a call): the feed list, 50 views listings and 51 probes one after another would
    /// take over 20 s; eight at a time they take about 3 s, well inside the 10 s budget given here (CI headroom).
    /// </summary>
    [Fact]
    public async Task Fifty_feeds_on_a_slow_azure_devops_finish_inside_the_private_budget_because_calls_run_eight_at_a_time()
    {
        var (run, ado, _) = await Timed(ado =>
        {
            for (var i = 1; i < PrivateFeedVerdict.MaxFeeds; i++) ado.OtherFeeds.Add(new FakeAzureDevOps.OtherFeed { Name = "f" + i });
            ado.Delay = TimeSpan.FromMilliseconds(200);
        }, privateDeadline: TimeSpan.FromSeconds(10));
        var r = R(run, "private");
        Assert.True(r.Status == CheckStatus.Pass, r.Detail);
        Assert.Equal(51, PackageHost(ado).Count);
    }

    [Fact]
    public async Task When_the_private_budget_runs_out_only_private_is_not_verified_and_the_other_results_stand()
    {
        var (run, _, took) = await Timed(ado => ado.PkgsDelay = TimeSpan.FromSeconds(10), privateDeadline: TimeSpan.FromMilliseconds(300));
        Assert.Equal(PrivateFeedVerdict.OutOfTime(SupportContact.None), R(run, "private"));
        foreach (var r in run.Results.Where(r => r.Id != "private")) Assert.True(r.Status == CheckStatus.Pass, $"{r.Id}: {r.Detail}");
        Assert.Equal(FeedCheckOutcome.Incomplete, AzureSetupRules.Outcome(run.Results));
        Assert.True(took < TimeSpan.FromSeconds(8), $"took {took}");   // the probes were cancelled, not waited for
    }

    [Fact]
    public async Task Exactly_a_hundred_probes_are_probed_and_one_more_is_too_many_with_nothing_probed()
    {
        static void Feeds(FakeAzureDevOps ado, int extraViews)
        {
            for (var i = 1; i < PrivateFeedVerdict.MaxFeeds; i++) ado.OtherFeeds.Add(new FakeAzureDevOps.OtherFeed { Name = "f" + i });
            foreach (var f in ado.OtherFeeds) f.Views.Add((Guid.NewGuid(), "Release", "organization", 403));   // 50 feeds + 50 views = 100
            for (var i = 0; i < extraViews; i++) ado.OtherFeeds[0].Views.Add((Guid.NewGuid(), "Extra" + i, "organization", 403));
        }
        var (hundred, ado, _) = await Private(ado => Feeds(ado, 0));
        Assert.Equal(CheckStatus.Pass, hundred.Status);
        Assert.Equal(PrivateFeedVerdict.MaxProbes + 1, PackageHost(ado).Count);   // the control and the 100

        var (more, ado2, _) = await Private(ado => Feeds(ado, 1));
        Assert.Equal(PrivateFeedVerdict.TooMany(50, 101, SupportContact.None), more);
        Assert.Empty(PackageHost(ado2));
    }

    [Theory]
    [InlineData(null)]          // Azure DevOps left the field out
    [InlineData("collection")]  // a value this build does not know
    [InlineData("")]
    public async Task A_view_whose_visibility_is_missing_or_unknown_is_probed_never_taken_as_private(string? visibility)
    {
        var view = Guid.NewGuid();
        var (r, ado, _) = await Private(ado => ado.OtherFeeds[0].Views.Add((view, "Release", visibility, 200)));
        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Equal("Packmoat feed access can read 1 other feed: team-packages@Release. " + PrivateFeedVerdict.FailHint, r.Detail);
        Assert.Contains(PackageHost(ado), s => s.Url.AbsoluteUri == Index(ado.OtherFeeds[0].Id, view: view));
    }

    /// <summary>Every listed feed and view carries a "url" into another organisation: every request stays on the checked one.</summary>
    [Fact]
    public async Task The_url_azure_devops_returns_is_never_followed_nor_used_for_the_organisation()
    {
        var (r, ado, run) = await Private(ado => ado.OtherFeeds[0].Views.Add((Guid.NewGuid(), "Release", "organization", 403)));
        Assert.Equal(CheckStatus.Pass, r.Status);
        Assert.Equal(FeedCheckOutcome.Passed, AzureSetupRules.Outcome(run.Results));
        AssertStaysOnContoso(ado);
    }

    internal static void AssertStaysOnContoso(FakeAzureDevOps ado)
    {
        Assert.NotEmpty(ado.Seen);
        foreach (var (url, _) in ado.Seen)
        {
            Assert.DoesNotContain("evil", url.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("contoso", url.AbsolutePath.Trim('/').Split('/')[0], ignoreCase: true);
        }
    }
}

/// <summary>The developer probe never uses the "url" Azure DevOps returns either (#64 I2 review).</summary>
public class DeveloperFeedProbeUrlTests
{
    [Fact]
    public async Task The_developer_probe_stays_on_the_feeds_organisation_whatever_url_the_list_returns()
    {
        var ado = new FakeAzureDevOps("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
        var probe = new DeveloperFeedProbe(() => new HttpClient(new DeveloperProbeHostsHandler { InnerHandler = ado }), DelegatedFeedCheckTests.SharedProbe(ado));
        var feed = $"https://pkgs.dev.azure.com/contoso/{FakeAzureDevOps.ProjectId:D}/_packaging/{FakeAzureDevOps.FeedId:D}/nuget/v3/index.json";
        var run = await probe.ProbeAsync(new DeveloperProbeRequest("dddddddd-dddd-4ddd-8ddd-dddddddddddd", feed), FakeAzureDevOps.DeveloperToken, default);
        Assert.Equal(DeveloperFinding.Refused, run.Finding);
        DelegatedFeedCheckTests.AssertStaysOnContoso(ado);
    }
}
