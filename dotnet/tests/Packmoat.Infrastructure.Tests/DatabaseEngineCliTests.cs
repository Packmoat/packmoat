using Packmoat.Admin;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// PACKMOAT_DB_ENGINE in packmoat-admin (#232 P1): unset is sqlite, as before; postgres, which this build cannot run yet, and an unknown
/// value are the usual "error:" line and exit 1, before the database is opened; backup, verify and restore refuse on postgres and point to
/// pg_dump.
/// </summary>
[Collection("sqlite-pools")]
public class DatabaseEngineCliTests
{
    private static async Task<(int Code, string Out, string Err)> Run(Func<CliEnvironment> env, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var code = await AdminCli.RunAsync(args, o, e, env, TestCli.Modules);
        return (code, o.ToString(), e.ToString());
    }

    private static async Task WithDirAsync(Func<string, Task> body)
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-cli-engine-").FullName;
        try { await body(dir); }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private static CliEnvironment On(string dir, DatabaseEngine engine) =>
        new(Path.Combine(dir, "packmoat.db"), () => AesGcmSecretProtector.FromBase64(AesGcmSecretProtector.GenerateKeyBase64()), new FakeClock(), "op") { Engine = engine };

    [Fact]   // mutation: the default is postgres
    public void Unset_is_sqlite_and_the_setting_is_read_through_the_settings_reader()
    {
        Assert.Equal(DatabaseEngine.Sqlite, CliEnvironment.FromSettings(_ => null).Engine);
        Assert.Equal(DatabaseEngine.Sqlite, new CliEnvironment("unused.db", () => throw new InvalidOperationException(), new FakeClock()).Engine);
        var file = new Settings(n => n == DatabaseEngines.Variable + Settings.FileSuffix ? "/run/secrets/engine" : null, _ => "postgres\n");
        Assert.Equal(DatabaseEngine.Postgres, CliEnvironment.FromSettings(file.Read).Engine);
    }

    [Fact]   // mutation: the tool runs on postgres
    public async Task Postgres_is_an_error_line_and_the_database_is_never_opened()
    {
        await WithDirAsync(async dir =>
        {
            var (code, output, error) = await Run(() => On(dir, DatabaseEngine.Postgres), "tenant", "list");
            Assert.Equal(1, code);
            Assert.Equal("", output);
            Assert.Equal("error: PACKMOAT_DB_ENGINE=postgres is not available in this build yet.", error.Trim());
            Assert.False(File.Exists(Path.Combine(dir, "packmoat.db")));
        });
    }

    [Theory]   // mutations: an unknown value is taken; whitespace only is read as unset
    [InlineData("mysql-xyz")]
    [InlineData("   ")]
    public async Task An_unknown_value_is_an_error_line_naming_the_setting_not_the_value(string value)
    {
        var (code, output, error) = await Run(() => CliEnvironment.FromSettings(n => n == DatabaseEngines.Variable ? value : null), "tenant", "list");
        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.Equal("error: PACKMOAT_DB_ENGINE must be sqlite or postgres.", error.Trim());
    }

    [Theory]   // mutation: backup (or verify, or restore) runs on postgres
    [InlineData("backup")]
    [InlineData("verify")]
    [InlineData("restore")]
    public async Task Backup_verify_and_restore_refuse_on_postgres_and_point_to_pg_dump(string command)
    {
        await WithDirAsync(async dir =>
        {
            var file = Path.Combine(dir, "backup.db");
            var (code, output, error) = await Run(() => On(dir, DatabaseEngine.Postgres), command, file);
            Assert.Equal(1, code);
            Assert.Equal("", output);
            Assert.Equal($"error: {command} works on SQLite only, and PACKMOAT_DB_ENGINE is postgres. Use pg_dump to back up a Postgres database, and pg_restore to restore it.",
                error.Trim());
            Assert.False(File.Exists(file));
            Assert.False(File.Exists(Path.Combine(dir, "packmoat.db")));
        });
    }

    [Fact]   // sqlite, said outright, is today's tool: a backup is written and verified
    public async Task Sqlite_backs_up_as_before()
    {
        await WithDirAsync(async dir =>
        {
            var file = Path.Combine(dir, "backup.db");
            var (code, output, error) = await Run(() => On(dir, DatabaseEngine.Sqlite), "backup", file);
            Assert.Equal(0, code);
            Assert.StartsWith("backup written and verified.", output);
            Assert.Equal("", error);
            Assert.True(File.Exists(file));
        });
    }
}
