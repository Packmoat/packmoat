using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>A fake nuget.org: scripted answers per path, a request counter, and an optional gate to hold requests open.</summary>
public sealed class FakeNuGetOrg : HttpMessageHandler
{
    public const string Base = "https://nuget.test/reg/";
    public ConcurrentDictionary<string, Func<HttpResponseMessage>> Routes { get; } = new();
    public int Calls;
    public ConcurrentQueue<string> Urls { get; } = new();
    public TaskCompletionSource? Hold { get; set; }
    /// <summary>Per URL: how many of its next requests never answer (int.MaxValue: always). Honours cancellation, so nothing hangs.</summary>
    public ConcurrentDictionary<string, int> StallHeaders { get; } = new();
    /// <summary>Per URL: how many of its next requests send their headers and then never send a body byte.</summary>
    public ConcurrentDictionary<string, int> StallBody { get; } = new();
    public ConcurrentDictionary<string, int> CallsByUrl { get; } = new();
    /// <summary>Per URL: requests wait here, ignoring cancellation, until it is released.</summary>
    public ConcurrentDictionary<string, TaskCompletionSource> HoldUrl { get; } = new();

    private static bool Take(ConcurrentDictionary<string, int> counts, string url)
    {
        while (counts.TryGetValue(url, out var n) && n > 0)
            if (counts.TryUpdate(url, n == int.MaxValue ? n : n - 1, n)) return true;
        return false;
    }

    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();   // a real handler does not start a request that is already cancelled
        Interlocked.Increment(ref Calls);
        Urls.Enqueue(request.RequestUri!.ToString());
        CallsByUrl.AddOrUpdate(request.RequestUri!.ToString(), 1, (_, n) => n + 1);
        if (Hold is { } hold) await hold.Task;
        if (HoldUrl.TryGetValue(request.RequestUri!.ToString(), out var holdUrl)) await holdUrl.Task;
        if (Take(StallHeaders, request.RequestUri!.ToString())) await Task.Delay(Timeout.Infinite, cancellationToken);
        if (Take(StallBody, request.RequestUri!.ToString()))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SilentStream()) { Headers = { ContentType = new("application/json") } } };
        return Routes.TryGetValue(request.RequestUri!.ToString(), out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    public static HttpResponseMessage Json(JsonNode body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    public static JsonObject Leaf(string version, string published) =>
        new() { ["catalogEntry"] = new JsonObject { ["version"] = version, ["published"] = published } };

    public static JsonObject Index(params JsonObject[] leaves) =>
        new() { ["items"] = new JsonArray(new JsonObject { ["items"] = new JsonArray(leaves.Select(l => (JsonNode)l).ToArray()) }) };

    public void Serve(string id, JsonNode body) => Routes[Base + id + "/index.json"] = () => Json(body.DeepClone());
}

public class NuGetOrgDateSourceTests
{
    private static readonly string Old = "2025-01-01T00:00:00+00:00";

    private static (NuGetOrgPublishDateSource Source, FakeNuGetOrg Net, FakeClock Clock) Make(Action<NuGetOrgDateOptions>? tweak = null, Action<string>? log = null, FakeClock? clock = null)
    {
        var net = new FakeNuGetOrg();
        clock ??= new FakeClock();
        var options = new NuGetOrgDateOptions { RegistrationBase = FakeNuGetOrg.Base };
        tweak?.Invoke(options);
        return (new NuGetOrgPublishDateSource(new HttpClient(net), clock, options, log, log), net, clock);
    }

    /// <summary>Short timeouts so a stall costs a fraction of a second; the retry count is the shipped one unless tweaked.</summary>
    private static (NuGetOrgPublishDateSource Source, FakeNuGetOrg Net, FakeClock Clock) MakeFast(Action<NuGetOrgDateOptions>? tweak = null, Action<string>? log = null, FakeClock? clock = null) =>
        Make(o => { o.AttemptTimeout = TimeSpan.FromMilliseconds(300); o.RetryDelay = TimeSpan.FromMilliseconds(10); o.FetchBudget = TimeSpan.FromSeconds(8); tweak?.Invoke(o); }, log, clock);

    private static string Page(int n) => FakeNuGetOrg.Base + $"moq/page/{n}.json";
    private static readonly string IndexUrl = FakeNuGetOrg.Base + "moq/index.json";

    /// <summary>An index of link-only pages, each holding one version (1.0.0 for page 1, 2.0.0 for page 2, ...).</summary>
    private static void ServePages(FakeNuGetOrg net, int pages)
    {
        net.Serve("moq", new JsonObject { ["items"] = new JsonArray(Enumerable.Range(1, pages).Select(n => (JsonNode)new JsonObject { ["@id"] = Page(n) }).ToArray()) });
        for (var n = 1; n <= pages; n++)
        {
            var version = $"{n}.0.0";
            net.Routes[Page(n)] = () => FakeNuGetOrg.Json(new JsonObject { ["items"] = new JsonArray(FakeNuGetOrg.Leaf(version, Old)) });
        }
    }

    private static CancellationToken Patience() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    private static Task<IReadOnlyDictionary<string, DateTimeOffset>?> Get(NuGetOrgPublishDateSource s, string id = "moq", CancellationToken ct = default) =>
        s.GetPublishDatesAsync(Ecosystem.NuGet, id, ct);

    [Fact]
    public async Task Dates_are_returned_by_normalised_version_and_the_1900_placeholder_is_unknown()
    {
        var (s, net, _) = Make();
        net.Serve("moq", FakeNuGetOrg.Index(
            FakeNuGetOrg.Leaf("4.20.0", Old),
            FakeNuGetOrg.Leaf("4.2", "2024-06-01T00:00:00+00:00"),                 // spelled short
            FakeNuGetOrg.Leaf("3.0.0", "1900-01-01T00:00:00+00:00"),               // unlisted placeholder
            FakeNuGetOrg.Leaf("5.0.0-Beta+meta", "2026-01-01T00:00:00+00:00")));

        var dates = (await Get(s))!;

        Assert.Equal(DateTimeOffset.Parse(Old), dates["4.20.0"]);
        Assert.True(dates.ContainsKey("4.2.0"));
        Assert.False(dates.ContainsKey("3.0.0"));
        Assert.True(dates.ContainsKey("5.0.0-beta"));
    }

    [Fact]
    public async Task Link_only_registration_pages_are_fetched_and_merged()
    {
        var (s, net, _) = Make();
        net.Serve("moq", new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["@id"] = FakeNuGetOrg.Base + "moq/page/1.json" },
                new JsonObject { ["@id"] = FakeNuGetOrg.Base + "moq/page/2.json" }),
        });
        net.Routes[FakeNuGetOrg.Base + "moq/page/1.json"] = () => FakeNuGetOrg.Json(new JsonObject { ["items"] = new JsonArray(FakeNuGetOrg.Leaf("1.0.0", Old)) });
        net.Routes[FakeNuGetOrg.Base + "moq/page/2.json"] = () => FakeNuGetOrg.Json(new JsonObject { ["items"] = new JsonArray(FakeNuGetOrg.Leaf("2.0.0", Old)) });

        var dates = (await Get(s))!;

        Assert.Equal(new[] { "1.0.0", "2.0.0" }, dates.Keys.Order());
        Assert.Equal(3, net.Calls);
    }

    [Fact]
    public async Task An_answer_is_reused_while_fresh_and_asked_for_again_after_that()
    {
        var (s, net, clock) = Make();
        net.Serve("moq", FakeNuGetOrg.Index(FakeNuGetOrg.Leaf("1.0.0", Old)));

        await Get(s); await Get(s);
        Assert.Equal(1, net.Calls);

        clock.UtcNow += TimeSpan.FromMinutes(9);
        await Get(s);
        Assert.Equal(1, net.Calls);

        clock.UtcNow += TimeSpan.FromMinutes(2);
        await Get(s);
        Assert.Equal(2, net.Calls);
    }

    [Fact]
    public async Task A_package_nuget_org_does_not_know_is_empty_not_null_and_is_asked_about_again_soon()
    {
        var (s, net, clock) = Make();

        var first = await Get(s, "newpkg");
        Assert.NotNull(first);
        Assert.Empty(first!);
        await Get(s, "newpkg");
        Assert.Equal(1, net.Calls);                                        // the 404 is remembered briefly

        net.Serve("newpkg", FakeNuGetOrg.Index(FakeNuGetOrg.Leaf("1.0.0", Old)));  // it has since been published
        clock.UtcNow += TimeSpan.FromMinutes(6);                            // ... and the 404 is not remembered forever
        Assert.Single((await Get(s, "newpkg"))!);
    }

    [Fact]
    public async Task A_failure_with_nothing_cached_is_null_and_is_not_retried_until_the_backoff_passes()
    {
        var (s, net, clock) = Make(o => o.RetryDelay = TimeSpan.FromMilliseconds(10));
        net.Routes[FakeNuGetOrg.Base + "moq/index.json"] = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        Assert.Null(await Get(s));
        Assert.Null(await Get(s));
        Assert.Null(await Get(s));
        Assert.Equal(2, net.Calls);                                        // tried twice, then negative-cached

        net.Serve("moq", FakeNuGetOrg.Index(FakeNuGetOrg.Leaf("1.0.0", Old)));
        clock.UtcNow += TimeSpan.FromSeconds(31);
        Assert.NotNull(await Get(s));                                      // recovers once the backoff is over
        Assert.Equal(3, net.Calls);
    }

    [Fact]
    public async Task Stale_answers_are_used_during_an_outage_only_up_to_the_stale_limit()
    {
        var (s, net, clock) = Make();
        net.Serve("moq", FakeNuGetOrg.Index(FakeNuGetOrg.Leaf("1.0.0", Old)));
        Assert.NotNull(await Get(s));

        net.Routes[FakeNuGetOrg.Base + "moq/index.json"] = () => new HttpResponseMessage(HttpStatusCode.BadGateway);
        clock.UtcNow += TimeSpan.FromHours(3);
        Assert.Single((await Get(s))!);                                    // outage, but a 3-hour-old answer is acceptable

        clock.UtcNow += TimeSpan.FromHours(22);                             // now 25 hours old
        Assert.Null(await Get(s));                                         // too old: fail closed
    }

    [Fact]
    public async Task Concurrent_requests_for_one_package_share_a_single_fetch()
    {
        var (s, net, _) = Make();
        net.Serve("moq", FakeNuGetOrg.Index(FakeNuGetOrg.Leaf("1.0.0", Old)));
        net.Hold = new TaskCompletionSource();

        var calls = Enumerable.Range(0, 25).Select(_ => Get(s)).ToArray();
        await Task.Delay(100);
        net.Hold.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.Equal(1, net.Calls);
        Assert.All(results, r => Assert.Single(r!));
    }

    [Fact]
    public async Task One_caller_giving_up_does_not_cancel_the_fetch_the_others_wait_for()
    {
        var (s, net, _) = Make();
        net.Serve("moq", FakeNuGetOrg.Index(FakeNuGetOrg.Leaf("1.0.0", Old)));
        net.Hold = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();

        var impatient = Get(s, "moq", cts.Token);
        var patient = Get(s);
        await Task.Delay(50);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient.WaitAsync(TimeSpan.FromSeconds(10)));
        net.Hold.SetResult();

        Assert.Single((await patient.WaitAsync(TimeSpan.FromSeconds(10)))!);
        Assert.Equal(1, net.Calls);
    }

    [Fact]
    public async Task The_cache_never_grows_past_its_limit()
    {
        var (s, net, _) = Make(o => o.MaxEntries = 5);
        for (var i = 0; i < 40; i++)
        {
            net.Serve($"pkg{i}", FakeNuGetOrg.Index(FakeNuGetOrg.Leaf("1.0.0", Old)));
            await Get(s, $"pkg{i}");
        }
        Assert.True(s.CachedPackages <= 5, $"cache holds {s.CachedPackages} packages");
    }

    [Fact]
    public async Task A_response_bigger_than_the_cap_is_a_failure_not_an_out_of_memory()
    {
        var (s, net, _) = Make(o => o.MaxResponseBytes = 4096);
        net.Routes[FakeNuGetOrg.Base + "moq/index.json"] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("{\"items\":[],\"pad\":\"" + new string('x', 1_000_000) + "\"}"))),
        };

        Assert.Null(await Get(s));
    }

    [Fact]
    public async Task Garbage_from_nuget_org_is_a_failure_not_an_answer()
    {
        var (s, net, _) = Make();
        net.Routes[FakeNuGetOrg.Base + "moq/index.json"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>oops</html>") };
        Assert.Null(await Get(s));
    }

    [Fact]
    public async Task A_registration_page_that_stalls_once_is_tried_again_and_the_whole_map_comes_back()
    {
        var (s, net, _) = MakeFast();
        ServePages(net, 3);
        net.StallHeaders[Page(2)] = 1;

        var dates = (await Get(s, "moq", Patience()))!;

        Assert.Equal(new[] { "1.0.0", "2.0.0", "3.0.0" }, dates.Keys.Order());
        Assert.Equal(2, net.CallsByUrl[Page(2)]);
        Assert.Equal(1, net.CallsByUrl[Page(1)]);
        Assert.Equal(1, net.CallsByUrl[Page(3)]);
    }

    [Fact]
    public async Task The_index_stalling_once_is_tried_again_too()
    {
        var (s, net, _) = MakeFast();
        net.Serve("moq", FakeNuGetOrg.Index(FakeNuGetOrg.Leaf("1.0.0", Old)));
        net.StallHeaders[IndexUrl] = 1;

        Assert.Single((await Get(s, "moq", Patience()))!);
        Assert.Equal(2, net.CallsByUrl[IndexUrl]);
    }

    [Fact]
    public async Task A_503_once_then_a_good_answer_is_a_success()
    {
        var (s, net, _) = MakeFast();
        var served = 0;
        net.Routes[IndexUrl] = () => Interlocked.Increment(ref served) == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : FakeNuGetOrg.Json(FakeNuGetOrg.Index(FakeNuGetOrg.Leaf("1.0.0", Old)));

        Assert.Single((await Get(s, "moq", Patience()))!);
        Assert.Equal(2, net.Calls);
    }

    [Fact]
    public async Task A_page_that_stalls_every_time_fails_the_whole_package_and_never_leaves_a_partial_map()
    {
        var log = new ConcurrentQueue<string>();
        var (s, net, clock) = MakeFast(log: log.Enqueue);
        ServePages(net, 3);
        net.StallHeaders[Page(2)] = int.MaxValue;

        Assert.Null(await Get(s, "moq", Patience()));                       // page 1 came back, page 2 did not: nothing is stored from it
        Assert.Equal(2, net.CallsByUrl[Page(2)]);
        Assert.False(net.CallsByUrl.ContainsKey(Page(3)));                  // pages are fetched one after another
        Assert.Single(log, m => m.Contains("trying again"));                 // the retry is reported
        Assert.Single(log, m => m.Contains("moq") && m.Contains("failed:"));  // and so is the final failure, with the package

        Assert.Null(await Get(s, "moq", Patience()));                       // still failing closed, and not asked again during the backoff
        Assert.Equal(2, net.CallsByUrl[Page(2)]);
        net.StallHeaders.Clear();
        clock.UtcNow += TimeSpan.FromSeconds(31);
        Assert.Equal(3, (await Get(s, "moq", Patience()))!.Count);           // recovers once the backoff is over
    }

    [Fact]
    public async Task A_404_a_403_and_unreadable_answers_are_not_tried_again()
    {
        var (s, net, _) = MakeFast();

        Assert.Empty((await Get(s, "newpkg", Patience()))!);                 // 404: simply not on nuget.org
        Assert.Equal(1, net.Calls);

        net.Routes[FakeNuGetOrg.Base + "forbidden/index.json"] = () => new HttpResponseMessage(HttpStatusCode.Forbidden);
        Assert.Null(await Get(s, "forbidden", Patience()));
        Assert.Equal(1, net.CallsByUrl[FakeNuGetOrg.Base + "forbidden/index.json"]);

        net.Routes[FakeNuGetOrg.Base + "garbage/index.json"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>oops</html>") };
        Assert.Null(await Get(s, "garbage", Patience()));
        Assert.Equal(1, net.CallsByUrl[FakeNuGetOrg.Base + "garbage/index.json"]);
    }

    [Fact]
    public async Task A_response_over_the_size_limit_is_not_tried_again()
    {
        var (s, net, _) = MakeFast(o => o.MaxResponseBytes = 100);
        net.Routes[IndexUrl] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 10_000)) };

        Assert.Null(await Get(s, "moq", Patience()));
        Assert.Equal(1, net.Calls);
    }

    [Fact]
    public async Task A_page_that_is_gone_is_a_failure_and_is_not_tried_again()
    {
        var (s, net, _) = MakeFast();
        ServePages(net, 2);
        net.Routes.TryRemove(Page(2), out _);                                 // 404 for a page the index promised

        Assert.Null(await Get(s, "moq", Patience()));
        Assert.Equal(1, net.CallsByUrl[Page(2)]);
    }

    [Fact]
    public async Task A_body_that_stalls_after_the_headers_is_cut_off_and_tried_again()
    {
        var (s, net, _) = MakeFast();
        net.Serve("moq", FakeNuGetOrg.Index(FakeNuGetOrg.Leaf("1.0.0", Old)));
        net.StallBody[IndexUrl] = 1;

        Assert.Single((await Get(s, "moq", Patience()))!);
        Assert.Equal(2, net.CallsByUrl[IndexUrl]);
    }

    [Fact]
    public async Task A_body_that_always_stalls_fails_closed_in_bounded_time()
    {
        var (s, net, _) = MakeFast();
        net.StallBody[IndexUrl] = int.MaxValue;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Null(await Get(s, "moq", Patience()));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.Equal(2, net.Calls);
    }

    [Fact]
    public async Task When_nothing_ever_answers_the_fetch_ends_within_its_budget_and_does_not_retry_after_it()
    {
        var log = new ConcurrentQueue<string>();
        var (s, net, _) = MakeFast(o => { o.AttemptTimeout = TimeSpan.FromSeconds(5); o.FetchBudget = TimeSpan.FromMilliseconds(300); }, log.Enqueue);
        net.StallHeaders[IndexUrl] = int.MaxValue;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Null(await Get(s, "moq", Patience()));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");   // the 5 s attempt limit was not what ended it
        Assert.Equal(1, net.Calls);                                                      // the budget was spent: no second try
        Assert.DoesNotContain(log, m => m.Contains("trying again"));
    }

    [Fact]
    public async Task The_budget_starts_when_the_fetch_gets_its_turn_not_while_it_queues()
    {
        var (s, net, _) = MakeFast(o =>
        {
            o.MaxConcurrentFetches = 1; o.FetchBudget = TimeSpan.FromSeconds(1);
            o.AttemptTimeout = TimeSpan.FromMilliseconds(300); o.RetryDelay = TimeSpan.FromMilliseconds(500);
        });
        var release = new TaskCompletionSource();
        net.HoldUrl[FakeNuGetOrg.Base + "slowa/index.json"] = release;       // a request that does not notice cancellation: it keeps the only slot
        net.Serve("quick", FakeNuGetOrg.Index(FakeNuGetOrg.Leaf("1.0.0", Old)));
        var ct = Patience();

        var a = Get(s, "slowa", ct);
        await Task.Delay(50);
        var b = Get(s, "quick", ct);                     // queues behind it
        await Task.Delay(1500);                          // longer than the whole budget
        release.SetResult();                             // the slot frees up

        Assert.Single((await b)!);                       // B's budget began when it got the slot, so it is not already spent
        await a.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
    }

    [Fact]
    public async Task The_pause_before_a_second_try_ends_when_the_budget_does()
    {
        var (s, net, _) = MakeFast(o =>
        {
            o.FetchBudget = TimeSpan.FromMilliseconds(1500); o.AttemptTimeout = TimeSpan.FromMilliseconds(200); o.RetryDelay = TimeSpan.FromSeconds(30);
        });
        net.StallHeaders[IndexUrl] = int.MaxValue;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Null(await Get(s, "moq", Patience()));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");   // the 30 s pause was cut short by the budget
        Assert.Equal(1, net.Calls);
    }

    [Fact]
    public async Task A_registration_page_on_another_host_fails_the_fetch_and_nothing_is_requested_from_that_host()
    {
        var (s, net, _) = MakeFast();
        net.Serve("moq", new JsonObject { ["items"] = new JsonArray(new JsonObject { ["@id"] = "https://evil.test/moq/page/1.json" }) });
        net.Routes["https://evil.test/moq/page/1.json"] = () => FakeNuGetOrg.Json(new JsonObject { ["items"] = new JsonArray(FakeNuGetOrg.Leaf("1.0.0", Old)) });

        Assert.Null(await Get(s, "moq", Patience()));
        Assert.DoesNotContain(net.Urls, u => u.Contains("evil.test"));
    }

    [Fact]
    public async Task The_budget_covers_the_whole_package_not_just_one_request()
    {
        var (s, net, _) = MakeFast(o => { o.AttemptTimeout = TimeSpan.FromMilliseconds(150); o.FetchBudget = TimeSpan.FromMilliseconds(500); });
        ServePages(net, 8);
        for (var n = 1; n <= 8; n++) net.StallHeaders[Page(n)] = 1;           // each page costs about 160 ms: 8 pages cannot fit in 500 ms

        Assert.Null(await Get(s, "moq", Patience()));
    }

    [Fact]
    public async Task A_stale_answer_never_makes_a_young_version_look_old_and_a_new_version_is_unknown()
    {
        var clock = new FakeClock();
        var (s, net, _) = MakeFast(clock: clock);
        var gate = new PackageGate(Packmoat.Policy.PolicyEngine.CreateDefault(clock), s);
        var tenant = new Tenant("acme", "Acme", "https://x/", "", "https://x/i.json", new TenantPolicy(14));
        net.Serve("moq", FakeNuGetOrg.Index(
            FakeNuGetOrg.Leaf("1.0.0", Old),
            FakeNuGetOrg.Leaf("2.0.0", (clock.UtcNow - TimeSpan.FromDays(1)).ToString("o"))));

        Assert.Equal(Decision.Allow, (await gate.DecideAsync(tenant, "moq", "1.0.0", default)).Decision);
        Assert.Equal(Decision.Hide, (await gate.DecideAsync(tenant, "moq", "2.0.0", default)).Decision);

        clock.UtcNow += TimeSpan.FromMinutes(9);                                // still the fresh cached answer
        var cooling = await gate.DecideAsync(tenant, "moq", "2.0.0", default);
        Assert.Equal(Decision.Hide, cooling.Decision);
        Assert.Equal(VerdictReason.Cooldown, cooling.Reason);

        net.StallHeaders[IndexUrl] = int.MaxValue;                              // an outage; the 3-hour-old answer is used
        clock.UtcNow += TimeSpan.FromHours(3);
        Assert.Equal(VerdictReason.Cooldown, (await gate.DecideAsync(tenant, "moq", "2.0.0", default)).Reason);
        var unseen = await gate.DecideAsync(tenant, "moq", "3.0.0", default);   // published during the outage: no date known
        Assert.Equal(Decision.Hide, unseen.Decision);
        Assert.Equal(VerdictReason.DateUnknown, unseen.Reason);
    }

    [Fact]
    public async Task A_fresh_answer_replaces_the_old_one_a_version_it_no_longer_dates_is_not_filled_in_from_before()
    {
        var clock = new FakeClock();
        var (s, net, _) = MakeFast(clock: clock);
        net.Serve("moq", FakeNuGetOrg.Index(FakeNuGetOrg.Leaf("1.0.0", Old), FakeNuGetOrg.Leaf("2.0.0", Old)));
        Assert.Equal(2, (await Get(s, "moq", Patience()))!.Count);

        net.Serve("moq", FakeNuGetOrg.Index(FakeNuGetOrg.Leaf("1.0.0", Old), FakeNuGetOrg.Leaf("2.0.0", "1900-01-01T00:00:00+00:00")));
        clock.UtcNow += TimeSpan.FromMinutes(11);

        var dates = (await Get(s, "moq", Patience()))!;
        Assert.Equal(new[] { "1.0.0" }, dates.Keys);
    }

    [Fact]
    public async Task Package_ids_are_case_insensitive_in_the_cache()
    {
        var (s, net, _) = Make();
        net.Serve("moq", FakeNuGetOrg.Index(FakeNuGetOrg.Leaf("1.0.0", Old)));
        await Get(s, "Moq"); await Get(s, "MOQ"); await Get(s, "moq");
        Assert.Equal(1, net.Calls);
    }
}

