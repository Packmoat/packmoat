using Microsoft.Data.Sqlite;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// #162: each migration statement runs as its own command, so an error from any statement is thrown and rolls the migration back.
/// Microsoft.Data.Sqlite does not report an error from a statement that follows an ADD COLUMN with a CHECK in the same command text,
/// so migrations 8, 9, 13 and 14 (one command each before #162) could have lost an error. Mutations each test kills are named on it.
/// </summary>
[Collection("sqlite-pools")]
public class MigrationStatementTests
{
    private static async Task<object?> ScalarAsync(SqliteDatabase db, string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task ExecAsync(SqliteDatabase db, string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> RowsAsync(SqliteDatabase db, string sql)
    {
        await using var c = await db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await r.ReadAsync())
        {
            var cells = new string[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++) cells[i] = r.IsDBNull(i) ? "NULL" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;
            rows.Add(string.Join("|", cells));
        }
        return rows;
    }

    [Fact]
    public async Task Canary_the_library_does_not_report_an_error_after_a_checked_add_column_in_one_command()
    {
        // Pins the library behaviour #162 works around. If this starts throwing, the library changed: the per-statement rule still holds,
        // but this canary and the comments that cite it can go.
        await using var t = new TestDb();
        await ExecAsync(t.Db, "CREATE TABLE t(a TEXT);");
        await ExecAsync(t.Db, "ALTER TABLE t ADD COLUMN b TEXT CHECK(b IN ('x')); CREATE INDEX ix_t_missing ON t(missing);");   // no column 'missing'
        Assert.Equal(1L, await ScalarAsync(t.Db, "SELECT COUNT(*) FROM pragma_table_info('t') WHERE name='b'"));
        Assert.Equal(0L, await ScalarAsync(t.Db, "SELECT COUNT(*) FROM sqlite_master WHERE name='ix_t_missing'"));
    }

    [Fact]
    public async Task A_failing_statement_after_a_checked_add_column_throws_and_rolls_the_whole_migration_back()
    {
        // Kills: re-concatenating the statements (nothing thrown), committing early (column b stays), skipping the last element (nothing
        // thrown). Dropping cmd.Transaction survives: Microsoft.Data.Sqlite 10 runs a command with no transaction set inside the
        // connection's open one, so the rollback still happens. MigrateAsync sets it anyway, so it does not rely on that.
        await using var t = new TestDb();
        string[][] migrations =
        [
            ["CREATE TABLE t(a TEXT);"],
            ["ALTER TABLE t ADD COLUMN b TEXT CHECK(b IN ('x'));", "CREATE INDEX ix_t_missing ON t(missing);"],
        ];
        await t.Db.MigrateAsync(migrations, 1);

        var ex = await Assert.ThrowsAsync<SqliteException>(() => t.Db.MigrateAsync(migrations, 2));
        Assert.Contains("missing", ex.Message);

        Assert.Equal(1L, await ScalarAsync(t.Db, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(0L, await ScalarAsync(t.Db, "SELECT COUNT(*) FROM schema_migrations WHERE version=2"));
        Assert.Equal(0L, await ScalarAsync(t.Db, "SELECT COUNT(*) FROM pragma_table_info('t') WHERE name='b'"));
        Assert.Equal(0L, await ScalarAsync(t.Db, "SELECT COUNT(*) FROM sqlite_master WHERE name='ix_t_missing'"));
    }

    [Fact]
    public async Task Every_statement_of_a_migration_runs_including_the_last_and_a_trigger_runs_whole()
    {
        // Kills: skipping the last element.
        await using var t = new TestDb();
        string[][] migrations =
        [
            [
                "CREATE TABLE t(a TEXT);",
                "ALTER TABLE t ADD COLUMN b TEXT CHECK(b IN ('x'));",
                "CREATE TRIGGER t_no_delete BEFORE DELETE ON t BEGIN SELECT RAISE(ABORT, 'no; never'); SELECT 1; END;",
                "CREATE INDEX ix_t_b ON t(b);",
            ],
        ];
        await t.Db.MigrateAsync(migrations, 1);

        Assert.Equal(1L, await ScalarAsync(t.Db, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(1L, await ScalarAsync(t.Db, "SELECT COUNT(*) FROM pragma_table_info('t') WHERE name='b'"));
        Assert.Equal(1L, await ScalarAsync(t.Db, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='t_no_delete'"));
        Assert.Equal(1L, await ScalarAsync(t.Db, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_t_b'"));
    }

    // The core slots #196 emptied (their statements are hosted H1). No other migration may be empty.
    private static readonly int[] EmptiedBySplit = [10, 12, 21];

    [Fact]
    public void Each_migration_element_is_one_statement()
    {
        // A second statement in one element would run in the same command and could lose its error again. A trigger is one statement,
        // BEGIN ... END included, so it may hold more semicolons, but nothing may follow its END.
        Assert.Equal(SqliteDatabase.LatestVersion, SqliteDatabase.Migrations.Length);
        foreach (var track in new[] { SqliteDatabase.Core }.Concat(TestTracks.Modules))   // the hosted track: HostedMigrationStatementTests
        {
            for (var m = 0; m < track.Migrations.Length; m++)
            {
                if (track == SqliteDatabase.Core && EmptiedBySplit.Contains(m + 1)) Assert.Empty(track.Migrations[m]);
                else Assert.NotEmpty(track.Migrations[m]);
                foreach (var statement in track.Migrations[m])
                {
                    var where = $"{track.Name} migration {m + 1}: {statement}";
                    var s = statement.Trim();
                    Assert.False(string.IsNullOrEmpty(s), where);
                    if (s.StartsWith("CREATE TRIGGER", StringComparison.OrdinalIgnoreCase))
                    {
                        Assert.True(s.EndsWith("END;", StringComparison.Ordinal), where);
                        Assert.Equal(1, CountOf(s, " END;"));
                        continue;
                    }
                    var semicolons = s.Count(ch => ch == ';');
                    Assert.True(semicolons == 0 || (semicolons == 1 && s.EndsWith(';')), where);
                }
            }
        }
    }

    private static int CountOf(string s, string part)
    {
        var n = 0;
        for (var i = s.IndexOf(part, StringComparison.Ordinal); i >= 0; i = s.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public async Task Snapshot_of_the_objects_migrations_8_9_13_and_14_add_after_a_checked_add_column()
    {
        // The objects at risk in #162: each follows an ADD COLUMN with a CHECK in the same migration. Kills: skipping the last element.
        await using var t = await TestDb.CreateAsync();

        // 8
        Assert.Equal(
            ["owner_tid|TEXT|0|NULL", "owner_oid|TEXT|0|NULL", "expires_at|TEXT|0|NULL", "issued_via|TEXT|1|'operator'", "revoked_by|TEXT|0|NULL"],
            await RowsAsync(t.Db, "SELECT name, type, \"notnull\", dflt_value FROM pragma_table_info('tokens') WHERE cid >= 9 ORDER BY cid"));
        Assert.Equal(["CREATE INDEX ix_tokens_owner ON tokens(tenant_id, owner_tid, owner_oid)"],
            await RowsAsync(t.Db, "SELECT sql FROM sqlite_master WHERE type='index' AND name='ix_tokens_owner'"));

        // 9
        Assert.Equal(
            ["origin|TEXT|1|'operator'", "created_by_oid|TEXT|0|NULL"],
            await RowsAsync(t.Db, "SELECT name, type, \"notnull\", dflt_value FROM pragma_table_info('tenants') WHERE name IN ('origin','created_by_oid') ORDER BY cid"));
        // (The tables old 9 also made are the hosted track's H1 since #196: HostedMigrationStatementTests checks them, #197.)

        // 13 and 14
        Assert.Equal(
            ["verified_via|TEXT|1|'operator'", "feed_check_id|INTEGER|0|NULL", "developer_check_id|INTEGER|0|NULL"],
            await RowsAsync(t.Db, "SELECT name, type, \"notnull\", dflt_value FROM pragma_table_info('shared_app_verifications') WHERE name IN ('verified_via','feed_check_id','developer_check_id') ORDER BY cid"));
        Assert.Equal(
            ["developer_check_id|feed_checks|id", "feed_check_id|feed_checks|id"],
            await RowsAsync(t.Db, "SELECT \"from\", \"table\", \"to\" FROM pragma_foreign_key_list('shared_app_verifications') WHERE \"table\"='feed_checks' ORDER BY \"from\""));
        Assert.Equal(["kind|TEXT|1|'settings'"],
            await RowsAsync(t.Db, "SELECT name, type, \"notnull\", dflt_value FROM pragma_table_info('feed_checks') WHERE name='kind'"));
        Assert.Equal(["CREATE INDEX ix_feed_checks_tenant_kind ON feed_checks(tenant_id, kind, checked_at)"],
            await RowsAsync(t.Db, "SELECT sql FROM sqlite_master WHERE type='index' AND name='ix_feed_checks_tenant_kind'"));
    }
}
