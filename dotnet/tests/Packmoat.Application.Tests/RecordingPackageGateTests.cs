using System.Collections.Concurrent;
using Packmoat.Domain;
using Packmoat.Policy;

namespace Packmoat.Application.Tests;

/// <summary>
/// The per-version decision record (#123): the gate decorator notes every verdict in memory (coalesced per tenant, package, version,
/// decision and reason), a background writer flushes it, and nothing about recording can change or fail what the gate answers.
/// </summary>
public class RecordingPackageGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed class StepClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = T0;
        public bool Broken { get; set; }
        DateTimeOffset IClock.UtcNow => Broken ? throw new InvalidOperationException("clock broke") : UtcNow;
    }

    /// <summary>Answers from a table; unknown versions get a cooldown hide.</summary>
    private sealed class TableGate : IPackageGate
    {
        public Dictionary<string, Verdict> Verdicts { get; } = new(StringComparer.Ordinal);
        public Exception? Throw { get; set; }

        private Verdict For(string version) => Verdicts.TryGetValue(NuGetVersionKey.Normalize(version), out var v) ? v : new Verdict(Decision.Hide, VerdictReason.Cooldown, CooldownDaysApplied: 14, PublishedAt: T0.AddDays(-2));

        public Task<Verdict> DecideAsync(Tenant tenant, string packageId, string version, CancellationToken ct)
            => Throw is { } e ? Task.FromException<Verdict>(e) : Task.FromResult(For(version));

        public Task<IReadOnlyDictionary<string, Verdict>> DecideManyAsync(Tenant tenant, string packageId, IReadOnlyCollection<string> versions, CancellationToken ct)
            => Throw is { } e ? Task.FromException<IReadOnlyDictionary<string, Verdict>>(e)
                : Task.FromResult<IReadOnlyDictionary<string, Verdict>>(versions.ToDictionary(v => v, For, StringComparer.Ordinal));
    }

    private sealed class MemoryLog : IDecisionRecordLog
    {
        public ConcurrentQueue<IReadOnlyList<DecisionRecord>> Batches { get; } = new();
        public Func<Exception>? Fail { get; set; }
        public TaskCompletionSource? Block { get; set; }
        public List<DecisionRecord> All => Batches.SelectMany(b => b).ToList();

        public async Task UpsertAsync(IReadOnlyList<DecisionRecord> records, CancellationToken ct)
        {
            if (Block is { } b) await b.Task.WaitAsync(ct);
            if (Fail is { } f) throw f();
            Batches.Enqueue(records.ToList());
        }

        public Task<int> PruneAsync(DateTimeOffset lastSeenBefore, CancellationToken ct) => Task.FromResult(0);
    }

    private static Tenant Acme(long revision = 7) =>
        new("acme", "Acme", "https://pkgs.dev.azure.com/", "org/", "https://pkgs.dev.azure.com/org/_packaging/f/nuget/v3/index.json", new TenantPolicy(14, revision: revision));

    /// <summary>A long flush interval: the tests flush by hand.</summary>
    private static RecordingPackageGate Recorder(IPackageGate inner, IDecisionRecordLog log, IClock clock, List<string>? warnings = null,
        int capacity = RecordingPackageGate.DefaultCapacity, int perTenant = RecordingPackageGate.MaxKeysPerTenant) =>
        new(inner, log, clock, warnings is null ? null : warnings.Add, capacity, flushInterval: TimeSpan.FromHours(1), perTenantCapacity: perTenant);

    [Fact]
    public async Task Every_verdict_of_a_listing_and_a_single_decision_is_recorded_with_what_the_policy_used()
    {
        var inner = new TableGate();
        inner.Verdicts["1.0.0"] = new Verdict(Decision.Allow, VerdictReason.CooldownElapsed, CooldownDaysApplied: 14, PublishedAt: T0.AddDays(-400));
        inner.Verdicts["2.0.0"] = new Verdict(Decision.Allow, VerdictReason.Waiver, WaiverId: "w-1");
        var log = new MemoryLog();
        var clock = new StepClock();
        using var gate = Recorder(inner, log, clock);

        var many = await gate.DecideManyAsync(Acme(), "Acme.Lib", new[] { "1.0.0", "2.0.0", "3.0.0" }, default);
        var one = await gate.DecideAsync(Acme(), "Acme.Lib", "1.0.0.0", default);

        Assert.Equal(3, many.Count);
        Assert.True(one.IsAllowed);
        Assert.Equal(3, await gate.FlushAsync());
        var rows = log.All.OrderBy(r => r.Version, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "1.0.0", "2.0.0", "3.0.0" }, rows.Select(r => r.Version));
        Assert.All(rows, r => Assert.Equal(("acme", Ecosystem.NuGet, "acme.lib", 7L), (r.TenantId, r.Ecosystem, r.PackageId, r.PolicyRevision)));

        var allowed = rows[0];   // "1.0.0" and "1.0.0.0" are the same version: one row, seen twice
        Assert.Equal((Decision.Allow, VerdictReason.CooldownElapsed, (string?)null, (int?)14, (DateTimeOffset?)T0.AddDays(-400), 2L),
            (allowed.Decision, allowed.Reason, allowed.WaiverId, allowed.CooldownDays, allowed.PublishedAt, allowed.SeenCount));
        Assert.Equal(("w-1", (int?)null, (DateTimeOffset?)null), (rows[1].WaiverId, rows[1].CooldownDays, rows[1].PublishedAt));
        Assert.Equal((Decision.Hide, VerdictReason.Cooldown), (rows[2].Decision, rows[2].Reason));
        Assert.All(rows, r => Assert.Equal((T0, T0), (r.FirstSeenAt, r.LastSeenAt)));
    }

    [Fact]
    public async Task Repeats_coalesce_into_one_row_with_first_and_last_seen_and_a_count()
    {
        var log = new MemoryLog();
        var clock = new StepClock();
        using var gate = Recorder(new TableGate(), log, clock);

        for (var i = 0; i < 5; i++)
        {
            await gate.DecideAsync(Acme(), "Moq", "4.21.0", default);
            clock.UtcNow += TimeSpan.FromMinutes(1);
        }
        await gate.FlushAsync();

        var row = Assert.Single(log.All);
        Assert.Equal((5L, T0, T0.AddMinutes(4)), (row.SeenCount, row.FirstSeenAt, row.LastSeenAt));
        Assert.Equal(0, await gate.FlushAsync());   // flushed: nothing left to write
    }

    [Fact]
    public async Task A_new_decision_or_reason_for_the_same_version_is_its_own_row_and_tenants_never_share_one()
    {
        var inner = new TableGate();
        var log = new MemoryLog();
        using var gate = Recorder(inner, log, new StepClock());

        await gate.DecideAsync(Acme(), "Moq", "4.21.0", default);                                      // Hide, Cooldown
        inner.Verdicts["4.21.0"] = new Verdict(Decision.Allow, VerdictReason.CooldownElapsed, CooldownDaysApplied: 14);
        await gate.DecideAsync(Acme(), "Moq", "4.21.0", default);                                      // Allow, CooldownElapsed
        await gate.DecideAsync(Acme() with { Id = "other" }, "Moq", "4.21.0", default);
        await gate.FlushAsync();

        Assert.Equal(3, log.All.Count);
        Assert.Equal(2, log.All.Count(r => r.TenantId == "acme"));
    }

    [Fact]
    public async Task The_latest_policy_facts_win_within_a_batch()
    {
        var inner = new TableGate();
        var log = new MemoryLog();
        var clock = new StepClock();
        using var gate = Recorder(inner, log, clock);

        inner.Verdicts["1.0.0"] = new Verdict(Decision.Allow, VerdictReason.CooldownElapsed, CooldownDaysApplied: 14, PublishedAt: T0.AddDays(-30));
        await gate.DecideAsync(Acme(revision: 1), "a", "1.0.0", default);
        clock.UtcNow += TimeSpan.FromSeconds(5);
        inner.Verdicts["1.0.0"] = new Verdict(Decision.Allow, VerdictReason.CooldownElapsed, CooldownDaysApplied: 7, PublishedAt: T0.AddDays(-30));
        await gate.DecideAsync(Acme(revision: 2), "a", "1.0.0", default);
        await gate.FlushAsync();

        var row = Assert.Single(log.All);
        Assert.Equal((7, 2L, 2L), (row.CooldownDays!.Value, row.PolicyRevision, row.SeenCount));
    }

    [Fact]
    public async Task A_full_map_drops_new_keys_counts_and_warns_once_and_still_counts_keys_it_holds()
    {
        var log = new MemoryLog();
        var warnings = new List<string>();
        using var gate = Recorder(new TableGate(), log, new StepClock(), warnings, capacity: 3);

        var versions = Enumerable.Range(1, 10).Select(i => $"{i}.0.0").ToArray();
        var verdicts = await gate.DecideManyAsync(Acme(), "Zeta.Lib", versions, default);
        await gate.DecideAsync(Acme(), "Zeta.Lib", "1.0.0", default);   // already held: still counted

        Assert.Equal(10, verdicts.Count);                         // the answer is complete whatever the recorder did
        Assert.Equal(7, gate.DroppedRecords);
        var warning = Assert.Single(warnings);
        Assert.DoesNotContain("zeta", warning, StringComparison.OrdinalIgnoreCase);   // no tenant, package or version in the log
        Assert.DoesNotContain("acme", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1.0.0", warning);
        await gate.FlushAsync();
        Assert.Equal(3, log.All.Count);
        Assert.Equal(2L, log.All.Single(r => r.Version == "1.0.0").SeenCount);

        await gate.DecideAsync(Acme(), "Zeta.Lib", "9.0.0", default);   // after a flush the map has room again
        await gate.FlushAsync();
        Assert.Contains(log.All, r => r.Version == "9.0.0");
    }

    [Fact]
    public async Task One_tenant_over_its_share_cannot_starve_another_and_its_drops_are_counted_for_it()
    {
        var log = new MemoryLog();
        var warnings = new List<string>();
        using var gate = Recorder(new TableGate(), log, new StepClock(), warnings, capacity: 100, perTenant: 3);
        var other = Acme() with { Id = "beta" };

        var versions = Enumerable.Range(1, 10).Select(i => $"{i}.0.0").ToArray();
        Assert.Equal(10, (await gate.DecideManyAsync(Acme(), "Zeta.Lib", versions, default)).Count);   // the answer is complete
        await gate.DecideAsync(Acme(), "Zeta.Lib", "1.0.0", default);                                   // a key it holds: still counted
        await gate.DecideManyAsync(other, "Zeta.Lib", versions.Take(3).ToArray(), default);             // tenant B still records

        Assert.Equal((7L, 0L, 7L), (gate.DroppedFor("acme"), gate.DroppedFor("beta"), gate.DroppedRecords));
        var warning = Assert.Single(warnings);
        Assert.Contains("acme", warning);                                                               // named by its id only
        Assert.DoesNotContain("zeta", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4.0.0", warning);

        await gate.FlushAsync();
        Assert.Equal(3, log.All.Count(r => r.TenantId == "acme"));
        Assert.Equal(3, log.All.Count(r => r.TenantId == "beta"));
        Assert.Equal(2L, log.All.Single(r => r.TenantId == "acme" && r.Version == "1.0.0").SeenCount);

        await gate.DecideManyAsync(Acme(), "Zeta.Lib", new[] { "8.0.0" }, default);                     // a new interval: a new share
        await gate.FlushAsync();
        Assert.Contains(log.All, r => r.TenantId == "acme" && r.Version == "8.0.0");
    }

    [Fact]
    public async Task The_per_tenant_share_holds_across_separate_calls()
    {
        const int share = 4;
        var log = new MemoryLog();
        using var gate = Recorder(new TableGate(), log, new StepClock(), perTenant: share);

        for (var i = 0; i <= share; i++)                                    // share + 1 separate calls, each a new key (Hide, Cooldown)
            Assert.Equal(Decision.Hide, (await gate.DecideAsync(Acme(), "a", $"{i}.0.0", default)).Decision);

        Assert.Equal((1L, 1L), (gate.DroppedFor("acme"), gate.DroppedRecords));
        Assert.Equal(share, await gate.FlushAsync());
    }

    [Fact]
    public async Task An_over_share_warning_never_hides_a_failed_write_warning()
    {
        var log = new MemoryLog { Fail = () => new IOException("disk gone") };
        var warnings = new List<string>();
        using var gate = Recorder(new TableGate(), log, new StepClock(), warnings, perTenant: 1);

        await gate.DecideManyAsync(Acme(), "a", new[] { "1.0.0", "2.0.0" }, default);   // over its share: warned
        Assert.Equal(0, await gate.FlushAsync());                                         // the write fails: warned too, same window

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("over its share"));
        Assert.Contains(warnings, w => w.Contains(nameof(IOException)) && w.Contains("up to 1 row(s) lost"));
    }

    [Fact]
    public async Task A_single_version_with_an_unknown_date_never_creates_a_key_but_counts_towards_one_held()
    {
        var inner = new TableGate();
        inner.Verdicts["6.6.6"] = new Verdict(Decision.Hide, VerdictReason.DateUnknown, CooldownDaysApplied: 14);
        inner.Verdicts["1.0.0"] = new Verdict(Decision.Hide, VerdictReason.DateUnknown, CooldownDaysApplied: 14);
        var log = new MemoryLog();
        using var gate = Recorder(inner, log, new StepClock());

        // Made-up versions in download or leaf URLs: hidden as always, not recorded.
        for (var i = 0; i < 5; i++) Assert.Equal(Decision.Hide, (await gate.DecideAsync(Acme(), "a", "6.6.6", default)).Decision);
        Assert.Equal(0, await gate.FlushAsync());

        // A version from a listing (the upstream named it) is recorded; a later single lookup of it is counted.
        await gate.DecideManyAsync(Acme(), "a", new[] { "1.0.0" }, default);
        await gate.DecideAsync(Acme(), "a", "1.0.0", default);
        await gate.FlushAsync();
        var row = Assert.Single(log.All);
        Assert.Equal(("1.0.0", VerdictReason.DateUnknown, 2L), (row.Version, row.Reason, row.SeenCount));
        Assert.Equal(0, gate.DroppedRecords);
    }

    [Fact]
    public async Task An_id_or_version_over_256_characters_is_dropped_counted_and_warned_like_a_full_map()
    {
        var log = new MemoryLog();
        var warnings = new List<string>();
        using var gate = Recorder(new TableGate(), log, new StepClock(), warnings);
        var longId = new string('x', RecordingPackageGate.MaxFieldLength + 1);
        var longVersion = "1.0.0-" + new string('y', RecordingPackageGate.MaxFieldLength);

        Assert.Equal(Decision.Hide, (await gate.DecideAsync(Acme(), longId, "1.0.0", default)).Decision);
        var many = await gate.DecideManyAsync(Acme(), "a", new[] { longVersion, "2.0.0" }, default);
        await gate.DecideAsync(Acme(), new string('z', RecordingPackageGate.MaxFieldLength), "3.0.0", default);   // exactly the limit: kept

        Assert.Equal(2, many.Count);
        Assert.Equal(2, gate.DroppedRecords);
        var warning = Assert.Single(warnings);                                                          // rate-limited: one warning
        Assert.Contains(RecordingPackageGate.MaxFieldLength.ToString(System.Globalization.CultureInfo.InvariantCulture), warning);
        Assert.DoesNotContain("xxx", warning);
        Assert.DoesNotContain("yyy", warning);
        await gate.FlushAsync();
        Assert.Equal(new[] { "2.0.0", "3.0.0" }, log.All.Select(r => r.Version).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Recording_never_fails_or_changes_the_gate()
    {
        var inner = new TableGate();
        inner.Verdicts["1.0.0"] = new Verdict(Decision.Allow, VerdictReason.CooldownElapsed, CooldownDaysApplied: 14);
        var clock = new StepClock { Broken = true };              // bookkeeping throws on every call
        var warnings = new List<string>();
        using var gate = Recorder(inner, new MemoryLog(), clock, warnings);

        Assert.Equal(inner.Verdicts["1.0.0"], await gate.DecideAsync(Acme(), "a", "1.0.0", default));
        var many = await gate.DecideManyAsync(Acme(), "a", new[] { "1.0.0", "2.0.0" }, default);
        Assert.Equal(inner.Verdicts["1.0.0"], many["1.0.0"]);
        Assert.Equal(Decision.Hide, many["2.0.0"].Decision);
        Assert.True(gate.FailedRecords >= 2);
        Assert.All(warnings, w => Assert.Contains(nameof(InvalidOperationException), w));
    }

    [Fact]
    public async Task The_inner_gates_failure_reaches_the_caller_unchanged_and_nothing_is_recorded()
    {
        var inner = new TableGate { Throw = new TimeoutException("upstream slow") };
        var log = new MemoryLog();
        using var gate = Recorder(inner, log, new StepClock());

        await Assert.ThrowsAsync<TimeoutException>(() => gate.DecideAsync(Acme(), "a", "1.0.0", default));
        await Assert.ThrowsAsync<TimeoutException>(() => gate.DecideManyAsync(Acme(), "a", new[] { "1.0.0" }, default));
        Assert.Equal(0, await gate.FlushAsync());
        Assert.Empty(log.All);
    }

    [Fact]
    public async Task A_failed_write_is_counted_and_logged_by_exception_type_only()
    {
        var log = new MemoryLog { Fail = () => new IOException("disk full at /var/lib/packmoat for acme.lib 1.0.0") };
        var warnings = new List<string>();
        using var gate = Recorder(new TableGate(), log, new StepClock(), warnings);

        await gate.DecideAsync(Acme(), "Acme.Lib", "1.0.0", default);
        Assert.Equal(0, await gate.FlushAsync());

        Assert.Equal(1, gate.FailedWrites);
        var warning = Assert.Single(warnings);
        Assert.Contains(nameof(IOException), warning);
        Assert.DoesNotContain("disk full", warning);
        Assert.DoesNotContain("acme", warning, StringComparison.OrdinalIgnoreCase);

        log.Fail = null;   // the store is back: later decisions are written again
        await gate.DecideAsync(Acme(), "Acme.Lib", "2.0.0", default);
        Assert.Equal(1, await gate.FlushAsync());
    }

    [Fact]
    public async Task The_request_path_never_waits_for_storage()
    {
        var log = new MemoryLog { Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var gate = Recorder(new TableGate(), log, new StepClock());

        await gate.DecideAsync(Acme(), "a", "1.0.0", default);
        var flushing = gate.FlushAsync();                          // the writer is stuck in storage...
        var decided = gate.DecideAsync(Acme(), "a", "2.0.0", default);
        await decided.WaitAsync(TimeSpan.FromSeconds(5));          // ...and a request still completes at once
        Assert.False(flushing.IsCompleted);

        log.Block.SetResult();
        Assert.Equal(1, await flushing);
        Assert.Equal(1, await gate.FlushAsync());                   // the decision made during the stuck write is in the next batch
    }

    [Fact]
    public async Task The_background_writer_flushes_on_its_own_and_dispose_writes_what_is_left()
    {
        var log = new MemoryLog();
        using (var gate = new RecordingPackageGate(new TableGate(), log, new StepClock(), null, flushInterval: TimeSpan.FromMilliseconds(20)))
        {
            await gate.DecideAsync(Acme(), "a", "1.0.0", default);
            var until = DateTime.UtcNow.AddSeconds(10);
            while (log.All.Count == 0 && DateTime.UtcNow < until) await Task.Delay(10);
            Assert.Single(log.All);
        }

        var slow = new MemoryLog();
        var last = new RecordingPackageGate(new TableGate(), slow, new StepClock(), null, flushInterval: TimeSpan.FromHours(1));
        await last.DecideAsync(Acme(), "a", "2.0.0", default);
        last.Dispose();
        Assert.Equal("2.0.0", Assert.Single(slow.All).Version);
        last.Dispose();   // idempotent
        Assert.Equal(Decision.Hide, (await last.DecideAsync(Acme(), "a", "3.0.0", default)).Decision);   // still answers after dispose
    }

    [Fact]
    public async Task With_the_real_gate_the_record_carries_the_publish_date_or_none_when_it_is_unknown()
    {
        var dates = new Dictionary<string, DateTimeOffset> { ["1.0.0"] = T0.AddDays(-400), ["2.0.0"] = T0.AddDays(-2) };   // 3.0.0: no date (an unlisted placeholder is dropped by the source)
        var engine = PolicyEngine.CreateDefault(new StepClock());
        var log = new MemoryLog();
        using var gate = Recorder(new PackageGate(engine, new FixedDates(dates)), log, new StepClock());

        var verdicts = await gate.DecideManyAsync(Acme(), "a", new[] { "1.0.0", "2.0.0", "3.0.0" }, default);
        await gate.FlushAsync();

        Assert.Equal(new[] { Decision.Allow, Decision.Hide, Decision.Hide }, new[] { "1.0.0", "2.0.0", "3.0.0" }.Select(v => verdicts[v].Decision));
        var rows = log.All.ToDictionary(r => r.Version);
        Assert.Equal(T0.AddDays(-400), rows["1.0.0"].PublishedAt);
        Assert.Equal(T0.AddDays(-2), rows["2.0.0"].PublishedAt);
        Assert.Equal((VerdictReason.DateUnknown, (DateTimeOffset?)null, (int?)14), (rows["3.0.0"].Reason, rows["3.0.0"].PublishedAt, rows["3.0.0"].CooldownDays));
    }

    [Fact]
    public void The_retention_is_one_named_figure_of_365_days()
    {
        Assert.Equal(365, DecisionRecordPolicy.RetentionDays);
        Assert.Equal(TimeSpan.FromDays(365), DecisionRecordPolicy.Retention);
    }

    [Fact]
    public void A_decision_record_holds_no_token_owner_or_name()
    {
        var forbidden = new[] { "token", "owner", "oid", "name", "label", "user", "email", "address", "agent", "request", "ip" };
        var props = typeof(DecisionRecord).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToArray();
        Assert.DoesNotContain(props, p => forbidden.Any(p.Contains));
    }

    private sealed class FixedDates(IReadOnlyDictionary<string, DateTimeOffset> dates) : IPublishDateSource
    {
        public Task<IReadOnlyDictionary<string, DateTimeOffset>?> GetPublishDatesAsync(Ecosystem ecosystem, string packageId, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>?>(dates);
    }
}
