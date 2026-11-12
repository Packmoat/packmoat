using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Application.Tests;

/// <summary>A developer probe that returns what the test says and records what it was asked.</summary>
internal sealed class FakeDeveloperProbe : IDeveloperFeedProbe
{
    public List<DeveloperProbeRequest> Requests { get; } = new();
    public string? LastToken { get; private set; }
    public bool Hang { get; set; }
    public DeveloperProbeRun Run { get; set; } = new(new[]
    {
        new CheckResult(DeveloperProbeVerdict.Id, DeveloperProbeVerdict.Title, CheckStatus.Pass, "refused"),
        new CheckResult(BypassAdvisory.Id, BypassAdvisory.Title, CheckStatus.Pass, "none"),
    }, DeveloperFinding.Refused);

    public async Task<DeveloperProbeRun> ProbeAsync(DeveloperProbeRequest request, string developerToken, CancellationToken ct)
    {
        Requests.Add(request);
        LastToken = developerToken;
        if (Hang) await Task.Delay(Timeout.Infinite, ct);
        return Run;
    }
}

/// <summary>
/// The developer step-up rule (through <see cref="CustomerSignIn.Decide"/>), the probe verdict shared with the CLI,
/// the bypass advisory, the private verdict, the GUID index URLs, and the developer proof's commands (who, when, what is recorded).
/// </summary>
public class DeveloperProofTests
{
    private const string Dir = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string OtherDir = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string AppClient = "11111111-2222-4333-8444-555555555555";
    private const string Client = "5a2ed000-0000-4000-8000-000000000001";
    private const string Token = "SENTINEL-DEV-TOKEN-77aa";
    private static readonly Guid Grace = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
    private static readonly Guid Ada = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static readonly Guid FeedId = Guid.Parse("0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f");
    private static readonly Guid ProjectId = Guid.Parse("7a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");
    private static readonly string Feed = $"https://pkgs.dev.azure.com/contoso/{ProjectId:D}/_packaging/{FeedId:D}/nuget/v3/index.json";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    // ---- the developer step-up

    private static CustomerActor Session(CustomerRole role = CustomerRole.Developer, Guid? oid = null) => new("selfco", oid ?? Grace, role, "Grace Hopper", Dir, Now);
    private static readonly DirectoryBinding Binding = new(Dir, "selfco", Now, "self-signup");

    private static VerifiedClaims Claims(string? role = CustomerSignIn.DeveloperRole, string tid = Dir, Guid? oid = null, string? acct = null, string? secondRole = null)
    {
        var roles = new List<string>();
        if (role is not null) roles.Add(role);
        if (secondRole is not null) roles.Add(secondRole);
        return new VerifiedClaims(new[] { $"https://login.microsoftonline.com/{tid}/v2.0" }, new[] { AppClient }, new[] { tid }, new[] { (oid ?? Grace).ToString("D") },
            "Grace Hopper", roles, acct is null ? null : new[] { acct });
    }

    private static StepUpRefusal? Check(VerifiedClaims claims, CustomerActor? session = null, bool autoJoin = false, DateTimeOffset? authTime = null, DirectoryBinding? binding = null)
    {
        var decision = CustomerSignIn.Decide(claims, AppClient, binding ?? Binding, autoJoin: false);
        if (decision is SignInDecision.Denied { Reason: SignInDenial.NoRole } && autoJoin) decision = CustomerSignIn.Decide(claims, AppClient, binding ?? Binding, autoJoin: true);
        return AzureSetupRules.CheckDeveloperStepUp(session ?? Session(), claims.DirectoryIds, claims.ObjectIds, decision, authTime ?? Now, Now);
    }

    [Fact]
    public void A_developer_signed_in_just_now_passes_the_developer_step_up() => Assert.Null(Check(Claims()));

