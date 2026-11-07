using System.Text.RegularExpressions;
using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The command line's own commands are the core's (#251, moved from #200 item 12): every first word the core's help lists (and -h and
/// --help) stops the tool when a module claims it, before anything runs. The list the tool checks is kept by hand next to its switch, so
/// this reads the words from the help instead of from that list.
/// </summary>
[Collection("sqlite-pools")]
public class AdminCliGroupTests
{
    /// <summary>The core's help with no modules (the help never builds the environment).</summary>
    private static string CoreHelp()
    {
        var output = new StringWriter();
        Assert.Equal(0, AdminCli.RunAsync(["--help"], output, new StringWriter(), () => throw new InvalidOperationException("the help needs no environment"), []).GetAwaiter().GetResult());
        return output.ToString();
    }

    /// <summary>The first word of each command line of the help: a line that starts with exactly two spaces and a letter.</summary>
    internal static string[] HelpCommands(string help) =>
        Regex.Matches(help.ReplaceLineEndings("\n"), @"(?m)^  ([a-z][a-z-]*)\b").Select(m => m.Groups[1].Value).Distinct().ToArray();

    public static TheoryData<string> CoreWords()
    {
        var data = new TheoryData<string> { "-h", "--help" };
        foreach (var word in HelpCommands(CoreHelp())) data.Add(word);
        return data;
    }

    private sealed class Claims(string group) : IAdminCliModule
    {
        public int Runs;
        public string Group => group;
        public string Usage => "";
        public string EnvironmentUsage => "";
        public Task<int> RunAsync(AdminCliContext context) { Runs++; return Task.FromResult(0); }
        public IServingGate? Gate(SqliteDatabase db, CliEnvironment env, Action<string> warn) => null;
        public TenantLifecycle? Lifecycle(SqliteDatabase db, CliEnvironment env, Action<string> note) => null;
        public MigrationTrack? Migrations => null;
    }

    [Theory]   // mutation: any one word left out of the core's hand-kept list (CoreGroups)
    [MemberData(nameof(CoreWords))]
    public async Task A_module_that_claims_a_core_command_stops_the_tool_before_anything_runs(string word)
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-cli-groups-").FullName;
        try
        {
            var built = false;
            var module = new Claims(word);
            var output = new StringWriter();
            var error = new StringWriter();
            var code = await AdminCli.RunAsync([word, "list"], output, error, () => { built = true; return new CliEnvironment(Path.Combine(dir, "packmoat.db"), () => throw new InvalidOperationException(), new FakeClock()); }, [module]);
            Assert.Equal(2, code);
            Assert.Equal($"The module command group \"{word}\" is one of the core's commands. Refusing to run.\n", error.ToString().ReplaceLineEndings("\n"));
            Assert.Equal("", output.ToString());
            Assert.False(built);
            Assert.Equal(0, module.Runs);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void The_help_lists_every_command_the_core_runs()
    {
        // Pins the reading of the help above, so a change to the help's layout cannot quietly shrink the theory.
        Assert.Equal(new[] { "keygen", "tenant", "prefix", "override", "waiver", "token", "identity", "onboard", "audit", "decision", "backup", "verify", "restore", "db", "jobs" },
            HelpCommands(CoreHelp()));
        Assert.Equal(new[] { "fake" }, HelpCommands("x\n  fake poke <id>\n          --confirm-admin \"NAME\"\n   deeper\n"));
    }

    [Fact]
    public async Task A_word_the_core_does_not_use_is_the_module_s()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, await AdminCli.RunAsync(["--help"], output, error, () => throw new InvalidOperationException(), [new Claims("tenants")]));
        Assert.Equal("", error.ToString());
    }
}
