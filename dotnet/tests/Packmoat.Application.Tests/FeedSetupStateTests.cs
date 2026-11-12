using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Application.Tests;

/// <summary>
/// The setup wizard's shared state in the commands: each check runs under the tenant's feed-check lease, for the deadline, released
/// whatever happens; and every failure of the scratch or the lease fails closed (nothing confirmed, nothing reported as passed) and is
/// logged with what failed, the kind and the error's type only.
/// </summary>
public class FeedSetupStateTests
{
    private const string Dir = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string Client = "5a2ed000-0000-4000-8000-000000000001";
    private const string Token = "SENTINEL-TOKEN-7c2b";
    private static readonly Guid Oid = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static readonly NamedFeedAdmin Ada = new("Ada Lovelace (feed creator)", "Ada Lovelace", "desc-ada");
    private static readonly NamedFeedAdmin Bob = new("Bob Ross (feed creator)", "Bob Ross", "desc-bob");

    /// <summary>A scratch that fails on demand, over the in-process one.</summary>
    private sealed class FlakyScratch(IClock clock) : IFeedSetupScratch
    {
        public const string Secret = "SECRET-STORE-DETAIL-91aa";
        private readonly InProcessFeedSetupScratch _inner = new(clock);
        public Exception? FailPut { get; set; }
        public Exception? FailGet { get; set; }
        public Exception? FailDelete { get; set; }
        public Task PutAsync(string tenantId, Guid personId, string kind, string payload, TimeSpan life, CancellationToken ct = default) =>
            FailPut is { } e ? throw e : _inner.PutAsync(tenantId, personId, kind, payload, life, ct);
        public Task<string?> GetAsync(string tenantId, Guid personId, string kind, CancellationToken ct = default) =>
            FailGet is { } e ? throw e : _inner.GetAsync(tenantId, personId, kind, ct);
        public Task DeleteAsync(string tenantId, Guid personId, string kind, CancellationToken ct = default) =>
            FailDelete is { } e ? throw e : _inner.DeleteAsync(tenantId, personId, kind, ct);
        public Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default) => _inner.PruneAsync(now, ct);
    }

    private sealed class World
    {
        public FakeStore Tenants { get; } = new();
        public FakeDirectories Directories { get; } = new();
        public FakeSharedStore Shared { get; } = new();
        public FakeFeedChecks Checks { get; } = new();
        public FakeDelegatedCheck Check { get; } = new();
        public MovingClock Clock { get; } = new();
        public FlakyScratch Scratch { get; }
        public TestJobLease Lease { get; }
        public AzureSetupState State { get; }
        public AzureSetupCommands Commands { get; }
        public List<(SetupLogLevel Level, string Line)> Log { get; } = new();

        public World()
        {
            Scratch = new FlakyScratch(Clock);
            Lease = new TestJobLease(Clock);
            State = new AzureSetupState(Scratch, Lease, Clock);
            Tenants.Tenants.Add(new Tenant("selfco", "Selfco", "", "", "", new TenantPolicy(14)));
            Directories.Rows[Dir] = new DirectoryBinding(Dir, "selfco", Clock.UtcNow, "self-signup");
            Tenants.Setups["selfco"] = new TenantSetup(TenantOrigin.Self, FeedSetup.Operator);
            Shared.Slots[SharedAppSlot.Active] = new SharedAppCredential(Client, "PEM");
            Commands = new AzureSetupCommands(Tenants, Directories, Shared, Checks, Check, State, AzureSetupSettings.Parse("on"), Clock, new InProcessRateCounter(Clock),
                log: (level, line) => Log.Add((level, line)));
        }

        public CustomerActor Admin => new("selfco", Oid, CustomerRole.Admin, "Ada Lovelace", Dir, Clock.UtcNow);
        public AzureSetupRequest Names => new(new FeedNames("contoso", null, "public-packages"), Array.Empty<string>());

        /// <summary>The engine finds these named admins, with the permissions check waiting on them.</summary>
        public void Finds(params NamedFeedAdmin[] admins) => Check.Run = Check.Run with
        {
            Results = AzureSetupRules.SettingsChecks.Select(id => new CheckResult(id, id, id == "permissions" ? CheckStatus.Warn : CheckStatus.Pass, "ok")).ToList(),
            NamedAdmins = admins,
        };

        public void Later() => Clock.UtcNow += TimeSpan.FromSeconds(1);
    }

    private static async Task<AzureSetupRefusal> Refusal(Func<Task> act) => (await Assert.ThrowsAsync<AzureSetupRefusedException>(act)).Reason;

    /// <summary>Every log line: one short line naming what failed and the error's type, never the tenant, the person, a name or the message.</summary>
    private static void OnlyScopeAndType(World w)
    {
        Assert.NotEmpty(w.Log);
        Assert.All(w.Log, l =>
        {
            Assert.Equal(SetupLogLevel.Warning, l.Level);
            Assert.StartsWith("Feed setup: ", l.Line, StringComparison.Ordinal);
            Assert.DoesNotContain("selfco", l.Line, StringComparison.Ordinal);
            Assert.DoesNotContain(Oid.ToString("D"), l.Line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Ada", l.Line, StringComparison.Ordinal);
            Assert.DoesNotContain(FlakyScratch.Secret, l.Line, StringComparison.Ordinal);
        });
    }

    // ---------------------------------------------------------------- the lease

    [Fact]   // mutations: the lease's TTL not the check lease (JobLeases.Ttl, an hour; or the bare deadline); the job not per tenant; one holder for every run
    public async Task Each_check_takes_the_tenants_feed_check_lease_for_the_deadline_and_a_minute_with_a_holder_of_its_own_and_ends_it()
    {
        var w = new World();
        await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        w.Later();
        await w.Commands.RunAsync(w.Admin, w.Names, Token, default);

        Assert.Equal(2, w.Lease.Taken.Count);
        Assert.All(w.Lease.Taken, t => Assert.Equal(("feed-check:selfco", AzureSetupRules.CheckLease), (t.Job, t.Ttl)));
        Assert.Equal((60, 120), (AzureSetupRules.Deadline.TotalSeconds, AzureSetupRules.CheckLease.TotalSeconds));
        Assert.NotEqual(w.Lease.Taken[0].Holder, w.Lease.Taken[1].Holder);
        Assert.All(w.Lease.Taken, t => Assert.StartsWith(JobLeases.ProcessHolder + "-", t.Holder, StringComparison.Ordinal));
        Assert.Equal(w.Lease.Taken.Select(t => (t.Job, t.Holder)), w.Lease.Released);
    }

    /// <summary>A copy that stops mid-check leaves its lease: the tenant's next check is refused until the check lease has run out, then runs.</summary>
    [Fact]   // mutation: the lease taken for an hour (or forever): a stopped copy would block the tenant's checks long after the deadline
    public async Task A_lease_left_by_a_copy_that_stopped_frees_the_tenant_once_the_check_lease_has_run_out()
    {
        var w = new World();
        Assert.NotNull(await w.State.TryBeginAsync("selfco"));   // a copy took it, then stopped: never ended
        w.Clock.UtcNow += AzureSetupRules.CheckLease - TimeSpan.FromSeconds(1);
        Assert.Equal(AzureSetupRefusal.CheckRunning, await Refusal(() => w.Commands.RunAsync(w.Admin, w.Names, Token, default)));
        Assert.Empty(w.Check.Calls);
        w.Clock.UtcNow += TimeSpan.FromSeconds(2);
        Assert.Equal(FeedCheckOutcome.Passed, (await w.Commands.RunAsync(w.Admin, w.Names, Token, default)).Outcome);
    }

    /// <summary>
    /// The deadline bounds only the Azure DevOps calls: a check that used all of it still blanks, records, withdraws and keeps its result
    /// afterwards, and the tenant stays held until it ends.
    /// </summary>
    [Fact]   // mutation: the lease taken for the bare deadline (a second check could start while the first is still recording)
    public async Task A_check_past_its_deadline_still_holds_the_tenant_until_it_ends()
    {
        var w = new World();
        string? second = "not tried";
        w.Check.During = async () =>
        {
            w.Clock.UtcNow += AzureSetupRules.Deadline + TimeSpan.FromMilliseconds(1);   // the engine used its whole deadline
            second = await w.State.TryBeginAsync("selfco");                                // another copy's check, before this one records
        };
        await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        Assert.Null(second);
    }

    [Fact]   // mutation: the lease ended after the check instead of in a finally (a check that throws would hold the tenant for the deadline)
    public async Task The_lease_is_ended_when_the_check_throws()
    {
        var w = new World();
        w.Check.During = () => throw new InvalidOperationException("the engine broke");
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Commands.RunAsync(w.Admin, w.Names, Token, default));
        Assert.Equal(w.Lease.Taken.Select(t => (t.Job, t.Holder)), w.Lease.Released);
        w.Check.During = null;
        w.Clock.UtcNow += TimeSpan.FromTicks(1);
        Assert.Equal(FeedCheckOutcome.Passed, (await w.Commands.RunAsync(w.Admin, w.Names, Token, default)).Outcome);   // not "A check is running"
    }

    [Fact]   // mutation: the lease ended after the check instead of in a finally, on the cancelled path
    public async Task The_lease_is_ended_when_the_caller_goes_away()
    {
        var w = new World();
        w.Check.Hang = true;
        using var gone = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w.Commands.RunAsync(w.Admin, w.Names, Token, gone.Token));
        Assert.Single(w.Lease.Released);
        Assert.Equal(w.Lease.Taken.Single().Holder, w.Lease.Released.Single().Holder);
    }

    [Fact]   // mutation: a failure to take the lease taken as "free" (two copies could run one tenant's check at once)
    public async Task When_the_lease_cannot_be_taken_the_check_is_refused_and_nothing_runs()
    {
        var w = new World();
        w.Lease.FailAcquire = new TimeoutException(FlakyScratch.Secret);
        Assert.Equal(AzureSetupRefusal.NotAvailable, await Refusal(() => w.Commands.RunAsync(w.Admin, w.Names, Token, default)));
        Assert.Empty(w.Check.Calls);
        Assert.Empty(w.Checks.Rows);
        Assert.Contains((SetupLogLevel.Warning, "Feed setup: taking the check lease failed (TimeoutException); the check is refused"), w.Log);
        OnlyScopeAndType(w);
    }

    [Fact]   // a failed release changes nothing but the log: the result stands, and the lease runs out by itself within the deadline
    public async Task When_the_lease_cannot_be_ended_the_result_stands_and_it_is_logged()
    {
        var w = new World();
        w.Lease.FailRelease = new TimeoutException(FlakyScratch.Secret);
        var held = await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        Assert.Equal(FeedCheckOutcome.Passed, held.Outcome);
        Assert.Single(w.Checks.Rows);
        Assert.Equal([(SetupLogLevel.Warning, "Feed setup: ending the check lease failed (TimeoutException); it runs out by itself within two minutes")], w.Log);
        w.Lease.FailRelease = null;
        w.Clock.UtcNow += AzureSetupRules.CheckLease + TimeSpan.FromTicks(1);
        Assert.NotNull(await w.State.TryBeginAsync("selfco"));
    }

    // ---------------------------------------------------------------- the scratch fails closed

    /// <summary>
    /// The confirm reads the last check's named admins. When that read fails the start is refused: a check run without them would confirm
    /// nobody while the person asked to confirm, and nothing else may stand in for them.
    /// </summary>
    /// <summary>The confirm reads the confirming person's own last check: another Admin of the tenant never sees or confirms the admins of Ada's.</summary>
    [Fact]   // mutations: the confirm (and the page) reading the check of whoever ran the tenant's last check, not the confirming person's
    public async Task Another_admin_never_confirms_the_admins_of_adas_check()
    {
        var w = new World();
        w.Finds(Ada);
        await w.Commands.RunAsync(w.Admin, w.Names, Token, default);   // Ada runs the check; it names Ada (the feed admin)
        var grace = new CustomerActor("selfco", Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"), CustomerRole.Admin, "Grace Hopper", Dir, w.Clock.UtcNow);
        Assert.Null((await w.Commands.ViewAsync(grace, default)).Held);
        Assert.Empty((await w.Commands.StartAsync(grace, "contoso", null, "public-packages", confirmAdmins: true, default)).ConfirmedAdmins);
        Assert.Equal(["desc-ada"], (await w.Commands.StartAsync(w.Admin, "contoso", null, "public-packages", confirmAdmins: true, default)).ConfirmedAdmins);
    }

    [Fact]   // mutation: the read failure taken as "nothing held" (the start goes on, confirming nobody)
    public async Task When_the_last_check_cannot_be_read_the_confirm_is_refused()
    {
        var w = new World();
        w.Finds(Ada);
        await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        w.Scratch.FailGet = new TimeoutException(FlakyScratch.Secret);

        Assert.Equal(AzureSetupRefusal.NotAvailable, await Refusal(() => w.Commands.StartAsync(w.Admin, "contoso", null, "public-packages", confirmAdmins: true, default)));
        Assert.Contains((SetupLogLevel.Warning, "Feed setup: reading the settings scratch failed (TimeoutException); no admin is confirmed and the check is refused"), w.Log);
        // Without the box ticked nothing is read, so a plain check still starts.
        Assert.Empty((await w.Commands.StartAsync(w.Admin, "contoso", null, "public-packages", confirmAdmins: false, default)).ConfirmedAdmins);
        OnlyScopeAndType(w);
    }

    /// <summary>
    /// A check whose result could not be kept leaves no earlier result behind: the earlier one was forgotten before the check ran, so
    /// "Confirm" can never confirm the admins of a check the page no longer reflects.
    /// </summary>
    [Fact]   // mutation: the earlier result not forgotten before the check (a store failure would confirm Ada, whom the latest check did not find)
    public async Task A_check_whose_result_cannot_be_kept_leaves_no_earlier_result_to_confirm()
    {
        var w = new World();
        w.Finds(Ada);
        await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        Assert.Equal(["desc-ada"], (await w.Commands.StartAsync(w.Admin, "contoso", null, "public-packages", confirmAdmins: true, default)).ConfirmedAdmins);

        w.Later();
        w.Finds(Bob);
        w.Scratch.FailPut = new TimeoutException(FlakyScratch.Secret);
        var held = await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        Assert.Equal(FeedCheckOutcome.Incomplete, held.Outcome);   // the result itself is as it was
        Assert.Equal(2, w.Checks.Rows.Count);                        // and recorded
        Assert.Contains((SetupLogLevel.Warning, "Feed setup: keeping the settings scratch failed (TimeoutException); the page shows the recorded result"), w.Log);

        w.Scratch.FailPut = null;
        Assert.Null(await w.State.GetAsync("selfco", Oid));
        Assert.Empty((await w.Commands.StartAsync(w.Admin, "contoso", null, "public-packages", confirmAdmins: true, default)).ConfirmedAdmins);
        Assert.Null((await w.Commands.ViewAsync(w.Admin, default)).Held);
        OnlyScopeAndType(w);
    }

    [Fact]   // mutation: the forget's failure ignored (an earlier result could then stand in for this check's)
    public async Task When_the_last_result_cannot_be_forgotten_the_check_is_refused_and_nothing_runs()
    {
        var w = new World();
        w.Finds(Ada);
        await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        w.Later();
        w.Scratch.FailDelete = new TimeoutException(FlakyScratch.Secret);

        Assert.Equal(AzureSetupRefusal.NotAvailable, await Refusal(() => w.Commands.RunAsync(w.Admin, w.Names, Token, default)));
        Assert.Single(w.Check.Calls);
        Assert.Single(w.Checks.Rows);
        Assert.Equal(2, w.Lease.Released.Count);   // ended, even so
        Assert.Contains((SetupLogLevel.Warning, "Feed setup: clearing the settings scratch failed (TimeoutException); the check is refused"), w.Log);
        OnlyScopeAndType(w);
    }

    [Fact]   // mutation: a read failure on the page thrown (the setup page would break with the store) or shown as a result
    public async Task When_the_scratch_cannot_be_read_the_page_shows_no_held_result_and_no_draft()
    {
        var w = new World();
        w.Finds(Ada);
        await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        await w.Commands.KeepDraftAsync(w.Admin, "contoso", null, "drafted", default);
        w.Scratch.FailGet = new TimeoutException(FlakyScratch.Secret);

        var view = await w.Commands.ViewAsync(w.Admin, default);
        Assert.Null(view.Held);
        Assert.Null(view.Draft);
        Assert.Equal(FeedCheckOutcome.Incomplete, view.Latest!.Outcome);   // the recorded result is still there to show
        Assert.Contains((SetupLogLevel.Warning, "Feed setup: reading the settings scratch failed (TimeoutException); the page shows the recorded result"), w.Log);
        Assert.Contains((SetupLogLevel.Warning, "Feed setup: reading the draft scratch failed (TimeoutException); step 1 is empty"), w.Log);
        OnlyScopeAndType(w);
    }

    [Fact]   // a draft that cannot be kept never stops the approval page: step 1 is only empty when they come back
    public async Task When_the_draft_cannot_be_kept_the_approval_page_still_opens()
    {
        var w = new World();
        w.Scratch.FailPut = new TimeoutException(FlakyScratch.Secret);
        Assert.Equal(Client, await w.Commands.KeepDraftAsync(w.Admin, "contoso", null, "drafted", default));
        w.Scratch.FailPut = null;
        Assert.Null((await w.Commands.ViewAsync(w.Admin, default)).Draft);
        Assert.Equal([(SetupLogLevel.Warning, "Feed setup: keeping the draft scratch failed (TimeoutException); step 1 is not kept")], w.Log);
    }

    [Fact]   // the caller's own cancellation is thrown, never logged as a store failure
    public async Task The_callers_cancellation_is_thrown_and_not_logged()
    {
        var w = new World();
        using var gone = new CancellationTokenSource();
        gone.Cancel();
        w.Scratch.FailGet = new OperationCanceledException(gone.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w.Commands.ViewAsync(w.Admin, gone.Token));
        Assert.Empty(w.Log);
    }

    // ---------------------------------------------------------------- the key

    [Fact]   // mutations: the held check, the developer proof and the draft keyed without the kind, the person or the tenant
    public async Task Each_persons_check_proof_and_draft_are_kept_apart()
    {
        var w = new World();
        var other = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
        var check = new HeldFeedCheck(w.Clock.UtcNow, null, "u", FeedCheckOutcome.Passed, [], [Ada], true);
        var proof = check with { Outcome = FeedCheckOutcome.Failed, NamedAdmins = [] };
        await w.State.HoldAsync("selfco", Oid, check);
        await w.State.HoldAsync("selfco", Oid, proof, FeedCheckKind.Developer);
        await w.State.KeepDraftAsync("selfco", Oid, FeedNamesDraft.From(w.Clock.UtcNow, "contoso", null, "drafted")!);

        SetupStates.SameCheck(check, await w.State.GetAsync("selfco", Oid));
        SetupStates.SameCheck(proof, await w.State.GetAsync("selfco", Oid, FeedCheckKind.Developer));
        Assert.Equal("drafted", (await w.State.GetDraftAsync("selfco", Oid))!.Feed);
        Assert.Null(await w.State.GetAsync("selfco", other));
        Assert.Null(await w.State.GetDraftAsync("selfco", other));
        Assert.Null(await w.State.GetAsync("acme", Oid));
        Assert.Null(await w.State.GetDraftAsync("acme", Oid));
        await w.State.ClearDraftAsync("selfco", Oid);
        Assert.Null(await w.State.GetDraftAsync("selfco", Oid));
        SetupStates.SameCheck(check, await w.State.GetAsync("selfco", Oid));   // clearing the draft leaves the check
    }

    /// <summary>
    /// The rule the in-memory state had, kept on top of the store's own expiry: a check or a draft from the future (the clock moved back
    /// since it was kept) reads as nothing, and one kept with an older time of its own lasts 30 minutes from that time, not from the write.
    /// </summary>
    [Fact]   // mutation: the state's own age rule dropped (only the store's expiry left)
    public async Task A_check_or_draft_from_the_future_or_older_than_30_minutes_by_its_own_time_reads_as_nothing()
    {
        var w = new World();
        var now = w.Clock.UtcNow;
        await w.State.HoldAsync("selfco", Oid, new HeldFeedCheck(now, null, "u", FeedCheckOutcome.Passed, [], [Ada], true));
        await w.State.KeepDraftAsync("selfco", Oid, FeedNamesDraft.From(now, "contoso", null, "drafted")!);
        w.Clock.UtcNow = now - TimeSpan.FromSeconds(1);   // the clock moved back
        Assert.Null(await w.State.GetAsync("selfco", Oid));
        Assert.Null(await w.State.GetDraftAsync("selfco", Oid));
        w.Clock.UtcNow = now;
        Assert.NotNull(await w.State.GetAsync("selfco", Oid));
        Assert.NotNull(await w.State.GetDraftAsync("selfco", Oid));

        await w.State.HoldAsync("selfco", Oid, new HeldFeedCheck(now - TimeSpan.FromMinutes(29), null, "u", FeedCheckOutcome.Passed, [], [Ada], true));
        w.Clock.UtcNow = now + TimeSpan.FromMinutes(1);   // 30 minutes after the check's own time, 1 after the write
        Assert.Null(await w.State.GetAsync("selfco", Oid));
    }

    /// <summary>Every field of a held check and a draft comes back from the scratch as it went in.</summary>
    [Fact]   // mutation: a field left out of the payload (the page would lose a finding, a withdrawal or a feed's names)
    public void A_held_check_and_a_draft_read_back_as_they_were_written()
    {
        var at = new DateTimeOffset(2026, 11, 12, 9, 30, 1, TimeSpan.Zero).AddTicks(1234567);
        var check = new HeldFeedCheck(at, new FeedNames("contoso", "Platform Team", "feed"), "https://pkgs.dev.azure.com/x/index.json", FeedCheckOutcome.Incomplete,
            [new CheckResult("bypass", "Bypass", CheckStatus.Warn, "two feeds", ["a feed", "b feed"]), new CheckResult("feed", "Feed", CheckStatus.Skip, "NOT VERIFIED")],
            [Ada, Bob], Recorded: true)
        { Finding = DeveloperFinding.ControlFailed, Withdrawn = true, WithdrawFailed = true };
        SetupStates.SameCheck(check, FeedSetupPayload.ReadCheck(FeedSetupPayload.Write(check)));
        Assert.Contains("\"Incomplete\"", FeedSetupPayload.Write(check));   // enums by name: a reordered enum never changes a kept row's meaning

        var draft = FeedNamesDraft.From(at, "contoso", "Platform Team", null)!;
        Assert.Equal(draft, FeedSetupPayload.ReadDraft(FeedSetupPayload.Write(draft)));
        Assert.Null(FeedSetupPayload.ReadCheck("not json"));
        Assert.Null(FeedSetupPayload.ReadCheck("{}"));
        Assert.Null(FeedSetupPayload.ReadDraft("[1]"));
    }
}