public class PackageGateOutageTests
{
    private sealed class Dates : IPublishDateSource
    {
        public Func<IReadOnlyDictionary<string, DateTimeOffset>?> Answer = () => null;
        public Task<IReadOnlyDictionary<string, DateTimeOffset>?> GetPublishDatesAsync(Ecosystem e, string id, CancellationToken ct) => Task.FromResult(Answer());
    }

    private static readonly Tenant Acme = new("acme", "Acme", "https://x/", "", "https://x/i.json",
        new TenantPolicy(14, new[] { "Contoso." }, new[] { new Waiver("w", "waived", "1.0.0", new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), "r", "d") }));

    private static PackageGate Gate(Dates dates) => new(Packmoat.Policy.PolicyEngine.CreateDefault(new FakeClock()), dates);

    [Fact]
    public async Task A_source_that_returns_null_is_an_outage_not_an_unknown_date()
    {
        var v = await Gate(new Dates()).DecideAsync(Acme, "moq", "1.0.0", default);
        Assert.Equal(VerdictReason.DateSourceUnavailable, v.Reason);
        Assert.Equal(Decision.Hide, v.Decision);
    }

    [Fact]
    public async Task A_source_that_throws_is_an_outage_too()
    {
        var gate = new PackageGate(Packmoat.Policy.PolicyEngine.CreateDefault(new FakeClock()), new Dates { Answer = () => throw new HttpRequestException("boom") });
        Assert.Equal(VerdictReason.DateSourceUnavailable, (await gate.DecideAsync(Acme, "moq", "1.0.0", default)).Reason);
    }

    [Fact]
    public async Task A_source_that_answers_with_no_date_for_the_version_is_an_unknown_date()
    {
        var v = await Gate(new Dates { Answer = () => new Dictionary<string, DateTimeOffset>() }).DecideAsync(Acme, "moq", "1.0.0", default);
        Assert.Equal(VerdictReason.DateUnknown, v.Reason);
    }

    [Fact]
    public async Task Decisions_that_need_no_date_do_not_depend_on_the_outage()
    {
        var gate = Gate(new Dates());
        Assert.Equal(VerdictReason.InternalNamespace, (await gate.DecideAsync(Acme, "Contoso.Core", "1.0.0", default)).Reason);
        Assert.Equal(VerdictReason.Waiver, (await gate.DecideAsync(Acme, "waived", "1.0.0", default)).Reason);
        var noCooldown = Acme with { Policy = new TenantPolicy(0) };
        Assert.True((await gate.DecideAsync(noCooldown, "moq", "1.0.0", default)).IsAllowed);
    }

    [Fact]
    public async Task The_date_source_is_not_called_at_all_when_nothing_needs_a_date()
    {
        var calls = 0;
        var gate = Gate(new Dates { Answer = () => { calls++; return null; } });
        await gate.DecideAsync(Acme, "Contoso.Core", "1.0.0", default);
        await gate.DecideAsync(Acme with { Policy = new TenantPolicy(0) }, "moq", "1.0.0", default);
        Assert.Equal(0, calls);
    }
}

