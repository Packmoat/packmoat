using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
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

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        Urls.Enqueue(request.RequestUri!.ToString());
        if (Hold is { } hold) await hold.Task;
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

    private static (NuGetOrgPublishDateSource Source, FakeNuGetOrg Net, FakeClock Clock) Make(Action<NuGetOrgDateOptions>? tweak = null)
    {
        var net = new FakeNuGetOrg();
        var clock = new FakeClock();
        var options = new NuGetOrgDateOptions { RegistrationBase = FakeNuGetOrg.Base };
        tweak?.Invoke(options);
        return (new NuGetOrgPublishDateSource(new HttpClient(net), clock, options), net, clock);
    }

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
        var (s, net, clock) = Make();
        net.Routes[FakeNuGetOrg.Base + "moq/index.json"] = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        Assert.Null(await Get(s));
        Assert.Null(await Get(s));
        Assert.Null(await Get(s));
        Assert.Equal(1, net.Calls);                                        // negative-cached

        net.Serve("moq", FakeNuGetOrg.Index(FakeNuGetOrg.Leaf("1.0.0", Old)));
        clock.UtcNow += TimeSpan.FromSeconds(31);
        Assert.NotNull(await Get(s));                                      // recovers once the backoff is over
        Assert.Equal(2, net.Calls);
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);
        net.Hold.SetResult();

        Assert.Single((await patient)!);
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
