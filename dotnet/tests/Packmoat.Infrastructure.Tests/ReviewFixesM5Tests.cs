using Packmoat.Admin;
using Packmoat.Application;

namespace Packmoat.Infrastructure.Tests;

/// <summary>Regression tests for the onboarding tools: safe certificate files and clean errors.</summary>
[Collection("sqlite-pools")]
public class ReviewFixesM5Tests
{
    private sealed class Cli : IAsyncDisposable
    {
        private readonly TempDir _temp = TempDir.Create("packmoat-m5-", clearSqlitePools: true);
        public string Dir => _temp.Path;
        private readonly CliEnvironment _env;
        public Cli()
        {
            var key = AesGcmSecretProtector.GenerateKeyBase64();
            _env = new CliEnvironment(Path.Combine(Dir, "p.db"), () => AesGcmSecretProtector.FromBase64(key), new FakeClock());
        }
        public async Task<(int Code, string Out, string Err)> Run(params string[] a)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var c = await AdminCli.RunAsync(a, o, e, _env, TestCli.Modules);
            return (c, o.ToString(), e.ToString());
        }
        public async Task<string> TenantAsync() { await Run("tenant", "create", "acme", "Acme", "--upstream-base", "https://x/", "--feed", "https://x/i.json"); return "acme"; }
        public ValueTask DisposeAsync() => _temp.DisposeAsync();
    }

    // ---- 22: atomic 0600, never overwrite

    [Fact]
    public async Task A_private_file_is_created_with_mode_600_and_refuses_to_replace_an_existing_one()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("packmoat-m5-").FullName;
        try
        {
            var path = Path.Combine(dir, "k.pem");
            await CertificateTools.WriteNewAsync(path, new byte[] { 1, 2, 3 }, privateFile: true, default);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            await Assert.ThrowsAsync<IOException>(() => CertificateTools.WriteNewAsync(path, new byte[] { 9 }, privateFile: true, default));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));   // untouched
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir); }
    }

    [Fact]
    public async Task New_cert_refuses_to_overwrite_either_file_and_leaves_both_alone()
    {
        await using var cli = new Cli();
        var id = await cli.TenantAsync();
        var outDir = Path.Combine(cli.Dir, "certs");
        Assert.Equal(0, (await cli.Run("identity", "new-cert", id, "--out-dir", outDir)).Code);
        var pem = Path.Combine(outDir, "acme.pem"); var cer = Path.Combine(outDir, "acme.cer");
        var before = (await File.ReadAllBytesAsync(pem), await File.ReadAllBytesAsync(cer));

        var again = await cli.Run("identity", "new-cert", id, "--out-dir", outDir);
        Assert.Equal(1, again.Code);
        Assert.Contains("already exists", again.Err);
        Assert.Equal(before.Item1, await File.ReadAllBytesAsync(pem));
        Assert.Equal(before.Item2, await File.ReadAllBytesAsync(cer));

        File.Delete(cer);   // only the private bundle is left: still must not be replaced
        Assert.Equal(1, (await cli.Run("identity", "new-cert", id, "--out-dir", outDir)).Code);
        Assert.Equal(before.Item1, await File.ReadAllBytesAsync(pem));
        Assert.False(File.Exists(cer));

        File.Delete(pem);   // and the other way round: a stray .cer must not leave a freshly made private key behind after the refusal
        await File.WriteAllBytesAsync(cer, new byte[] { 7 });
        Assert.Equal(1, (await cli.Run("identity", "new-cert", id, "--out-dir", outDir)).Code);
        Assert.False(File.Exists(pem));
        Assert.Equal(new byte[] { 7 }, await File.ReadAllBytesAsync(cer));
    }

    [Fact]
    public async Task A_new_output_directory_is_private_to_the_operator()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var cli = new Cli();
        var id = await cli.TenantAsync();
        var outDir = Path.Combine(cli.Dir, "fresh");
        await cli.Run("identity", "new-cert", id, "--out-dir", outDir);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(outDir));
    }

    // ---- 25: clean errors

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("-5")]
    [InlineData("10")]            // below the 30-day minimum
    [InlineData("99999")]
    [InlineData("99999999999999999999")]
    public async Task A_bad_days_value_gives_one_clear_line_and_no_stack_trace(string days)
    {
        await using var cli = new Cli();
        var id = await cli.TenantAsync();

        var r = await cli.Run("identity", "new-cert", id, "--out-dir", Path.Combine(cli.Dir, "c"), "--days", days);

        Assert.Equal(1, r.Code);
        Assert.StartsWith("error: ", r.Err);
        Assert.Contains("--days", r.Err);
        Assert.DoesNotContain(" at ", r.Err);
        Assert.False(Directory.Exists(Path.Combine(cli.Dir, "c")));   // nothing was created for a refused request
    }

    [Fact]
    public async Task An_out_dir_that_is_a_file_gives_a_clean_error()
    {
        await using var cli = new Cli();
        var id = await cli.TenantAsync();
        var blocker = Path.Combine(cli.Dir, "afile");
        await File.WriteAllTextAsync(blocker, "x");

        var r = await cli.Run("identity", "new-cert", id, "--out-dir", blocker);

        Assert.Equal(1, r.Code);
        Assert.StartsWith("error: ", r.Err);
        Assert.DoesNotContain("   at ", r.Err);
    }

    [Fact]
    public async Task An_unwritable_out_dir_gives_a_clean_error()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        await using var cli = new Cli();
        var id = await cli.TenantAsync();
        var locked = Directory.CreateDirectory(Path.Combine(cli.Dir, "locked"));
        File.SetUnixFileMode(locked.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var r = await cli.Run("identity", "new-cert", id, "--out-dir", Path.Combine(locked.FullName, "sub"));
            Assert.Equal(1, r.Code);
            Assert.StartsWith("error: ", r.Err);
        }
        finally { File.SetUnixFileMode(locked.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public async Task Other_numeric_options_also_fail_cleanly()
    {
        await using var cli = new Cli();
        await cli.TenantAsync();
        foreach (var args in new[] { new[] { "audit", "tail", "--limit", "x" }, new[] { "identity", "expiry", "--warn-days", "x" }, new[] { "waiver", "add", "acme", "p", "*", "--days", "x", "--reason", "r", "--by", "b" } })
        {
            var r = await cli.Run(args);
            Assert.Equal(1, r.Code);
            Assert.StartsWith("error: ", r.Err);
        }
    }
}
