using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Infrastructure;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// A module's last check before a purge, in the help: the core's <c>tenant purge</c> line names nothing with no module, and a
/// module's words go between "only when due" and "recorded". The hint under <c>tenant purge-due</c> is pinned with the hosted module's
/// words in the hosted lifecycle golden. The purge-due line is the core's generic words unless one module gives its own.
/// </summary>
public class PurgeCheckNoteTests
{
    private sealed class NoteModule(string group, PurgeCheckNote? note) : IAdminCliModule
    {
        public string Group => group;
        public string Usage => "";
        public string EnvironmentUsage => "";
        public Task<int> RunAsync(AdminCliContext context) => Task.FromResult(0);
        public IServingGate? Gate(IRelationalDatabase db, CliEnvironment env, Action<string> warn) => null;
        public TenantLifecycle? Lifecycle(IRelationalDatabase db, CliEnvironment env, Action<string> note) => null;
        public MigrationTrack? Migrations => null;
        public PurgeCheckNote? PurgeCheck => note;
    }

    private static async Task<string> HelpAsync(params IAdminCliModule[] modules)
    {
        var o = new StringWriter();
        Assert.Equal(0, await AdminCli.RunAsync(["--help"], o, new StringWriter(), new CliEnvironment("unused.db", () => throw new InvalidOperationException(), new FakeClock(), "op"), modules));
        return o.ToString();
    }

    private const string Line = "tenant purge <id> --confirm <id>                    DELETE the tenant's data for good ";

    [Fact]   // mutations: ignore the module's words; drop the core's "recorded"
    public async Task The_purge_line_names_a_modules_check_and_nothing_without_one()
    {
        Assert.Contains(Line + "(only when due; recorded)\n", (await HelpAsync()).Replace("\r", ""));
        Assert.Contains(Line + "(only when due; recorded)\n", (await HelpAsync(new NoteModule("plain", null))).Replace("\r", ""));
        var help = (await HelpAsync(new NoteModule("one", new("the provider is asked first", "unused")), new NoteModule("two", null))).Replace("\r", "");
        Assert.Contains(Line + "(only when due; the provider is asked first; recorded)\n", help);
        Assert.DoesNotContain("(only when due; recorded)", help);
    }

    private const string DueLine = "tenant purge-due                                    ";
    private static readonly string Column = new(' ', 54);

    [Fact]
    public async Task Without_a_module_the_purge_due_line_is_generic()
    {
        foreach (var help in new[] { await HelpAsync(), await HelpAsync(new NoteModule("plain", new("checked", "checked"))) })
        {
            var h = help.Replace("\r", "");
            Assert.Contains(DueLine + "tenants due for purge, and any to check by hand. Deletes nothing.\n", h);
            Assert.DoesNotContain("subscription", h);
            Assert.DoesNotContain("sign-up", h);
        }
    }

    [Fact]   // mutations: ignore DueLines; drop the indent
    public async Task A_modules_purge_due_lines_replace_the_core_words_with_later_lines_indented()
    {
        var help = (await HelpAsync(new NoteModule("one", new("asked", "asked", ["first line of the module", "second line"])), new NoteModule("two", null))).Replace("\r", "");
        Assert.Contains("\n  " + DueLine + "first line of the module\n" + Column + "second line\n  tenant purge <id>", help);
        Assert.DoesNotContain("any to check by hand", help);
    }

    [Fact]   // mutation: accept two modules
    public async Task Two_modules_giving_purge_due_lines_stop_the_tool()
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await AdminCli.RunAsync(["--help"], o, e, new CliEnvironment("unused.db", () => throw new InvalidOperationException(), new FakeClock(), "op"),
            [new NoteModule("one", new("a", "a", ["one's line"])), new NoteModule("two", new("b", "b", ["two's line"]))]);
        Assert.Equal(2, code);
        Assert.Equal("", o.ToString());
        Assert.Contains("Two modules give the tenant purge-due help lines", e.ToString());
    }

    [Fact]   // mutations: count an empty list as given, in the two-module check or in the lines' choice
    public async Task An_empty_list_of_purge_due_lines_counts_as_not_given()
    {
        var one = (await HelpAsync(new NoteModule("one", new("a", "a", [])))).Replace("\r", "");
        Assert.Contains(DueLine + "tenants due for purge, and any to check by hand. Deletes nothing.\n", one);

        var two = (await HelpAsync(new NoteModule("one", new("a", "a", [])), new NoteModule("two", new("b", "b", ["two's line"])))).Replace("\r", "");
        Assert.Contains("\n  " + DueLine + "two's line\n  tenant purge <id>", two);
        Assert.DoesNotContain("any to check by hand", two);
    }
}
