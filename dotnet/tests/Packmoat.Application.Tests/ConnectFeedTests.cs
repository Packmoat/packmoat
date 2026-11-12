using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Application.Tests;

/// <summary>A self-service feed store that records what it was asked and answers what the test says.</summary>
internal sealed class FakeSelfServiceFeeds : ISelfServiceFeedStore
{
    public List<ConnectFeedRequest> Connects { get; } = new();
    public List<(SharedAppVerification Verification, AdminActionRecord Action)> Withdrawals { get; } = new();
    public ConnectResult Answer { get; set; } = ConnectResult.Connected;
    /// <summary>How many WithdrawAsync calls throw (an I/O error, say) before one goes through.</summary>
    public int ThrowTimes { get; set; }
    public FakeSharedStore? Shared { get; set; }

    public Task<ConnectResult> ConnectSelfServiceFeedAsync(ConnectFeedRequest request, CancellationToken ct)
    {
        Connects.Add(request);
        return Task.FromResult(Answer);
    }

    public async Task<bool> WithdrawAsync(SharedAppVerification verification, AdminActionRecord action, CancellationToken ct)
    {
        Withdrawals.Add((verification, action));
        ct.ThrowIfCancellationRequested();   // as the real store does (it opens the database with the token)
        if (ThrowTimes-- > 0) throw new IOException("disk I/O error");
        return Shared is null || await Shared.DeleteVerificationAsync(verification, ct);
    }
}

