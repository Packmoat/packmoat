using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// `decision show` (#123 PR 2): one tenant's decision record for a package, newest last seen first, bounded, with every field the support
/// question needs. Package ids and versions came from clients, so no control or direction character reaches the terminal.
/// </summary>
[Collection("sqlite-pools")]
public class DecisionCliTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDir _temp = TempDir.Create("packmoat-cli-decisions-", clearSqlitePools: true);
    private readonly string _key = AesGcmSecretProtector.GenerateKeyBase64();
    private CliEnvironment Env => new(Path.Combine(_temp.Path, "packmoat.db"), () => AesGcmSecretProtector.FromBase64(_key), new FakeClock());

    private async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var code = await AdminCli.RunAsync(args, o, e, Env, TestCli.Modules);
        return (code, o.ToString(), e.ToString());
    }

    private Task SeedAsync(params DecisionRecord[] rows) =>
        new SqliteDecisionRecordLog(new SqliteDatabase(Env.DbPath)).UpsertAsync(rows, default);

    private static DecisionRecord Rec(string package, string version, int minutes = 0, Decision decision = Decision.Hide, VerdictReason reason = VerdictReason.Cooldown,
        int? cooldown = 14, DateTimeOffset? published = null, long revision = 3, long count = 1, string tenant = "acme", string? waiver = null) =>
        new(tenant, Ecosystem.NuGet, package, version, decision, reason, waiver, cooldown, published, revision, T0, T0.AddMinutes(minutes), count);

    public async Task InitializeAsync()
    {
        Assert.Equal(0, (await Run("tenant", "create", "acme", "Acme", "--upstream-base", "https://x/", "--feed", "https://x/i.json")).Code);
        Assert.Equal(0, (await Run("tenant", "create", "other", "Other", "--upstream-base", "https://x/", "--feed", "https://x/i.json")).Code);
    }

    public Task DisposeAsync()
    {
        _temp.Dispose();
        return Task.CompletedTask;
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();

    [Fact]
    public async Task Shows_every_field_newest_last_seen_first_and_counts_decisions_not_downloads()
    {
        await SeedAsync(
            Rec("moq", "4.21.0", minutes: 1, published: T0.AddDays(-3), count: 5),
            Rec("moq", "4.21.0", minutes: 9, decision: Decision.Allow, reason: VerdictReason.CooldownElapsed, cooldown: 7, published: T0.AddDays(-30), revision: 4, count: 2),
            Rec("moq", "4.20.0", minutes: 5, decision: Decision.Allow, reason: VerdictReason.Waiver, cooldown: null, waiver: "w-9"),
            Rec("moq.extra", "1.0.0", minutes: 50),
            Rec("moq", "9.9.9", tenant: "other", minutes: 60));

        var (code, output, err) = await Run("decision", "show", "--tenant", "acme", "--package", "Moq");
        Assert.Equal(0, code);
        Assert.Equal("", err);
        var lines = Lines(output);
        Assert.Equal("3 row(s) for tenant acme, package moq, newest last seen first", lines[0]);
        Assert.Equal(
            "moq\t4.21.0\tAllow\tCooldownElapsed\tcooldown=7 days\tpublished=2026-09-10 12:00:00Z\tfirst seen=2026-10-10 12:00:00Z\tlast seen=2026-10-10 12:09:00Z\tdecisions=2\tpolicy revision=4\twaiver=none",
            lines[1]);
        Assert.StartsWith("moq\t4.20.0\tAllow\tWaiver\tcooldown=none\tpublished=unknown\t", lines[2]);
        Assert.EndsWith("\twaiver=w-9", lines[2]);
        Assert.StartsWith("moq\t4.21.0\tHide\tCooldown\tcooldown=14 days\tpublished=2026-10-07 12:00:00Z\t", lines[3]);
        Assert.Contains("\tdecisions=5\t", lines[3]);
        Assert.Equal(4, lines.Length);
        Assert.DoesNotContain("download", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_version_narrows_it_and_is_normalised_like_the_record()
    {
        await SeedAsync(Rec("moq", "4.21.0"), Rec("moq", "4.20.0"));
        var (code, output, _) = await Run("decision", "show", "--tenant", "acme", "--package", "moq", "--version", "4.21");
        Assert.Equal(0, code);
        var lines = Lines(output);
        Assert.Equal("1 row(s) for tenant acme, package moq, version 4.21.0, newest last seen first", lines[0]);
        Assert.StartsWith("moq\t4.21.0\t", lines[1]);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public async Task A_prefix_search_and_the_limit_say_when_more_rows_matched()
    {
        await SeedAsync(Rec("moq", "1.0.0", minutes: 1), Rec("moq.extra", "1.0.0", minutes: 2), Rec("moq.more", "1.0.0", minutes: 3), Rec("xmoq", "1.0.0", minutes: 4));
        var (code, output, _) = await Run("decision", "show", "--tenant", "acme", "--package", "moq", "--prefix", "--limit", "2");
        Assert.Equal(0, code);
        var lines = Lines(output);
        Assert.Equal("2 row(s) for tenant acme, package prefix moq, newest last seen first", lines[0]);
        Assert.StartsWith("moq.more\t", lines[1]);
        Assert.StartsWith("moq.extra\t", lines[2]);
        Assert.Equal("More rows match: raise --limit (at most 500) or narrow the search.", lines[3]);
        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public async Task No_rows_says_so()
    {
        var (code, output, _) = await Run("decision", "show", "--tenant", "acme", "--package", "nothing");
        Assert.Equal(0, code);
        Assert.Equal(new[] { "0 row(s) for tenant acme, package nothing, newest last seen first" }, Lines(output));
    }

    [Theory]
    [InlineData("decision", "show", "--package", "moq")]                                         // no tenant
    [InlineData("decision", "show", "--tenant", "acme")]                                         // no package
    [InlineData("decision", "show", "--tenant", "ghost", "--package", "moq")]                    // no such tenant
    [InlineData("decision", "show", "--tenant", "acme", "--package", "moq", "--limit", "0")]
    [InlineData("decision", "show", "--tenant", "acme", "--package", "moq", "--limit", "501")]
    [InlineData("decision", "show", "--tenant", "acme", "--package", "moq", "--limit", "x")]
    [InlineData("decision", "list", "--tenant", "acme", "--package", "moq")]                     // no such verb
    public async Task Bad_arguments_are_errors_not_stack_traces(params string[] args)
    {
        var (code, output, err) = await Run(args);
        Assert.Equal(1, code);
        Assert.StartsWith("error: ", err);
        Assert.Equal("", output);
    }

    [Fact]
    public async Task Control_and_direction_characters_from_clients_never_reach_the_terminal()
    {
        // What a client could put in a download URL: an escape sequence, a new line that would forge a row, a tab, a bell, a
        // right-to-left override, a line separator and a C1 control.
        await SeedAsync(Rec("evil\u001b[2J\nmoq\t4.21.0\tAllow", "1.0.0\u0007\u202e\u2028\u0085x", waiver: "w\u001b[31m\u202e1"));
        var (code, output, _) = await Run("decision", "show", "--tenant", "acme", "--package", "evil", "--prefix");
        Assert.Equal(0, code);
        var lines = Lines(output);
        Assert.Equal(2, lines.Length);
        Assert.Equal(11, lines[1].Split('\t').Length);   // the tab inside the id did not add a column
        Assert.StartsWith("evil?[2J?moq?4.21.0?Allow\t1.0.0????x\tHide\t", lines[1]);
        Assert.EndsWith("\twaiver=w?[31m?1", lines[1]);   // the waiver id is passed through Terminal too
        Assert.DoesNotContain(output, c => c != '\n' && c != '\t' && (char.IsControl(c) || char.GetUnicodeCategory(c) is
            System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator));
    }

    [Fact]
    public async Task The_typed_package_is_echoed_safely_too()
    {
        var (code, output, _) = await Run("decision", "show", "--tenant", "acme", "--package", "x\u001b]0;pwned\u0007");
        Assert.Equal(0, code);
        Assert.DoesNotContain('\u001b', output);
        Assert.DoesNotContain('\u0007', output);
    }

    [Fact]
    public async Task The_usage_lists_the_command()
    {
        var (_, output, _) = await Run("--help");
        Assert.Contains("decision show --tenant <id> --package <id> [--version <v>] [--prefix] [--limit 50]", output);
    }
}