    [Fact]
    public void An_admin_or_someone_with_both_roles_is_refused_as_the_developer()
    {
        Assert.Equal(StepUpRefusal.NotDeveloper, Check(Claims(CustomerSignIn.AdminRole)));
        Assert.Equal(StepUpRefusal.NotDeveloper, Check(Claims(CustomerSignIn.DeveloperRole, secondRole: CustomerSignIn.AdminRole)));
        // and an Admin session never passes, whatever the new token says
        Assert.Equal(StepUpRefusal.NotDeveloper, Check(Claims(), Session(CustomerRole.Admin)));
    }

    [Fact]
    public void An_auto_joined_member_passes_only_while_auto_join_is_on()
    {
        Assert.Null(Check(Claims(role: null, acct: "0"), autoJoin: true));
        Assert.Equal(StepUpRefusal.SignInRefused, Check(Claims(role: null, acct: "0"), autoJoin: false));
        Assert.Equal(StepUpRefusal.SignInRefused, Check(Claims(role: null, acct: "1"), autoJoin: true));   // a guest never auto-joins
    }

    [Fact]
    public void Another_person_another_directory_or_two_ids_are_refused()
    {
        Assert.Equal(StepUpRefusal.DifferentPerson, Check(Claims(oid: Ada)));
        Assert.Equal(StepUpRefusal.DifferentPerson, Check(Claims(tid: OtherDir), binding: new DirectoryBinding(OtherDir, "selfco", Now, "x")));
        var two = Claims() with { ObjectIds = new[] { Grace.ToString("D"), Grace.ToString("D") } };
        Assert.Equal(StepUpRefusal.DifferentPerson, AzureSetupRules.CheckDeveloperStepUp(Session(), two.DirectoryIds, two.ObjectIds,
            CustomerSignIn.Decide(two, AppClient, Binding), Now, Now));
        // the decision let in someone of another tenant (the binding moved): not this session's person
        var moved = new SignInDecision.Allowed(Guid.Parse(Dir), Grace, "othertenant", CustomerRole.Developer, "Grace");
        Assert.Equal(StepUpRefusal.DifferentPerson, AzureSetupRules.CheckDeveloperStepUp(Session(), new[] { Dir }, new[] { Grace.ToString("D") }, moved, Now, Now));
        // the directory is not linked any more
        Assert.Equal(StepUpRefusal.SignInRefused, AzureSetupRules.CheckDeveloperStepUp(Session(), new[] { Dir }, new[] { Grace.ToString("D") },
            CustomerSignIn.Decide(Claims(), AppClient, null), Now, Now));
    }

    [Fact]
    public void A_stale_unknown_or_future_sign_in_time_is_not_fresh()
    {
        Assert.Equal(StepUpRefusal.NotFresh, Check(Claims(), authTime: Now - TimeSpan.FromMinutes(16)));
        Assert.Null(Check(Claims(), authTime: Now - TimeSpan.FromMinutes(15)));
        var decision = CustomerSignIn.Decide(Claims(), AppClient, Binding);
        Assert.Equal(StepUpRefusal.NotFresh, AzureSetupRules.CheckDeveloperStepUp(Session(), new[] { Dir }, new[] { Grace.ToString("D") }, decision, null, Now));
        Assert.Equal(StepUpRefusal.NotFresh, Check(Claims(), authTime: Now + TimeSpan.FromMinutes(5)));
    }

    // ---- the verdict (shared with the CLI)

