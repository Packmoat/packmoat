using System.Text.RegularExpressions;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// The public docs are followed by people who cannot read the code. The settings table in docs/operations.md and
/// deploy/packmoat.env.example must name exactly the settings the code reads, no doc may name a setting nothing reads, and every
/// repository path a doc names must exist.
/// </summary>
public class SelfHostingDocTests
{
    private static readonly Regex Setting = new(@"\bPACKMOAT_[A-Z0-9_]*[A-Z0-9]\b", RegexOptions.CultureInvariant);

    /// <summary>The settings reference: the one table that lists every setting.</summary>
    private static string Doc() => CoreRoot.Read("docs", "operations.md");

    /// <summary>Every Markdown file in the core (bin, obj, .git and node_modules aside), relative to the core's root, with / separators.</summary>
    internal static string[] MarkdownFiles(string root) =>
        PublicFixtureTests.TextFiles(root)
            .Where(f => f.EndsWith(".md", StringComparison.Ordinal))
            .Select(f => System.IO.Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.Split('/').Contains("node_modules"))
            .Order(StringComparer.Ordinal).ToArray();

    [Fact]   // mutations: delete docs/concepts.md; add docs/deploy/extra.md (a new doc is added here on purpose, so its place is decided)
    public void The_markdown_scan_finds_exactly_the_public_docs()
    {
        Assert.Equal(new[]
        {
            "CODE_OF_CONDUCT.md", "CONTRIBUTING.md", "README.md", "SECURITY.md",
            "docs/architecture.md", "docs/concepts.md", "docs/deploy/README.md", "docs/deploy/azure-container-apps.md",
            "docs/deploy/kubernetes.md", "docs/deploy/own-proxy.md", "docs/deploy/single-server.md", "docs/operations.md",
            "docs/self-hosting.md", "spec/README.md",
        }, MarkdownFiles(CoreRoot.Path()));
    }

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
            "docs/operations.md's settings table must list exactly the PACKMOAT_* names in dotnet/src.\n" +
            "  missing from the table: " + string.Join(", ", expected.Except(actual)) + "\n" +
            "  not read by the code: " + string.Join(", ", actual.Except(expected)));
    }

    [Fact]   // mutation: a setting only a hosted module reads, in any doc (the deploy overview, a guide)
    public void No_doc_names_a_setting_that_neither_the_code_nor_the_deploy_kit_reads()
    {
        var known = SourceSettings();
        // The compose file's own variables (the image tag), which the server never reads.
        known.UnionWith(Regex.Matches(CoreRoot.Read("deploy", "docker-compose.yml"), @"\$\{(PACKMOAT_[A-Z0-9_]+)").Select(m => m.Groups[1].Value));
        var unknown = MarkdownFiles(CoreRoot.Path())
            .SelectMany(d => Setting.Matches(CoreRoot.Read(d)).Select(m => (Doc: d, Name: m.Value)))
            .Where(x => !known.Contains(x.Name) && !(x.Name.EndsWith("_FILE", StringComparison.Ordinal) && known.Contains(x.Name[..^"_FILE".Length])))
            .Select(x => $"{x.Doc}: {x.Name}").Distinct().ToArray();   // a setting's file form (PACKMOAT_MASTER_KEY_FILE) is the setting
        Assert.True(unknown.Length == 0, "a doc names settings nothing reads: " + string.Join(", ", unknown));
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
    /// The repository paths a doc names: anything that starts with one of the core's top-level folders, in a code span, a code block or
    /// bare in the text (but not inside a server path such as /opt/packmoat/deploy/), and every relative link. Links resolve from the doc's
    /// own folder (<paramref name="docFolder"/>, relative to the core's root); the rest from the core's root. A full stop that ends a
    /// sentence is not part of the path, nor is a link's #anchor. A link's text is a label, not a path: [deploy/README.md](deploy/README.md)
    /// in docs/ names docs/deploy/README.md only.
    /// </summary>
    internal static List<string> RepoPaths(string doc, string docFolder)
    {
        var withoutLinks = Regex.Replace(doc, @"\]\([^)\s]*\)", "]");
        var paths = Regex.Matches(withoutLinks, @"(?<![\w/.~<>\[-])((?:deploy|dotnet|docs|spec)/[A-Za-z0-9_./-]*)").Select(m => m.Groups[1].Value.TrimEnd('.')).ToList();
        paths.AddRange(Regex.Matches(doc, @"\]\(([^)#\s]+)(?:#[^)\s]*)?\)").Select(m => m.Groups[1].Value)
            .Where(l => !l.Contains("://", StringComparison.Ordinal) && !l.StartsWith("mailto:", StringComparison.Ordinal))
            .Select(l => System.IO.Path.GetRelativePath(CoreRoot.Path(), System.IO.Path.GetFullPath(System.IO.Path.Combine(CoreRoot.Path(), docFolder, l))).Replace('\\', '/')));
        return paths;
    }

    [Fact]
    public void The_path_finder_sees_code_spans_and_links_and_skips_server_paths()
    {
        var doc = "Run `deploy/packmoat-deploy` or see [x](architecture.md), not `/opt/packmoat/deploy/` nor [y](https://example.com/a). " +
            "Also docs/runbooks/deploy.md, and [z](../operations.md#settings).\n```\ndocker build /opt/packmoat/dotnet && cp dotnet/x .\n```";
        Assert.Equal(new[] { "deploy/packmoat-deploy", "docs/runbooks/deploy.md", "dotnet/x", "docs/deploy/architecture.md", "docs/operations.md" },
            RepoPaths(doc, "docs/deploy"));
        Assert.Equal(new[] { "architecture.md" }, RepoPaths("[x](architecture.md)", ""));   // a link in a root doc resolves from the root
        Assert.Equal(new[] { "docs/deploy/README.md" }, RepoPaths("see [deploy/README.md](deploy/README.md).", "docs"));
    }

    [Fact]   // mutations: name `deploy/packmoat-deploy` in a guide; name docs/runbooks/deploy.md bare in the text; a broken link in docs/deploy
    public void Every_repository_path_a_doc_names_exists_in_the_core()
    {
        var root = CoreRoot.Path();
        var paths = MarkdownFiles(root)
            .SelectMany(d => RepoPaths(CoreRoot.Read(d), System.IO.Path.GetDirectoryName(d) ?? "").Select(p => (Doc: d, Path: p))).ToArray();
        Assert.True(paths.Count(p => p.Doc == "docs/deploy/single-server.md") >= 8,
            "the single-server guide names too few repository paths for this check to mean anything");
        var missing = paths.Where(p => !OpenSourceFilesTests.ExistsExactly(root, System.IO.Path.Combine(root, p.Path)))   // case too, as on GitHub
            .Select(p => $"{p.Doc}: {p.Path}").Distinct().ToArray();
        Assert.True(missing.Length == 0, "a doc names paths that are not in the core: " + string.Join(", ", missing));
    }

    [Fact]   // mutation: a backup setting in the guide that backup.sh does not read (or a typo in one)
    public void The_backup_settings_the_guide_names_are_the_scripts_own()
    {
        var doc = CoreRoot.Read("docs", "deploy", "single-server.md");
        var backup = CoreRoot.Read("deploy", "backup.sh");
        var restore = CoreRoot.Read("deploy", "restore.sh");
        var named = Regex.Matches(doc, @"\b(?:BACKUP_[A-Z0-9_]+|KEEP_[A-Z0-9_]+|RETAIN_DAYS)\b").Select(m => m.Value).Distinct().ToArray();
        Assert.True(named.Length >= 4, "the doc names too few backup settings for this check to mean anything");
        Assert.All(named, n => Assert.Matches(@"\$\{" + n + @"[:}]", backup));
        foreach (var n in new[] { "AGE_IDENTITY", "IMAGE" })
            if (Regex.IsMatch(doc, @"\b" + n + "=")) Assert.Matches(@"\$\{" + n + @"[:}]", restore);
    }
}
