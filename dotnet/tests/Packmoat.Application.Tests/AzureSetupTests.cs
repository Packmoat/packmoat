using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Application.Tests;

internal sealed class MovingClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
}

internal sealed class FakeFeedChecks : IFeedCheckStore
{
    public List<FeedCheck> Rows { get; } = new();
    public List<string> Calls { get; } = new();
    public Task<long> RecordAsync(FeedCheck check, CancellationToken ct) { Calls.Add("record"); Rows.Add(check with { Id = Rows.Count + 1 }); return Task.FromResult((long)Rows.Count); }
    public Task<FeedCheck?> LatestAsync(string tenantId, FeedCheckKind kind, CancellationToken ct) => Task.FromResult(Rows.LastOrDefault(r => r.TenantId == tenantId && r.Kind == kind));
    /// <summary>When set, the blanking throws it (after noting the call).</summary>
    public Exception? BlankThrows { get; set; }
    public Task<int> BlankPersonalDataAsync(DateTimeOffset checkedBefore, CancellationToken ct)
    {
        Calls.Add("blank " + checkedBefore.ToString("O"));
        return BlankThrows is { } ex ? Task.FromException<int>(ex) : Task.FromResult(0);
    }
}

/// <summary>A delegated check that returns what the test says, records what it was asked, and can hang until cancelled.</summary>
internal sealed class FakeDelegatedCheck : IDelegatedFeedCheck
{
    public List<string> Calls { get; } = new();
    public DelegatedCheckRun Run { get; set; } = new("https://pkgs.dev.azure.com/contoso/_packaging/" + Guid.Empty.ToString("D").Replace('0', '1') + "/nuget/v3/index.json",
        AzureSetupRules.SettingsChecks.Select(id => new CheckResult(id, id, CheckStatus.Pass, "ok")).ToList(), Array.Empty<NamedFeedAdmin>());
    public bool Hang { get; set; }
    /// <summary>Runs while the check is "in flight" (after it started, before it returns): something else changing the tenant meanwhile.</summary>
    public Func<Task>? During { get; set; }
    public DelegatedCheckRequest? LastRequest { get; private set; }
    public string? LastToken { get; private set; }

    private async Task<DelegatedCheckRun> Go(string what, DelegatedCheckRequest request, string token, CancellationToken ct)
    {
        Calls.Add(what);
        LastRequest = request;
        LastToken = token;
        if (Hang) await Task.Delay(Timeout.Infinite, ct);
        if (During is not null) await During();
        return Run;
    }

    public Task<DelegatedCheckRun> CheckNewFeedAsync(FeedNames names, DelegatedCheckRequest request, string accessToken, CancellationToken ct) => Go($"new {names.Organization}/{names.Project}/{names.Feed}", request, accessToken, ct);
    public Task<DelegatedCheckRun> CheckStoredFeedAsync(Tenant tenant, DelegatedCheckRequest request, string accessToken, CancellationToken ct) => Go("stored " + tenant.Id, request, accessToken, ct);
}

public class AzureSetupTests
{
    private const string Dir = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string OtherDir = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string Client = "5a2ed000-0000-4000-8000-000000000001";
    private const string Token = "SENTINEL-TOKEN-0d1e2f";
    private static readonly Guid Oid = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static readonly Guid ProjectId = Guid.Parse("7a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");
    private static readonly Guid FeedId = Guid.Parse("0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f");

    // ---- names

    [Theory]
    [InlineData("a")]
    [InlineData("contoso")]
    [InlineData("Contoso-2")]
    [InlineData("a-b-c")]
    [InlineData("abcdefghijabcdefghijabcdefghijabcdefghijabcdefghij")]   // 50
    public void Organisation_names_that_azure_devops_allows_pass(string org) => Assert.True(AzureFeedNames.IsOrganization(org));

    [Theory]
    [InlineData("")]
    [InlineData("-contoso")]
    [InlineData("contoso-")]
    [InlineData("con_toso")]
    [InlineData("con.toso")]
    [InlineData("con toso")]
    [InlineData("contoso\n")]                       // "$" would have let this through
    [InlineData("abcdefghijabcdefghijabcdefghijabcdefghijabcdefghijk")]   // 51
    [InlineData("ｃｏｎｔｏｓｏ")]                   // full-width letters
    [InlineData("contoso/other")]
    [InlineData("..")]
    [InlineData("%2e%2e")]
    [InlineData("https://dev.azure.com/contoso")]
    [InlineData("contoso?x")]
    [InlineData("contoso#x")]
    [InlineData("contóso")]                    // a combining mark
    public void Other_organisation_names_are_refused(string org) => Assert.False(AzureFeedNames.IsOrganization(org));

