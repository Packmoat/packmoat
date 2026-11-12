using System.Reflection;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Application.Tests;

/// <summary>
/// every limit counts in the shared counter (<see cref="IRateCounter"/>) through <see cref="RateLimit"/>, which fails closed,
/// and every call site keeps the limit it had before (the same max and window, now with its own scope).
/// </summary>
public class RateLimitTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class ThrowingCounter(Exception error) : IRateCounter
    {
        public int Hits;
        public Task<RateDecision> HitAsync(string scope, string key, int max, TimeSpan window, CancellationToken ct = default)
        {
            Hits++;
            throw error;
        }
        public Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default) => throw error;
    }

    [Theory]   // mutation: the catch returning RateDecision.Allow (the limit would fail open while the store is down)
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(OperationCanceledException))]   // a driver's timeout, not the caller's cancellation
    public async Task A_counter_that_fails_refuses_the_attempt_with_the_whole_window(Type error)
    {
        var counter = new ThrowingCounter((Exception)Activator.CreateInstance(error)!);
        var limit = new RateLimit(counter, RateScopes.TokenIssue, 10, TimeSpan.FromHours(1));
        Assert.Equal(new RateDecision(false, TimeSpan.FromHours(1)), await limit.HitAsync("dir/oid", Ct));
        Assert.False(await limit.TryAcquireAsync("dir/oid", Ct));
        Assert.Equal(2, counter.Hits);
    }

    [Fact]   // the caller's own cancellation is not a refusal: it is thrown
    public async Task The_callers_cancellation_is_thrown_not_turned_into_a_refusal()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var limit = new RateLimit(new ThrowingCounter(new OperationCanceledException(cts.Token)), RateScopes.TokenIssue, 10, TimeSpan.FromHours(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limit.HitAsync("dir/oid", cts.Token));
    }

    [Fact]   // mutation: the failure swallowed without a line, or the key put in the line
    public async Task A_failure_is_reported_once_with_the_scope_and_the_type_never_the_key()
    {
        var lines = new List<string>();
        var counter = new ReportingRateCounter(new ThrowingCounter(new InvalidOperationException("db down: dir/secret-oid")), lines.Add);
        var limit = new RateLimit(counter, RateScopes.TokenIssue, 10, TimeSpan.FromHours(1));
        Assert.False(await limit.TryAcquireAsync("dir/secret-oid", Ct));
        var line = Assert.Single(lines);
        Assert.Equal("Rate limit token-issue: the shared counter failed (InvalidOperationException); the attempt was refused (fail closed)", line);
        Assert.DoesNotContain("secret", line);
        Assert.DoesNotContain(RateCounters.Subject("dir/secret-oid"), line);
    }

    [Fact]   // the caller's own cancellation is no failure: nothing reported, and it is thrown; a sink that throws changes nothing
    public async Task A_cancellation_is_not_reported_and_a_broken_sink_still_refuses()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var lines = new List<string>();
        var cancelled = new RateLimit(new ReportingRateCounter(new ThrowingCounter(new OperationCanceledException(cts.Token)), lines.Add), RateScopes.TokenIssue, 10, TimeSpan.FromHours(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.HitAsync("k", cts.Token));
        Assert.Empty(lines);

        var broken = new RateLimit(new ReportingRateCounter(new ThrowingCounter(new IOException("down")), _ => throw new InvalidOperationException("sink")),
            RateScopes.TokenIssue, 10, TimeSpan.FromHours(1));
        Assert.Equal(new RateDecision(false, TimeSpan.FromHours(1)), await broken.HitAsync("k", Ct));
    }

    [Fact]   // through a real call site: the store is down, so the token is refused as too many requests, and nothing is written
    public async Task A_call_site_refuses_when_the_counter_fails()
    {
        var clock = new MovableClock();
        var tenants = new FakeStore();
        var directories = new FakeDirectories();
        var log = new FakeActionLog();
        var tokens = new FakeTokens(clock);
        tenants.Tenants.Add(new Tenant("acme", "ACME", "https://pkgs.dev.azure.com/", "acme/", "https://pkgs.dev.azure.com/acme/_packaging/f/nuget/v3/index.json", new TenantPolicy(14)));
        tenants.Bindings["acme"] = new UpstreamBinding(IdentityKind.Own, Dir, "99999999-9999-4999-8999-999999999999");
        directories.Rows[Dir] = new DirectoryBinding(Dir, "acme", clock.UtcNow, "op");
        var commands = new CustomerTokenCommands(tenants, tokens, new FakeMembers(), directories, log, clock, new FakeSharedStore(), ServeAllGate.Instance,
            new ThrowingCounter(new InvalidOperationException("down")));
        var actor = new CustomerActor("acme", Guid.Parse("11111111-1111-4111-8111-111111111111"), CustomerRole.Developer, "Ada", Dir);
        var e = await Assert.ThrowsAsync<CustomerTokenRefusedException>(() => commands.IssueDeveloperTokenAsync(actor, "laptop", Ct));
        Assert.Equal(CustomerTokenRefusal.TooManyRequests, e.Reason);
        Assert.Empty(log.Rows);
        Assert.Empty(await tokens.ListAsync("acme", Ct));
    }

    private const string Dir = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

    /// <summary>Each object's limits, read from its <see cref="RateLimit"/> fields: (field, scope, max, window).</summary>
    internal static List<(string Field, string Scope, int Max, TimeSpan Window)> LimitsOf(object o) =>
        o.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(f => f.FieldType == typeof(RateLimit))
            .Select(f => (f.Name, Limit: (RateLimit)f.GetValue(o)!))
            .Select(x => (x.Name, x.Limit.Scope, x.Limit.Max, x.Limit.Window))
            .OrderBy(l => l.Name, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The pin of the limits: each core call site counts with the max and window it had before the shared counter, each in a
    /// scope of its own. The equivalence of the counting itself is RateCounterTests' (Infrastructure.Tests).
    /// </summary>
    [Fact]   // mutation: a limit given another max, window or scope; a site left with a field of another type (no RateLimit read here)
    public void Every_core_call_site_keeps_its_limit()
    {
        var clock = new MovableClock();
        var rates = new InProcessRateCounter(clock);
        var hour = TimeSpan.FromHours(1);
        Assert.Equal([("_issues", "token-issue", 10, hour), ("_revokes", "token-revoke", 10, hour)],
            LimitsOf(new CustomerTokenCommands(null!, null!, null!, null!, null!, clock, null, ServeAllGate.Instance, rates)));
        Assert.Equal([("_adds", "waiver-add", 10, hour), ("_revokes", "waiver-revoke", 10, hour)],
            LimitsOf(new CustomerWaiverCommands(null!, null!, null!, null!, clock, rates)));
        Assert.Equal([("_capAlerts", "waiver-cap-alert", 1, hour)], LimitsOf(new AdminCommands(null!, null!, clock, rates)));
        Assert.Equal([("_changes", "auto-join-change", 10, hour)], LimitsOf(new AutoJoinCommands(null!, null!, null!, null!, clock, rates)));
        Assert.Equal([("_limiter", "feed-check", 10, hour)],
            LimitsOf(new AzureSetupCommands(null!, null!, null, null, null, SetupStates.New(clock), AzureSetupSettings.Parse("on"), clock, rates)));
        // The scopes are all different: no two limits share a count.
        Assert.Equal(RateScopes.All.Count, RateScopes.All.Distinct().Count());
    }
}
