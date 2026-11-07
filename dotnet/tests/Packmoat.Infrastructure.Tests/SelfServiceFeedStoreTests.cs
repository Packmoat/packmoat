using Microsoft.Data.Sqlite;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

/// <summary><see cref="SelfServiceFeedStoreContract"/> on SQLite, and the tests only SQLite has (#232 S1).</summary>
[Collection("sqlite-pools")]
public class SelfServiceFeedStoreTests : SelfServiceFeedStoreContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    [Fact]
    public async Task Migration_14_on_a_v13_database_makes_every_check_a_settings_check_and_adds_the_developer_check_id()
    {
        await using var t = new TestDb();
        await t.Db.MigrateAsync(13);
        await t.AddLegacyTenantAsync("e2e-shared");
        await ExecAsync(t,
            "INSERT INTO feed_checks(tenant_id, checked_at, checked_by_tid, checked_by_oid, checked_by_name, azure_tenant_id, feed_index_url, client_id, outcome, results) " +
            $"VALUES('e2e-shared','2026-10-01T00:00:00.0000000+00:00','{Dir}','{Admin}','Ada','{Dir}','{Feed}','{Client}','passed','[]')");
        await ExecAsync(t, $"INSERT INTO shared_app_verifications(tenant_id, azure_tenant_id, feed_index_url, client_id, verified_at, verified_by) VALUES('e2e-shared','{Dir}','{Feed}','{Client}','2026-10-01T00:00:00.0000000+00:00','onboard check')");

        await t.Db.MigrateAsync([]);
        Assert.True(SqliteDatabase.LatestVersion >= 14);
        Assert.Equal((long)SqliteDatabase.LatestVersion, await ScalarAsync(t, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal("settings", await ScalarAsync(t, "SELECT kind FROM feed_checks"));
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_feed_checks_tenant_kind'"));
        Assert.Equal(DBNull.Value, await ScalarAsync(t, "SELECT developer_check_id FROM shared_app_verifications"));
        Assert.Equal("operator", await ScalarAsync(t, "SELECT verified_via FROM shared_app_verifications"));
        Assert.Equal(FeedCheckKind.Settings, (await new SqliteFeedCheckStore(t.Db).LatestAsync("e2e-shared", FeedCheckKind.Settings, default))!.Kind);
        Assert.Null(await new SqliteFeedCheckStore(t.Db).LatestAsync("e2e-shared", FeedCheckKind.Developer, default));

        // kind takes only settings or developer; developer_check_id names only a real check
        const string check = "INSERT INTO feed_checks(tenant_id, checked_at, checked_by_tid, checked_by_oid, checked_by_name, azure_tenant_id, feed_index_url, client_id, outcome, results, kind) " +
                             "VALUES('e2e-shared','t','d','o','n','d','f','c','passed','[]',";
        await Assert.ThrowsAsync<SqliteException>(() => ExecAsync(t, check + "'admin')"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecAsync(t, check + "NULL)"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecAsync(t, "UPDATE shared_app_verifications SET developer_check_id=4242"));
        await ExecAsync(t, check + "'developer')");
        await ExecAsync(t, "UPDATE shared_app_verifications SET developer_check_id=(SELECT MAX(id) FROM feed_checks)");

        await t.Db.MigrateAsync([]);   // running again changes nothing
        Assert.Equal((long)SqliteDatabase.LatestVersion, await ScalarAsync(t, "SELECT MAX(version) FROM schema_migrations"));
    }

    [Fact]
    public async Task The_directory_race_is_serialised_the_second_writer_sees_the_first()
    {
        await using var s = await StartAsync();
        await s.T.AddTenantAsync("e2e-shared");
        await using var writer = await s.T.Db.OpenAsync();
        await using (var begin = writer.CreateCommand()) { begin.CommandText = "BEGIN IMMEDIATE"; await begin.ExecuteNonQueryAsync(); }
        var connect = Task.Run(() => s.Store.ConnectSelfServiceFeedAsync(Request(s), default));   // waits for the write lock (busy_timeout)
        await Task.Delay(300);
        Assert.False(connect.IsCompleted);
        await using (var bind = writer.CreateCommand())
        {
            bind.CommandText = $"INSERT INTO upstream_identities(tenant_id, kind, azure_tenant_id, client_id, cert_encrypted, updated_at) VALUES('e2e-shared','shared','{Dir}',NULL,NULL,'t'); COMMIT";
            await bind.ExecuteNonQueryAsync();
        }
        Assert.Equal(ConnectResult.DirectoryTaken, await connect);
        Assert.Null(await s.T.Tenants.GetUpstreamBindingAsync("selfco", default));
        Assert.Null(await s.Shared.GetVerificationAsync("selfco", default));
    }
}

/// <summary>
/// #207: the whole Connect suite above for an operator-made tenant made for its customer to connect (feed_setup 'customer'), plus the
/// operator's takeover: any operator write of the upstream or identity sets feed_setup back to 'operator' in its own transaction, and a
/// later Connect is refused.
/// </summary>
[Collection("sqlite-pools")]
public class FlaggedOperatorSelfServiceFeedStoreTests : SelfServiceFeedStoreTests
{
    protected override bool Flagged => true;

    // Dir, Client, Admin, Dev, Feed, SettingsIds and Pass are the contract's.

    private static async Task<object?> Scalar(TestDb t, string sql)
    {
        await using var c = await t.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private static FeedCheck Settings(TestDb t, TimeSpan age) =>
        new("acme", t.Clock.UtcNow - age, Dir, Admin, "Ada", Dir, Feed, Client, FeedCheckOutcome.Passed, Pass(SettingsIds), new[] { "Ada" });

    /// <summary>A flagged tenant as `tenant create … --azure-tenant` leaves it, its directory linked, the shared app active, two qualifying checks.</summary>
    private static async Task<(TestDb T, long Settings, long Developer)> FlaggedAsync()
    {
        var t = await TestDb.CreateAsync();
        await t.Tenants.CreateAsync(new NewTenant("acme", "Acme", "", "", "", 14, TenantOrigin.Operator) { FeedSetup = FeedSetup.Customer }, default);
        await new SqliteSignInDirectoryStore(t.Db).BindAsync(new DirectoryBinding(Dir, "acme", t.Clock.UtcNow, "op"), default);
        await new SqliteSharedAppCredentialStore(t.Db, t.Clock, t.Protector).SetAsync(SharedAppSlot.Active, new SharedAppCredential(Client, "PEM"), default);
        var checks = new SqliteFeedCheckStore(t.Db);
        var settings = await checks.RecordAsync(Settings(t, TimeSpan.FromHours(1)), default);
        var developer = await checks.RecordAsync(new FeedCheck("acme", t.Clock.UtcNow - TimeSpan.FromHours(2), Dir, Dev, "Grace", Dir, Feed, Client, FeedCheckOutcome.Passed,
            Pass("developer"), Array.Empty<string>()) { Kind = FeedCheckKind.Developer }, default);
        return (t, settings, developer);
    }

    private static ConnectFeedRequest Connect(TestDb t, long settings, long developer) =>
        new("acme", Dir, settings, developer, Feed, Client, t.Clock.UtcNow, "wizard: Ada (" + Admin + ")",
            new AdminActionRecord(t.Clock.UtcNow, Dir, Admin, "Ada", ConnectRules.ConnectAction, "acme", "feed=" + Feed, "app"));

    private static Task<object?> Written(TestDb t) =>
        Scalar(t, "SELECT (SELECT upstream_base || allowed_path_prefix || feed_index_url || policy_revision FROM tenants WHERE id='acme') || '|' || " +
                  "(SELECT COUNT(*) FROM admin_actions) || '|' || (SELECT COALESCE(MAX(verified_at), '') FROM shared_app_verifications)");

    public static TheoryData<string> OperatorWrites => new() { "onboard check", "identity bind", "identity set" };

    [Theory]   // mutations: onboard (or identity bind, or identity set) does not clear the flag
    [MemberData(nameof(OperatorWrites))]
    public async Task An_operator_write_of_the_upstream_or_identity_takes_the_feed_over_and_a_later_connect_is_refused(string write)
    {
        var (t, settings, developer) = await FlaggedAsync();
        await using var _ = t;
        var store = new SqliteSelfServiceFeedStore(t.Db);
        var shared = new SqliteSharedAppCredentialStore(t.Db, t.Clock, t.Protector);
        Assert.Equal(new TenantSetup(TenantOrigin.Operator, FeedSetup.Customer), await t.Tenants.GetSetupAsync("acme", default));
        Assert.Equal(ConnectResult.Connected, await store.ConnectSelfServiceFeedAsync(Connect(t, settings, developer), default));
        Assert.Equal("customer", await Scalar(t, "SELECT feed_setup FROM tenants WHERE id='acme'"));   // Connect keeps it: the customer may reconnect

        switch (write)
        {
            case "onboard check":   // the operator's record of a passing onboard check, for what Connect set
                Assert.True(await shared.RecordVerificationAsync(new SharedAppVerification("acme", Dir, Feed, Client, t.Clock.UtcNow, "onboard check"), default));
                break;
            case "identity bind":
                await t.Tenants.BindSharedIdentityAsync("acme", Dir, default);
                break;
            case "identity set":
                await t.Tenants.SetUpstreamIdentityAsync("acme", new UpstreamIdentity(Dir, "99999999-9999-4999-8999-999999999999", "PEM"), default);
                break;
        }
        Assert.Equal(new TenantSetup(TenantOrigin.Operator, FeedSetup.Operator), await t.Tenants.GetSetupAsync("acme", default));

        // A later Connect, with a fresh qualifying settings check, is refused at its first step and writes nothing.
        t.Clock.UtcNow += TimeSpan.FromMinutes(5);
        var newer = await new SqliteFeedCheckStore(t.Db).RecordAsync(Settings(t, TimeSpan.Zero), default);
        var before = await Written(t);
        Assert.Equal(ConnectResult.NotSelfMade, await store.ConnectSelfServiceFeedAsync(Connect(t, newer, developer), default));
        Assert.Equal(before, await Written(t));
    }

    [Fact]   // mutation: drop "OR feed_setup='customer'" from Connect's first step
    public async Task The_same_operator_tenant_is_refused_unflagged_and_connects_flagged()
    {
        var (t, settings, developer) = await FlaggedAsync();
        await using var _ = t;
        var store = new SqliteSelfServiceFeedStore(t.Db);
        await Scalar(t, "UPDATE tenants SET feed_setup='operator' WHERE id='acme'");
        Assert.Equal(ConnectResult.NotSelfMade, await store.ConnectSelfServiceFeedAsync(Connect(t, settings, developer), default));
        await Scalar(t, "UPDATE tenants SET feed_setup='customer' WHERE id='acme'");
        Assert.Equal(ConnectResult.Connected, await store.ConnectSelfServiceFeedAsync(Connect(t, settings, developer), default));
    }
}
