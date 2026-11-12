using Packmoat.Admin;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// PACKMOAT_DB_ENGINE in packmoat-admin: unset is sqlite, as before; an unknown value is the usual "error:" line and exit 1,
/// before the database is opened; backup, verify and restore refuse on postgres and point to pg_dump. Postgres itself is taken
/// Its commands against a real server are in PostgresCliTests.
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
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
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

    [Theory]   // Postgres is taken, but PACKMOAT_DB must then be a connection string. mutation: a file path opened as SQLite
    [InlineData("tenant", "list")]
    [InlineData("db", "check-migrations")]
    public async Task On_postgres_a_file_path_is_an_error_line_naming_the_setting_and_no_file_is_made(string group, string verb)
    {
        await WithDirAsync(async dir =>
        {
            var (code, output, error) = await Run(() => On(dir, DatabaseEngine.Postgres), group, verb);
            Assert.Equal(1, code);
            Assert.Equal("", output);
            Assert.Equal("error: PACKMOAT_DB is not a valid PostgreSQL connection string.", error.Trim());
            Assert.False(File.Exists(Path.Combine(dir, "packmoat.db")));
        });
    }

    // PACKMOAT_DB_SINGLE_ROLE reaches the command line's start checks, read as the server reads it: strictly, on PostgreSQL only.
    // mutations: the setting not read into the environment; read on SQLite too
    [Theory]
    [InlineData("postgres", "on", true)]
    [InlineData("postgres", "off", false)]
    [InlineData("postgres", null, false)]
    [InlineData("sqlite", "on", false)]
    [InlineData("sqlite", "yes", false)]   // not read at all on SQLite, so not refused
    public void The_single_role_setting_is_read_on_postgres_only(string engine, string? singleRole, bool expected)
    {
        var env = CliEnvironment.FromSettings(n => n == DatabaseEngines.Variable ? engine : n == "PACKMOAT_DB_SINGLE_ROLE" ? singleRole : null);
        Assert.Equal(expected, env.SingleRole);
    }

    [Fact]
    public async Task On_postgres_a_bad_single_role_setting_is_an_error_line_naming_it()
    {
        var (code, output, error) = await Run(() => CliEnvironment.FromSettings(n => n == DatabaseEngines.Variable ? "postgres" : n == "PACKMOAT_DB_SINGLE_ROLE" ? "yes" : null), "tenant", "list");
        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.Equal("error: PACKMOAT_DB_SINGLE_ROLE must be on or off.", error.Trim());
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