/// <summary>
/// <see cref="ConnectRules"/> (results, the two windows to the edge, two different people, the URL shape), the withdraw rule,
/// <see cref="ConnectFeedCommands"/>' person checks, and the re-check hook in <see cref="AzureSetupCommands"/>.
/// </summary>
public class ConnectFeedTests
{
    private const string Dir = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string OtherDir = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string Client = "5a2ed000-0000-4000-8000-000000000001";
    private const string Admin = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    private const string Dev = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
    private const string Token = "SENTINEL-TOKEN-0d1e2f";
    private static readonly Guid AdminOid = Guid.Parse(Admin);
    private const string Feed = "https://pkgs.dev.azure.com/contoso/7a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/nuget/v3/index.json";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] SettingsIds = { "feed", "identity", "upstreams", "permissions", "views", "private", "project" };

    private static IReadOnlyList<CheckResult> Pass(params string[] ids) => ids.Select(id => new CheckResult(id, id, CheckStatus.Pass, "ok")).ToList();

    private static FeedCheck Settings(TimeSpan? age = null, IReadOnlyList<CheckResult>? results = null, string feed = Feed, string oid = Admin) =>
        new FeedCheck("selfco", Now - (age ?? TimeSpan.FromHours(1)), Dir, oid, "Ada Lovelace", Dir, feed, Client, FeedCheckOutcome.Passed,
            results ?? Pass(SettingsIds), Array.Empty<string>()) { Id = 10, Kind = FeedCheckKind.Settings };

    private static FeedCheck Developer(TimeSpan? age = null, string oid = Dev, string tid = Dir, string feed = Feed, string client = Client) =>
        new FeedCheck("selfco", Now - (age ?? TimeSpan.FromDays(1)), tid, oid, "Grace Hopper", Dir, feed, client, FeedCheckOutcome.Passed,
            Pass("developer"), Array.Empty<string>()) { Id = 11, Kind = FeedCheckKind.Developer };

    // ---- ConnectRules

    [Fact]
    public void A_passed_settings_check_and_a_passed_developer_check_by_someone_else_qualify()
    {
        Assert.Null(ConnectRules.Check(Settings(), Developer(), Now));
        // 'project' only when it ran (an organisation-scoped feed has none)
        Assert.Null(ConnectRules.Check(Settings(results: Pass("feed", "identity", "upstreams", "permissions", "views", "private")), Developer(), Now));
    }

    [Fact]
    public void The_windows_are_24_hours_and_7_days_named_and_both_edges_count()
    {
        Assert.Equal(TimeSpan.FromHours(24), ConnectRules.SettingsWindow);
        Assert.Equal(TimeSpan.FromDays(7), ConnectRules.DeveloperWindow);
        Assert.Null(ConnectRules.Check(Settings(age: TimeSpan.FromHours(24)), Developer(), Now));
        Assert.Equal(ConnectResult.SettingsTooOld, ConnectRules.Check(Settings(age: TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1)), Developer(), Now));
        Assert.Null(ConnectRules.Check(Settings(), Developer(age: TimeSpan.FromDays(7)), Now));
        Assert.Equal(ConnectResult.DeveloperTooOld, ConnectRules.Check(Settings(), Developer(age: TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1)), Now));
        // never from the future (beyond a minute of clock skew)
        Assert.Null(ConnectRules.Check(Settings(age: -TimeSpan.FromMinutes(1)), Developer(), Now));
        Assert.Equal(ConnectResult.SettingsTooOld, ConnectRules.Check(Settings(age: -TimeSpan.FromMinutes(2)), Developer(), Now));
        Assert.Equal(ConnectResult.DeveloperTooOld, ConnectRules.Check(Settings(), Developer(age: -TimeSpan.FromMinutes(2)), Now));
    }

    [Fact]
    public void An_h2_settings_check_without_private_never_qualifies()
    {
        var h2 = Pass("feed", "identity", "upstreams", "permissions", "views", "project");   // what the delegated check records
        Assert.Equal(ConnectResult.NotPassed, ConnectRules.Check(Settings(results: h2), Developer(), Now));
    }

    [Theory]
    [InlineData("feed")]
    [InlineData("identity")]
    [InlineData("upstreams")]
    [InlineData("permissions")]
    [InlineData("views")]
    [InlineData("private")]
    [InlineData("project")]
    public void Every_required_setting_must_pass_a_warning_or_not_verified_is_not_enough(string id)
    {
        foreach (var status in new[] { CheckStatus.Warn, CheckStatus.Skip, CheckStatus.Fail })
        {
            var results = Pass(SettingsIds).Select(r => r.Id == id ? r with { Status = status } : r).ToList();
            Assert.Equal(ConnectResult.NotPassed, ConnectRules.Check(Settings(results: results), Developer(), Now));
        }
        if (id != "project")   // missing counts as not passed ('project' is required only when it ran)
            Assert.Equal(ConnectResult.NotPassed, ConnectRules.Check(Settings(results: Pass(SettingsIds).Where(r => r.Id != id).ToList()), Developer(), Now));
    }

    [Fact]
    public void Any_failed_result_or_an_outcome_that_is_not_passed_refuses()
    {
        var extra = Pass(SettingsIds).Append(new CheckResult("proxy", "proxy", CheckStatus.Fail, "x")).ToList();
        Assert.Equal(ConnectResult.NotPassed, ConnectRules.Check(Settings(results: extra), Developer(), Now));
        Assert.Equal(ConnectResult.NotPassed, ConnectRules.Check(Settings() with { Outcome = FeedCheckOutcome.Incomplete }, Developer(), Now));
        Assert.Equal(ConnectResult.NotPassed, ConnectRules.Check(Settings(), Developer() with { Outcome = FeedCheckOutcome.Failed }, Now));
        Assert.Equal(ConnectResult.NotPassed, ConnectRules.Check(Settings(), Developer() with { Results = new[] { new CheckResult("developer", "d", CheckStatus.Skip, "404") } }, Now));
        Assert.Equal(ConnectResult.NotPassed, ConnectRules.Check(Settings(), Developer() with { Results = Pass("feed") }, Now));
    }

    [Fact]
    public void The_developer_must_be_someone_other_than_the_settings_checker()
    {
        Assert.Equal(ConnectResult.SamePerson, ConnectRules.Check(Settings(), Developer(oid: Admin), Now));
        Assert.Equal(ConnectResult.SamePerson, ConnectRules.Check(Settings(), Developer(oid: Admin.ToUpperInvariant()), Now));   // the same GUID
        Assert.Equal(ConnectResult.SamePerson, ConnectRules.Check(Settings(), Developer(oid: "not-a-guid"), Now));             // fail closed
    }

    [Fact]
    public void The_two_checks_must_be_about_the_same_directory_feed_and_client_and_of_the_right_kinds()
    {
        Assert.Equal(ConnectResult.CheckNotFound, ConnectRules.Check(Settings() with { Kind = FeedCheckKind.Developer }, Developer(), Now));
        Assert.Equal(ConnectResult.CheckNotFound, ConnectRules.Check(Settings(), Developer() with { Kind = FeedCheckKind.Settings }, Now));
        Assert.Equal(ConnectResult.CheckNotFound, ConnectRules.Check(Settings(), Developer() with { Kind = (FeedCheckKind)(-1) }, Now));
        Assert.Equal(ConnectResult.ChecksDisagree, ConnectRules.Check(Settings(), Developer(feed: Feed.Replace("contoso", "fabrikam")), Now));
        Assert.Equal(ConnectResult.ChecksDisagree, ConnectRules.Check(Settings(), Developer(client: "99999999-9999-4999-8999-999999999999"), Now));
        Assert.Equal(ConnectResult.ChecksDisagree, ConnectRules.Check(Settings(), Developer(tid: OtherDir), Now));
        Assert.Equal(ConnectResult.ChecksDisagree, ConnectRules.Check(Settings(), Developer() with { AzureTenantId = OtherDir }, Now));
        Assert.Null(ConnectRules.Check(Settings(), Developer(tid: Dir.ToUpperInvariant()), Now));   // directories compare as GUIDs
    }

    [Theory]
    [InlineData("https://pkgs.dev.azure.com/contoso/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/nuget/v3/index.json", "contoso/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/")]
    [InlineData(Feed, "contoso/7a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/")]
    public void The_tenant_urls_come_from_the_checked_feed_url(string feed, string prefix)
    {
        Assert.Equal(("https://pkgs.dev.azure.com/", prefix), ConnectRules.UrlsFor(feed));
        var resolved = new ResolvedFeed("Contoso", feed.Contains("7a1b") ? Guid.Parse("7a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d") : null, Guid.Parse("0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f"));
        Assert.Equal((ResolvedFeed.PackagesBase, resolved.PathPrefix), ConnectRules.UrlsFor(resolved.FeedIndexUrl));   // what the wizard builds
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://pkgs.dev.azure.com/contoso/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/nuget/v3/index.json")]
    [InlineData("https://evil.example/contoso/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/nuget/v3/index.json")]
    [InlineData("https://pkgs.dev.azure.com/contoso/_packaging/public-packages/nuget/v3/index.json")]                     // a name, not a GUID
    [InlineData("https://pkgs.dev.azure.com/contoso/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/../x/nuget/v3/index.json")]
    [InlineData("https://pkgs.dev.azure.com/Contoso/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/nuget/v3/index.json")]  // the wizard writes lower case
    [InlineData("https://pkgs.dev.azure.com/contoso/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/nuget/v3/index.json?x=1")]
    [InlineData("https://pkgs.dev.azure.com/contoso/other/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/nuget/v3/index.json")]
    public void Any_other_feed_url_gives_no_tenant_urls_and_never_qualifies(string feed)
    {
        Assert.Null(ConnectRules.UrlsFor(feed));
        Assert.Equal(ConnectResult.BadFeedUrl, ConnectRules.Check(Settings(feed: feed), Developer(feed: feed), Now));
    }

    // ---- the withdraw rule

    private static SharedAppVerification Connected(string feed = Feed) => new("selfco", Dir, feed, Client, Now - TimeSpan.FromDays(3), "wizard: Ada");

    private static IReadOnlyList<CheckResult> WithOne(CheckStatus status, string detail = "remove 1 extra upstream source(s)") =>
        Pass(SettingsIds).Select(r => r.Id == "upstreams" ? r with { Status = status, Detail = detail } : r).ToList();

    [Fact]
    public void A_failed_recheck_of_the_connected_feed_with_a_definite_fail_withdraws()
    {
        Assert.True(ConnectRules.ShouldWithdraw(FeedCheckOutcome.Failed, Feed, WithOne(CheckStatus.Fail), Connected()));
    }

    [Fact]
    public void Incomplete_or_passed_never_withdraws()
    {
        Assert.False(ConnectRules.ShouldWithdraw(FeedCheckOutcome.Incomplete, Feed, WithOne(CheckStatus.Fail), Connected()));
        Assert.False(ConnectRules.ShouldWithdraw(FeedCheckOutcome.Incomplete, Feed, WithOne(CheckStatus.Warn), Connected()));
        Assert.False(ConnectRules.ShouldWithdraw(FeedCheckOutcome.Passed, Feed, WithOne(CheckStatus.Fail), Connected()));
    }

    [Fact]
    public void A_failed_check_of_another_feed_never_withdraws()
    {
        var other = Feed.Replace("contoso", "fabrikam");
        Assert.False(ConnectRules.ShouldWithdraw(FeedCheckOutcome.Failed, other, WithOne(CheckStatus.Fail), Connected()));
        Assert.False(ConnectRules.ShouldWithdraw(FeedCheckOutcome.Failed, "", WithOne(CheckStatus.Fail), Connected()));   // the names did not resolve
        Assert.False(ConnectRules.ShouldWithdraw(FeedCheckOutcome.Failed, Feed, WithOne(CheckStatus.Fail), null));          // nothing connected
    }

    [Fact]
    public void A_check_that_could_not_complete_is_not_verified_and_never_withdraws()
    {
        var errored = WithOne(CheckStatus.Fail, "could not complete: HttpRequestException: connection reset");
        Assert.False(ConnectRules.IsDefiniteFail(errored.Single(r => r.Id == "upstreams")));
        Assert.False(ConnectRules.ShouldWithdraw(FeedCheckOutcome.Failed, Feed, errored, Connected()));
        // one definite FAIL among errors is enough
        var mixed = errored.Append(new CheckResult("views", "views", CheckStatus.Fail, "1 view is shared with the organisation")).ToList();
        Assert.True(ConnectRules.ShouldWithdraw(FeedCheckOutcome.Failed, Feed, mixed, Connected()));
    }

    // ---- ConnectFeedCommands

    private static CustomerActor Actor(CustomerRole role = CustomerRole.Admin, DateTimeOffset? signedIn = null) =>
        new("selfco", AdminOid, role, "Ada Lovelace", Dir.ToUpperInvariant(), signedIn ?? Now - TimeSpan.FromMinutes(5));

    private static ConnectFeedCommands Commands(FakeSelfServiceFeeds store, string flag = "on") =>
        new(store, AzureSetupSettings.Parse(flag), new MovingClock { UtcNow = Now });

    [Fact]
    public async Task Connect_passes_the_tenant_from_the_session_and_a_feed_connect_record_to_the_store()
    {
        var store = new FakeSelfServiceFeeds();
        Assert.Equal(ConnectResult.Connected, await Commands(store).ConnectAsync(Actor(), 10, 11, Feed, Client, default));
        var r = Assert.Single(store.Connects);
        Assert.Equal(("selfco", Dir, 10L, 11L, Feed, Client, Now), (r.TenantId, r.DirectoryId, r.SettingsCheckId, r.DeveloperCheckId, r.SeenFeedUrl, r.SeenClientId, r.At));
        Assert.Equal((ConnectRules.ConnectAction, "selfco", Dir, Admin, "Ada Lovelace", "app"), (r.Action.Action, r.Action.TenantId, r.Action.ActorTid, r.Action.ActorOid, r.Action.ActorName, r.Action.Site));
        Assert.Contains("settings_check=10", r.Action.Detail);
        Assert.Contains("developer_check=11", r.Action.Detail);
        // and the store's refusal comes back as it is
        store.Answer = ConnectResult.NotLatest;
        Assert.Equal(ConnectResult.NotLatest, await Commands(store).ConnectAsync(Actor(), 10, 11, Feed, Client, default));
    }

    [Fact]
    public async Task Connect_needs_the_switch_an_admin_and_a_fresh_sign_in_before_the_store_is_asked()
    {
        var store = new FakeSelfServiceFeeds();
        Assert.Equal(ConnectResult.Disabled, await Commands(store, "off").ConnectAsync(Actor(), 10, 11, Feed, Client, default));
        Assert.Equal(ConnectResult.Disabled, await Commands(store, OtherDir).ConnectAsync(Actor(), 10, 11, Feed, Client, default));
        Assert.Equal(ConnectResult.NotAdmin, await Commands(store).ConnectAsync(Actor(CustomerRole.Developer), 10, 11, Feed, Client, default));
        Assert.Equal(ConnectResult.FreshSignInRequired, await Commands(store).ConnectAsync(Actor(signedIn: Now - TimeSpan.FromMinutes(16)), 10, 11, Feed, Client, default));
        Assert.Equal(ConnectResult.FreshSignInRequired, await Commands(store).ConnectAsync(Actor() with { SignedInAt = null }, 10, 11, Feed, Client, default));
        Assert.Equal(ConnectResult.CheckNotFound, await Commands(store).ConnectAsync(Actor(), 0, 11, Feed, Client, default));
        Assert.Equal(ConnectResult.FeedChanged, await Commands(store).ConnectAsync(Actor(), 10, 11, null, Client, default));
        Assert.Equal(ConnectResult.ClientChanged, await Commands(store).ConnectAsync(Actor(), 10, 11, Feed, "x", default));
        Assert.Empty(store.Connects);
    }

    // ---- the re-check hook

    private sealed class World
    {
        public FakeStore Tenants { get; } = new();
        public FakeDirectories Directories { get; } = new();
        public FakeSharedStore Shared { get; } = new();
        public FakeFeedChecks Checks { get; } = new();
        public FakeDelegatedCheck Check { get; } = new();
        public FakeSelfServiceFeeds Feeds { get; } = new();
        public MovingClock Clock { get; } = new() { UtcNow = Now };
        public AzureSetupCommands Commands { get; }
        public SharedAppVerification? Verification { get; }
        public List<(SetupLogLevel Level, string Message)> Logs { get; } = new();

        public World(TenantOrigin origin = TenantOrigin.Self, bool connected = true, string? feed = null, FeedSetup feedSetup = FeedSetup.Operator)
        {
            var url = feed ?? Check.Run.FeedIndexUrl;
            var urls = ConnectRules.UrlsFor(url);
            Tenants.Tenants.Add(connected && urls is { } u ? new Tenant("selfco", "Selfco", u.UpstreamBase, u.PathPrefix, url, new TenantPolicy(14)) : new Tenant("selfco", "Selfco", "", "", "", new TenantPolicy(14)));
            Directories.Rows[Dir] = new DirectoryBinding(Dir, "selfco", Clock.UtcNow, "self-signup");
            Tenants.Setups["selfco"] = new TenantSetup(origin, feedSetup);
            Shared.Slots[SharedAppSlot.Active] = new SharedAppCredential(Client, "PEM");
            if (connected)
            {
                Tenants.Bindings["selfco"] = new UpstreamBinding(IdentityKind.Shared, Dir, null);
                Verification = new SharedAppVerification("selfco", Dir, url, Client, Now - TimeSpan.FromDays(3), "wizard: Ada");
                Shared.Verifications["selfco"] = Verification;
            }
            Feeds.Shared = Shared;
            Commands = new AzureSetupCommands(Tenants, Directories, Shared, Checks, Check, SetupStates.New(Clock), AzureSetupSettings.Parse("on"), Clock, new InProcessRateCounter(Clock), feeds: Feeds,
                log: (level, message) => Logs.Add((level, message)));
        }

        public CustomerActor Admin => new("selfco", AdminOid, CustomerRole.Admin, "Ada Lovelace", Dir, Clock.UtcNow);
        public AzureSetupRequest Names => new(new FeedNames("contoso", null, "public-packages"), Array.Empty<string>());

        public void Results(params (string Id, CheckStatus Status, string Detail)[] changes) =>
            Check.Run = Check.Run with
            {
                Results = AzureSetupRules.SettingsChecks.Select(id => changes.Where(c => c.Id == id).Select(c => new CheckResult(id, id, c.Status, c.Detail))
                    .DefaultIfEmpty(new CheckResult(id, id, CheckStatus.Pass, "ok")).First()).ToList(),
            };
    }

    [Fact]
    public async Task A_failed_recheck_of_a_connected_self_made_tenant_withdraws_its_exact_verification_and_records_feed_withdrawn()
    {
        var w = new World();
        w.Results(("upstreams", CheckStatus.Fail, "remove 1 extra upstream source(s): https://evil.example/"));
        var held = await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        Assert.Equal(FeedCheckOutcome.Failed, held.Outcome);
        Assert.True(held.Recorded);
        Assert.True(held.Withdrawn);
        var (verification, action) = Assert.Single(w.Feeds.Withdrawals);
        Assert.Equal(w.Verification, verification);
        Assert.Equal((ConnectRules.WithdrawnAction, "selfco", Dir, Admin, "app"), (action.Action, action.TenantId, action.ActorTid, action.ActorOid, action.Site));
        Assert.Contains("failed=upstreams", action.Detail);
        Assert.Contains("settings_check=1", action.Detail);   // the check that withdrew it
        Assert.DoesNotContain(Token, action.Detail);
        Assert.Null(await w.Shared.GetVerificationAsync("selfco", default));
        Assert.Single(w.Checks.Rows);   // the check itself is recorded first
    }

    [Fact]   // mutation: skip the withdrawal for a flagged (operator-made) tenant
    public async Task A_failed_recheck_of_a_connected_flagged_operator_tenant_withdraws_it_too()
    {
        var w = new World(origin: TenantOrigin.Operator, feedSetup: FeedSetup.Customer);   // Made for its customer to connect, and connected
        w.Results(("upstreams", CheckStatus.Fail, "remove 1 extra upstream source(s): https://evil.example/"));
        var held = await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        Assert.Equal(FeedCheckOutcome.Failed, held.Outcome);
        Assert.True(held.Recorded);
        Assert.True(held.Withdrawn);
        var (verification, action) = Assert.Single(w.Feeds.Withdrawals);
        Assert.Equal(w.Verification, verification);
        Assert.Equal((ConnectRules.WithdrawnAction, "selfco"), (action.Action, action.TenantId));
        Assert.Null(await w.Shared.GetVerificationAsync("selfco", default));
    }

    /// <summary>The 30-day blanking before a recorded check is best effort; its failure never blocks the record or the withdrawal.</summary>
    [Fact]
    public async Task A_blanking_failure_is_logged_by_kind_and_the_check_is_still_recorded_and_withdraws()
    {
        var w = new World();
        w.Checks.BlankThrows = new NullReferenceException("row 7 Grace Hopper");
        w.Results(("upstreams", CheckStatus.Fail, "remove 1 extra upstream source(s): https://evil.example/"));
        var held = await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        Assert.True(held.Recorded);
        Assert.True(held.Withdrawn);
        Assert.Single(w.Checks.Rows);
        Assert.Single(w.Feeds.Withdrawals);
        Assert.Null(await w.Shared.GetVerificationAsync("selfco", default));
        var line = Assert.Single(w.Logs);
        Assert.Equal(SetupLogLevel.Warning, line.Level);
        Assert.Contains(nameof(NullReferenceException), line.Message);
        Assert.DoesNotContain("Grace", line.Message);   // the kind only, never the exception's message
    }

    [Fact]
    public async Task A_connect_made_while_a_failing_check_runs_is_withdrawn_when_the_check_is_recorded()
    {
        var w = new World(connected: false);   // not connected when the check starts
        SharedAppVerification? madeMeanwhile = null;
        w.Check.During = () =>
        {
            w.Tenants.Bindings["selfco"] = new UpstreamBinding(IdentityKind.Shared, Dir, null);
            madeMeanwhile = new SharedAppVerification("selfco", Dir, w.Check.Run.FeedIndexUrl, Client, Now, "wizard: Grace");
            w.Shared.Verifications["selfco"] = madeMeanwhile;
            return Task.CompletedTask;
        };
        w.Results(("upstreams", CheckStatus.Fail, "remove 1 extra upstream source(s)"));
        var held = await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        Assert.True(held.Withdrawn);
        Assert.Equal(madeMeanwhile, Assert.Single(w.Feeds.Withdrawals).Verification);
        Assert.Null(await w.Shared.GetVerificationAsync("selfco", default));
    }

    [Fact]
    public async Task A_withdraw_error_is_logged_and_tried_once_more()
    {
        var w = new World();
        w.Feeds.ThrowTimes = 1;
        w.Results(("upstreams", CheckStatus.Fail, "remove 1 extra upstream source(s)"));
        var held = await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        Assert.True(held.Withdrawn);
        Assert.False(held.WithdrawFailed);
        Assert.Equal(2, w.Feeds.Withdrawals.Count);
        Assert.Null(await w.Shared.GetVerificationAsync("selfco", default));
        var line = Assert.Single(w.Logs);
        Assert.Equal(SetupLogLevel.Warning, line.Level);
        Assert.Contains("IOException", line.Message);
    }

    [Fact]
    public async Task A_withdraw_that_fails_twice_is_logged_as_an_error_and_shown_and_the_check_stays_recorded()
    {
        var w = new World();
        w.Feeds.ThrowTimes = 5;
        w.Results(("upstreams", CheckStatus.Fail, "remove 1 extra upstream source(s)"));
        var held = await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        Assert.False(held.Withdrawn);
        Assert.True(held.WithdrawFailed);
        Assert.True(held.Recorded);
        Assert.Single(w.Checks.Rows);
        Assert.Equal(2, w.Feeds.Withdrawals.Count);   // once more, never more
        Assert.Equal(w.Verification, await w.Shared.GetVerificationAsync("selfco", default));
        Assert.Equal(new[] { SetupLogLevel.Warning, SetupLogLevel.Error }, w.Logs.Select(l => l.Level));
        Assert.All(w.Logs, l => Assert.DoesNotContain(Token, l.Message));
    }

    [Fact]
    public async Task An_incomplete_or_passed_recheck_changes_nothing()
    {
        foreach (var change in new[] { ("permissions", CheckStatus.Warn, "named admin"), ("views", CheckStatus.Skip, "NOT VERIFIED"), ("feed", CheckStatus.Pass, "ok") })
        {
            var w = new World();
            w.Results(change);
            var held = await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
            Assert.NotEqual(FeedCheckOutcome.Failed, held.Outcome);
            Assert.False(held.Withdrawn);
            Assert.Empty(w.Feeds.Withdrawals);
            Assert.Equal(w.Verification, await w.Shared.GetVerificationAsync("selfco", default));
        }
    }

    [Fact]
    public async Task A_failed_check_that_only_could_not_complete_changes_nothing()
    {
        var w = new World();
        w.Results(("views", CheckStatus.Fail, "could not complete: HttpRequestException: reset"));
        var held = await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        Assert.Equal(FeedCheckOutcome.Failed, held.Outcome);
        Assert.False(held.Withdrawn);
        Assert.Empty(w.Feeds.Withdrawals);
    }

    [Fact]
    public async Task A_failed_check_of_another_feed_changes_nothing()
    {
        var w = new World(feed: Feed);   // connected to Feed; the check below resolves to the fake's other URL
        w.Results(("upstreams", CheckStatus.Fail, "remove 1 extra upstream source(s)"));
        var held = await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        Assert.Equal(FeedCheckOutcome.Failed, held.Outcome);
        Assert.NotEqual(Feed, held.FeedIndexUrl);
        Assert.False(held.Withdrawn);
        Assert.Empty(w.Feeds.Withdrawals);
        Assert.NotNull(await w.Shared.GetVerificationAsync("selfco", default));
    }

    [Fact]
    public async Task An_operator_made_shared_tenant_is_never_withdrawn_by_the_wizard()
    {
        var w = new World(origin: TenantOrigin.Operator);   // e2e-shared's shape: check only
        w.Results(("upstreams", CheckStatus.Fail, "remove 1 extra upstream source(s)"));
        var held = await w.Commands.RunAsync(w.Admin, new AzureSetupRequest(null, Array.Empty<string>()), Token, default);
        Assert.Equal(FeedCheckOutcome.Failed, held.Outcome);
        Assert.False(held.Recorded || held.Withdrawn);
        Assert.Empty(w.Feeds.Withdrawals);
        Assert.Equal(w.Verification, await w.Shared.GetVerificationAsync("selfco", default));
    }

    [Fact]
    public async Task A_failed_check_of_a_tenant_that_is_not_connected_withdraws_nothing()
    {
        var w = new World(connected: false);
        w.Results(("upstreams", CheckStatus.Fail, "remove 1 extra upstream source(s)"));
        var held = await w.Commands.RunAsync(w.Admin, w.Names, Token, default);
        Assert.False(held.Withdrawn);
        Assert.Empty(w.Feeds.Withdrawals);
    }

    // ---- the Connect offer on the page

    [Fact]
    public async Task The_page_offers_connect_only_when_the_latest_settings_and_developer_checks_qualify()
    {
        var w = new World(connected: false);
        Assert.Null((await w.Commands.ViewAsync(w.Admin, default)).Connect);
        w.Checks.Rows.Add(Settings() with { Id = 1, ClientId = Client });
        Assert.Null((await w.Commands.ViewAsync(w.Admin, default)).Connect);   // no developer proof yet
        w.Checks.Rows.Add(Developer() with { Id = 2 });
        var offer = (await w.Commands.ViewAsync(w.Admin, default)).Connect;
        Assert.NotNull(offer);
        Assert.Equal((1L, 2L), (offer.Settings.Id, offer.Developer.Id));
        // an H2 row (no 'private') as the latest settings check: no button
        w.Checks.Rows.Add(Settings(results: Pass("feed", "identity", "upstreams", "permissions", "views", "project")) with { Id = 3 });
        Assert.Null((await w.Commands.ViewAsync(w.Admin, default)).Connect);
    }

    [Fact]
    public async Task A_connected_tenant_is_offered_no_connect()
    {
        var w = new World(feed: Feed);
        w.Checks.Rows.Add(Settings() with { Id = 1 });
        w.Checks.Rows.Add(Developer() with { Id = 2 });
        var view = await w.Commands.ViewAsync(w.Admin, default);
        Assert.Equal(UpstreamReadiness.Ready, view.Readiness);
        Assert.Null(view.Connect);
    }

    /// <summary>Once the failed check is recorded, the browser going away (the request's token cancelled) must not skip the withdrawal.</summary>
    [Fact]
    public async Task A_browser_that_goes_away_after_a_failed_check_is_recorded_still_gets_the_feed_withdrawn()
    {
        var w = new World();
        using var request = new CancellationTokenSource();
        w.Check.During = () => { request.Cancel(); return Task.CompletedTask; };   // the person closes the tab while the check runs
        w.Results(("upstreams", CheckStatus.Fail, "remove 1 extra upstream source(s): https://evil.example/"));
        var held = await w.Commands.RunAsync(w.Admin, w.Names, Token, request.Token);
        Assert.True(held.Withdrawn);
        Assert.Single(w.Feeds.Withdrawals);
        Assert.Null(await w.Shared.GetVerificationAsync("selfco", default));
    }
}
