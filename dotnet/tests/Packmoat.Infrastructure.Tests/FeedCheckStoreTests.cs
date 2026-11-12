using Microsoft.Data.Sqlite;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// <see cref="FeedCheckStoreContract"/> on PostgreSQL, each test on a database of its own. The migrations are SQLite's own
/// and stay in <see cref="FeedCheckStoreTests"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresFeedCheckStoreTests : FeedCheckStoreContract
{
    protected override IStoreEngine Engine => PostgresStoreEngine.Instance;
}

/// <summary><see cref="FeedCheckStoreContract"/> on SQLite, and the tests only SQLite has.</summary>
[Collection("sqlite-pools")]
public class FeedCheckStoreTests : FeedCheckStoreContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    /// <summary>
    /// The stored results of old rows, odd and ordinary, that the parity test below runs through both redactions: what an older build or a
    /// hand edit could leave, and every rule of SQLite's pre-filter met and broken once.
    /// </summary>
    private static readonly string[] ResultShapes = Shapes();

    private static string[] Shapes()
    {
        var removed = FeedCheckRedaction.RemovedDetail;
        string Obj(string body) => "[{" + body + "}]";
        var clean = $"\"id\":\"feed\",\"title\":\"F\",\"status\":\"pass\",\"detail\":\"{removed}\"";
        return
        [
            "[]", "", " []", "[ ]", "null", "5", "\"Grace Hopper\"", "not json, Grace Hopper", "{\"detail\":\"Grace Hopper\"}", "[null]", "[1,2]",
            "[[\"Grace Hopper\"]]", "[\"Grace Hopper\"]", "[{}]", "[{\"id\":\"a\",}]", "[{id:'a'}]",
            Obj(clean), Obj(clean) + " ", "[" + "{" + clean + "}," + "{" + clean + "}]", Obj(clean) + ",null",
            Obj($"\"title\":\"F\",\"id\":\"feed\",\"status\":\"pass\",\"detail\":\"{removed}\""),
            $"[ {{ \"id\": \"feed\", \"title\": \"F\", \"status\": \"pass\", \"detail\": \"{removed}\" }} ]",
            Obj($"\"id\":\"feed\",\"title\":\"F\",\"status\":\"glorious\",\"detail\":\"{removed}\""),
            Obj($"\"id\":null,\"title\":\"F\",\"status\":\"pass\",\"detail\":\"{removed}\""),
            Obj($"\"id\":\"feed\",\"title\":\"\\u00e9\",\"status\":\"pass\",\"detail\":\"{removed}\""),
            Obj($"\"id\":\"feed\",\"title\":\"\u00e9\",\"status\":\"pass\",\"detail\":\"{removed}\""),
            Obj($"\"id\":\"feed\",\"title\":\"F\",\"status\":\"pass\",\"detail\":\"{removed[..^1]}\\u002e\""),
            Obj("\"id\":\"feed\",\"title\":\"F\",\"status\":\"pass\",\"detail\":\"Grace Hopper\""),
            Obj("\"id\":\"feed\",\"title\":\"F\",\"status\":\"pass\""),
            Obj("\"id\":\"feed\",\"title\":\"F\",\"status\":\"pass\",\"detail\":null"),
            Obj("\"id\":\"feed\",\"title\":\"F\",\"status\":\"pass\",\"detail\":30"),
            Obj($"\"id\":\"feed\",\"title\":\"F\",\"status\":\"pass\",\"detail\":\"{removed}\",\"detail\":\"Grace Hopper\""),
            Obj($"\"id\":\"feed\",\"title\":\"F\",\"status\":\"pass\",\"detail\":\"Grace Hopper\",\"detail\":\"{removed}\""),
            Obj($"\"id\":\"feed\",\"title\":\"F\",\"status\":\"pass\",\"detail\":\"{removed}\",\"names\":[\"private\"]"),
            Obj($"\"id\":\"feed\",\"title\":\"F\",\"detail\":\"{removed}\",\"who\":\"Grace\""),
            Obj($"\"id\":\"feed\",\"title\":\"F\",\"status\":\"pass\",\"detail\":\"{removed}\",\"id\":\"again\""),
            Obj($"\"id\":{new string('[', 70)}{new string(']', 70)},\"title\":\"F\",\"status\":\"pass\",\"detail\":\"{removed}\""),
            Obj($"\"id\":\"\\ud800\",\"title\":\"F\",\"status\":\"pass\",\"detail\":\"{removed}\""),
            Obj($"\"id\":\"feed\",\"title\":\"F\",\"status\":\"pass\",\"detail\":\"{removed}\"") + "x",
            // A lone surrogate in a detail or a key, which System.Text.Json will not read as text; and an upper-case key.
            Obj("\"id\":\"feed\",\"title\":\"F\",\"status\":\"pass\",\"detail\":\"\\ud800\""),
            Obj("\"id\":\"feed\",\"title\":\"F\",\"status\":\"pass\",\"detail\":\"Grace \\udc00 Hopper\""),
            Obj($"\"id\":\"feed\",\"title\":\"F\",\"status\":\"pass\",\"detail\":\"{removed}\",\"\\ud800\":\"x\""),
            Obj("\"id\":\"feed\",\"title\":\"F\",\"status\":\"pass\",\"\\ud800\":\"Grace\""),
            Obj($"\"ID\":\"Grace Hopper\",\"title\":\"F\",\"status\":\"pass\",\"detail\":\"{removed}\""),
        ];
    }

    /// <summary>
    /// PostgreSQL's redaction selects old rows by date alone and decides each in C#
    /// (<see cref="Infrastructure.Postgres.PostgresFeedCheckStore.RedactionOf"/>), where SQLite selects with its JSON pre-filter, then decides
    /// in C#. Every shape above, with and without a checker's name and confirmed admins, goes through SQLite's redaction here, and each row
    /// must come out as the PostgreSQL decision says: left exactly as it was, or written with those results, no name and no admins. And the
    /// count SQLite returns is the number of rows PostgreSQL would change.
    /// </summary>
    [Fact]   // mutations: a rule of the C# pre-filter dropped or changed (the key count, the first "detail", the "[]" text, the depth)
    public async Task The_postgres_redaction_decides_every_row_as_sqlites_does()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync("acme");
        var store = new SqliteFeedCheckStore(t.Db);
        var rows = new List<(long Id, string Results, string Admins, string Name)>();
        foreach (var shape in ResultShapes)
            foreach (var (admins, name) in new[] { ("[]", ""), ("[]", "Grace Hopper"), ("[\"Ada\"]", "") })
            {
                var id = await store.RecordAsync(Check(t, after: TimeSpan.FromDays(-31)), default);
                await using var c = await t.Db.OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "UPDATE feed_checks SET results=@r, confirmed_admins=@a, checked_by_name=@n WHERE id=@id";
                cmd.Parameters.AddWithValue("@r", shape);
                cmd.Parameters.AddWithValue("@a", admins);
                cmd.Parameters.AddWithValue("@n", name);
                cmd.Parameters.AddWithValue("@id", id);
                await cmd.ExecuteNonQueryAsync();
                rows.Add((id, shape, admins, name));
            }

        var changed = await store.BlankPersonalDataAsync(t.Clock.UtcNow - TimeSpan.FromDays(30), default);

        var expectedCount = 0;
        var mismatches = new List<string>();   // every row that differs, so a failure names each shape at once
        foreach (var (id, results, admins, name) in rows)
        {
            var after = Assert.Single(await ColumnAsync(t, $"SELECT results || char(10) || confirmed_admins || char(10) || checked_by_name FROM feed_checks WHERE id={id}"));
            string? postgres;
            try { postgres = Infrastructure.Postgres.PostgresFeedCheckStore.RedactionOf(results, admins, name); }
            catch (Exception e) { mismatches.Add($"results {results}, admins {admins}, name '{name}': PostgreSQL threw {e.GetType().Name}"); continue; }
            if (postgres is not null) expectedCount++;
            if ((postgres is null ? $"{results}\n{admins}\n{name}" : $"{postgres}\n[]\n") != after)
                mismatches.Add($"results {results}, admins {admins}, name '{name}': SQLite wrote {after}, PostgreSQL would write {postgres ?? "nothing"}");
        }
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
        Assert.Equal(expectedCount, changed);
        Assert.True(expectedCount > 0 && expectedCount < rows.Count);
    }

    [Fact]
    public void The_postgres_store_writes_outcomes_kinds_and_slots_as_sqlites_does()
    {
        foreach (var outcome in Enum.GetValues<FeedCheckOutcome>())
            Assert.Equal(SqliteFeedCheckStore.OutcomeText(outcome), Infrastructure.Postgres.PostgresFeedCheckStore.OutcomeText(outcome));
        foreach (var kind in Enum.GetValues<FeedCheckKind>())
            Assert.Equal(SqliteFeedCheckStore.KindText(kind), Infrastructure.Postgres.PostgresFeedCheckStore.KindText(kind));
        Assert.Throws<ArgumentOutOfRangeException>(() => Infrastructure.Postgres.PostgresFeedCheckStore.OutcomeText((FeedCheckOutcome)42));
        Assert.Throws<ArgumentOutOfRangeException>(() => Infrastructure.Postgres.PostgresFeedCheckStore.KindText((FeedCheckKind)7));
        foreach (var slot in Enum.GetValues<SharedAppSlot>())
        {
            Assert.Equal(SqliteSharedAppCredentialStore.SlotName(slot), Infrastructure.Postgres.PostgresSharedAppCredentialStore.SlotName(slot));
            Assert.Equal(SqliteSharedAppCredentialStore.Context(slot), Infrastructure.Postgres.PostgresSharedAppCredentialStore.Context(slot));
        }
    }

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
}
