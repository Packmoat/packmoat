using Microsoft.Data.Sqlite;
using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

[Collection("sqlite-pools")]
public class RestoreKeepsTheOldDatabaseCompleteTests
{
    private static async Task<long> CountTenants(string path, string tenantId)
    {
        await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tenants WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", tenantId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Builds what a crashed server leaves behind: a database whose latest committed rows are only in the -wal file.
    /// (A connection is held open so SQLite cannot checkpoint, and the three files are copied while it is open.)
    /// </summary>
    private static async Task<string> UncheckpointedDatabase(string dir)
    {
        var src = Path.Combine(dir, "src");
        var dst = Path.Combine(dir, "crashed");
        Directory.CreateDirectory(dst);
        var db = new SqliteDatabase(Path.Combine(src, "packmoat.db"));
        await db.MigrateAsync(TestTracks.Modules);
        await using var holder = await db.OpenAsync();
        await using (var cmd = holder.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO tenants(id,name,upstream_base,allowed_path_prefix,feed_index_url,cooldown_days,created_at) VALUES('only-in-wal','W','https://x/','','https://x/i',1,'2026-01-01')";
            await cmd.ExecuteNonQueryAsync();
        }
        var target = Path.Combine(dst, "packmoat.db");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(Path.Combine(src, "packmoat.db") + suffix)) File.Copy(Path.Combine(src, "packmoat.db") + suffix, target + suffix);
        Assert.True(new FileInfo(target + "-wal").Length > 0, "the test must start with committed data that lives only in the -wal file");
        return target;
    }

