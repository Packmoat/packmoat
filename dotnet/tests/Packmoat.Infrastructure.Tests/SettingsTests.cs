namespace Packmoat.Infrastructure.Tests;

/// <summary>The settings reader (#233): a variable, or the file its _FILE variable names, and the errors that stop the start.</summary>
public class SettingsTests
{
    private const string Name = "PACKMOAT_EXAMPLE";
    private const string FileName = Name + Settings.FileSuffix;
    private const string Secret = "s3cret-value-xyz";
    private const string Path = "/run/secrets/do-not-print-this-path";

    private static Settings Reader(Dictionary<string, string?> env, Func<string, string>? readFile = null) =>
        new(n => env.TryGetValue(n, out var v) ? v : null, readFile ?? (_ => throw new FileNotFoundException("unexpected read")));

    [Fact]
    public void The_variable_alone_is_returned_exactly_as_the_environment_gives_it()
    {
        Assert.Equal(" value with spaces \n", Reader(new() { [Name] = " value with spaces \n" }).Read(Name));
        Assert.Equal("", Reader(new() { [Name] = "" }).Read(Name));
        Assert.Null(Reader(new()).Read(Name));
    }

    [Fact]   // an empty file variable is not set, so an empty line in an env file changes nothing
    public void An_empty_file_variable_counts_as_not_set()
    {
        Assert.Equal("v", Reader(new() { [Name] = "v", [FileName] = "" }).Read(Name));
        Assert.Null(Reader(new() { [FileName] = "" }).Read(Name));
    }

    [Fact]
    public void The_file_alone_is_read_with_one_trailing_newline_removed()
    {
        string? asked = null;
        var reader = Reader(new() { [FileName] = Path }, p => { asked = p; return Secret + "\n"; });
        Assert.Equal(Secret, reader.Read(Name));
        Assert.Equal(Path, asked);
    }

    [Theory]   // one line ending (\n, or \r\n from Windows) only: nothing else is trimmed
    [InlineData("v", "v")]
    [InlineData("v\n", "v")]
    [InlineData("v\n\n", "v\n")]
    [InlineData("v\r\n", "v")]
    [InlineData("v\r\n\r\n", "v\r\n")]
    [InlineData("v\n\r\n", "v\n")]
    [InlineData("v\r", "v\r")]
    [InlineData(" v \n", " v ")]
    [InlineData("\tv", "\tv")]
    public void Only_one_trailing_newline_is_trimmed(string content, string expected) =>
        Assert.Equal(expected, Reader(new() { [FileName] = Path }, _ => content).Read(Name));

    [Fact]
    public void A_real_file_is_read()
    {
        using var temp = TempDir.Create("packmoat-settings-");
        var file = temp.File("secret");
        File.WriteAllText(file, Secret + "\n");
        Assert.Equal(Secret, new Settings(n => n == FileName ? file : null, File.ReadAllText).Read(Name));
    }

    public static TheoryData<string, Dictionary<string, string?>, Func<string, string>> Failures() => new()
    {
        { "are both set", new() { [Name] = Secret, [FileName] = Path }, _ => Secret },
        { "is missing or cannot be read", new() { [FileName] = Path }, _ => throw new FileNotFoundException("Could not find file " + Path, Path) },
        { "is missing or cannot be read", new() { [FileName] = Path }, _ => throw new UnauthorizedAccessException("Access to the path " + Path + " is denied.") },
        { "is missing or cannot be read", new() { [FileName] = Path }, _ => throw new DirectoryNotFoundException("Could not find a part of the path " + Path) },
        { "is missing or cannot be read", new() { [FileName] = Path }, _ => throw new IOException("I/O error on " + Path) },
        { "is empty", new() { [FileName] = Path }, _ => "" },
        { "is empty", new() { [FileName] = Path }, _ => "\n" },
        { "is empty", new() { [FileName] = Path }, _ => "\r\n" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void Each_failure_stops_with_the_variable_named_and_neither_the_value_nor_the_path(string reason, Dictionary<string, string?> env, Func<string, string> readFile)
    {
        var e = Assert.Throws<SettingsException>(() => Reader(env, readFile).Read(Name));
        Assert.Contains(reason, e.Message);
        Assert.Contains(Name, e.Message);
        Assert.DoesNotContain(Secret, e.Message);
        Assert.DoesNotContain(Path, e.Message);
        Assert.DoesNotContain("do-not-print", e.ToString());   // not in an inner exception either
        Assert.Null(e.InnerException);
    }

    [Fact]
    public void A_missing_real_file_stops_without_naming_its_path()
    {
        var missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "packmoat-missing-" + Guid.NewGuid().ToString("N"), "secret");
        var e = Assert.Throws<SettingsException>(() => new Settings(n => n == FileName ? missing : null, File.ReadAllText).Read(Name));
        Assert.Equal($"The file named by {FileName} is missing or cannot be read.", e.Message);
        Assert.DoesNotContain(missing, e.ToString());
    }

    [Fact]
    public void A_directory_in_place_of_the_file_stops_the_start()
    {
        var dir = System.IO.Path.GetTempPath();
        Assert.Throws<SettingsException>(() => new Settings(n => n == FileName ? dir : null, File.ReadAllText).Read(Name));
    }

    [Fact]
    public void The_process_reader_returns_the_environment_as_it_is()
    {
        const string variable = "PACKMOAT_SETTINGS_TEST_ONLY_7F3A";
        Environment.SetEnvironmentVariable(variable, " x ");
        try { Assert.Equal(" x ", Settings.Get(variable)); }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public void The_master_key_can_come_from_a_file()
    {
        using var temp = TempDir.Create("packmoat-settings-");
        var file = temp.File("key");
        const string variable = "PACKMOAT_SETTINGS_TEST_KEY_7F3A";
        try
        {
            File.WriteAllText(file, AesGcmSecretProtector.GenerateKeyBase64() + "\n");
            Environment.SetEnvironmentVariable(variable + Settings.FileSuffix, file);
            Assert.NotNull(AesGcmSecretProtector.FromEnvironment(variable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable + Settings.FileSuffix, null);
        }
    }
}

/// <summary>The settings reader's errors in packmoat-admin (#233). Apart from SettingsTests because it opens a database.</summary>
[Collection("sqlite-pools")]
public class SettingsCliTests
{
    [Fact]   // a bad file setting in packmoat-admin is the usual "error:" line and exit 1, not a crash (#233)
    public async Task The_admin_tool_reports_a_bad_file_setting_as_an_error_line()
    {
        const string variable = "PACKMOAT_SETTINGS_TEST_CLI_KEY_7F3A";
        var dir = Directory.CreateTempSubdirectory("packmoat-settings-cli-").FullName;
        var missing = System.IO.Path.Combine(dir, "no-such-key");
        Environment.SetEnvironmentVariable(variable + Settings.FileSuffix, missing);
        try
        {
            var env = new Packmoat.Admin.CliEnvironment(System.IO.Path.Combine(dir, "packmoat.db"), () => AesGcmSecretProtector.FromEnvironment(variable), new FakeClock());
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = await Packmoat.Admin.AdminCli.RunAsync(["tenant", "list"], output, error, env, TestCli.Modules);
            Assert.Equal(1, code);
            Assert.Equal($"error: The file named by {variable}{Settings.FileSuffix} is missing or cannot be read.", error.ToString().Trim());
            Assert.DoesNotContain(missing, error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable + Settings.FileSuffix, null);
            TempDir.Delete(dir, clearSqlitePools: true);
        }
    }
}
