using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

[Collection("sqlite-pools")]
public class BackupTests
{
    private static async Task<TestDb> Populated()
    {
        var t = await TestDb.CreateAsync();
        try
        {
            await t.AddTenantAsync();
            await t.Tenants.AddInternalPrefixAsync("acme", "Contoso.", default);
            await t.Tenants.AddWaiverAsync("acme", "moq", "4.21.0", t.Clock.UtcNow.AddDays(3), "hotfix", "olivia", default);
            await t.Tenants.SetUpstreamIdentityAsync("acme", new UpstreamIdentity("az", "client", "-----BEGIN PRIVATE KEY-----\nBACKUP-SECRET\n-----END PRIVATE KEY-----"), default);
            await t.Audit.AppendAsync(new AuditEvent(t.Clock.UtcNow, "r1", "acme", "tok_1", AuditKind.Download, "moq", "4.20.72", Decision.Allow, VerdictReason.CooldownElapsed, null), default);
            return t;
        }
        catch
        {
            await t.DisposeAsync();   // A failed set-up leaves no folder behind
            throw;
        }
    }

    [Fact]
    public async Task A_restored_backup_brings_back_tenants_tokens_identities_and_the_audit_trail()
    {
        await using var live = await Populated();
        var token = await live.Tokens.IssueAsync("acme", "ci", default);
        var backup = Path.Combine(live.Dir, "backups", "snap.db");

        var made = await SqliteBackup.BackupAsync(live.Db, backup, overwrite: false);
        Assert.True(made.Ok);
        Assert.Equal(1, made.Tenants); Assert.Equal(1, made.Tokens); Assert.Equal(1, made.AuditEvents);

        // A brand new server: a different directory, the same master key.
        var newDir = Directory.CreateTempSubdirectory("packmoat-restore-").FullName;
        try
        {
            var target = Path.Combine(newDir, "packmoat.db");
            var restored = await SqliteBackup.RestoreAsync(backup, target, replaceExisting: false, live.Clock.UtcNow);
            Assert.True(restored.Ok);

            var db = new SqliteDatabase(target);
            await db.MigrateAsync(TestTracks.Modules);
            var tenants = new SqliteTenantStore(db, live.Clock, AesGcmSecretProtector.FromBase64(live.MasterKey), TestLifecycle.Participants);
            var tokens = new SqliteTokenService(db, tenants, live.Clock, TestDb.Gate(db, live.Clock));
            var audit = new SqliteAuditLog(db);

            var tenant = (await tenants.GetAsync("acme", default))!;
            Assert.Equal(new[] { "Contoso." }, tenant.Policy.InternalPrefixes);
            Assert.Single(tenant.Policy.Waivers);
            Assert.NotNull(await tokens.AuthenticateAsync(token.Plaintext, default));                                   // the old token still works
            Assert.Contains("BACKUP-SECRET", (await tenants.GetUpstreamIdentityAsync("acme", default))!.CertificatePem); // key decrypts with the same master key
            Assert.Single(await audit.QueryAsync("acme", 10, default));
        }
        catch when (TempDir.Failing(newDir)) { throw; }
        finally { TempDir.Delete(newDir, clearSqlitePools: true); }
    }

    [Fact]
    public async Task A_backup_taken_while_the_server_is_writing_is_still_consistent()
    {
        await using var live = await Populated();
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 150; i++)
                await live.Audit.AppendAsync(new AuditEvent(live.Clock.UtcNow, $"r{i}", "acme", "tok_1", AuditKind.Download, "p", $"1.0.{i}", Decision.Allow, VerdictReason.CooldownElapsed, null), default);
        });

        var made = await SqliteBackup.BackupAsync(live.Db, Path.Combine(live.Dir, "b", "mid-write.db"), false);
        await writer;

        Assert.True(made.Ok, made.Detail);
        Assert.True(made.AuditEvents >= 1);
    }

    [Fact]
    public async Task A_backup_never_overwrites_without_force_and_is_private_to_the_owner()
    {
        await using var live = await Populated();
        var path = Path.Combine(live.Dir, "b", "snap.db");
        await SqliteBackup.BackupAsync(live.Db, path, false);

        await Assert.ThrowsAsync<IOException>(() => SqliteBackup.BackupAsync(live.Db, path, false));
        await SqliteBackup.BackupAsync(live.Db, path, true);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task Garbage_and_truncated_files_are_refused_and_nothing_is_replaced()
    {
        await using var live = await Populated();
        var good = Path.Combine(live.Dir, "b", "good.db");
        await SqliteBackup.BackupAsync(live.Db, good, false);
        var bytes = await File.ReadAllBytesAsync(good);

        var garbage = Path.Combine(live.Dir, "garbage.db");
        await File.WriteAllTextAsync(garbage, "this is not a database");
        var truncated = Path.Combine(live.Dir, "truncated.db");
        await File.WriteAllBytesAsync(truncated, bytes[..(bytes.Length / 3)]);
        var target = Path.Combine(live.Dir, "restored.db");

        foreach (var bad in new[] { garbage, truncated, Path.Combine(live.Dir, "missing.db") })
        {
            Assert.False((await SqliteBackup.VerifyAsync(bad)).Ok);
            await Assert.ThrowsAsync<IOException>(() => SqliteBackup.RestoreAsync(bad, target, false, live.Clock.UtcNow));
        }
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Restoring_over_an_existing_database_needs_force_and_keeps_the_old_one()
    {
        await using var live = await Populated();
        var good = Path.Combine(live.Dir, "b", "good.db");
        await SqliteBackup.BackupAsync(live.Db, good, false);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        await Assert.ThrowsAsync<IOException>(() => SqliteBackup.RestoreAsync(good, live.Path, false, live.Clock.UtcNow));
        var done = await SqliteBackup.RestoreAsync(good, live.Path, true, live.Clock.UtcNow);

        Assert.True(done.Ok);
        Assert.Single(Directory.GetFiles(live.Dir, "packmoat.db.before-restore-*"));
    }

    [Fact]
    public async Task The_admin_tool_backs_up_verifies_and_restores()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-clibackup-").FullName;
        try
        {
            var key = AesGcmSecretProtector.GenerateKeyBase64();
            var env = new CliEnvironment(Path.Combine(dir, "live.db"), () => AesGcmSecretProtector.FromBase64(key), new FakeClock());
            async Task<(int, string)> Run(CliEnvironment e, params string[] a) { var o = new StringWriter(); var er = new StringWriter(); var c = await AdminCli.RunAsync(a, o, er, e, TestCli.Modules); return (c, o + er.ToString()); }

            await Run(env, "tenant", "create", "acme", "Acme", "--upstream-base", "https://x/", "--feed", "https://x/i.json");
            var backup = Path.Combine(dir, "snap.db");

            var (code, text) = await Run(env, "backup", backup);
            Assert.Equal(0, code); Assert.Contains("1 tenants", text);
            Assert.Equal(1, (await Run(env, "backup", backup)).Item1);          // no overwrite
            Assert.Equal(0, (await Run(env, "backup", backup, "--force")).Item1);
            Assert.Contains("integrity ok", (await Run(env, "verify", backup)).Item2);

            var fresh = env with { DbPath = Path.Combine(dir, "new", "live.db") };
            Assert.Equal(0, (await Run(fresh, "restore", backup)).Item1);
            Assert.Contains("acme", (await Run(fresh, "tenant", "list")).Item2);
            Assert.Equal(1, (await Run(fresh, "restore", backup)).Item1);        // refuses to replace without --force
            Assert.Equal(1, (await Run(fresh, "verify", Path.Combine(dir, "nope.db"))).Item1);
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }
}