    [Fact]
    public void Both_controls_must_pass_before_the_index_status_counts()
    {
        Assert.Null(DeveloperProbeVerdict.ControlFailure(200));
        foreach (var code in new[] { 401, 403, 404, 203, 302, 500 })
        {
            var failure = DeveloperProbeVerdict.ControlFailure(code)!;
            Assert.Contains($"HTTP {code}", failure);
            var d = DeveloperProbeVerdict.ControlNotMet(failure);
            Assert.Equal((DeveloperFinding.ControlFailed, CheckStatus.Skip), (d.Finding, d.Status));
            Assert.StartsWith("NOT VERIFIED: control failed", d.Detail);
        }
        Assert.Null(DeveloperProbeVerdict.ListControl(200));
        foreach (var code in new[] { 401, 403, 404, 203, 302, 500 })
            Assert.Equal((DeveloperFinding.NotMember, CheckStatus.Skip), (DeveloperProbeVerdict.ListControl(code)!.Finding, DeveloperProbeVerdict.ListControl(code)!.Status));
        Assert.Equal((DeveloperFinding.NotMember, CheckStatus.Skip), (DeveloperProbeVerdict.ListControl(200, readable: false)!.Finding, DeveloperProbeVerdict.ListControl(200, readable: false)!.Status));
        var listed = DeveloperProbeVerdict.ListControl(200, listsPublicFeed: true)!;
        Assert.Equal((DeveloperFinding.CanRead, CheckStatus.Fail), (listed.Finding, listed.Status));
    }

    [Theory]
    [InlineData(401, CheckStatus.Pass, DeveloperFinding.Refused)]
    [InlineData(403, CheckStatus.Pass, DeveloperFinding.Refused)]
    [InlineData(200, CheckStatus.Fail, DeveloperFinding.CanRead)]
    [InlineData(404, CheckStatus.Skip, DeveloperFinding.Unclear)]   // the I0 decision: never a refusal until seen live
    [InlineData(203, CheckStatus.Skip, DeveloperFinding.Unclear)]
    [InlineData(302, CheckStatus.Skip, DeveloperFinding.Unclear)]
    [InlineData(429, CheckStatus.Skip, DeveloperFinding.Unclear)]
    [InlineData(500, CheckStatus.Skip, DeveloperFinding.Unclear)]
    [InlineData(204, CheckStatus.Skip, DeveloperFinding.Unclear)]
    public void The_index_status_decides_only_401_and_403_as_a_refusal(int code, CheckStatus status, DeveloperFinding finding)
    {
        var d = DeveloperProbeVerdict.Index(code);
        Assert.Equal((finding, status), (d.Finding, d.Status));
        Assert.Contains(code.ToString(), d.Detail);
    }

    [Fact]
    public void The_outcome_follows_the_developer_result_and_never_the_advisory()
    {
        static IReadOnlyList<CheckResult> R(CheckStatus developer, CheckStatus bypass) => new[]
        {
            new CheckResult("developer", "d", developer, ""), new CheckResult("bypass", "b", bypass, ""),
        };
        Assert.Equal(FeedCheckOutcome.Passed, DeveloperProbeVerdict.Outcome(R(CheckStatus.Pass, CheckStatus.Warn)));
        Assert.Equal(FeedCheckOutcome.Passed, DeveloperProbeVerdict.Outcome(R(CheckStatus.Pass, CheckStatus.Skip)));
        Assert.Equal(FeedCheckOutcome.Failed, DeveloperProbeVerdict.Outcome(R(CheckStatus.Fail, CheckStatus.Pass)));
        Assert.Equal(FeedCheckOutcome.Incomplete, DeveloperProbeVerdict.Outcome(R(CheckStatus.Skip, CheckStatus.Pass)));
        Assert.Equal(FeedCheckOutcome.Incomplete, DeveloperProbeVerdict.Outcome(Array.Empty<CheckResult>()));
    }

    // ---- bypass

    private static VisibleFeed F(string name, params FeedUpstream[] ups) => new(Guid.NewGuid().ToString("D"), name, ups);
    private static FeedUpstream Public(string protocol) => new(protocol, "public", null);

    [Fact]
    public void Another_feed_with_a_public_nuget_upstream_any_case_is_a_bypass_warning_with_its_name()
    {
        var r = BypassAdvisory.Check(new[] { F("private", Public("NuGet"), Public("npm")), F("tools", Public("Maven")), F("other", Public("nuget")) }, FeedId);
        Assert.Equal((BypassAdvisory.Id, CheckStatus.Warn), (r.Id, r.Status));
        Assert.Equal(string.Format(BypassAdvisory.FoundSentence, "private, other"), r.Detail);
    }