    [Theory]
    [InlineData("public-packages")]
    [InlineData("feed.v2")]
    [InlineData("a_b")]
    [InlineData("0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f")]   // plain, so it passes here; the resolve's name check refuses an id
    [InlineData("abcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghij1234")]   // 64
    public void Plain_feed_names_up_to_64_characters_pass(string feed) => Assert.True(AzureFeedNames.IsFeed(feed));

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData("%2e%2e")]
    [InlineData("a%2fb")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a?b")]
    [InlineData("a#b")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    [InlineData("ｆｅｅｄ")]                  // full-width
    [InlineData("feed／x")]                  // full-width solidus
    [InlineData("fééd")]
    [InlineData("abcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghij12345")]   // 65
    public void Other_feed_names_are_refused(string feed) => Assert.False(AzureFeedNames.IsFeed(feed));

    [Theory]
    [InlineData("Platform")]
    [InlineData("Platform Team")]
    [InlineData("Projet été")]
    [InlineData("a-b_c.d")]
    public void Project_names_may_hold_spaces_and_letters(string project) => Assert.True(AzureFeedNames.IsProject(project));

    [Theory]
    [InlineData(" Platform")]
    [InlineData("Platform ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a%2eb")]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData(".hidden")]
    [InlineData("_x")]
    [InlineData("ends.")]
    [InlineData("a?b")]
    [InlineData("a#b")]
    [InlineData("a:b")]
    [InlineData("a\u0000b")]
    [InlineData("a​b")]               // zero-width space
    [InlineData("a\tb")]
    [InlineData("ａ／ｂ")]                  // full-width
    [InlineData("abcdefghijabcdefghijabcdefghijabcdefghijabcdefghijabcdefghij12345")]   // 65
    public void Other_project_names_are_refused(string project) => Assert.False(AzureFeedNames.IsProject(project));

    [Fact]
    public void Validate_says_which_names_were_refused_and_an_empty_project_means_organisation_scoped()
    {
        var (names, problems) = AzureFeedNames.Validate("contoso", "  ", "public-packages");
        Assert.Empty(problems);
        Assert.Equal(new FeedNames("contoso", null, "public-packages"), names);
        var (none, all) = AzureFeedNames.Validate("-x", "a/b", "a b");
        Assert.Null(none);
        Assert.Equal(new[] { FeedNameProblem.Organization, FeedNameProblem.Project, FeedNameProblem.Feed }, all);
        Assert.Equal(new[] { FeedNameProblem.Organization, FeedNameProblem.Feed }, AzureFeedNames.Validate(null, null, null).Problems);
    }

    // ---- resolve and build

    private static readonly FeedNames Typed = new("Contoso", "Platform Team", "public-packages");

    [Fact]
    public void A_resolved_project_feed_builds_GUID_urls_under_the_tight_prefix_with_the_organisation_in_lower_case()
    {
        var (feed, problem) = FeedResolution.Validate(Typed, FeedId.ToString("D"), "Public-Packages", true, ProjectId.ToString("D"), "platform team");
        Assert.Null(problem);
        Assert.Equal($"contoso/{ProjectId:D}/_packaging/{FeedId:D}/", feed!.PathPrefix);
        Assert.Equal($"https://pkgs.dev.azure.com/contoso/{ProjectId:D}/_packaging/{FeedId:D}/nuget/v3/index.json", feed.FeedIndexUrl);
        Assert.Null(TenantUrlRules.Check(ResolvedFeed.PackagesBase, feed.PathPrefix, feed.FeedIndexUrl, requireHttps: true, requirePrefix: true));
        // The proxy compares the prefix ignoring case, as the live index writes the organisation in lower case.
        Assert.True(TenantUrlRules.PathIsUnderPrefix($"Contoso/{ProjectId:D}/_packaging/{FeedId:D}/nuget/v3/registrations2/x/index.json", feed.PathPrefix));
        Assert.False(TenantUrlRules.PathIsUnderPrefix($"contoso/{ProjectId:D}/_packaging/{Guid.NewGuid():D}/nuget/v3/index.json", feed.PathPrefix));   // another feed
        Assert.False(TenantUrlRules.PathIsUnderPrefix("contoso/_packaging/private/nuget/v3/index.json", feed.PathPrefix));
    }

    [Fact]
    public void An_organisation_scoped_feed_has_no_project_in_its_urls()
    {
        var (feed, problem) = FeedResolution.Validate(Typed with { Project = null }, FeedId.ToString("D"), "public-packages", false, null, null);
        Assert.Null(problem);
        Assert.Equal($"contoso/_packaging/{FeedId:D}/", feed!.PathPrefix);
        Assert.Equal($"https://pkgs.dev.azure.com/contoso/_packaging/{FeedId:D}/nuget/v3/index.json", feed.FeedIndexUrl);
    }

    [Theory]
    [InlineData("other-feed", true, "Platform Team", FeedResolveProblem.NameMismatch)]
    [InlineData("0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f", true, "Platform Team", FeedResolveProblem.NameMismatch)]
    [InlineData(null, true, "Platform Team", FeedResolveProblem.NameMismatch)]
    [InlineData("public-packages", true, "Other Project", FeedResolveProblem.ProjectMismatch)]
    [InlineData("public-packages", false, null, FeedResolveProblem.ProjectMismatch)]       // a project was named, the feed has none
    public void A_resolve_that_does_not_match_what_was_typed_is_refused(string? name, bool hasProject, string? projectName, FeedResolveProblem expected)
    {
        var (feed, problem) = FeedResolution.Validate(Typed, FeedId.ToString("D"), name, hasProject, hasProject ? ProjectId.ToString("D") : null, projectName);
        Assert.Null(feed);
        Assert.Equal(expected, problem);
    }

    [Fact]
    public void An_organisation_scoped_name_that_resolves_to_a_project_feed_is_refused() =>
        Assert.Equal(FeedResolveProblem.ProjectMismatch, FeedResolution.Validate(Typed with { Project = null }, FeedId.ToString("D"), "public-packages", true, ProjectId.ToString("D"), "Platform Team").Problem);

    [Theory]
    [InlineData(null, "7a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d")]
    [InlineData("not-a-guid", "7a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d")]
    [InlineData("00000000-0000-0000-0000-000000000000", "7a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d")]
    [InlineData("{0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f}", "7a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d")]
    [InlineData("0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f", "../../other")]
    [InlineData("0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f", null)]
    public void Ids_that_are_not_GUIDs_are_refused(string? id, string? projectId) =>
        Assert.Equal(FeedResolveProblem.BadIds, FeedResolution.Validate(Typed, id, "public-packages", true, projectId, "Platform Team").Problem);

    // ---- the step-up sign-in

    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static CustomerActor Session(CustomerRole role = CustomerRole.Admin) => new("selfco", Oid, role, "Ada", Dir, Now.AddMinutes(-20));
    private static StepUpRefusal? StepUp(string[]? tids = null, string[]? oids = null, string[]? roles = null, DateTimeOffset? authTime = null, CustomerActor? session = null, bool noAuthTime = false) =>
        AzureSetupRules.CheckStepUp(session ?? Session(), tids ?? new[] { Dir }, oids ?? new[] { Oid.ToString("D") }, roles ?? new[] { CustomerSignIn.AdminRole },
            noAuthTime ? null : authTime ?? Now, Now);

    [Fact]
    public void The_same_admin_signed_in_just_now_passes_the_step_up()
    {
        Assert.Null(StepUp());
        Assert.Null(StepUp(tids: new[] { Dir.ToUpperInvariant() }, oids: new[] { Oid.ToString("D").ToUpperInvariant() }));
        Assert.Null(StepUp(authTime: Now - CustomerTokenRules.FreshSignIn));   // exactly 15 minutes
    }

    [Fact]
    public void Another_person_or_directory_is_refused()
    {
        Assert.Equal(StepUpRefusal.DifferentPerson, StepUp(oids: new[] { Guid.NewGuid().ToString("D") }));
        Assert.Equal(StepUpRefusal.DifferentPerson, StepUp(tids: new[] { OtherDir }));
        Assert.Equal(StepUpRefusal.DifferentPerson, StepUp(oids: new[] { Oid.ToString("D"), Guid.NewGuid().ToString("D") }));
        Assert.Equal(StepUpRefusal.DifferentPerson, StepUp(oids: Array.Empty<string>()));
        Assert.Equal(StepUpRefusal.DifferentPerson, StepUp(tids: new[] { Dir, Dir }));
        Assert.Equal(StepUpRefusal.DifferentPerson, StepUp(tids: new[] { "9188040d-6c67-4c5b-b112-36a304b66dad" }));   // the consumer directory
    }

    [Fact]
    public void Only_an_admin_in_the_new_token_and_in_the_session_passes()
    {
        Assert.Equal(StepUpRefusal.NotAdmin, StepUp(roles: new[] { CustomerSignIn.DeveloperRole }));
        Assert.Equal(StepUpRefusal.NotAdmin, StepUp(roles: Array.Empty<string>()));
        Assert.Equal(StepUpRefusal.NotAdmin, StepUp(roles: new[] { "packmoat.admin" }));
        Assert.Equal(StepUpRefusal.NotAdmin, StepUp(session: Session(CustomerRole.Developer)));
    }

    [Fact]
    public void A_stale_unknown_or_future_sign_in_time_is_not_fresh()
    {
        Assert.Equal(StepUpRefusal.NotFresh, StepUp(authTime: Now - CustomerTokenRules.FreshSignIn - TimeSpan.FromSeconds(1)));
        Assert.Equal(StepUpRefusal.NotFresh, StepUp(noAuthTime: true));
        Assert.Equal(StepUpRefusal.NotFresh, StepUp(authTime: Now + TimeSpan.FromMinutes(2)));
    }

    // ---- the outcome

    private static List<CheckResult> AllPass(params string[] extra) =>
        AzureSetupRules.SettingsChecks.Concat(extra).Select(id => new CheckResult(id, id, CheckStatus.Pass, "")).ToList();

    [Fact]
    public void Passed_only_when_every_settings_check_passed()
    {
        Assert.Equal(FeedCheckOutcome.Passed, AzureSetupRules.Outcome(AllPass()));
        Assert.Equal(FeedCheckOutcome.Passed, AzureSetupRules.Outcome(AllPass("project")));
        foreach (var status in new[] { CheckStatus.Warn, CheckStatus.Skip })
        {
            foreach (var id in AzureSetupRules.SettingsChecks.Append("project"))
            {
                var r = AllPass("project");
                r[r.FindIndex(x => x.Id == id)] = new CheckResult(id, id, status, "");
                Assert.Equal(FeedCheckOutcome.Incomplete, AzureSetupRules.Outcome(r));   // a WARN or NOT VERIFIED never passes
            }
        }
        var missing = AllPass().Where(r => r.Id != "identity").ToList();
        Assert.Equal(FeedCheckOutcome.Incomplete, AzureSetupRules.Outcome(missing));
        var failed = AllPass();
        failed[0] = failed[0] with { Status = CheckStatus.Fail };
        Assert.Equal(FeedCheckOutcome.Failed, AzureSetupRules.Outcome(failed));
        Assert.Equal(FeedCheckOutcome.Failed, AzureSetupRules.Outcome(new[] { new CheckResult("feed", "", CheckStatus.Fail, "") }));
    }

    [Fact]
    public void Project_is_required_by_the_summary_whenever_it_ran()
    {
        var results = AllPass("project", "developer", "private", "proxy").Select(r => r.Id == "project" ? r with { Status = CheckStatus.Skip } : r).ToList();
        Assert.Equal(2, OnboardingSummary.Evaluate(results).ExitCode);
        Assert.False(OnboardingSummary.PhaseOnePassed(results));
        Assert.Equal(0, OnboardingSummary.Evaluate(AllPass("developer", "private", "proxy")).ExitCode);   // not run: not required
    }

    // ---- the commands

    private sealed class World
    {
        public FakeStore Tenants { get; } = new();
        public FakeDirectories Directories { get; } = new();
        public FakeSharedStore Shared { get; } = new();
        public FakeFeedChecks Checks { get; } = new();
        public FakeDelegatedCheck Check { get; } = new();
        public MovingClock Clock { get; } = new();
        public AzureSetupState State { get; }
        public AzureSetupCommands Commands { get; }

        public World(string flag = "on", TenantOrigin origin = TenantOrigin.Self, UpstreamBinding? upstream = null, bool shared = true, TimeSpan? deadline = null,
            FeedSetup feedSetup = FeedSetup.Operator)
        {
            State = SetupStates.New(Clock);
            Tenants.Tenants.Add(new Tenant("selfco", "Selfco", "", "", "", new TenantPolicy(14)));
            Directories.Rows[Dir] = new DirectoryBinding(Dir, "selfco", Clock.UtcNow, "self-signup");
            Tenants.Setups["selfco"] = new TenantSetup(origin, feedSetup);
            if (upstream is not null) Tenants.Bindings["selfco"] = upstream;
            if (shared) Shared.Slots[SharedAppSlot.Active] = new SharedAppCredential(Client, "PEM");
            Commands = new AzureSetupCommands(Tenants, Directories, Shared, Checks, Check, State, AzureSetupSettings.Parse(flag), Clock, new InProcessRateCounter(Clock), deadline);
        }

        public CustomerActor Admin => new("selfco", Oid, CustomerRole.Admin, "Ada Lovelace", Dir, Clock.UtcNow);
        public AzureSetupRequest Names => new(new FeedNames("contoso", null, "public-packages"), Array.Empty<string>());
    }

    [Fact]
    public async Task A_self_made_tenants_check_is_recorded_with_who_ran_it_and_what_was_checked()
    {
        var w = new World();
        var held = await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        Assert.Equal(FeedCheckOutcome.Passed, held.Outcome);
        Assert.True(held.Recorded);
        var row = Assert.Single(w.Checks.Rows);
        Assert.Equal(("selfco", Dir, Oid.ToString("D"), "Ada Lovelace", Dir, Client), (row.TenantId, row.CheckedByTid, row.CheckedByOid, row.CheckedByName, row.AzureTenantId, row.ClientId));
        Assert.Equal(w.Check.Run.FeedIndexUrl, row.FeedIndexUrl);
        Assert.Equal(new[] { "new contoso//public-packages" }, w.Check.Calls);
        Assert.Equal((Dir, Client), (w.Check.LastRequest!.DirectoryId, w.Check.LastRequest.SharedClientId));
        Assert.Equal(Token, w.Check.LastToken);
        // the 30-day blanking runs first, every time
        Assert.Equal(new[] { "blank " + (w.Clock.UtcNow - TimeSpan.FromDays(30)).ToString("O"), "record" }, w.Checks.Calls);
        SetupStates.SameCheck(held, await w.State.GetAsync("selfco", Oid));
        Assert.Null(await w.State.GetAsync("othertenant", Oid));   // held per tenant and person, never per person only
        // nothing else is written: no tenant URL, no binding, no verification (part I)
        Assert.DoesNotContain(w.Tenants.Calls, c => c.StartsWith("bind", StringComparison.Ordinal) || c.StartsWith("identity-set", StringComparison.Ordinal));
        Assert.DoesNotContain(w.Shared.Calls, c => c.StartsWith("verify", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_operator_made_shared_tenant_is_checked_on_its_stored_feed_and_never_recorded()
    {
        var w = new World(origin: TenantOrigin.Operator, upstream: new UpstreamBinding(IdentityKind.Shared, Dir, null));
        Assert.Equal(AzureSetupKind.CheckOnly, (await w.Commands.ViewAsync(w.Admin, default)).Kind);
        var request = await w.Commands.StartAsync(w.Admin, "ignored", null, "ignored", false, default);
        Assert.Null(request.Names);
        var held = await w.Commands.RunAsync(w.Admin, request, Token, default);
        Assert.False(held.Recorded);
        Assert.Empty(w.Checks.Rows);
        Assert.Empty(w.Checks.Calls);
        Assert.Equal(new[] { "stored selfco" }, w.Check.Calls);
        SetupStates.SameCheck(held, await w.State.GetAsync("selfco", Oid));
    }

    [Theory]
    [InlineData(TenantOrigin.Operator, null)]                       // the canary's shape
    [InlineData(TenantOrigin.Operator, IdentityKind.Own)]           // e2e-test's shape
    [InlineData(TenantOrigin.Self, IdentityKind.Own)]
    public async Task Own_app_and_other_operator_made_tenants_are_read_only(TenantOrigin origin, IdentityKind? kind)
    {
        var w = new World(origin: origin, upstream: kind is { } k ? new UpstreamBinding(k, Dir, k == IdentityKind.Own ? "99999999-9999-4999-8999-999999999999" : null) : null);
        var view = await w.Commands.ViewAsync(w.Admin, default);
        Assert.Equal(AzureSetupKind.ReadOnly, view.Kind);
        Assert.False(view.CanCheck);
        Assert.Equal(AzureSetupRefusal.ReadOnly, (await Assert.ThrowsAsync<AzureSetupRefusedException>(() => w.Commands.StartAsync(w.Admin, "contoso", null, "f", false, default))).Reason);
        Assert.Equal(AzureSetupRefusal.ReadOnly, (await Assert.ThrowsAsync<AzureSetupRefusedException>(() => w.Commands.RunAsync(w.Admin, w.Names, Token, default))).Reason);
        Assert.Empty(w.Check.Calls);
        Assert.Empty(w.Checks.Rows);
    }

    [Fact]
    public async Task A_shared_binding_in_another_directory_is_read_only()
    {
        var w = new World(origin: TenantOrigin.Operator, upstream: new UpstreamBinding(IdentityKind.Shared, OtherDir, null));
        Assert.Equal(AzureSetupKind.ReadOnly, (await w.Commands.ViewAsync(w.Admin, default)).Kind);
        var self = new World(upstream: new UpstreamBinding(IdentityKind.Shared, OtherDir, null));
        Assert.Equal(AzureSetupKind.ReadOnly, (await self.Commands.ViewAsync(self.Admin, default)).Kind);
    }

    [Fact]
    public async Task An_operator_made_tenant_with_no_customer_flag_is_check_only_and_never_recorded()
    {
        var w = new World(upstream: new UpstreamBinding(IdentityKind.Shared, Dir, null));
        w.Tenants.Setups.Clear();   // the fake's default: operator-made, operator-set (what every row is after migration 22)
        Assert.Equal(AzureSetupKind.CheckOnly, (await w.Commands.ViewAsync(w.Admin, default)).Kind);
        await w.Commands.RunAsync(w.Admin, new AzureSetupRequest(null, Array.Empty<string>()), Token, default);
        Assert.Empty(w.Checks.Rows);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")]
    public async Task With_the_switch_off_for_the_directory_everything_is_refused(string flag)
    {
        var w = new World(flag);
        Assert.Equal(AzureSetupRefusal.Disabled, (await Assert.ThrowsAsync<AzureSetupRefusedException>(() => w.Commands.ViewAsync(w.Admin, default))).Reason);
        Assert.Equal(AzureSetupRefusal.Disabled, (await Assert.ThrowsAsync<AzureSetupRefusedException>(() => w.Commands.StartAsync(w.Admin, "contoso", null, "f", false, default))).Reason);
        Assert.Equal(AzureSetupRefusal.Disabled, (await Assert.ThrowsAsync<AzureSetupRefusedException>(() => w.Commands.RunAsync(w.Admin, w.Names, Token, default))).Reason);
        Assert.False(await w.Commands.ShowConnectCardAsync(w.Admin, default));
        Assert.Empty(w.Check.Calls);
    }

    [Fact]
    public async Task A_developer_or_an_unlinked_directory_is_refused()
    {
        var w = new World();
        var dev = w.Admin with { Role = CustomerRole.Developer };
        Assert.Equal(AzureSetupRefusal.NotAdmin, (await Assert.ThrowsAsync<AzureSetupRefusedException>(() => w.Commands.RunAsync(dev, w.Names, Token, default))).Reason);
        Assert.True(await w.Commands.ShowConnectCardAsync(dev, default));   // the card's sentence is for everyone
        Assert.Equal(AzureSetupRefusal.NotLinked, (await Assert.ThrowsAsync<AzureSetupRefusedException>(() => w.Commands.RunAsync(w.Admin with { TenantId = "other" }, w.Names, Token, default))).Reason);
        w.Directories.Rows.Clear();
        Assert.Equal(AzureSetupRefusal.NotLinked, (await Assert.ThrowsAsync<AzureSetupRefusedException>(() => w.Commands.RunAsync(w.Admin, w.Names, Token, default))).Reason);
        Assert.Empty(w.Check.Calls);
    }

    [Fact]
    public async Task Without_the_shared_app_or_with_bad_names_nothing_runs()
    {
        var w = new World(shared: false);
        Assert.False((await w.Commands.ViewAsync(w.Admin, default)).CanCheck);
        Assert.Equal(AzureSetupRefusal.NotAvailable, (await Assert.ThrowsAsync<AzureSetupRefusedException>(() => w.Commands.StartAsync(w.Admin, "contoso", null, "f", false, default))).Reason);
        var x = new World();
        Assert.Equal(AzureSetupRefusal.BadNames, (await Assert.ThrowsAsync<AzureSetupRefusedException>(() => x.Commands.StartAsync(x.Admin, "contoso", null, "a b", false, default))).Reason);
        Assert.Equal(AzureSetupRefusal.BadNames, (await Assert.ThrowsAsync<AzureSetupRefusedException>(() => x.Commands.RunAsync(x.Admin, new AzureSetupRequest(new FeedNames("con/toso", null, "f"), Array.Empty<string>()), Token, default))).Reason);
        Assert.Equal(AzureSetupRefusal.BadNames, (await Assert.ThrowsAsync<AzureSetupRefusedException>(() => x.Commands.RunAsync(x.Admin, new AzureSetupRequest(null, Array.Empty<string>()), Token, default))).Reason);
        Assert.Empty(x.Check.Calls);
    }

    [Fact]
    public async Task Ten_starts_an_hour_per_person()
    {
        var w = new World();
        for (var i = 0; i < 10; i++) await w.Commands.StartAsync(w.Admin, "contoso", null, "f", false, default);
        Assert.Equal(AzureSetupRefusal.TooManyRequests, (await Assert.ThrowsAsync<AzureSetupRefusedException>(() => w.Commands.StartAsync(w.Admin, "contoso", null, "f", false, default))).Reason);
        w.Clock.UtcNow += TimeSpan.FromHours(1);
        await w.Commands.StartAsync(w.Admin, "contoso", null, "f", false, default);
    }

    [Fact]
    public async Task One_check_at_a_time_per_tenant_and_the_slot_is_freed_afterwards()
    {
        var w = new World();
        var other = await w.State.TryBeginAsync("selfco");
        Assert.NotNull(other);
        Assert.Equal(AzureSetupRefusal.CheckRunning, (await Assert.ThrowsAsync<AzureSetupRefusedException>(() => w.Commands.RunAsync(w.Admin, w.Names, Token, default))).Reason);
        Assert.Empty(w.Check.Calls);
        await w.State.EndAsync("selfco", other!);
        w.Clock.UtcNow += TimeSpan.FromTicks(1);   // a released lease is free once time has moved past its end
        await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        w.Clock.UtcNow += TimeSpan.FromTicks(1);
        Assert.NotNull(await w.State.TryBeginAsync("selfco"));   // freed after a run
    }

    [Fact(Timeout = 10_000)]   // the fake hangs until cancelled: never let a broken deadline hang the run
    public async Task A_check_past_the_deadline_is_incomplete_not_failed_and_frees_the_slot()
    {
        var w = new World(deadline: TimeSpan.FromMilliseconds(50));
        w.Check.Hang = true;
        var held = await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        Assert.Equal(FeedCheckOutcome.Incomplete, held.Outcome);
        Assert.Equal(AzureSetupCommands.OutOfTime, Assert.Single(held.Results).Detail);
        Assert.Equal(FeedCheckOutcome.Incomplete, Assert.Single(w.Checks.Rows).Outcome);
        w.Clock.UtcNow += TimeSpan.FromTicks(1);
        Assert.NotNull(await w.State.TryBeginAsync("selfco"));
        Assert.Equal(60, AzureSetupRules.Deadline.TotalSeconds);
        Assert.Equal((1, 5.0), (AzureSetupRules.MaxRetries, AzureSetupRules.RetryWaitCap.TotalSeconds));
    }

    /// <summary>One call's own HttpClient timeout (well inside the deadline) is reported by the engine as a failure; it is recorded as not verified.</summary>
    [Fact]
    public async Task A_call_timeout_inside_the_deadline_is_not_verified_rather_than_failed()
    {
        var w = new World();
        w.Check.Run = w.Check.Run with
        {
            Results = w.Check.Run.Results.Select(r => r.Id == "permissions"
                ? r with { Status = CheckStatus.Fail, Detail = "could not complete: TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of 20 seconds elapsing." }
                : r).ToList(),
        };
        var held = await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        var permissions = held.Results.Single(r => r.Id == "permissions");
        Assert.Equal((CheckStatus.Skip, AzureSetupCommands.NoAnswerInTime), (permissions.Status, permissions.Detail));
        Assert.Equal(FeedCheckOutcome.Incomplete, held.Outcome);
        Assert.Equal(FeedCheckOutcome.Incomplete, Assert.Single(w.Checks.Rows).Outcome);
        // a real failure is still a failure
        w.Clock.UtcNow += TimeSpan.FromSeconds(1);   // the next check, a moment later: the first one's lease is over
        w.Check.Run = w.Check.Run with { Results = AllPass().Select(r => r.Id == "views" ? r with { Status = CheckStatus.Fail, Detail = "set to specific people: Local is 'organization'" } : r).ToList() };
        Assert.Equal(FeedCheckOutcome.Failed, (await w.Commands.RunAsync(w.Admin, w.Names, Token, default)).Outcome);
    }

    [Fact]
    public async Task The_token_is_scrubbed_from_whatever_the_engine_wrote()
    {
        var w = new World();
        w.Check.Run = w.Check.Run with
        {
            Results = w.Check.Run.Results.Select(r => r with { Detail = "echo " + Token }).ToList(),
            NamedAdmins = new[] { new NamedFeedAdmin("Ada " + Token, "Ada", "desc") },
        };
        var held = await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        // Never Assert.DoesNotContain on the token: its failure message would print it.
        Assert.True(held.Results.All(r => !r.Detail.Contains(Token, StringComparison.Ordinal)), "a held result carried the token");
        Assert.True(w.Checks.Rows.Single().Results.All(r => !r.Detail.Contains(Token, StringComparison.Ordinal)), "a recorded result carried the token");
        Assert.False(held.NamedAdmins.Single().Label.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Confirming_takes_the_named_admins_of_the_persons_last_check_and_records_their_names()
    {
        var w = new World();
        var ada = new NamedFeedAdmin("Ada Lovelace (feed creator)", "Ada Lovelace", "desc-ada");
        w.Check.Run = w.Check.Run with
        {
            Results = w.Check.Run.Results.Select(r => r.Id == "permissions" ? r with { Status = CheckStatus.Warn } : r).ToList(),
            NamedAdmins = new[] { ada },
        };
        await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        var request = await w.Commands.StartAsync(w.Admin, "contoso", null, "public-packages", confirmAdmins: true, default);
        Assert.Equal(new[] { "desc-ada" }, request.ConfirmedAdmins);
        w.Clock.UtcNow += TimeSpan.FromSeconds(1);   // the step-up's return, a moment later: the first check's lease is over
        w.Check.Run = w.Check.Run with { Results = AllPass() };
        await w.Commands.RunAsync(w.Admin, request, Token, default);
        Assert.Equal(new[] { "desc-ada" }, w.Check.LastRequest!.ConfirmedAdmins);
        Assert.Equal(new[] { "Ada Lovelace" }, w.Checks.Rows.Last().ConfirmedAdmins);
        Assert.Empty(w.Checks.Rows.First().ConfirmedAdmins);   // nothing was confirmed the first time
        // without the box ticked, nothing is confirmed
        Assert.Empty((await w.Commands.StartAsync(w.Admin, "contoso", null, "public-packages", confirmAdmins: false, default)).ConfirmedAdmins);
    }

    [Fact]
    public async Task The_held_check_is_the_persons_own_and_expires()
    {
        var w = new World();
        await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        Assert.Null(await w.State.GetAsync("selfco", Guid.NewGuid()));
        w.Clock.UtcNow += AzureSetupState.HoldFor;
        Assert.Null(await w.State.GetAsync("selfco", Oid));
    }

    [Fact]
    public async Task The_home_card_shows_only_for_a_self_made_tenant_with_no_feed()
    {
        Assert.True(await new World().Commands.ShowConnectCardAsync(new World().Admin, default));
        var op = new World(origin: TenantOrigin.Operator);
        Assert.False(await op.Commands.ShowConnectCardAsync(op.Admin, default));
        var set = new World(upstream: new UpstreamBinding(IdentityKind.Shared, Dir, null));
        set.Tenants.Tenants[0] = set.Tenants.Tenants[0] with { UpstreamBase = "https://pkgs.dev.azure.com/", AllowedPathPrefix = "contoso/", UpstreamFeedIndexUrl = "https://pkgs.dev.azure.com/contoso/_packaging/f/nuget/v3/index.json" };
        Assert.False(await set.Commands.ShowConnectCardAsync(set.Admin, default));   // set up (not yet verified): not "no feed"
    }

    // ---- who sets up the feed: origin x flag x upstream x active

    public static TheoryData<TenantOrigin, FeedSetup, string, bool, AzureSetupKind> SetupKinds => new()
    {
        // self-made: the customer sets up the feed, flag or not
        { TenantOrigin.Self, FeedSetup.Operator, "none", true, AzureSetupKind.Wizard },
        { TenantOrigin.Self, FeedSetup.Operator, "shared-here", true, AzureSetupKind.Wizard },
        { TenantOrigin.Self, FeedSetup.Operator, "shared-elsewhere", true, AzureSetupKind.ReadOnly },
        { TenantOrigin.Self, FeedSetup.Operator, "own", true, AzureSetupKind.ReadOnly },
        { TenantOrigin.Self, FeedSetup.Operator, "none", false, AzureSetupKind.ReadOnly },
        { TenantOrigin.Self, FeedSetup.Customer, "none", true, AzureSetupKind.Wizard },
        // operator-made for the customer to connect: as a self-made tenant
        { TenantOrigin.Operator, FeedSetup.Customer, "none", true, AzureSetupKind.Wizard },
        { TenantOrigin.Operator, FeedSetup.Customer, "shared-here", true, AzureSetupKind.Wizard },
        { TenantOrigin.Operator, FeedSetup.Customer, "shared-elsewhere", true, AzureSetupKind.ReadOnly },
        { TenantOrigin.Operator, FeedSetup.Customer, "own", true, AzureSetupKind.ReadOnly },
        { TenantOrigin.Operator, FeedSetup.Customer, "none", false, AzureSetupKind.ReadOnly },
        { TenantOrigin.Operator, FeedSetup.Customer, "shared-here", false, AzureSetupKind.ReadOnly },
        // operator-made, operator-set (every tenant made before customers could connect their own feed): as before
        { TenantOrigin.Operator, FeedSetup.Operator, "none", true, AzureSetupKind.ReadOnly },
        { TenantOrigin.Operator, FeedSetup.Operator, "shared-here", true, AzureSetupKind.CheckOnly },
        { TenantOrigin.Operator, FeedSetup.Operator, "shared-elsewhere", true, AzureSetupKind.ReadOnly },
        { TenantOrigin.Operator, FeedSetup.Operator, "own", true, AzureSetupKind.ReadOnly },
        { TenantOrigin.Operator, FeedSetup.Operator, "shared-here", false, AzureSetupKind.ReadOnly },
    };

    private static UpstreamBinding? Upstream(string shape) => shape switch
    {
        "none" => null,
        "shared-here" => new UpstreamBinding(IdentityKind.Shared, Dir, null),
        "shared-elsewhere" => new UpstreamBinding(IdentityKind.Shared, OtherDir, null),
        "own" => new UpstreamBinding(IdentityKind.Own, Dir, "99999999-9999-4999-8999-999999999999"),
        _ => throw new ArgumentException(shape),
    };

    [Theory]   // mutations: drop "OR flag" from the rule; ignore the flag in the kind; CheckOnly for a flagged tenant
    [MemberData(nameof(SetupKinds))]
    public async Task The_setup_kind_follows_who_sets_up_the_feed(TenantOrigin origin, FeedSetup feedSetup, string upstream, bool active, AzureSetupKind expected)
    {
        var binding = Upstream(upstream);
        var sameDirectory = binding is not null && binding.AzureTenantId == Dir;
        Assert.Equal(expected, FeedSetupRules.Kind(active, FeedSetupRules.CustomerSetsUpFeed(origin, feedSetup), binding, sameDirectory));

        // and through the commands, reading the tenant store (no module's store at all)
        var w = new World(origin: origin, upstream: binding, feedSetup: feedSetup);
        if (!active) w.Tenants.Tenants[0] = w.Tenants.Tenants[0] with { Active = false };
        Assert.Equal(expected, (await w.Commands.ViewAsync(w.Admin, default)).Kind);
        Assert.Contains("setup selfco", w.Tenants.Reads);
    }

    [Theory]
    [InlineData(TenantOrigin.Self, FeedSetup.Operator, true)]
    [InlineData(TenantOrigin.Self, FeedSetup.Customer, true)]
    [InlineData(TenantOrigin.Operator, FeedSetup.Customer, true)]
    [InlineData(TenantOrigin.Operator, FeedSetup.Operator, false)]
    public void The_customer_sets_up_the_feed_when_self_made_or_flagged(TenantOrigin origin, FeedSetup feedSetup, bool expected) =>
        Assert.Equal(expected, FeedSetupRules.CustomerSetsUpFeed(origin, feedSetup));

    [Fact]   // mutations: ignore the flag in the card; the card still needs a module's store
    public async Task A_flagged_operator_tenant_gets_the_wizard_and_the_home_card_and_its_check_is_recorded()
    {
        var w = new World(origin: TenantOrigin.Operator, feedSetup: FeedSetup.Customer);
        var view = await w.Commands.ViewAsync(w.Admin, default);
        Assert.Equal(AzureSetupKind.Wizard, view.Kind);
        Assert.True(view.CanCheck);
        Assert.True(await w.Commands.ShowConnectCardAsync(w.Admin, default));
        var held = await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        Assert.True(held.Recorded);
        Assert.Equal("selfco", Assert.Single(w.Checks.Rows).TenantId);

        // the same tenant once the operator has taken the feed over: no card, nothing to set up
        var taken = new World(origin: TenantOrigin.Operator, feedSetup: FeedSetup.Operator);
        Assert.False(await taken.Commands.ShowConnectCardAsync(taken.Admin, default));
        Assert.Equal(AzureSetupKind.ReadOnly, (await taken.Commands.ViewAsync(taken.Admin, default)).Kind);
    }
}
