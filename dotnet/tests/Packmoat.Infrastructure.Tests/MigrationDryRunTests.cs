using System.Security.Cryptography;
using Packmoat.Admin;
using Packmoat.Infrastructure;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// #196: <c>packmoat-admin db check-migrations FILE</c> never opens the live database, by whatever path it is named. The core's part of
/// the dry run's tests (#197): the rest run with the hosted track, in the hosted tests.
/// </summary>
[Collection("sqlite-pools")]
public class MigrationDryRunTests
{
    private static async Task<(int Exit, string Out, string Err)> RunAsync(CliEnvironment env, IReadOnlyList<IAdminCliModule> modules, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = await AdminCli.RunAsync(args, o, e, env, modules);
        return (exit, o.ToString().Replace("\r", ""), e.ToString().Replace("\r", ""));
    }

    private static CliEnvironment Env(TestDb t, string live) => new(live, () => t.Protector, t.Clock, "op");

    /// <summary>A file's bytes and time, and its companions: what "untouched" means.</summary>
    private static string Fingerprint(string path) =>
        !File.Exists(path) ? "absent"
        : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) + "|" + File.GetLastWriteTimeUtc(path).Ticks + "|" + File.Exists(path + "-wal") + File.Exists(path + "-shm");

    [Fact]   // mutations: compare the paths as typed; follow no symbolic link
    public async Task The_live_database_is_refused_by_any_path_that_resolves_to_it()
    {
        await using var t = new TestDb();
        var live = Path.Combine(t.Dir, "data", "packmoat.db");
        Directory.CreateDirectory(Path.GetDirectoryName(live)!);
        await File.WriteAllBytesAsync(live, [1, 2, 3]);
        var link = Path.Combine(t.Dir, "link-to-data");
        Directory.CreateSymbolicLink(link, Path.Combine(t.Dir, "data"));
        var fileLink = Path.Combine(t.Dir, "copy-link.db");
        File.CreateSymbolicLink(fileLink, live);
        var before = Fingerprint(live);

        foreach (var path in new[] { live, Path.Combine(t.Dir, "data", "..", "data", "packmoat.db"), Path.Combine(link, "packmoat.db"), fileLink })
        {
            var (exit, output, err) = await RunAsync(Env(t, live), [], "db", "check-migrations", path);
            Assert.Equal(1, exit);
            Assert.Contains("is the live database (PACKMOAT_DB). Refusing", err);
            Assert.Equal("", output);
        }
        // And the other way round: PACKMOAT_DB given through the link.
        Assert.Equal(1, (await RunAsync(Env(t, Path.Combine(link, "packmoat.db")), [], "db", "check-migrations", live)).Exit);
        Assert.Equal(before, Fingerprint(live));
        Assert.True(MigrationDryRun.SamePath(Path.Combine(link, "packmoat.db"), live));
        Assert.False(MigrationDryRun.SamePath(Path.Combine(t.Dir, "data", "other.db"), live));
    }

    [Fact]   // mutation: drop the existence check (the "checked a temporary copy" and "the deploy would not start" lines come back)
    public async Task A_missing_file_says_it_was_not_found_and_exits_1()
    {
        await using var t = new TestDb();
        var missing = Path.Combine(t.Dir, "no-such-backup.db");
        var (exit, output, err) = await RunAsync(Env(t, Path.Combine(t.Dir, "packmoat.db")), [], "db", "check-migrations", missing);
        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Equal($"error: {missing} was not found. Nothing was checked; give the path of a backup file.{Environment.NewLine}", err);
        Assert.False(File.Exists(missing));
    }

    /// <summary>
    /// #232 M1 on the core alone: a core-22 backup goes to 23; the two tables the migration adds are new and empty, which is no row change,
    /// so the dry run says OK. A table the migrations create with rows in it is still a change (the hosted track's adoption test shows one).
    /// </summary>
    [Fact]   // mutations: count a new, empty table as changed; count any new table as equal
    public async Task A_core_22_backup_goes_to_23_with_two_new_empty_tables_and_result_OK()
    {
        await using var t = new TestDb();
        await t.Db.MigrateAsync(22);
        await t.AddTenantAsync("acme");
        var backup = Path.Combine(t.Dir, "backup.db");
        await using (var c = await t.Db.OpenAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"VACUUM INTO '{backup}'";
            await cmd.ExecuteNonQueryAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var (exit, output, err) = await RunAsync(Env(t, Path.Combine(t.Dir, "live.db")), [], "db", "check-migrations", backup);

        Assert.True(exit == 0, output + err);
        Assert.Contains("track core: before v22, after v23: migrated v22 to v23\n", output.Replace("\r", ""));
        Assert.Contains("rows cookie_keys: none -> 0  (new, empty)\n", output.Replace("\r", ""));
        Assert.Contains("rows job_leases: none -> 0  (new, empty)\n", output.Replace("\r", ""));
        Assert.Contains("rows tenants: 1 -> 1\n", output.Replace("\r", ""));
        Assert.Contains("schema: equal to a fresh build", output);
        Assert.DoesNotContain("CHANGED", output);
        Assert.EndsWith("result: OK", output.TrimEnd());

        // The rule itself: none before and 0 after is equal; none before and rows after, or a table gone, is not.
        Assert.True(new MigrationDryRun.RowLine("job_leases", null, 0).Equal);
        Assert.False(new MigrationDryRun.RowLine("job_leases", null, 1).Equal);
        Assert.False(new MigrationDryRun.RowLine("job_leases", 0, null).Equal);
        Assert.False(new MigrationDryRun.RowLine("tenants", 2, 0).NewAndEmpty);
    }
}
