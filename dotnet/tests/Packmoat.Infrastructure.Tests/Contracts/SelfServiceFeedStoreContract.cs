using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The contract: LatestAsync by kind, and the self-service feed store: Connect is all or nothing (every refusal writes
/// nothing), a check from another tenant or a settings check that is not the latest is refused, and Withdraw deletes only the exact record.
/// Every test here runs for a self-made tenant (<see cref="SelfServiceFeedStoreTests"/>) and again for an operator-made tenant made
/// for its customer to connect (<see cref="FlaggedOperatorSelfServiceFeedStoreTests"/>): every Connect check holds for both. Migration 14
/// and the directory race (two SQLite connections) stay in the SQLite fixture. the snapshot reads a check id as text (on
/// PostgreSQL COALESCE cannot mix a bigint and ''), and two tenants claiming one directory at once is a contract test.
/// </summary>
public abstract class SelfServiceFeedStoreContract : StoreContract
{
    /// <summary>The tenant shape under test: self-made (origin 'self'), or operator-made with feed_setup 'customer'.</summary>
    protected virtual bool Flagged => false;

    protected const string Dir = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    protected const string OtherDir = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    protected const string Client = "5a2ed000-0000-4000-8000-000000000001";
    protected const string Admin = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    protected const string Dev = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
    protected const string Prefix = "contoso/7a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d/_packaging/0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f/";
    protected const string Feed = "https://pkgs.dev.azure.com/" + Prefix + "nuget/v3/index.json";

    protected static readonly string[] SettingsIds = { "feed", "identity", "upstreams", "permissions", "views", "private", "project" };

    protected static IReadOnlyList<CheckResult> Pass(params string[] ids) => ids.Select(id => new CheckResult(id, id, CheckStatus.Pass, "ok")).ToList();

    protected sealed class Setup : IAsyncDisposable
    {
        public required IEngineDb T { get; init; }
        public required IStoreEngine Engine { get; init; }
        public ValueTask DisposeAsync() => T.DisposeAsync();
        public IFeedCheckStore Checks => Engine.FeedChecks(T);
        public ISelfServiceFeedStore Store => Engine.SelfServiceFeeds(T);
        public ISharedAppCredentialStore Shared => Engine.SharedApp(T);
        public long SettingsId { get; set; }
        public long DeveloperId { get; set; }
    }

    /// <summary>A self-made tenant as self sign-up leaves it (no URLs), or a flagged operator-made one as `tenant create … --azure-tenant`
    /// leaves it; its directory linked, the shared app active, and two qualifying checks.</summary>
    protected async Task<Setup> StartAsync(string tenant = "selfco", string dir = Dir, bool checks = true)
    {
        var t = await CreateDbAsync();
        await AddSelfMadeAsync(t, tenant, dir);
        var s = new Setup { T = t, Engine = Engine };
        await s.Shared.SetAsync(SharedAppSlot.Active, new SharedAppCredential(Client, "PEM"), default);
        if (checks)
        {
            s.SettingsId = await s.Checks.RecordAsync(SettingsCheck(t, tenant), default);
            s.DeveloperId = await s.Checks.RecordAsync(DeveloperCheck(t, tenant), default);
        }
        return s;
    }

    protected async Task AddSelfMadeAsync(IEngineDb t, string tenant, string dir)
    {
        if (Flagged)
            await Engine.Tenants(t).CreateAsync(new NewTenant(tenant, tenant.ToUpperInvariant(), "", "", "", 14, TenantOrigin.Operator) { FeedSetup = FeedSetup.Customer }, default);
        else
        {
            await Engine.Tenants(t).CreateAsync(new NewTenant(tenant, tenant.ToUpperInvariant(), "https://pkgs.dev.azure.com/", "", "", 14, TenantOrigin.Self) { CreatedByOid = Admin }, default);
            await ExecAsync(t, $"UPDATE tenants SET upstream_base='' WHERE id='{tenant}'");   // as self sign-up leaves it: no URLs at all
        }
        await Engine.SignInDirectory(t).BindAsync(new DirectoryBinding(dir, tenant, t.Clock.UtcNow, Flagged ? "op" : "self-signup"), default);
    }

    /// <summary>
    /// An operator write of the identity takes a flagged tenant's feed over, so Connect then refuses at its first step. To reach
    /// the later check a test is about, the flag is put back by hand (nothing in the product does this). A self-made tenant: no change.
    /// </summary>
    protected async Task KeepShapeAsync(IEngineDb t, string tenant = "selfco")
    {
        if (Flagged) await ExecAsync(t, $"UPDATE tenants SET feed_setup='customer' WHERE id='{tenant}'");
    }

    protected static FeedCheck SettingsCheck(IEngineDb t, string tenant = "selfco", TimeSpan? age = null, IReadOnlyList<CheckResult>? results = null,
        string dir = Dir, string feed = Feed, FeedCheckOutcome outcome = FeedCheckOutcome.Passed) =>
        new FeedCheck(tenant, t.Clock.UtcNow - (age ?? TimeSpan.FromHours(1)), dir, Admin, "Ada Lovelace", dir, feed, Client, outcome,
            results ?? Pass(SettingsIds), new[] { "Ada Lovelace" });

    protected static FeedCheck DeveloperCheck(IEngineDb t, string tenant = "selfco", TimeSpan? age = null, string oid = Dev, string dir = Dir) =>
        new FeedCheck(tenant, t.Clock.UtcNow - (age ?? TimeSpan.FromHours(2)), dir, oid, "Grace Hopper", dir, Feed, Client, FeedCheckOutcome.Passed,
            Pass("developer"), Array.Empty<string>()) { Kind = FeedCheckKind.Developer };

    protected static ConnectFeedRequest Request(Setup s, long? settings = null, long? developer = null, string tenant = "selfco", string dir = Dir,
        string feed = Feed, string client = Client) =>
        new(tenant, dir, settings ?? s.SettingsId, developer ?? s.DeveloperId, feed, client, s.T.Clock.UtcNow, "wizard: Ada Lovelace (" + Admin + ")",
            new AdminActionRecord(s.T.Clock.UtcNow, dir, Admin, "Ada Lovelace", ConnectRules.ConnectAction, tenant, $"feed={feed}", "app"));

    /// <summary>Everything Connect could write, as text: compare before and after a refusal.</summary>
    protected async Task<string> Snapshot(IEngineDb t)
    {
        var parts = new List<string>();
        foreach (var sql in new[]
                 {
                     "SELECT id || upstream_base || allowed_path_prefix || feed_index_url || policy_revision FROM tenants ORDER BY id",
                     $"SELECT tenant_id || kind || azure_tenant_id || COALESCE(client_id,'') || COALESCE({Engine.HexSql("cert_encrypted")},'') FROM upstream_identities ORDER BY tenant_id",
                     "SELECT tenant_id || feed_index_url || verified_at || verified_via || COALESCE(CAST(feed_check_id AS TEXT),'') || COALESCE(CAST(developer_check_id AS TEXT),'') FROM shared_app_verifications ORDER BY tenant_id",
                     "SELECT COUNT(*) FROM admin_actions",
                 })
        {
            parts.AddRange(await ColumnAsync(t, sql));
            parts.Add("|");
        }
        return string.Join("\n", parts);
    }

    protected async Task Refused(Setup s, ConnectResult expected, ConnectFeedRequest? request = null)
    {
        var before = await Snapshot(s.T);
        Assert.Equal(expected, await s.Store.ConnectSelfServiceFeedAsync(request ?? Request(s), default));
        Assert.Equal(before, await Snapshot(s.T));
    }

    [EngineFact]
    public async Task Latest_is_per_kind_and_an_unknown_stored_kind_never_reads_as_either()
    {
        await using var s = await StartAsync();
        var newerSettings = await s.Checks.RecordAsync(SettingsCheck(s.T, age: TimeSpan.Zero), default);
        Assert.Equal(newerSettings, (await s.Checks.LatestAsync("selfco", FeedCheckKind.Settings, default))!.Id);
        var dev = await s.Checks.LatestAsync("selfco", FeedCheckKind.Developer, default);
        Assert.Equal((s.DeveloperId, FeedCheckKind.Developer, Dev), (dev!.Id, dev.Kind, dev.CheckedByOid));
        Assert.Null(await s.Checks.LatestAsync("other", FeedCheckKind.Developer, default));
        // A row with a kind this build does not know cannot be written (CHECK), and a kind enum this build does not know cannot be recorded.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => s.Checks.RecordAsync(SettingsCheck(s.T) with { Kind = (FeedCheckKind)7 }, default));
    }

    // ---- Connect

    [EngineFact]
    public async Task Connect_writes_the_tenant_urls_the_shared_binding_the_wizard_verification_and_feed_connect_in_one_go()
    {
        await using var s = await StartAsync();
        var revision = (long)(await ScalarAsync(s.T, "SELECT policy_revision FROM tenants WHERE id='selfco'"))!;
        Assert.Equal(ConnectResult.Connected, await s.Store.ConnectSelfServiceFeedAsync(Request(s), default));

        var tenant = (await Engine.Tenants(s.T).GetAsync("selfco", default))!;
        Assert.Equal(("https://pkgs.dev.azure.com/", Prefix, Feed), (tenant.UpstreamBase, tenant.AllowedPathPrefix, tenant.UpstreamFeedIndexUrl));
        Assert.Equal(revision + 1, await ScalarAsync(s.T, "SELECT policy_revision FROM tenants WHERE id='selfco'"));
        var binding = await Engine.Tenants(s.T).GetUpstreamBindingAsync("selfco", default);
        Assert.Equal(new UpstreamBinding(IdentityKind.Shared, Dir, null), binding);
        Assert.Equal("wizard", await ScalarAsync(s.T, "SELECT verified_via FROM shared_app_verifications WHERE tenant_id='selfco'"));
        Assert.Equal(s.SettingsId, await ScalarAsync(s.T, "SELECT feed_check_id FROM shared_app_verifications WHERE tenant_id='selfco'"));
        Assert.Equal(s.DeveloperId, await ScalarAsync(s.T, "SELECT developer_check_id FROM shared_app_verifications WHERE tenant_id='selfco'"));
        var v = await s.Shared.GetVerificationAsync("selfco", default);
        Assert.Equal((Dir, Feed, Client, s.T.Clock.UtcNow), (v!.AzureTenantId, v.FeedIndexUrl, v.ClientId, v.VerifiedAt));
        var action = Assert.Single(await Engine.AdminActions(s.T).LatestAsync(10, default));
        Assert.Equal((ConnectRules.ConnectAction, "selfco", "app"), (action.Action, action.TenantId, action.Site));
        // and the tenant now serves (the shared readiness rule, with the active client)
        Assert.Equal(UpstreamReadiness.Ready, UpstreamReadiness.Of(tenant, binding, v, Client));
    }

    [EngineFact]
    public async Task A_check_from_another_tenant_is_refused_even_when_everything_else_matches()
    {
        await using var s = await StartAsync();
        // "rival" is another self-made tenant (in another directory); its rows claim this directory, feed and client.
        await AddSelfMadeAsync(s.T, "rival", OtherDir);
        var foreignSettings = await s.Checks.RecordAsync(SettingsCheck(s.T, "rival", age: TimeSpan.FromMinutes(1)), default);
        var foreignDeveloper = await s.Checks.RecordAsync(DeveloperCheck(s.T, "rival"), default);
        await Refused(s, ConnectResult.CheckNotFound, Request(s, developer: foreignDeveloper));
        // a newer foreign settings row is not this tenant's latest either: still the tenant check that refuses
        await Refused(s, ConnectResult.CheckNotFound, Request(s, settings: foreignSettings));
        Assert.Equal(ConnectResult.Connected, await s.Store.ConnectSelfServiceFeedAsync(Request(s), default));   // its own rows still connect
    }

    [EngineFact]
    public async Task A_settings_check_that_is_not_the_latest_is_refused()
    {
        await using var s = await StartAsync();
        // a newer settings check, even an incomplete one, replaces the one the page showed
        await s.Checks.RecordAsync(SettingsCheck(s.T, age: TimeSpan.FromMinutes(5), outcome: FeedCheckOutcome.Incomplete), default);
        await Refused(s, ConnectResult.NotLatest);
    }

    [EngineFact]
    public async Task An_older_passing_developer_check_never_outweighs_a_newer_failed_one_for_the_same_feed()
    {
        await using var s = await StartAsync();   // the developer pass is 2 hours old
        await s.Checks.RecordAsync(DeveloperCheck(s.T, age: TimeSpan.FromMinutes(30)) with
        {
            Outcome = FeedCheckOutcome.Failed,
            Results = new[] { new CheckResult("developer", "developer", CheckStatus.Fail, "200: the developer can read the feed") },
        }, default);
        await Refused(s, ConnectResult.NotLatest);
    }

    /// <summary>A newer developer check that could not complete also replaces an older pass (the latest decides, whatever its outcome).</summary>
    [EngineFact]
    public async Task An_older_passing_developer_check_and_a_newer_incomplete_one_for_the_same_feed_is_not_the_latest()
    {
        await using var s = await StartAsync();   // the developer pass is 2 hours old
        await s.Checks.RecordAsync(DeveloperCheck(s.T, age: TimeSpan.FromMinutes(30)) with
        {
            Outcome = FeedCheckOutcome.Incomplete,
            Results = new[] { new CheckResult("developer", "developer", CheckStatus.Skip, "NOT VERIFIED: developer account got HTTP 404") },
        }, default);
        await Refused(s, ConnectResult.NotLatest);
    }

    [EngineFact]
    public async Task A_newer_developer_check_of_another_feed_does_not_replace_this_feeds()
    {
        await using var s = await StartAsync();
        await s.Checks.RecordAsync(DeveloperCheck(s.T, age: TimeSpan.FromMinutes(30)) with { FeedIndexUrl = Feed.Replace("contoso", "fabrikam"), Outcome = FeedCheckOutcome.Failed }, default);
        Assert.Equal(ConnectResult.Connected, await s.Store.ConnectSelfServiceFeedAsync(Request(s), default));
    }

    [EngineFact]
    public async Task Missing_rows_and_rows_of_the_wrong_kind_are_refused()
    {
        await using var s = await StartAsync();
        await Refused(s, ConnectResult.CheckNotFound, Request(s, settings: 4242));
        await Refused(s, ConnectResult.CheckNotFound, Request(s, developer: 4242));
        await Refused(s, ConnectResult.CheckNotFound, Request(s, settings: s.DeveloperId, developer: s.SettingsId));
        await Refused(s, ConnectResult.CheckNotFound, Request(s, developer: s.SettingsId));
    }

    [EngineFact]
    public async Task The_tenant_must_be_active_self_made_and_linked_to_the_directory()
    {
        await using (var s = await StartAsync())
        {
            await ExecAsync(s.T, "UPDATE tenants SET origin='operator', feed_setup='operator' WHERE id='selfco'");   // neither self-made nor flagged
            await Refused(s, ConnectResult.NotSelfMade);
        }
        await using (var s = await StartAsync())
        {
            await Engine.Tenants(s.T).SetActiveAsync("selfco", false, default);
            await Refused(s, ConnectResult.NotSelfMade);
        }
        await using (var s = await StartAsync())
        {
            await ExecAsync(s.T, "DELETE FROM sign_in_directories");
            await Refused(s, ConnectResult.NotLinked);
        }
        await using (var s = await StartAsync())
        {
            await Refused(s, ConnectResult.NotSelfMade, Request(s, tenant: "ghost"));
            await Refused(s, ConnectResult.NotLinked, Request(s, dir: OtherDir));
        }
    }

    [EngineFact]
    public async Task The_client_and_feed_must_be_the_ones_the_page_showed()
    {
        await using var s = await StartAsync();
        await Refused(s, ConnectResult.ClientChanged, Request(s, client: "99999999-9999-4999-8999-999999999999"));
        await Refused(s, ConnectResult.FeedChanged, Request(s, feed: Feed.Replace("contoso", "fabrikam")));
        // a rotation since the check: the active client is not the one the rows were about
        await s.Shared.SetAsync(SharedAppSlot.Active, new SharedAppCredential("99999999-9999-4999-8999-999999999999", "PEM"), default);
        await Refused(s, ConnectResult.ClientChanged, Request(s, client: "99999999-9999-4999-8999-999999999999"));
        await ExecAsync(s.T, "DELETE FROM shared_app_credentials");
        await Refused(s, ConnectResult.ClientChanged);
    }

    [EngineFact]
    public async Task An_own_app_identity_is_never_overwritten()
    {
        await using var s = await StartAsync();
        await Engine.Tenants(s.T).SetUpstreamIdentityAsync("selfco", new UpstreamIdentity(Dir, "99999999-9999-4999-8999-999999999999", "-----BEGIN PRIVATE KEY-----\nOWN\n-----END PRIVATE KEY-----"), default);
        await KeepShapeAsync(s.T);
        await Refused(s, ConnectResult.OwnApp);
        Assert.NotNull(await Engine.Tenants(s.T).GetUpstreamIdentityAsync("selfco", default));
    }

    [EngineFact]
    public async Task A_directory_another_tenant_uses_is_refused_explicitly()
    {
        await using var s = await StartAsync();
        await AddTenantAsync(s.T, "e2e-shared");   // operator-made
        await Engine.Tenants(s.T).BindSharedIdentityAsync("e2e-shared", Dir.ToUpperInvariant(), default);
        await Refused(s, ConnectResult.DirectoryTaken);
        Assert.Equal(new UpstreamBinding(IdentityKind.Shared, Dir.ToUpperInvariant(), null), await Engine.Tenants(s.T).GetUpstreamBindingAsync("e2e-shared", default));
    }

    /// <summary>
    /// Two tenants claim one shared directory at once, this tenant through Connect and another through the operator's bind,
    /// started together, five times over. Exactly one holds the directory afterwards. If it is this tenant, Connect said Connected and the
    /// bind was refused by the database. If it is the other, Connect said DirectoryTaken, whether it saw the bind's row or met the
    /// one-directory index (on PostgreSQL), and wrote nothing (no binding, no verification, no record). SQLite serialises the two; PostgreSQL
    /// locks each claimant's own tenant row, so the unique index is what decides between two tenants.
    /// </summary>
    [EngineFact]
    public async Task Two_tenants_claiming_one_shared_directory_at_once_exactly_one_wins()
    {
        for (var round = 0; round < 5; round++)
        {
            await using var s = await StartAsync();
            await AddTenantAsync(s.T, "e2e-shared");
            var actionsBefore = (long)(await ScalarAsync(s.T, "SELECT COUNT(*) FROM admin_actions"))!;
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connect = Task.Run(async () => { await go.Task; return await s.Store.ConnectSelfServiceFeedAsync(Request(s), default); });
            var bind = Task.Run(async () => { await go.Task; await Engine.Tenants(s.T).BindSharedIdentityAsync("e2e-shared", Dir, default); });
            go.SetResult();
            await Task.WhenAll(connect, bind).ContinueWith(_ => { }, TaskScheduler.Default);   // both done, however each ended

            var holder = Assert.Single(await ColumnAsync(s.T, $"SELECT tenant_id FROM upstream_identities WHERE kind='shared' AND lower(azure_tenant_id)='{Dir}'"));
            if (holder == "selfco")
            {
                Assert.Equal(ConnectResult.Connected, await connect);
                await RefusedAsync(() => bind);
                Assert.Equal("wizard", await ScalarAsync(s.T, "SELECT verified_via FROM shared_app_verifications WHERE tenant_id='selfco'"));
            }
            else
            {
                Assert.Equal("e2e-shared", holder);
                await bind;
                Assert.Equal(ConnectResult.DirectoryTaken, await connect);   // never the index's error
                Assert.Null(await Engine.Tenants(s.T).GetUpstreamBindingAsync("selfco", default));
                Assert.Null(await s.Shared.GetVerificationAsync("selfco", default));
                Assert.Equal(actionsBefore, await ScalarAsync(s.T, "SELECT COUNT(*) FROM admin_actions"));
            }
        }
    }

    [EngineFact]
    public async Task A_shared_binding_of_this_tenant_in_another_directory_is_refused()
    {
        await using var s = await StartAsync();
        await Engine.Tenants(s.T).BindSharedIdentityAsync("selfco", OtherDir, default);
        await KeepShapeAsync(s.T);
        await Refused(s, ConnectResult.DirectoryTaken);
    }

    [EngineFact]
    public async Task The_rules_run_inside_the_transaction_windows_people_and_results()
    {
        await using (var s = await StartAsync())
        {
            s.T.Clock.UtcNow += TimeSpan.FromHours(23);   // the settings row is now exactly 24 hours old: still fine
            Assert.Equal(ConnectResult.Connected, await s.Store.ConnectSelfServiceFeedAsync(Request(s), default));
        }
        await using (var s = await StartAsync())
        {
            s.T.Clock.UtcNow += TimeSpan.FromHours(23) + TimeSpan.FromSeconds(1);
            await Refused(s, ConnectResult.SettingsTooOld);
        }
        await using (var s = await StartAsync(checks: false))
        {
            s.SettingsId = await s.Checks.RecordAsync(SettingsCheck(s.T), default);
            s.DeveloperId = await s.Checks.RecordAsync(DeveloperCheck(s.T, oid: Admin), default);
            await Refused(s, ConnectResult.SamePerson);
        }
        await using (var s = await StartAsync(checks: false))
        {
            s.DeveloperId = await s.Checks.RecordAsync(DeveloperCheck(s.T, age: TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1)), default);
            s.SettingsId = await s.Checks.RecordAsync(SettingsCheck(s.T), default);
            await Refused(s, ConnectResult.DeveloperTooOld);
        }
        await using (var s = await StartAsync(checks: false))
        {
            // what the delegated check records: no 'private' result, so it can never be connected
            s.SettingsId = await s.Checks.RecordAsync(SettingsCheck(s.T, results: Pass("feed", "identity", "upstreams", "permissions", "views", "project")), default);
            s.DeveloperId = await s.Checks.RecordAsync(DeveloperCheck(s.T), default);
            await Refused(s, ConnectResult.NotPassed);
        }
        await using (var s = await StartAsync(checks: false))
        {
            s.SettingsId = await s.Checks.RecordAsync(SettingsCheck(s.T, dir: OtherDir), default);
            s.DeveloperId = await s.Checks.RecordAsync(DeveloperCheck(s.T, dir: OtherDir), default);
            await Refused(s, ConnectResult.ChecksDisagree);
        }
    }

    [EngineFact]
    public async Task Connecting_again_after_a_withdrawal_replaces_the_binding_and_record_and_keeps_one_row_each()
    {
        await using var s = await StartAsync();
        Assert.Equal(ConnectResult.Connected, await s.Store.ConnectSelfServiceFeedAsync(Request(s), default));
        var v = (await s.Shared.GetVerificationAsync("selfco", default))!;
        Assert.True(await s.Store.WithdrawAsync(v, new AdminActionRecord(s.T.Clock.UtcNow, Dir, Admin, "Ada", ConnectRules.WithdrawnAction, "selfco", "x", "app"), default));
        s.T.Clock.UtcNow += TimeSpan.FromMinutes(10);
        s.SettingsId = await s.Checks.RecordAsync(SettingsCheck(s.T, age: TimeSpan.Zero), default);
        Assert.Equal(ConnectResult.Connected, await s.Store.ConnectSelfServiceFeedAsync(Request(s), default));
        Assert.Equal(1L, await ScalarAsync(s.T, "SELECT COUNT(*) FROM upstream_identities WHERE tenant_id='selfco'"));
        Assert.Equal(s.SettingsId, await ScalarAsync(s.T, "SELECT feed_check_id FROM shared_app_verifications WHERE tenant_id='selfco'"));
        Assert.Equal(new[] { ConnectRules.ConnectAction, ConnectRules.WithdrawnAction, ConnectRules.ConnectAction },
            (await Engine.AdminActions(s.T).LatestAsync(10, default)).Select(a => a.Action));
    }

    // ---- Withdraw and the operator's record

    [EngineFact]
    public async Task Withdraw_deletes_only_the_exact_record_and_writes_feed_withdrawn_with_it()
    {
        await using var s = await StartAsync();
        Assert.Equal(ConnectResult.Connected, await s.Store.ConnectSelfServiceFeedAsync(Request(s), default));
        var v = (await s.Shared.GetVerificationAsync("selfco", default))!;
        var action = new AdminActionRecord(s.T.Clock.UtcNow, Dir, Admin, "Ada", ConnectRules.WithdrawnAction, "selfco", "failed=upstreams", "app");

        var before = await Snapshot(s.T);
        Assert.False(await s.Store.WithdrawAsync(v with { VerifiedAt = v.VerifiedAt.AddSeconds(1) }, action, default));   // a newer record is not this one
        Assert.False(await s.Store.WithdrawAsync(v with { FeedIndexUrl = Feed.Replace("contoso", "fabrikam") }, action, default));
        Assert.Equal(before, await Snapshot(s.T));

        Assert.True(await s.Store.WithdrawAsync(v, action, default));
        Assert.Null(await s.Shared.GetVerificationAsync("selfco", default));
        Assert.Equal(ConnectRules.WithdrawnAction, (await Engine.AdminActions(s.T).LatestAsync(1, default))[0].Action);
        // the URLs and binding stay: the tenant is simply not verified any more, so it serves nothing
        var tenant = (await Engine.Tenants(s.T).GetAsync("selfco", default))!;
        Assert.Equal(UpstreamReadiness.NotVerified, UpstreamReadiness.Of(tenant, await Engine.Tenants(s.T).GetUpstreamBindingAsync("selfco", default), null, Client));
        Assert.False(await s.Store.WithdrawAsync(v, action, default));   // only once
    }

    [EngineFact]
    public async Task The_operators_record_replacing_a_wizard_record_is_operator_made_with_no_check_ids()
    {
        await using var s = await StartAsync();
        Assert.Equal(ConnectResult.Connected, await s.Store.ConnectSelfServiceFeedAsync(Request(s), default));
        Assert.True(await s.Shared.RecordVerificationAsync(new SharedAppVerification("selfco", Dir, Feed, Client, s.T.Clock.UtcNow, "onboard check"), default));
        Assert.Equal("operator", await ScalarAsync(s.T, "SELECT verified_via FROM shared_app_verifications WHERE tenant_id='selfco'"));
        Assert.Equal(DBNull.Value, await ScalarAsync(s.T, "SELECT feed_check_id FROM shared_app_verifications WHERE tenant_id='selfco'"));
        Assert.Equal(DBNull.Value, await ScalarAsync(s.T, "SELECT developer_check_id FROM shared_app_verifications WHERE tenant_id='selfco'"));
    }

    /// <summary>
    /// The withdraw race: the wizard's failed check read its record, then the operator verified the tenant afresh. Even
    /// with every value the same, the operator's record is never withdrawn: only a wizard-made record is. Mutation: drop verified_via='wizard'.
    /// </summary>
    [EngineFact]
    public async Task Withdraw_never_deletes_an_operators_record_even_with_the_same_values()
    {
        await using var s = await StartAsync();
        Assert.Equal(ConnectResult.Connected, await s.Store.ConnectSelfServiceFeedAsync(Request(s), default));
        var op = new SharedAppVerification("selfco", Dir, Feed, Client, s.T.Clock.UtcNow, "onboard check");
        Assert.True(await s.Shared.RecordVerificationAsync(op, default));
        Assert.Equal(op, await s.Shared.GetVerificationAsync("selfco", default));
        var action = new AdminActionRecord(s.T.Clock.UtcNow, Dir, Admin, "Ada", ConnectRules.WithdrawnAction, "selfco", "failed=upstreams", "app");

        var before = await Snapshot(s.T);
        Assert.False(await s.Store.WithdrawAsync(op, action, default));
        Assert.Equal(before, await Snapshot(s.T));
        Assert.Equal("operator", await ScalarAsync(s.T, "SELECT verified_via FROM shared_app_verifications WHERE tenant_id='selfco'"));
        Assert.NotEqual(ConnectRules.WithdrawnAction, (await Engine.AdminActions(s.T).LatestAsync(1, default))[0].Action);
    }

    [EngineFact]
    public async Task Operator_made_tenants_like_the_canary_and_e2e_tenants_are_never_connected()
    {
        await using var s = await StartAsync();
        foreach (var id in new[] { "canary", "e2e-test" })
        {
            await AddTenantAsync(s.T, id);
            await s.Checks.RecordAsync(SettingsCheck(s.T, id), default);
        }
        await Engine.Tenants(s.T).SetUpstreamIdentityAsync("e2e-test", new UpstreamIdentity(OtherDir, "99999999-9999-4999-8999-999999999999", "PEM"), default);
        var canaryCheck = (await s.Checks.LatestAsync("canary", FeedCheckKind.Settings, default))!.Id;
        await Refused(s, ConnectResult.NotSelfMade, Request(s, tenant: "canary", settings: canaryCheck));
        await Refused(s, ConnectResult.NotSelfMade, Request(s, tenant: "e2e-test"));
        Assert.Equal("https://pkgs.dev.azure.com/example-org/example-project/_packaging/f/nuget/v3/index.json", (await Engine.Tenants(s.T).GetAsync("canary", default))!.UpstreamFeedIndexUrl);
    }
}
