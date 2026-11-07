using System.Text.RegularExpressions;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// docs/self-hosting.md (#188) is public and is followed by people who cannot read the code. Its settings table and
/// deploy/packmoat.env.example must name exactly the settings the code reads, and every repository path it names must exist.
/// </summary>
public class SelfHostingDocTests
{
    private static readonly Regex Setting = new(@"\bPACKMOAT_[A-Z0-9_]*[A-Z0-9]\b", RegexOptions.CultureInvariant);

    private static string Doc() => CoreRoot.Read("docs", "self-hosting.md");

    private static bool Built(string path) =>
        path.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}")
        || path.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}");

    /// <summary>Every PACKMOAT_* name in the core's source (dotnet/src, every file, bin and obj aside).</summary>
    internal static SortedSet<string> SourceSettings()
    {
        var src = System.IO.Path.Combine(CoreRoot.Path(), "dotnet", "src");
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories).Where(f => !Built(f)))
            foreach (Match m in Setting.Matches(File.ReadAllText(file))) names.Add(m.Value);
        return names;
    }

    /// <summary>The settings named in the first column of the doc's table rows (a row starting "| `PACKMOAT_...` |").</summary>
    internal static List<string> TableSettings(string doc) =>
        Regex.Matches(doc, @"^\| `(PACKMOAT_[A-Z0-9_]+)` \|", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();

    /// <summary>The settings an env example sets, commented out or not ("NAME=" or "# NAME=" at the start of a line).</summary>
    internal static List<string> ExampleSettings(string example) =>
        Regex.Matches(example, @"^(?:#\s*)?(PACKMOAT_[A-Z0-9_]+)=", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();

    [Fact]
    public void The_source_scan_finds_the_settings_it_must()
    {
        var names = SourceSettings();
        Assert.True(names.Count >= 15, "the scan found too few PACKMOAT_* names in dotnet/src to mean anything");
        Assert.Contains("PACKMOAT_MASTER_KEY", names);
        Assert.Contains("PACKMOAT_AZURE_SETUP", names);
    }

    [Fact]   // mutations: a new PACKMOAT_* name in dotnet/src with no row; a row for a setting the code does not read
    public void The_doc_table_lists_exactly_the_settings_the_code_reads_once_each()
    {
        var table = TableSettings(Doc());
        Assert.Equal(table.Count, table.Distinct(StringComparer.Ordinal).Count());
        var expected = SourceSettings();
        var actual = new SortedSet<string>(table, StringComparer.Ordinal);
        Assert.True(expected.SetEquals(actual),
            "docs/self-hosting.md's settings table must list exactly the PACKMOAT_* names in dotnet/src.\n" +
            "  missing from the table: " + string.Join(", ", expected.Except(actual)) + "\n" +
            "  not read by the code: " + string.Join(", ", actual.Except(expected)));
    }

    [Fact]   // mutation: a setting only a hosted module reads, anywhere in the doc
    public void The_doc_names_no_setting_that_neither_the_code_nor_the_deploy_kit_reads()
    {
        var known = SourceSettings();
        // The compose file's own variables (the image tag), which the server never reads.
        known.UnionWith(Regex.Matches(CoreRoot.Read("deploy", "docker-compose.yml"), @"\$\{(PACKMOAT_[A-Z0-9_]+)").Select(m => m.Groups[1].Value));
        var unknown = Setting.Matches(Doc()).Select(m => m.Value).Where(n => !known.Contains(n)).Distinct().ToArray();
        Assert.True(unknown.Length == 0, "docs/self-hosting.md names settings nothing reads: " + string.Join(", ", unknown));
    }

    [Fact]   // mutations: drop a setting from packmoat.env.example; add one the code does not read
    public void The_env_example_sets_exactly_the_settings_the_code_reads()
    {
        var example = CoreRoot.Read("deploy", "packmoat.env.example");
        var set = ExampleSettings(example);
        Assert.Equal(set.Count, set.Distinct(StringComparer.Ordinal).Count());
        Assert.True(SourceSettings().SetEquals(set), "deploy/packmoat.env.example must set exactly the PACKMOAT_* names in dotnet/src.");
        Assert.All(Setting.Matches(example).Select(m => m.Value), n => Assert.Contains(n, SourceSettings()));
    }

    [Fact]   // mutation: PACKMOAT_HOOKS_HOST in caddy.env.example or the Caddyfile
    public void Caddy_is_given_only_host_names_the_code_reads()
    {
        var caddyEnv = ExampleSettings(CoreRoot.Read("deploy", "caddy.env.example"));
        Assert.Equal(new[] { "PACKMOAT_ADMIN_HOST", "PACKMOAT_APP_HOST", "PACKMOAT_HOSTS" }, caddyEnv.Order(StringComparer.Ordinal));
        var placeholders = Regex.Matches(CoreRoot.Read("deploy", "Caddyfile"), @"\{\$(PACKMOAT_[A-Z0-9_]+)").Select(m => m.Groups[1].Value).Distinct();
        Assert.All(placeholders, p => Assert.Contains(p, caddyEnv));
    }

    /// <summary>
    /// The repository paths the doc names: anything that starts with one of the core's top-level folders, in a code span, a code block or
    /// bare in the text (but not inside a server path such as /opt/packmoat/deploy/), and every relative link. Links resolve from docs/;
    /// the rest from the core's root. A full stop that ends a sentence is not part of the path.
    /// </summary>
    internal static List<string> RepoPaths(string doc)
    {
        var paths = Regex.Matches(doc, @"(?<![\w/.~<>-])((?:deploy|dotnet|docs|spec)/[A-Za-z0-9_./-]*)").Select(m => m.Groups[1].Value.TrimEnd('.')).ToList();
        paths.AddRange(Regex.Matches(doc, @"\]\(([^)#\s]+)\)").Select(m => m.Groups[1].Value)
            .Where(l => !l.Contains("://", StringComparison.Ordinal) && !l.StartsWith("mailto:", StringComparison.Ordinal))
            .Select(l => System.IO.Path.GetRelativePath(CoreRoot.Path(), System.IO.Path.GetFullPath(System.IO.Path.Combine(CoreRoot.Path(), "docs", l))).Replace('\\', '/')));
        return paths;
    }

    [Fact]
    public void The_path_finder_sees_code_spans_and_links_and_skips_server_paths()
    {
        var found = RepoPaths("Run `deploy/packmoat-deploy` or see [x](ARCHITECTURE.md), not `/opt/packmoat/deploy/` nor [y](https://example.com/a). " +
            "Also docs/runbooks/deploy.md.\n```\ndocker build /opt/packmoat/dotnet && cp dotnet/x .\n```");
        Assert.Equal(new[] { "deploy/packmoat-deploy", "docs/runbooks/deploy.md", "dotnet/x", "docs/ARCHITECTURE.md" }, found);
    }

    [Fact]   // mutations: name `deploy/packmoat-deploy` in the doc; name docs/runbooks/deploy.md bare in the text
    public void Every_repository_path_the_doc_names_exists_in_the_core()
    {
        var paths = RepoPaths(Doc());
        Assert.True(paths.Count >= 8, "the doc names too few repository paths for this check to mean anything");
        var root = CoreRoot.Path();
        var missing = paths.Where(p => !File.Exists(System.IO.Path.Combine(root, p)) && !Directory.Exists(System.IO.Path.Combine(root, p))).Distinct().ToArray();
        Assert.True(missing.Length == 0, "docs/self-hosting.md names paths that are not in the core: " + string.Join(", ", missing));
    }

    [Fact]   // mutation: a backup setting in the doc that backup.sh does not read (or a typo in one)
    public void The_backup_settings_the_doc_names_are_the_scripts_own()
    {
        var doc = Doc();
        var backup = CoreRoot.Read("deploy", "backup.sh");
        var restore = CoreRoot.Read("deploy", "restore.sh");
        var named = Regex.Matches(doc, @"\b(?:BACKUP_[A-Z0-9_]+|KEEP_[A-Z0-9_]+|RETAIN_DAYS)\b").Select(m => m.Value).Distinct().ToArray();
        Assert.True(named.Length >= 4, "the doc names too few backup settings for this check to mean anything");
        Assert.All(named, n => Assert.Matches(@"\$\{" + n + @"[:}]", backup));
        foreach (var n in new[] { "AGE_IDENTITY", "IMAGE" })
            if (Regex.IsMatch(doc, @"\b" + n + "=")) Assert.Matches(@"\$\{" + n + @"[:}]", restore);
    }
}