    [Fact]
    public void An_internal_upstream_to_packmoats_feed_is_a_bypass_but_one_to_another_feed_is_not()
    {
        var toPackmoat = BypassAdvisory.Check(new[] { F("team", new FeedUpstream("nuget", "internal", FeedId.ToString("D"))) }, FeedId);
        Assert.Equal(CheckStatus.Warn, toPackmoat.Status);
        Assert.Contains("team", toPackmoat.Detail);
        var elsewhere = BypassAdvisory.Check(new[] { F("team", new FeedUpstream("nuget", "internal", Guid.NewGuid().ToString("D"))) }, FeedId);
        Assert.Equal(CheckStatus.Pass, elsewhere.Status);
    }

    [Fact]
    public void Packmoats_own_feed_is_not_a_bypass_and_no_other_feed_passes()
    {
        var r = BypassAdvisory.Check(new[] { new VisibleFeed(FeedId.ToString("D").ToUpperInvariant(), "public-packages", new[] { Public("nuget") }), F("tools", Public("Maven")) }, FeedId);
        Assert.Equal(CheckStatus.Pass, r.Status);
        Assert.Equal(string.Format(BypassAdvisory.NoneSentence, 1), r.Detail);
        Assert.Equal(CheckStatus.Pass, BypassAdvisory.Check(Array.Empty<VisibleFeed>(), FeedId).Status);
    }

    [Fact]
    public void Missing_upstream_sources_are_reported_as_not_checked_never_as_a_pass()
    {
        var r = BypassAdvisory.Check(new[] { new VisibleFeed(Guid.NewGuid().ToString("D"), "hidden", null), F("tools") }, FeedId);
        Assert.Equal(CheckStatus.Skip, r.Status);
        Assert.StartsWith("Bypass not checked", r.Detail);
        // a bypass that is found still wins: it is certain
        Assert.Equal(CheckStatus.Warn, BypassAdvisory.Check(new[] { new VisibleFeed(Guid.NewGuid().ToString("D"), "hidden", null), F("p", Public("nuget")) }, FeedId).Status);
        Assert.Equal(CheckStatus.Skip, BypassAdvisory.NotChecked().Status);
    }

    // ---- private

    private static PrivateProbe P(string name, int? status) => new(name, status);