    [Fact]
    public async Task Writes_that_were_only_in_the_wal_survive_in_the_kept_copy()
    {
        await using var live = await TestDb.CreateAsync();
        await live.AddTenantAsync("fresh");
        var good = Path.Combine(live.Dir, "b", "good.db");
        await SqliteBackup.BackupAsync(live.Db, good, false);
        var target = await UncheckpointedDatabase(live.Dir);

        var restored = await SqliteBackup.RestoreAsync(good, target, replaceExisting: true, live.Clock.UtcNow);

        Assert.True(restored.Ok);
        Assert.Equal(1, await CountTenants(target, "fresh"));            // the restored database is the backup
        Assert.Equal(0, await CountTenants(target, "only-in-wal"));
        var kept = Directory.GetFiles(Path.GetDirectoryName(target)!, "packmoat.db.before-restore-*").Single(f => !f.EndsWith("-wal") && !f.EndsWith("-shm"));
        Assert.Equal(1, await CountTenants(kept, "only-in-wal"));        // ... and the old one still has its latest committed row
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task No_stray_wal_or_shm_is_left_beside_the_restored_database()
    {
        await using var live = await TestDb.CreateAsync();
        var good = Path.Combine(live.Dir, "b", "good.db");
        await SqliteBackup.BackupAsync(live.Db, good, false);
        var target = await UncheckpointedDatabase(live.Dir);

        await SqliteBackup.RestoreAsync(good, target, true, live.Clock.UtcNow);

        Assert.False(File.Exists(target + "-wal") && new FileInfo(target + "-wal").Length > 0);
        Assert.Equal(0, await CountTenants(target, "only-in-wal"));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Restore_refuses_while_another_process_still_has_the_database_open_for_writing()
    {
        await using var live = await TestDb.CreateAsync();
        var good = Path.Combine(live.Dir, "b", "good.db");
        await SqliteBackup.BackupAsync(live.Db, good, false);
        var dir = Path.Combine(live.Dir, "inuse");
        var db = new SqliteDatabase(Path.Combine(dir, "packmoat.db"));
        await db.MigrateAsync(TestTracks.Modules);
        await using var writer = await db.OpenAsync();
        await using (var begin = writer.CreateCommand()) { begin.CommandText = "BEGIN IMMEDIATE"; await begin.ExecuteNonQueryAsync(); }

        await Assert.ThrowsAsync<IOException>(() => SqliteBackup.RestoreAsync(good, Path.Combine(dir, "packmoat.db"), true, live.Clock.UtcNow));

        Assert.Empty(Directory.GetFiles(dir, "*.before-restore-*"));     // nothing was moved
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task A_backup_is_one_self_contained_file()
    {
        await using var live = await TestDb.CreateAsync();
        await live.AddTenantAsync();
        var path = Path.Combine(live.Dir, "b", "snap.db");
        await SqliteBackup.BackupAsync(live.Db, path, false);
        await SqliteBackup.VerifyAsync(path);
        SqliteConnection.ClearAllPools();

        Assert.Equal(new[] { "snap.db" }, Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName).Order());
    }
}

// The master-key check's backup tests. The class's other part (MasterKeyCheckTests.cs) is the SQLite fixture of MasterKeyCheckContract and
// carries the collection (#288 P2).
public partial class MasterKeyCheckTests
{
    private static AesGcmSecretProtector NewKey() => AesGcmSecretProtector.FromBase64(AesGcmSecretProtector.GenerateKeyBase64());

    [Fact]
    public async Task A_backup_is_checked_against_the_master_key_in_use()
    {
        await using var live = await TestDb.CreateAsync();
        await live.AddTenantAsync();
        await new SqliteMasterKeyCheck(live.Db, live.Protector).EnsureAsync(default);
        var path = Path.Combine(live.Dir, "b", "snap.db");
        await SqliteBackup.BackupAsync(live.Db, path, false, default, live.Protector);

        Assert.Equal(SqliteBackup.KeyStatus.Matches, (await SqliteBackup.VerifyAsync(path, default, live.Protector)).Key);

        var wrong = await SqliteBackup.VerifyAsync(path, default, NewKey());
        Assert.False(wrong.Ok);
        Assert.Equal(SqliteBackup.KeyStatus.Mismatch, wrong.Key);
        Assert.Contains("does not match", wrong.Detail);

        var unchecked_ = await SqliteBackup.VerifyAsync(path);
        Assert.True(unchecked_.Ok);
        Assert.Equal(SqliteBackup.KeyStatus.NotChecked, unchecked_.Key);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Restore_refuses_a_backup_made_under_a_different_key_and_replaces_nothing()
    {
        await using var live = await TestDb.CreateAsync();
        await new SqliteMasterKeyCheck(live.Db, live.Protector).EnsureAsync(default);
        var path = Path.Combine(live.Dir, "b", "snap.db");
        await SqliteBackup.BackupAsync(live.Db, path, false, default, live.Protector);
        var target = Path.Combine(live.Dir, "restored", "packmoat.db");

        await Assert.ThrowsAsync<IOException>(() => SqliteBackup.RestoreAsync(path, target, false, live.Clock.UtcNow, default, NewKey()));

        Assert.False(File.Exists(target));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task The_marker_is_written_once_and_a_wrong_key_is_then_refused()
    {
        await using var live = await TestDb.CreateAsync();
        await new SqliteMasterKeyCheck(live.Db, live.Protector).EnsureAsync(default);
        await new SqliteMasterKeyCheck(live.Db, live.Protector).EnsureAsync(default);                      // same key again: fine

        var other = new SqliteMasterKeyCheck(live.Db, NewKey());
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.EnsureAsync(default));
    }

    [Fact]
    public async Task An_older_database_with_no_marker_gets_one_and_a_backup_of_it_says_so()
    {
        await using var live = await TestDb.CreateAsync();
        var path = Path.Combine(live.Dir, "b", "legacy.db");
        await SqliteBackup.BackupAsync(live.Db, path, false);                 // no marker has ever been written

        Assert.Equal(SqliteBackup.KeyStatus.NotRecorded, (await SqliteBackup.VerifyAsync(path, default, live.Protector)).Key);
        await new SqliteMasterKeyCheck(live.Db, live.Protector).EnsureAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqliteMasterKeyCheck(live.Db, NewKey()).EnsureAsync(default));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task The_admin_tool_reports_the_key_status_and_fails_on_a_mismatch()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-keycheck-").FullName;
        try
        {
            var keyA = AesGcmSecretProtector.GenerateKeyBase64();
            var keyB = AesGcmSecretProtector.GenerateKeyBase64();
            CliEnvironment Env(string key, string db) => new(db, () => AesGcmSecretProtector.FromBase64(key), new FakeClock());
            async Task<(int, string)> Run(CliEnvironment e, params string[] a) { var o = new StringWriter(); var er = new StringWriter(); var c = await AdminCli.RunAsync(a, o, er, e, TestCli.Modules); return (c, o + er.ToString()); }

            var live = Env(keyA, Path.Combine(dir, "live.db"));
            await Run(live, "tenant", "create", "acme", "Acme", "--upstream-base", "https://x/", "--feed", "https://x/i.json");
            var snap = Path.Combine(dir, "snap.db");
            Assert.Equal(0, (await Run(live, "backup", snap)).Item1);

            Assert.Contains("master key: matches", (await Run(live, "verify", snap)).Item2);
            var wrong = await Run(Env(keyB, Path.Combine(dir, "other.db")), "verify", snap);
            Assert.Equal(1, wrong.Item1);
            Assert.Contains("does not match", wrong.Item2);
            Assert.Equal(1, (await Run(Env(keyB, Path.Combine(dir, "restored.db")), "restore", snap)).Item1);
            Assert.False(File.Exists(Path.Combine(dir, "restored.db")));
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }
}
