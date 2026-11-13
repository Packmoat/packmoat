using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// Guards <see cref="SqlitePoolsCollection"/>: a test class that builds a database through TestDb or clears SQLite's connection pools
/// (process-wide) must be in the collection that runs alone. A class left out would run beside the others and could dispose a
/// connection one of them was using. The scan reads this project's own sources, top-level type by type (a nested type counts as part of
/// the type that holds it; the parts of a partial type, in one namespace, are one type). It reads the project's style: file-scoped
/// namespaces and top-level types in column 0, and says so, loudly, when a file does otherwise (a block namespace).
/// </summary>
[Collection("sqlite-pools")]
public class SqlitePoolsCollectionTests
{
    /// <summary>What marks a class as one that builds a database or clears the pools, looked for in code (comments are removed first).</summary>
    private static readonly (string Name, Regex Pattern)[] Triggers =
    [
        ("TestDb", new Regex(@"\bTestDb\b(?!\s*\.\s*Lifecycle\b)")),   // TestDb.Lifecycle is a static value, no database
        ("ClearAllPools", new Regex(@"(?<!\bNpgsqlConnection\s*\.\s*)\bClearAllPools\b")),   // Npgsql's own pool is another matter
        ("clearSqlitePools: true", new Regex(@"\bclearSqlitePools\s*:\s*true\b")),
    ];

    private const string StringLiteral = @"@""(?:[^""]|"""")*""|""(?:[^""\\\r\n]|\\.)*""|'(?:[^'\\\r\n]|\\.)'";

    /// <summary>
    /// A string or char literal (kept) or a comment (removed), so a "//" or "/*" inside a string is not taken for a comment.
    /// Raw string literals (three quotes) are not handled; the project's sources have none that hold code.
    /// </summary>
    private static readonly Regex StringOrComment = new($@"{StringLiteral}|/\*.*?\*/|//[^\r\n]*", RegexOptions.Singleline);

    private const string Attribute = @"\[(?:""(?:[^""\\]|\\.)*""|@""(?:[^""]|"""")*""|[^\]""])*\]";

    /// <summary>A top-level type: its attributes (before it, or on its line, over any lines), its modifiers, its kind and its name, from column 0.</summary>
    private static readonly Regex TypeStart = new(
        $@"^(?<attrs>(?:{Attribute}\s*)*)(?:(?:public|internal|sealed|static|abstract|partial|file|unsafe|readonly|ref)\s+)*(?:record\s+(?:struct|class)|record|class|struct|interface|enum)\s+(?<name>\w+)",
        RegexOptions.Multiline);

    private static readonly Regex CollectionAttribute = new(@"\bCollection(?:Attribute)?\s*\(\s*""sqlite-pools""\s*\)");
    private static readonly Regex FileScopedNamespace = new(@"^namespace\s+([\w.]+)\s*;", RegexOptions.Multiline);
    private static readonly Regex BlockNamespace = new(@"^\s*namespace\s+[\w.]+\s*\{", RegexOptions.Multiline);

    /// <summary>The collection, the helper itself and this guard (whose samples name the triggers): by path under the project.</summary>
    private static readonly string[] ExcludedFiles = ["SqlitePoolsCollection.cs", "SqlitePoolsCollectionTests.cs", "Kit.cs", "TestComposition.cs"];

    /// <summary>
    /// Types that name TestDb but are no test class: the contracts' SQLite adapter, built by fixtures that carry the collection themselves.
    /// As "path under the project::type".
    /// </summary>
    private static readonly string[] NotTestClasses = ["Contracts/StoreContract.cs::SqliteStoreEngine"];

    private static string ProjectDir([CallerFilePath] string self = "") => Path.GetDirectoryName(self)!;

