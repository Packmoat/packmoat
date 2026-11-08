using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// The files a public repository needs (#265), at the core's root: the licence and its notice, a README, the security policy, how to
/// contribute and the code of conduct. The licence is Apache-2.0 everywhere one is declared, and the code says nothing of who approves its
/// wording: an issue number is the reference.
/// </summary>
public class OpenSourceFilesTests
{
    private static readonly string[] Files = ["LICENSE", "NOTICE", "README.md", "SECURITY.md", "CONTRIBUTING.md", "CODE_OF_CONDUCT.md"];

    /// <summary>A file's text with Windows line ends made Unix ones, so a checkout that converts them still compares.</summary>
    private static string Unix(string text) => text.Replace("\r\n", "\n");

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]   // mutation: delete SECURITY.md
    public void The_open_source_files_are_at_the_core_root()
    {
        var missing = Files.Where(f => !File.Exists(Path.Combine(CoreRoot.Path(), f))).ToArray();
        Assert.True(missing.Length == 0, "missing at the core's root: " + string.Join(", ", missing));
    }

    [Fact]   // mutation: change one word in LICENSE
    public void The_licence_is_the_Apache_2_0_text_unchanged()
    {
        // The SHA-256 of https://www.apache.org/licenses/LICENSE-2.0.txt, as served on 2026-11-06.
        Assert.Equal("cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30", Sha256(Unix(CoreRoot.Read("LICENSE"))));
    }

    [Fact]   // mutation: another copyright holder in NOTICE
    public void The_notice_names_Packmoat_exactly()
    {
        Assert.Equal("Packmoat\nCopyright 2026 Packmoat\n", Unix(CoreRoot.Read("NOTICE")));
    }

    /// <summary>Every licence declared: each package.json's "license" and each lockfile's own package (packages[""]), as "file: value".</summary>
    internal static string[] DeclaredLicences(string root)
    {
        bool Skipped(string path) => PublicFixtureTests.Skipped(root, path) || Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Contains("node_modules");
        var found = new List<string>();
        foreach (var file in Directory.GetFiles(root, "package.json", SearchOption.AllDirectories).Where(f => !Skipped(f)))
            using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
                found.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}: {(doc.RootElement.TryGetProperty("license", out var l) ? l.GetString() : "(none)")}");
        foreach (var file in Directory.GetFiles(root, "package-lock.json", SearchOption.AllDirectories).Where(f => !Skipped(f)))
            using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
            {
                var own = doc.RootElement.GetProperty("packages").GetProperty("");
                found.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}: {(own.TryGetProperty("license", out var l) ? l.GetString() : "(none)")}");
            }
        return found.Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]   // mutations: package.json back to MIT; only the lockfile back to MIT
    public void Every_declared_licence_is_Apache_2_0()
    {
        var declared = DeclaredLicences(CoreRoot.Path());
        Assert.Equal(["spec/check/package-lock.json: Apache-2.0", "spec/check/package.json: Apache-2.0"], declared);
    }

    [Fact]   // mutation: the private reporting link replaced by an email address
    public void Security_reports_go_to_private_vulnerability_reporting()
    {
        Assert.Contains("https://github.com/Packmoat/packmoat/security/advisories/new", CoreRoot.Read("SECURITY.md"));
    }

    [Fact]
    public void Contributing_asks_for_a_DCO_sign_off()
    {
        var text = CoreRoot.Read("CONTRIBUTING.md");
        Assert.Contains("Signed-off-by", text);
        Assert.Contains("git commit -s", text);
    }

    [Fact]
    public void The_code_of_conduct_is_the_Contributor_Covenant_2_1_with_a_contact()
    {
        var text = Unix(CoreRoot.Read("CODE_OF_CONDUCT.md"));
        Assert.DoesNotContain("[INSERT CONTACT METHOD]", text);
        // Put the template's placeholder back where the contact is: what is left is the official text, unchanged
        // (https://www.contributor-covenant.org/version/2/1/code_of_conduct/code_of_conduct.md, as served on 2026-11-06).
        var contact = new Regex(@"responsible for enforcement at (\S+)\. All complaints", RegexOptions.CultureInvariant);
        Assert.Single(contact.Matches(text));
        var template = contact.Replace(text, "responsible for enforcement at [INSERT CONTACT METHOD]. All complaints");
        Assert.Equal("977d781349351fd7c1f076e4c7dc7de2a05b40e12c773542c3815dd4ce7f37ba", Sha256(template));
    }

    private static readonly Regex Word = new("founder", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The comments in C# source, as (line, text), one entry for each line a comment covers: // to the end of the line, and /* to */.
    /// String literals (regular, verbatim, interpolated with their holes, raw) and char literals are stepped over, so data, and a URL or a
    /// quote in data, is never taken for a comment.
    /// </summary>
    internal static List<(int Line, string Text)> CSharpComments(string text) => CSharpSource.Comments(text);

    /// <summary>A line of a test file that is not C#: a comment when it starts as one (#, //, /*, *, &lt;!--).</summary>
    private static bool CommentLine(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith('#') || t.StartsWith("//") || t.StartsWith("/*") || t.StartsWith('*') || t.StartsWith("<!--");
    }

    /// <summary>
    /// Each line that names the word, as "file:line": anywhere outside dotnet/tests, and in a comment inside it. The tests' data (a test
    /// person's name, an actor) and their names are code, and stay.
    /// </summary>
    internal static string[] FounderMentions(string root, IEnumerable<string> files) =>
        files.SelectMany(f =>
            {
                var name = Path.GetRelativePath(root, f).Replace('\\', '/');
                var text = File.ReadAllText(f);
                IEnumerable<int> lines =
                    !name.StartsWith("dotnet/tests/", StringComparison.Ordinal)
                        ? text.Split('\n').Select((line, i) => (line, i)).Where(x => Word.IsMatch(x.line)).Select(x => x.i + 1)
                    : name.EndsWith(".cs", StringComparison.Ordinal)
                        ? CSharpComments(text).Where(c => Word.IsMatch(c.Text)).Select(c => c.Line)
                    : text.Split('\n').Select((line, i) => (line, i)).Where(x => CommentLine(x.line) && Word.IsMatch(x.line)).Select(x => x.i + 1);
                return lines.Distinct().Select(l => $"{name}:{l}");
            })
            .Order(StringComparer.Ordinal).ToArray();

    [Fact]   // mutations: a draft-approval marker back in a src comment (AppAzureSetup.cs); the word in a test's comment
    public void The_code_names_no_approver_only_an_issue()
    {
        var files = PublicFixtureTests.TextFiles(CoreRoot.Path());
        Assert.True(files.Length > 100, "the scan found too few files in core/ to mean anything");
        var found = FounderMentions(CoreRoot.Path(), files);
        Assert.True(found.Length == 0, "core/ says who approves or decides. Name the issue instead (#63, #117):\n" +
            string.Join("\n", found.Select(x => "  " + x)));
    }

    [Fact]   // mutations: a trailing // found inside a string again; a block comment read line by line again; .git read again
    public void The_approver_scan_sees_the_word_where_it_must()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-oss-").FullName;
        try
        {
            var tests = Path.Combine(dir, "dotnet", "tests", "X.Tests");
            var src = Path.Combine(dir, "dotnet", "src", "X");
            Directory.CreateDirectory(tests);
            Directory.CreateDirectory(src);
            const string F = "founder";   // the word, put together into each line below
            File.WriteAllText(Path.Combine(dir, "README.md"), $"ok\nThe {F.ToUpperInvariant()} says.\n");
            File.WriteAllText(Path.Combine(src, "A.cs"), "var a = 1;\nconst string S = \"" + F + "\";\n");   // outside the tests, code too
            File.WriteAllText(Path.Combine(tests, "T.cs"), string.Join('\n',
                "var name = \"Test Founder\";",                              // 1: test data, stays
                "// the " + F + " approves",                                 // 2
                "    /// <summary>" + F + ".</summary>",                     // 3
                "/*",                                                        // 4
                "   " + F + " inside, on a line with no star",               // 5
                " * " + F,                                                   // 6
                "*/",                                                        // 7
                "Assert.Equal(\"" + F + "\", x);   // " + F,                 // 8: the trailing comment
                "Assert.Equal(\"" + F + "\", x);",                           // 9: test data, stays
                "x(); /* " + F + " */ y();",                                 // 10: a block comment mid-line
                "Exec(\"VALUES('https://x/','" + F + "')\");",               // 11: a URL in test data is not a comment
                "const char Q = '\"'; // " + F,                              // 12: a quote in a char literal opens no string
                "var p = @\"a\\\"; // " + F,                                 // 13: a verbatim string ends at its quote
                "var v = @\"say \"\"" + F + "\"\" // in the string\";",      // 14: stays
                "var i = $\"{(ok ? \"" + F + "\" : \"b\")} // in the string\";",   // 15: stays
                "var r = \"\"\"",                                            // 16
                F + " // in the raw string",                                 // 17: stays
                "\"\"\";",                                                   // 18
                "var n = 2 * 3; An_" + F + "_test();"));                     // 19: a name in code stays (the test names, for now)
            File.WriteAllText(Path.Combine(tests, "run.sh"), $"# {F}\necho {F}\n");
            Directory.CreateDirectory(Path.Combine(dir, "obj"));
            File.WriteAllText(Path.Combine(dir, "obj", "x.cs"), F);   // build output is not read
            Directory.CreateDirectory(Path.Combine(dir, ".git", "logs"));
            File.WriteAllText(Path.Combine(dir, ".git", "logs", "HEAD"), $"0 1 Ada <ada@contoso.example> 1700000000 +0000\tcheckout: moving from main to fix/{F}-wording\n");
            File.WriteAllText(Path.Combine(tests, ".git"), $"gitdir: /src/{F}/.git/worktrees/x\n");   // a worktree's .git is a file
            Assert.Equal(new[]
            {
                "README.md:2",
                "dotnet/src/X/A.cs:2",
                "dotnet/tests/X.Tests/T.cs:10",
                "dotnet/tests/X.Tests/T.cs:12",
                "dotnet/tests/X.Tests/T.cs:13",
                "dotnet/tests/X.Tests/T.cs:2",
                "dotnet/tests/X.Tests/T.cs:3",
                "dotnet/tests/X.Tests/T.cs:5",
                "dotnet/tests/X.Tests/T.cs:6",
                "dotnet/tests/X.Tests/T.cs:8",
                "dotnet/tests/X.Tests/run.sh:1",
            }, FounderMentions(dir, PublicFixtureTests.TextFiles(dir)));
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir); }
    }

    /// <summary>
    /// Whether the file or folder at <paramref name="path"/> (full, inside <paramref name="root"/>) exists with exactly that case in every
    /// segment below the root. A Mac's or Windows' file system finds docs/ARCHITECTURE.md for docs/architecture.md; GitHub and Linux do not.
    /// </summary>
    internal static bool ExistsExactly(string root, string path)
    {
        var dir = Path.GetFullPath(root);
        foreach (var segment in Path.GetRelativePath(dir, Path.GetFullPath(path)).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (!Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any(e => Path.GetFileName(e) == segment)) return false;
            dir = Path.Combine(dir, segment);
        }
        return true;
    }

    /// <summary>
    /// The anchors GitHub gives a Markdown file's headings (outside code blocks): the heading's text in lower case, with every character
    /// but a letter, a digit, a space, a hyphen or an underscore removed and each space made a hyphen. A repeated anchor gets -1, -2.
    /// </summary>
    internal static HashSet<string> Anchors(string markdown)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        var inFence = false;
        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (Regex.IsMatch(line, @"^\s*(```|~~~)")) { inFence = !inFence; continue; }
            var heading = Regex.Match(line, @"^#{1,6}\s+(.+?)\s*#*\s*$");
            if (inFence || !heading.Success) continue;
            var slug = new string(heading.Groups[1].Value.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray()).Replace(' ', '-');
            var unique = slug;
            for (var n = 1; !anchors.Add(unique); n++) unique = $"{slug}-{n}";
        }
        return anchors;
    }

    /// <summary>
    /// Each relative link in a Markdown file at <paramref name="root"/> ([text](target) and [label]: target), as "file -> target", that
    /// does not resolve to a file or folder inside <paramref name="root"/>, or whose #anchor names no heading in the Markdown file it points
    /// to (#275). A link with a scheme (https:, mailto:) is not relative; an anchor alone points into its own file.
    /// </summary>
    internal static string[] BrokenLinks(string root, IEnumerable<string> docs)
    {
        var full = Path.GetFullPath(root);
        var broken = new List<string>();
        foreach (var doc in docs)
        {
            var text = File.ReadAllText(Path.Combine(root, doc));
            var targets = Regex.Matches(text, @"\]\(([^)\s]+)\)").Select(m => m.Groups[1].Value)
                .Concat(Regex.Matches(text, @"^\[[^\]]+\]:\s*(\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value));
            foreach (var target in targets)
            {
                if (Regex.IsMatch(target, "^[A-Za-z][A-Za-z0-9+.-]*:")) continue;
                var hash = target.IndexOf('#');
                var path = hash < 0 ? target : target[..hash];
                var resolved = Path.GetFullPath(Path.Combine(full, Path.GetDirectoryName(doc) ?? "", path.Length == 0 ? Path.GetFileName(doc) : path));
                var relative = Path.GetRelativePath(full, resolved);
                var inside = relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative);
                if (!inside || !ExistsExactly(full, resolved)) broken.Add($"{doc} -> {target}");
                else if (hash >= 0 && resolved.EndsWith(".md", StringComparison.Ordinal) && File.Exists(resolved)
                         && !Anchors(File.ReadAllText(resolved)).Contains(target[(hash + 1)..])) broken.Add($"{doc} -> {target}");
            }
        }
        return broken.ToArray();
    }

    [Fact]   // mutations: docs/self-hostin.md in README; ../docs/architecture.md in README; a broken link or anchor in docs/deploy
    public void Every_relative_link_in_every_doc_resolves_inside_the_core()
    {
        var docs = SelfHostingDocTests.MarkdownFiles(CoreRoot.Path());
        Assert.True(docs.Length >= 13, "the scan found too few Markdown files in the core to mean anything");
        var broken = BrokenLinks(CoreRoot.Path(), docs);
        Assert.True(broken.Length == 0, "a doc links to a path or heading that is not in the core:\n  " + string.Join("\n  ", broken));
    }

    [Fact]
    public void The_link_check_sees_a_missing_path_and_one_outside_the_root()
    {
        var parent = Directory.CreateTempSubdirectory("packmoat-links-").FullName;
        try
        {
            var root = Path.Combine(parent, "core");
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            File.WriteAllText(Path.Combine(root, "docs", "guide.md"), string.Join('\n',
                "# Setup", "## 4. Settings (`X_Y`)", "## Setup", "```", "# not-a-heading", "```", "[back](../README.md#top) [self](#setup-1) [gone](#nope)"));
            File.WriteAllText(Path.Combine(parent, "outside.md"), "x");   // exists, but outside the root
            File.WriteAllText(Path.Combine(root, "README.md"), string.Join('\n',
                "# Top",
                "[ok](docs/guide.md) [ok](docs/guide.md#setup) [ok](docs) [web](https://example.com/a) [mail](mailto:a@b.test) [top](#top)",
                "[ok](docs/guide.md#4-settings-x_y)",
                "[typo](docs/guid.md) [escape](../outside.md) [escape](docs/../../outside.md) [case](docs/GUIDE.md) [case](Docs/guide.md)",
                "[anchor](docs/guide.md#settings) [fenced](docs/guide.md#not-a-heading) [here](#bottom)",
                "[ref]: docs/nope.md"));
            Assert.Equal(new[]
            {
                "README.md -> docs/guid.md",
                "README.md -> ../outside.md",
                "README.md -> docs/../../outside.md",
                "README.md -> docs/GUIDE.md",
                "README.md -> Docs/guide.md",
                "README.md -> docs/guide.md#settings",
                "README.md -> docs/guide.md#not-a-heading",
                "README.md -> #bottom",
                "README.md -> docs/nope.md",
                "docs/guide.md -> #nope",
            }, BrokenLinks(root, ["README.md", "docs/guide.md"]));
        }
        catch when (TempDir.Failing(parent)) { throw; }
        finally { TempDir.Delete(parent); }
    }
}
