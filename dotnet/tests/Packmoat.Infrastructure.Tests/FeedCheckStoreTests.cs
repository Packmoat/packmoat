using Microsoft.Data.Sqlite;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

/// <summary><see cref="FeedCheckStoreContract"/> on SQLite, and the tests only SQLite has (#232 S1).</summary>
[Collection("sqlite-pools")]
public class FeedCheckStoreTests : FeedCheckStoreContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    [Fact]
    public async Task Migration_13_on_a_v12_database_adds_feed_checks_and_keeps_existing_verifications_as_operator_made()
    {
        await using var t = new TestDb();
        await t.Db.MigrateAsync(12);
        await t.AddLegacyTenantAsync("e2e-shared");
        await ExecAsync(t, $"INSERT INTO shared_app_verifications(tenant_id, azure_tenant_id, feed_index_url, client_id, verified_at, verified_by) VALUES('e2e-shared','{Dir}','{Feed}','{Client}','2026-10-01T00:00:00.0000000+00:00','onboard check')");
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='feed_checks'"));

        await t.Db.MigrateAsync([]);
        Assert.True(SqliteDatabase.LatestVersion >= 13);
        Assert.Equal((long)SqliteDatabase.LatestVersion, await ScalarAsync(t, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='feed_checks'"));
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_feed_checks_tenant'"));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM feed_checks"));
        Assert.Equal("operator", await ScalarAsync(t, "SELECT verified_via FROM shared_app_verifications WHERE tenant_id='e2e-shared'"));
        Assert.Equal(DBNull.Value, await ScalarAsync(t, "SELECT feed_check_id FROM shared_app_verifications WHERE tenant_id='e2e-shared'"));
        // and the record still reads, and still vouches for the same binding
        var v = await new SqliteSharedAppCredentialStore(t.Db, t.Clock, t.Protector).GetVerificationAsync("e2e-shared", default);
        Assert.Equal((Dir, Feed, Client), (v!.AzureTenantId, v.FeedIndexUrl, v.ClientId));

        await t.Db.MigrateAsync([]);   // running again changes nothing
        Assert.Equal((long)SqliteDatabase.LatestVersion, await ScalarAsync(t, "SELECT MAX(version) FROM schema_migrations"));
    }

    [Fact]
    public async Task Migration_16_on_a_copy_of_a_v15_database_adds_a_nullable_feed_names_column_and_changes_no_row()
    {
        await using var t = new TestDb();
        await t.Db.MigrateAsync(15);
        await t.AddCoreTenantAsync("acme");
        const string results = "[{\"id\":\"feed\",\"title\":\"F\",\"status\":\"pass\",\"detail\":\"ok\"}]";
        await ExecAsync(t, "INSERT INTO feed_checks(tenant_id, checked_at, checked_by_tid, checked_by_oid, checked_by_name, azure_tenant_id, feed_index_url, client_id, outcome, results, confirmed_admins, kind) " +
                      $"VALUES('acme','2026-10-18T10:00:00.0000000+00:00','{Dir}','ffffffff-ffff-4fff-8fff-ffffffffffff','Grace Hopper','{Dir}','{Feed}','{Client}','passed','{results}','[\"Ada Lovelace\"]','settings')");
        await ExecAsync(t, "INSERT INTO feed_checks(tenant_id, checked_at, checked_by_tid, checked_by_oid, checked_by_name, azure_tenant_id, feed_index_url, client_id, outcome, results, kind) " +
                      $"VALUES('acme','2026-10-18T11:00:00.0000000+00:00','{Dir}','dddddddd-dddd-4ddd-8ddd-dddddddddddd','Dev','{Dir}','{Feed}','{Client}','passed','{results}','developer')");
        const string snapshot = "SELECT group_concat(id || '|' || tenant_id || '|' || checked_at || '|' || checked_by_name || '|' || feed_index_url || '|' || outcome || '|' || results || '|' || confirmed_admins || '|' || kind, ';') FROM (SELECT * FROM feed_checks ORDER BY id)";
        var before = await ScalarAsync(t, snapshot);

        // On a copy, as on the server (the deploy takes a backup first): the original stays at 15.
        var copyPath = System.IO.Path.Combine(t.Dir, "copy.db");
        await ExecAsync(t, $"VACUUM INTO '{copyPath}'");
        var copy = new SqliteDatabase(copyPath);
        await copy.MigrateAsync(16);
        Assert.True(SqliteDatabase.LatestVersion >= 16);
        await using (var c = await copy.OpenAsync())
        {
            async Task<object?> Q(string sql) { await using var cmd = c.CreateCommand(); cmd.CommandText = sql; return await cmd.ExecuteScalarAsync(); }
            Assert.Equal(16L, await Q("SELECT MAX(version) FROM schema_migrations"));
            Assert.Equal("ok", await Q("PRAGMA integrity_check"));
            Assert.Null(await Q("PRAGMA foreign_key_check"));
            Assert.Equal(before, await Q(snapshot));                                                 // no existing value changed
            Assert.Equal(2L, await Q("SELECT COUNT(*) FROM feed_checks WHERE feed_names IS NULL"));   // every existing row: no names
            Assert.Equal(0L, await Q("SELECT \"notnull\" FROM pragma_table_info('feed_checks') WHERE name='feed_names'"));
        }
        Assert.Equal(15L, await ScalarAsync(t, "SELECT MAX(version) FROM schema_migrations"));

        // the old rows read as before, with no names
        var store = new SqliteFeedCheckStore(copy);
        var settings = (await store.LatestAsync("acme", FeedCheckKind.Settings, default))!;
        Assert.Null(settings.Names);
        Assert.Equal((Feed, "Grace Hopper", "Ada Lovelace"), (settings.FeedIndexUrl, settings.CheckedByName, Assert.Single(settings.ConfirmedAdmins)));
        Assert.Null((await store.LatestAsync("acme", FeedCheckKind.Developer, default))!.Names);

        await copy.MigrateAsync(16);   // again: nothing to do
        await using (var c = await copy.OpenAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT MAX(version) FROM schema_migrations";
            Assert.Equal(16L, await cmd.ExecuteScalarAsync());
        }
    }

    /// <summary>
    /// The 30-day retention (#63 H1 review F3, #107): a check older than the cutoff loses its confirmed admins, the checker's display name
    /// and every result's detail; the outcome, kind, feed names, the checker's tid and oid, each result's id, title and status, and the row
    /// itself stay (a verification may refer to it). A check at the cutoff or newer keeps everything.
    /// </summary>
    [Fact]
    public async Task Personal_data_of_checks_older_than_the_cutoff_is_blanked_and_the_rows_stay()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync("acme");
        var store = new SqliteFeedCheckStore(t.Db);
        var cutoff = t.Clock.UtcNow - TimeSpan.FromDays(30);
        var names = new FeedNames("contoso", "p", "f");
        var oldCheck = Check(t, outcome: FeedCheckOutcome.Failed, after: TimeSpan.FromDays(-31)) with { Names = names };
        var old = await store.RecordAsync(oldCheck, default);
        await ExecAsync(t, $"INSERT INTO shared_app_verifications(tenant_id, azure_tenant_id, feed_index_url, client_id, verified_at, verified_by, verified_via, feed_check_id) VALUES('acme','{Dir}','{Feed}','{Client}','2026-09-01T00:00:00.0000000+00:00','wizard','wizard',{old})");
        var atCutoff = await store.RecordAsync(Check(t, after: TimeSpan.FromDays(-30)), default);   // exactly 30 days: not yet
        var recent = await store.RecordAsync(Check(t, after: TimeSpan.FromDays(-29)), default);
        var before = await ScalarAsync(t, $"SELECT group_concat(id || '|' || checked_by_name || '|' || results || '|' || confirmed_admins, ';') FROM feed_checks WHERE id IN ({atCutoff},{recent})");

        Assert.Equal(1, await store.BlankPersonalDataAsync(cutoff, default));

        Assert.Equal(3L, await ScalarAsync(t, "SELECT COUNT(*) FROM feed_checks"));
        Assert.Equal("[]", await ScalarAsync(t, $"SELECT confirmed_admins FROM feed_checks WHERE id={old}"));
        Assert.Equal("", await ScalarAsync(t, $"SELECT checked_by_name FROM feed_checks WHERE id={old}"));
        var oldResults = (string)(await ScalarAsync(t, $"SELECT results FROM feed_checks WHERE id={old}"))!;
        Assert.DoesNotContain("Ada", oldResults);
        Assert.DoesNotContain("evil.example", oldResults);
        var read = (await store.LatestAsync("acme", FeedCheckKind.Settings, default))!;
        Assert.Equal(recent, read.Id);
        Assert.Equal("Grace Hopper", read.CheckedByName);
        Assert.Contains("Ada Lovelace", read.ConfirmedAdmins);
        Assert.Equal(before, await ScalarAsync(t, $"SELECT group_concat(id || '|' || checked_by_name || '|' || results || '|' || confirmed_admins, ';') FROM feed_checks WHERE id IN ({atCutoff},{recent})"));

        // the old row as it now reads: everything but the personal data is as recorded
        await ExecAsync(t, $"DELETE FROM feed_checks WHERE id IN ({atCutoff},{recent})");
        var redacted = (await store.LatestAsync("acme", FeedCheckKind.Settings, default))!;
        Assert.Equal(old, redacted.Id);
        Assert.Equal((oldCheck.TenantId, oldCheck.CheckedAt, oldCheck.CheckedByTid, oldCheck.CheckedByOid, oldCheck.AzureTenantId, oldCheck.FeedIndexUrl, oldCheck.ClientId),
            (redacted.TenantId, redacted.CheckedAt, redacted.CheckedByTid, redacted.CheckedByOid, redacted.AzureTenantId, redacted.FeedIndexUrl, redacted.ClientId));
        Assert.Equal((FeedCheckOutcome.Failed, FeedCheckKind.Settings, names), (redacted.Outcome, redacted.Kind, redacted.Names));
        Assert.Equal("", redacted.CheckedByName);
        Assert.Empty(redacted.ConfirmedAdmins);
        Assert.Equal(FeedCheckRedaction.Redact(oldCheck.Results), redacted.Results);
        Assert.Equal(oldCheck.Results.Select(r => (r.Id, r.Title, r.Status)), redacted.Results.Select(r => (r.Id, r.Title, r.Status)));
        Assert.Equal(old, await ScalarAsync(t, "SELECT feed_check_id FROM shared_app_verifications WHERE tenant_id='acme'"));

        Assert.Equal(0, await store.BlankPersonalDataAsync(cutoff, default));   // nothing left to blank
    }
}