    [Fact]
    public void Private_passes_only_with_identity_the_control_and_every_probe_refused()
    {
        var pass = PrivateFeedVerdict.Decide(CheckStatus.Pass, 200, 2, new[] { P("a", 401), P("b", 403), P("b@Local", 401) });
        Assert.Equal((PrivateFeedVerdict.Id, CheckStatus.Pass), (pass.Id, pass.Status));
        Assert.Equal(string.Format(PrivateFeedVerdict.PassSentence, 2), pass.Detail);
        Assert.Contains("Feeds your account can't see weren't checked", pass.Detail);
        Assert.Equal(CheckStatus.Skip, PrivateFeedVerdict.Decide(CheckStatus.Warn, 200, 1, new[] { P("a", 401) }).Status);
        Assert.Equal(CheckStatus.Skip, PrivateFeedVerdict.Decide(CheckStatus.Pass, 401, 1, new[] { P("a", 401) }).Status);
        Assert.Equal(CheckStatus.Skip, PrivateFeedVerdict.Decide(CheckStatus.Pass, null, 1, new[] { P("a", 401) }).Status);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    public void Any_200_or_redirect_fails_with_the_names_whatever_else_is_unclear(int code)
    {
        var r = PrivateFeedVerdict.Decide(CheckStatus.Skip, null, 3, new[] { P("hr", code), P("finance@Release", code), P("a", 404), P("b", 401) });
        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Equal("Packmoat feed access can read 2 other feeds: hr, finance@Release. " + PrivateFeedVerdict.FailHint, r.Detail);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(203)]
    [InlineData(500)]
    [InlineData(null)]
    public void Anything_but_a_refusal_is_not_verified(int? code)
    {
        var r = PrivateFeedVerdict.Decide(CheckStatus.Pass, 200, 1, new[] { P("hr", code) });
        Assert.Equal(CheckStatus.Skip, r.Status);
        Assert.StartsWith("NOT VERIFIED", r.Detail);
        Assert.Contains("hr", r.Detail);
        Assert.Equal(CheckStatus.Skip, PrivateFeedVerdict.TooMany(51, 51, SupportContact.None).Status);
    }

    // ---- GUID index URLs

    [Fact]
    public void Index_urls_are_built_only_from_guids_and_parsed_back()
    {
        var view = Guid.Parse("11111111-1111-4111-8111-111111111111");
        Assert.Equal(Feed, GuidIndexUrls.Build("Contoso", ProjectId, FeedId));
        Assert.Equal($"https://pkgs.dev.azure.com/contoso/_packaging/{FeedId:D}@{view:D}/nuget/v3/index.json", GuidIndexUrls.Build("contoso", null, FeedId, view));
        Assert.Null(GuidIndexUrls.Build("con/toso", null, FeedId));
        Assert.Null(GuidIndexUrls.Build("contoso", null, Guid.Empty));
        Assert.True(GuidIndexUrls.IsGuidIndexUrl(Feed));
        foreach (var bad in new[] { Feed + "?x=1", Feed.Replace("contoso", "Contoso"), "https://pkgs.dev.azure.com/contoso/_packaging/public-packages/nuget/v3/index.json",
                     Feed.Replace("pkgs.dev.azure.com", "evil.example"), "http" + Feed[5..], Feed.Replace("/nuget/v3/index.json", "/nuget/v3/flat2/x") })
            Assert.False(GuidIndexUrls.IsGuidIndexUrl(bad), bad);
        Assert.Equal(("contoso", (Guid?)ProjectId, FeedId), GuidIndexUrls.Parse(Feed));
        Assert.Equal(("contoso", (Guid?)null, FeedId), GuidIndexUrls.Parse($"https://pkgs.dev.azure.com/contoso/_packaging/{FeedId:D}/nuget/v3/index.json"));
        Assert.Null(GuidIndexUrls.Parse("https://pkgs.dev.azure.com/contoso/_packaging/public-packages/nuget/v3/index.json"));
    }

    // ---- the commands

    private sealed class World
    {
        public FakeStore Tenants { get; } = new();
        public FakeDirectories Directories { get; } = new();
        public FakeSharedStore Shared { get; } = new();
        public FakeFeedChecks Checks { get; } = new();
        public FakeDelegatedCheck Check { get; } = new();
        public FakeDeveloperProbe Probe { get; } = new();
        public FakeAutoJoinStore AutoJoin { get; } = new();
        public MovingClock Clock { get; } = new() { UtcNow = Now };
        public AzureSetupState State { get; }
        public AzureSetupCommands Commands { get; }

        public World(string flag = "on", TenantOrigin origin = TenantOrigin.Self, bool settings = true, TimeSpan? deadline = null, bool probe = true)
        {
            State = SetupStates.New(Clock);
            Tenants.Tenants.Add(new Tenant("selfco", "Selfco", "", "", "", new TenantPolicy(14)));
            Directories.Rows[Dir] = new DirectoryBinding(Dir, "selfco", Clock.UtcNow, "self-signup");
            Tenants.Setups["selfco"] = new TenantSetup(origin, FeedSetup.Operator);
            Shared.Slots[SharedAppSlot.Active] = new SharedAppCredential(Client, "PEM");
            if (settings) Checks.Rows.Add(SettingsRow());
            Commands = new AzureSetupCommands(Tenants, Directories, Shared, Checks, Check, State, AzureSetupSettings.Parse(flag), Clock, new InProcessRateCounter(Clock), deadline,
                developerProbe: probe ? Probe : null, autoJoin: AutoJoin);
        }

        public FeedCheck SettingsRow(TimeSpan? age = null, FeedCheckOutcome outcome = FeedCheckOutcome.Passed, string client = Client, string url = "") =>
            new FeedCheck("selfco", Clock.UtcNow - (age ?? TimeSpan.FromHours(1)), Dir, Ada.ToString("D"), "Ada Lovelace", Dir, url.Length > 0 ? url : Feed, client, outcome,
                AzureSetupRules.SettingsChecks.Append("project").Select(id => new CheckResult(id, id, CheckStatus.Pass, "ok")).ToList(), Array.Empty<string>()) { Id = Checks.Rows.Count + 1 };

        public CustomerActor Developer => new("selfco", Grace, CustomerRole.Developer, "Grace Hopper", Dir, Clock.UtcNow);
        public CustomerActor Admin => new("selfco", Ada, CustomerRole.Admin, "Ada Lovelace", Dir, Clock.UtcNow);
    }

    private static async Task<AzureSetupRefusal> Refusal(Func<Task> act) => (await Assert.ThrowsAsync<AzureSetupRefusedException>(act)).Reason;

    [Fact]
    public async Task The_developer_proof_targets_the_latest_settings_checks_feed_and_records_a_developer_row()
    {
        var w = new World();
        var view = await w.Commands.DeveloperViewAsync(w.Developer, default);
        Assert.Equal(Feed, view.Settings.FeedIndexUrl);
        await w.Commands.StartDeveloperAsync(w.Developer, default);

        var held = await w.Commands.RunDeveloperAsync(w.Developer, Token, default);
        Assert.Equal((FeedCheckOutcome.Passed, DeveloperFinding.Refused), (held.Outcome, held.Finding));
        Assert.Equal(new DeveloperProbeRequest(Dir, Feed), Assert.Single(w.Probe.Requests));
        Assert.Equal(Token, w.Probe.LastToken);
        var row = w.Checks.Rows.Last();
        Assert.Equal(FeedCheckKind.Developer, row.Kind);
        Assert.Equal(("selfco", Dir, Grace.ToString("D"), "Grace Hopper", Dir, Feed, Client), (row.TenantId, row.CheckedByTid, row.CheckedByOid, row.CheckedByName, row.AzureTenantId, row.FeedIndexUrl, row.ClientId));
        Assert.Equal(new[] { "developer", "bypass" }, row.Results.Select(r => r.Id));
        Assert.Empty(row.ConfirmedAdmins);
        SetupStates.SameCheck(held, await w.State.GetAsync("selfco", Grace, FeedCheckKind.Developer));
        Assert.Null(await w.State.GetAsync("selfco", Grace));   // held apart from any settings check
        // and it is the row Connect needs, with the Admin's settings row
        Assert.Null(ConnectRules.Check(w.Checks.Rows[0], row, w.Clock.UtcNow));
    }

    [Fact]
    public async Task An_admin_is_refused_as_the_developer_everywhere()
    {
        var w = new World();
        Assert.Equal(AzureSetupRefusal.DevelopersOnly, await Refusal(() => w.Commands.DeveloperViewAsync(w.Admin, default)));
        Assert.Equal(AzureSetupRefusal.DevelopersOnly, await Refusal(() => w.Commands.StartDeveloperAsync(w.Admin, default)));
        Assert.Equal(AzureSetupRefusal.DevelopersOnly, await Refusal(() => w.Commands.RunDeveloperAsync(w.Admin, Token, default)));
        Assert.False(await w.Commands.ShowDeveloperCardAsync(w.Admin, default));
        Assert.Empty(w.Probe.Requests);
        Assert.Single(w.Checks.Rows);
    }

    [Fact]
    public async Task Not_ready_until_the_latest_settings_check_passed_within_seven_days_for_the_active_client()
    {
        var none = new World(settings: false);
        Assert.Equal(AzureSetupRefusal.NotReady, await Refusal(() => none.Commands.DeveloperViewAsync(none.Developer, default)));
        Assert.False(await none.Commands.ShowDeveloperCardAsync(none.Developer, default));

        var w = new World(settings: false);
        w.Checks.Rows.Add(w.SettingsRow(age: TimeSpan.FromDays(7)));   // the edge counts
        await w.Commands.DeveloperViewAsync(w.Developer, default);
        w.Clock.UtcNow += TimeSpan.FromSeconds(1);
        Assert.Equal(AzureSetupRefusal.NotReady, await Refusal(() => w.Commands.DeveloperViewAsync(w.Developer, default)));

        foreach (var latest in new[]
                 {
                     w.SettingsRow(outcome: FeedCheckOutcome.Incomplete), w.SettingsRow(outcome: FeedCheckOutcome.Failed),
                     w.SettingsRow(client: "a991c000-0000-4000-8000-000000000000"),
                     w.SettingsRow(url: "https://pkgs.dev.azure.com/contoso/_packaging/public-packages/nuget/v3/index.json"),
                     w.SettingsRow() with { AzureTenantId = OtherDir },
                 })
        {
            var x = new World(settings: false);
            x.Checks.Rows.Add(x.SettingsRow(age: TimeSpan.FromHours(2)));   // an older pass never counts once a newer check exists
            x.Checks.Rows.Add(latest with { Id = 2 });
            Assert.Equal(AzureSetupRefusal.NotReady, await Refusal(() => x.Commands.RunDeveloperAsync(x.Developer, Token, default)));
            Assert.Empty(x.Probe.Requests);
        }
    }

    [Theory]
    [InlineData("off")]
    [InlineData(OtherDir)]
    public async Task With_the_switch_off_for_the_directory_the_developer_proof_does_not_exist(string flag)
    {
        var w = new World(flag);
        Assert.Equal(AzureSetupRefusal.Disabled, await Refusal(() => w.Commands.DeveloperViewAsync(w.Developer, default)));
        Assert.False(await w.Commands.ShowDeveloperCardAsync(w.Developer, default));
    }

    [Fact]
    public async Task An_operator_made_tenant_or_no_probe_gets_no_developer_proof()
    {
        var op = new World(origin: TenantOrigin.Operator);
        Assert.Equal(AzureSetupRefusal.ReadOnly, await Refusal(() => op.Commands.DeveloperViewAsync(op.Developer, default)));
        var noProbe = new World(probe: false);
        Assert.Equal(AzureSetupRefusal.NotAvailable, await Refusal(() => noProbe.Commands.DeveloperViewAsync(noProbe.Developer, default)));
        var unlinked = new World();
        unlinked.Directories.Rows.Clear();
        Assert.Equal(AzureSetupRefusal.NotLinked, await Refusal(() => unlinked.Commands.DeveloperViewAsync(unlinked.Developer, default)));
    }

    [Fact]
    public async Task Ten_developer_starts_an_hour_and_one_check_at_a_time_per_tenant()
    {
        var w = new World();
        for (var i = 0; i < AzureSetupRules.MaxChecksPerWindow; i++) await w.Commands.StartDeveloperAsync(w.Developer, default);
        Assert.Equal(AzureSetupRefusal.TooManyRequests, await Refusal(() => w.Commands.StartDeveloperAsync(w.Developer, default)));
        var admins = await w.State.TryBeginAsync("selfco");   // an Admin's check is running
        Assert.NotNull(admins);
        Assert.Equal(AzureSetupRefusal.CheckRunning, await Refusal(() => w.Commands.RunDeveloperAsync(w.Developer, Token, default)));
        await w.State.EndAsync("selfco", admins!);
        w.Clock.UtcNow += TimeSpan.FromTicks(1);   // a released lease is free once time has moved past its end
        await w.Commands.RunDeveloperAsync(w.Developer, Token, default);
        w.Clock.UtcNow += TimeSpan.FromTicks(1);
        Assert.NotNull(await w.State.TryBeginAsync("selfco"));   // freed afterwards
    }

    [Fact]
    public async Task A_probe_past_the_deadline_is_recorded_as_incomplete_and_the_token_is_scrubbed()
    {
        var w = new World(deadline: TimeSpan.FromMilliseconds(50));
        w.Probe.Hang = true;
        var held = await w.Commands.RunDeveloperAsync(w.Developer, Token, default);
        Assert.Equal(FeedCheckOutcome.Incomplete, held.Outcome);
        Assert.Equal((CheckStatus.Skip, AzureSetupCommands.OutOfTime), (held.Results[0].Status, held.Results[0].Detail));
        Assert.Equal(DeveloperFinding.Unclear, held.Finding);

        var leaky = new World();
        leaky.Probe.Run = new DeveloperProbeRun(new[] { new CheckResult("developer", "t " + Token, CheckStatus.Fail, "got 200 with " + Token) }, DeveloperFinding.CanRead);
        var r = await leaky.Commands.RunDeveloperAsync(leaky.Developer, Token, default);
        Assert.Equal(FeedCheckOutcome.Failed, r.Outcome);
        var leaked = leaky.Checks.Rows.Last().Results.Any(x => x.Detail.Contains(Token, StringComparison.Ordinal) || x.Title.Contains(Token, StringComparison.Ordinal));
        Assert.False(leaked, "a recorded result carried the token");   // never DoesNotContain(token): its message would print it
    }

    [Fact]
    public async Task The_developer_card_shows_until_a_passing_proof_of_that_feed_and_never_once_connected()
    {
        var w = new World();
        Assert.True(await w.Commands.ShowDeveloperCardAsync(w.Developer, default));
        w.Probe.Run = w.Probe.Run with { Results = new[] { new CheckResult("developer", "d", CheckStatus.Fail, "200") }, Finding = DeveloperFinding.CanRead };
        await w.Commands.RunDeveloperAsync(w.Developer, Token, default);
        Assert.True(await w.Commands.ShowDeveloperCardAsync(w.Developer, default));   // a failed proof: still needed
        w.Clock.UtcNow += TimeSpan.FromSeconds(1);   // the next proof, a moment later: the first one's lease is over
        w.Probe.Run = new FakeDeveloperProbe().Run;
        await w.Commands.RunDeveloperAsync(w.Developer, Token, default);
        Assert.False(await w.Commands.ShowDeveloperCardAsync(w.Developer, default));
        // a newer settings check needs a newer proof
        w.Clock.UtcNow += TimeSpan.FromMinutes(5);
        w.Checks.Rows.Add(w.SettingsRow(age: TimeSpan.Zero));
        Assert.True(await w.Commands.ShowDeveloperCardAsync(w.Developer, default));
    }

    [Fact]
    public async Task The_admin_page_waits_for_a_developer_and_says_how_people_join()
    {
        var w = new World();
        var view = await w.Commands.ViewAsync(w.Admin, default);
        Assert.True(view.WaitingForDeveloper);
        Assert.True(view.AutoJoinOn);   // no row: on
        await w.AutoJoin.SetAsync("selfco", false, Now, "test", default);
        Assert.False((await w.Commands.ViewAsync(w.Admin, default)).AutoJoinOn);

        await w.Commands.RunDeveloperAsync(w.Developer, Token, default);
        var proven = await w.Commands.ViewAsync(w.Admin, default);
        Assert.NotNull(proven.Connect);
        Assert.False(proven.WaitingForDeveloper);

        var notYet = new World(settings: false);
        notYet.Checks.Rows.Add(notYet.SettingsRow(outcome: FeedCheckOutcome.Incomplete));
        Assert.False((await notYet.Commands.ViewAsync(notYet.Admin, default)).WaitingForDeveloper);
    }
}