    private static (string File, string Source)[] ProjectSources(string dir) =>
        Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (Path.GetRelativePath(dir, f).Replace('\\', '/'), File.ReadAllText(f)))
            .ToArray();

    private static string Code(string source) => StringOrComment.Replace(source, m => m.Value[0] == '/' ? "" : m.Value);

    /// <summary>
    /// Every top-level type in <paramref name="source"/>: its namespace, name, the trigger it uses (or null) and whether it carries the
    /// collection. A type runs from its declaration to the next one.
    /// </summary>
    private static List<(string Namespace, string Type, string? Trigger, bool InCollection)> Types(string source)
    {
        var code = Code(source);
        var ns = FileScopedNamespace.Match(code) is { Success: true } n ? n.Groups[1].Value : "";
        var starts = TypeStart.Matches(code);
        var found = new List<(string, string, string?, bool)>();
        for (var k = 0; k < starts.Count; k++)
        {
            var from = starts[k].Index + starts[k].Length;
            var to = k + 1 < starts.Count ? starts[k + 1].Index : code.Length;
            var body = code[from..to];
            found.Add((ns, starts[k].Groups["name"].Value, Triggers.FirstOrDefault(t => t.Pattern.IsMatch(body)).Name,
                CollectionAttribute.IsMatch(starts[k].Groups["attrs"].Value)));
        }
        return found;
    }

    /// <summary>The files with a block namespace, which the column-0 split cannot read.</summary>
    private static string[] BlockNamespaces((string File, string Source)[] files) =>
        files.Where(f => BlockNamespace.IsMatch(Code(f.Source))).Select(f => f.File).Order(StringComparer.Ordinal).ToArray();

    /// <summary>"Namespace.Type (files; uses X)" for each type with a trigger and no collection on any of its parts.</summary>
    private static string[] Offenders((string File, string Source)[] files) =>
        files.Where(f => !ExcludedFiles.Contains(f.File))
            .SelectMany(f => Types(f.Source).Select(t => (f.File, t.Namespace, t.Type, t.Trigger, t.InCollection)))
            .Where(t => !NotTestClasses.Contains($"{t.File}::{t.Type}"))
            .GroupBy(t => $"{t.Namespace}.{t.Type}")
            .Where(g => g.Any(t => t.Trigger is not null) && !g.Any(t => t.InCollection))
            .Select(g => $"{g.Key} ({string.Join(", ", g.Select(t => t.File).Distinct())}; uses {string.Join(", ", g.Select(t => t.Trigger).Where(t => t is not null).Distinct())})")
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]   // mutation: remove [Collection("sqlite-pools")] from a class that uses TestDb or clears the pools
    public void Every_test_class_that_builds_a_database_or_clears_the_pools_is_in_the_sqlite_pools_collection()
    {
        var files = ProjectSources(ProjectDir());
        Assert.True(files.Length > 50, "the scan found too few source files to mean anything");
        Assert.True(files.Sum(f => Types(f.Source).Count(t => t.Trigger is not null)) > 30, "the scan found too few users of TestDb or the pool clear to mean anything");
        Assert.True(BlockNamespaces(files).Length == 0, "These files use a block namespace, which this scan cannot read (it splits types at column 0 under a file-scoped " +
            "namespace). Use a file-scoped namespace:\n  " + string.Join("\n  ", BlockNamespaces(files)));
        var offenders = Offenders(files);
        Assert.True(offenders.Length == 0, "These test classes build a database or clear SQLite's connection pools but are not in " +
            "[Collection(\"sqlite-pools\")], so they could run beside another one and dispose its connection:\n  " + string.Join("\n  ", offenders));
    }

    private static string[] OffendersIn(string source) => Offenders([("x.cs", source)]);

    [Fact]
    public void The_scan_sees_each_way_of_reaching_the_pools_and_reads_attributes_and_partial_types()
    {
        const string source = """
            namespace N;

            [Collection("sqlite-pools")]
            public class A { void M() { using var t = new TestDb(); } }

            public class B { void M() => SqliteConnection.ClearAllPools(); }

            public sealed class C { void M() => TempDir.Delete(d, clearSqlitePools: true); }

            [Collection("sqlite-pools")]
            public partial class D { void M() { var t = new TestDb(); } }

            public partial class D { }

            // TestDb in a comment, and PostgresTestDb is another name
            public class E { /* ClearAllPools */ PostgresTestDb t; void M() => TempDir.Delete(d, clearSqlitePools: false); }

            public class G { void M() => NpgsqlConnection.ClearAllPools(); var l = TestDb.Lifecycle; }
            """;
        var found = Types(source).Where(u => u.Trigger is not null).ToList();
        Assert.Equal(["A", "B", "C", "D"], found.Select(f => f.Type).ToArray());
        Assert.Equal([true, false, false, true], found.Select(f => f.InCollection).ToArray());
        Assert.Equal(["TestDb", "ClearAllPools", "clearSqlitePools: true", "TestDb"], found.Select(f => f.Trigger!).ToArray());
        Assert.Equal(["N.B (x.cs; uses ClearAllPools)", "N.C (x.cs; uses clearSqlitePools: true)"], OffendersIn(source));

        // The parts of a partial type are one type: the attribute on one part covers the other.
        Assert.Empty(Offenders([("a.cs", "namespace N;\n[Collection(\"sqlite-pools\")]\npublic partial class F { }\n"), ("b.cs", "namespace N;\npublic partial class F { TestDb t; }\n")]));
    }

    [Fact]
    public void The_scan_does_not_miss_a_class_in_the_shapes_that_once_hid_one()
    {
        // A "//" or a "/*" inside a string is no comment: the trigger after it still counts.
        Assert.Equal(["N.Bad (x.cs; uses TestDb)"], OffendersIn("namespace N;\npublic class Bad { void M() { var u = \"http://x\"; var t = new TestDb(); } }\n"));
        Assert.Equal(["N.Bad (x.cs; uses TestDb)"], OffendersIn("namespace N;\npublic class Bad { void M() { var g = \"src/*.cs\"; var h = \"**/*.cs\"; var t = new TestDb(); } }\n"));
        Assert.Equal(["N.Bad (x.cs; uses TestDb)"], OffendersIn("namespace N;\npublic class Bad { void M() { var p = @\"C:\\a\\\"\"//b\"; var t = new TestDb(); } }\n"));

        // An attribute in a doc comment is no attribute.
        Assert.Equal(["N.Bad (x.cs; uses TestDb)"], OffendersIn("namespace N;\n/// [Collection(\"sqlite-pools\")]\npublic class Bad { TestDb t; }\n"));

        // An attribute on the type's own line belongs to it, not to the type before, and the type before keeps its own.
        Assert.Equal(["N.Bad (x.cs; uses TestDb)"], OffendersIn(
            "namespace N;\n[Collection(\"sqlite-pools\")]\npublic class Good { }\n[Trait(\"a\", \"b\")] public class Bad { TestDb t; }\n"));

        // An attribute over several lines is read.
        Assert.Empty(OffendersIn("namespace N;\n[Collection(\n    \"sqlite-pools\")]\npublic class Good { TestDb t; }\n"));

        // The same name in another namespace is another type: an attributed twin does not cover it.
        Assert.Equal(["N2.Twin (b.cs; uses TestDb)"], Offenders([
            ("a.cs", "namespace N1;\n[Collection(\"sqlite-pools\")]\npublic class Twin { TestDb t; }\n"),
            ("b.cs", "namespace N2;\npublic class Twin { TestDb t; }\n")]));

        // A record struct is named by its name, and an interface or an enum ends the type before it.
        Assert.Equal(["N.R (x.cs; uses TestDb)"], OffendersIn("namespace N;\npublic readonly record struct R { TestDb t; }\n"));
        Assert.Empty(OffendersIn("namespace N;\n[Collection(\"sqlite-pools\")]\npublic class Good { TestDb t; }\npublic enum Z { A }\npublic interface I { }\n"));
        Assert.Equal(["N.I (x.cs; uses TestDb)"], OffendersIn("namespace N;\n[Collection(\"sqlite-pools\")]\npublic class Good { }\npublic interface I { void M(TestDb t); }\n"));

        // A block namespace is refused, not skipped.
        Assert.Equal(["x.cs"], BlockNamespaces([("x.cs", "namespace N\n{\n    public class Bad { TestDb t; }\n}\n"), ("y.cs", "namespace N;\npublic class Fine { }\n")]));
    }
}