public class NuGetOrgStallProxyTests
{
    private const string Flat = "/v3/u/org/proj/_packaging/feed/nuget/v3/flat2/";

    [Fact]
    public async Task When_nuget_org_stalls_on_both_tries_the_proxy_answers_503_with_retry_after_and_serves_nothing()
    {
        var net = new FakeNuGetOrg();
        net.StallHeaders[FakeNuGetOrg.Base + "acme.lib/index.json"] = int.MaxValue;
        var source = new NuGetOrgPublishDateSource(new HttpClient(net), new SystemClock(), new NuGetOrgDateOptions
        {
            RegistrationBase = FakeNuGetOrg.Base,
            AttemptTimeout = TimeSpan.FromMilliseconds(200),
            RetryDelay = TimeSpan.FromMilliseconds(10),
            FetchBudget = TimeSpan.FromSeconds(5),
        });
        await using var p = await ProxyFixture.StartAsync(configure: services => services.AddSingleton<IPublishDateSource>(source));
        p.Publish("Acme.Lib", "1.0.0", 900);
        using var http = p.Client(ProxyFixture.AcmeToken);
        http.Timeout = TimeSpan.FromSeconds(20);

        var response = await http.GetAsync(Flat + "acme.lib/1.0.0/acme.lib.1.0.0.nupkg");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(30, response.Headers.RetryAfter!.Delta!.Value.TotalSeconds);
        Assert.Equal(2, net.Calls);
        Assert.DoesNotContain(p.Upstream.Requests, r => r.Path.EndsWith(".nupkg"));
    }
}
