using Packmoat.Admin;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// packmoat-admin's setting errors are the usual "error:" line and exit 1, never a crash, and never print a value or a path.
/// A master key that is set but bad (or whose file is missing) is an error even where the key check is optional: only a key that is not
/// set at all skips the check in verify, restore, backup and db check-migrations.
/// </summary>
[Collection("sqlite-pools")]
public class CliErrorTests
{
    // Variables only these tests use, so nothing else in the process sees them.
    private const string KeyVariable = "PACKMOAT_CLI_ERROR_TEST_KEY_7F3A";
    private const string KeyFileVariable = KeyVariable + Settings.FileSuffix;

    private static async Task<(int Code, string Out, string Err)> Run(Func<CliEnvironment> env, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var code = await AdminCli.RunAsync(args, o, e, env, TestCli.Modules);
        return (code, o.ToString(), e.ToString());
    }

    private static async Task WithDirAsync(Func<string, Task> body)
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-cli-error-").FullName;
        try { await body(dir); }
        finally
        {
            Environment.SetEnvironmentVariable(KeyVariable, null);
            Environment.SetEnvironmentVariable(KeyFileVariable, null);
            TempDir.Delete(dir, clearSqlitePools: true);
        }
    }

    private static CliEnvironment KeyFromEnvironment(string dir) =>
        new(Path.Combine(dir, "packmoat.db"), () => AesGcmSecretProtector.FromEnvironment(KeyVariable), new FakeClock(), "op");

    [Fact]   // mutation: FromEnvironment throws a plain InvalidOperationException again (it escapes: a crash)
    public async Task Purge_due_with_no_master_key_is_an_error_line()
    {
        await WithDirAsync(async dir =>
        {
            var (code, output, error) = await Run(() => KeyFromEnvironment(dir), "tenant", "purge-due");
            Assert.Equal(1, code);
            Assert.Equal("", output);
            Assert.Equal($"error: {KeyVariable} is not set.", error.Trim());
        });
    }

    [Fact]   // mutation: FromSettings treats an empty variable as set again (tenant list: a key-length error, not "not set")
    public async Task An_empty_master_key_counts_as_not_set()
    {
        await WithDirAsync(async dir =>
        {
            var empty = new Settings(n => n == KeyVariable ? "" : null, File.ReadAllText);
            CliEnvironment Env() => new(Path.Combine(dir, "packmoat.db"), () => AesGcmSecretProtector.FromSettings(empty.Read, KeyVariable), new FakeClock(), "op");

            var list = await Run(Env, "tenant", "list");
            Assert.Equal(1, list.Code);
            Assert.Equal("", list.Out);
            Assert.Equal($"error: {KeyVariable} is not set.", list.Err.Trim());

            var backup = await BackupAsync(dir, AesGcmSecretProtector.GenerateKeyBase64());
            var verify = await Run(Env, "verify", backup);
            Assert.Equal(0, verify.Code);
            Assert.Contains("master key: NOT CHECKED (PACKMOAT_MASTER_KEY is not set)", verify.Out);
            Assert.Equal("", verify.Err);
        });
    }

    [Fact]   // mutation: drop RunAsync's null check on the environment builder (the help then runs with no environment at all)
    public async Task A_null_environment_builder_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            AdminCli.RunAsync(["--help"], new StringWriter(), new StringWriter(), (Func<CliEnvironment>)null!, TestCli.Modules));
    }

    [Fact]   // mutation: build the environment before the try (the settings error escapes: a crash)
    public async Task A_missing_database_file_is_an_error_line_without_its_path()
    {
        await WithDirAsync(async dir =>
        {
            var missing = Path.Combine(dir, "no-such-db-path");
            var env = new Settings(n => n == "PACKMOAT_DB_FILE" ? missing : null, File.ReadAllText);
            var (code, output, error) = await Run(() => new CliEnvironment(env.Read("PACKMOAT_DB") ?? "unused.db", () => throw new InvalidOperationException(), new FakeClock()),
                "tenant", "list");
            Assert.Equal(1, code);
            Assert.Equal("", output);
            Assert.Equal("error: The file named by PACKMOAT_DB_FILE is missing or cannot be read.", error.Trim());
            Assert.DoesNotContain(missing, error);
            Assert.DoesNotContain(dir, error);
        });
    }

    [Fact]   // the help and keygen never build the environment, so a bad setting does not stop them
    public async Task The_help_and_keygen_work_with_a_bad_setting()
    {
        CliEnvironment Broken() => throw new SettingsException("The file named by PACKMOAT_DB_FILE is missing or cannot be read.");
        Assert.Equal(0, (await Run(Broken, "--help")).Code);
        var keygen = await Run(Broken, "keygen");
        Assert.Equal(0, keygen.Code);
        Assert.Equal(44, keygen.Out.Trim().Length);
    }

    [Fact]   // the production entry point: CliEnvironment.FromProcess with a real PACKMOAT_DB_FILE that names no file
    public async Task The_process_environment_with_a_missing_database_file_is_an_error_line()
    {
        await WithDirAsync(async dir =>
        {
            var missing = Path.Combine(dir, "no-such-db-path");
            Environment.SetEnvironmentVariable("PACKMOAT_DB_FILE", missing);
            try
            {
                var o = new StringWriter(); var e = new StringWriter();
                var code = await AdminCli.RunAsync(["tenant", "list"], o, e, CliEnvironment.FromProcess, []);
                Assert.Equal(1, code);
                Assert.StartsWith("error: ", e.ToString());
                Assert.Contains("PACKMOAT_DB_FILE", e.ToString());
                Assert.DoesNotContain(dir, e.ToString());
            }
            finally { Environment.SetEnvironmentVariable("PACKMOAT_DB_FILE", null); }
        });
    }

    /// <summary>A verified backup made under a good key, in the directory.</summary>
    private static async Task<string> BackupAsync(string dir, string key)
    {
        var file = Path.Combine(dir, "backup.db");
        var env = new CliEnvironment(Path.Combine(dir, "packmoat.db"), () => AesGcmSecretProtector.FromBase64(key), new FakeClock(), "op");
        Assert.Equal(0, (await Run(() => env, "tenant", "create", "acme", "Acme", "--upstream-base", "https://x/", "--feed", "https://x/i.json")).Code);
        Assert.Equal(0, (await Run(() => env, "backup", file)).Code);
        return file;
    }

    [Theory]   // mutation: TryProtector returns null for any key error again (verify prints "NOT CHECKED" and exits 0)
    [InlineData("not a base64 key!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]   // 32 zero bytes: trivially weak
    public async Task Verify_with_a_bad_key_file_is_an_error_line(string content)
    {
        await WithDirAsync(async dir =>
        {
            var backup = await BackupAsync(dir, AesGcmSecretProtector.GenerateKeyBase64());
            var keyFile = Path.Combine(dir, "master.key");
            File.WriteAllText(keyFile, content + "\n");
            Environment.SetEnvironmentVariable(KeyFileVariable, keyFile);
            foreach (var args in new[] { new[] { "verify", backup }, ["restore", backup, "--force"], ["backup", Path.Combine(dir, "again.db")], ["db", "check-migrations", backup] })
            {
                var (code, output, error) = await Run(() => KeyFromEnvironment(dir), args);
                Assert.Equal(1, code);
                Assert.Equal("", output);
                Assert.StartsWith("error: ", error);
                Assert.DoesNotContain(content, error);
                Assert.DoesNotContain(dir, error);
            }
            Assert.False(File.Exists(Path.Combine(dir, "again.db")));
        });
    }

    [Fact]   // mutation: TryProtector catches a settings error of any kind (a missing key file skips the check)
    public async Task Verify_with_a_missing_key_file_is_an_error_line_without_its_path()
    {
        await WithDirAsync(async dir =>
        {
            var backup = await BackupAsync(dir, AesGcmSecretProtector.GenerateKeyBase64());
            var keyFile = Path.Combine(dir, "no-such-key");
            Environment.SetEnvironmentVariable(KeyFileVariable, keyFile);
            var (code, output, error) = await Run(() => KeyFromEnvironment(dir), "verify", backup);
            Assert.Equal(1, code);
            Assert.Equal("", output);
            Assert.Equal($"error: The file named by {KeyFileVariable} is missing or cannot be read.", error.Trim());
        });
    }

    [Fact]   // the key check is still skipped when the key is not set at all, and still passes with the right key
    public async Task Verify_skips_the_key_check_only_when_no_key_is_set()
    {
        await WithDirAsync(async dir =>
        {
            var key = AesGcmSecretProtector.GenerateKeyBase64();
            var backup = await BackupAsync(dir, key);

            var unset = await Run(() => KeyFromEnvironment(dir), "verify", backup);
            Assert.Equal(0, unset.Code);
            Assert.Contains("master key: NOT CHECKED (PACKMOAT_MASTER_KEY is not set)", unset.Out);

            var keyFile = Path.Combine(dir, "master.key");
            File.WriteAllText(keyFile, key + "\n");
            Environment.SetEnvironmentVariable(KeyFileVariable, keyFile);
            var set = await Run(() => KeyFromEnvironment(dir), "verify", backup);
            Assert.Equal(0, set.Code);
            Assert.Contains("master key: matches", set.Out);
            Assert.Equal("", set.Err);
        });
    }
}
