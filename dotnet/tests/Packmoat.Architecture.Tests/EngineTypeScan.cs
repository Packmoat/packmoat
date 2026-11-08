using System.Text.RegularExpressions;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// Architecture test 1's scanner (#278 step 0): which source files name a database engine's type (<c>Microsoft.Data.Sqlite</c>, a
/// <c>Sqlite*</c> type or <c>Npgsql</c>) outside the adapters and the composition allowlist. Comments are stripped first by the C# comment
/// reader (<see cref="CSharpSource"/>: string and char literals kept whole), so a <c>&lt;see cref="SqliteDatabase"/&gt;</c> in a doc comment does not count. Shared with the hosted twin
/// (linked into Packmoat.Hosted.Architecture.Tests with CSharpSource.cs), which passes its own allowlist.
/// </summary>
internal static class EngineTypeScan
{
    private static readonly Regex EngineType = new(@"Microsoft\.Data\.Sqlite|\bSqlite[A-Z]\w*|Npgsql", RegexOptions.CultureInvariant);

    /// <summary>
    /// The code with its comments taken out by the C# comment reader (<see cref="CSharpSource"/>, #270): char literals, verbatim strings
    /// and raw strings are stepped over as the compiler reads them, so a quote or a // inside one cannot hide the code after it.
    /// </summary>
    internal static string WithoutComments(string source) => CSharpSource.WithoutComments(source);

    /// <summary>The engine type names <paramref name="source"/>'s code (comments aside) uses, in order, each once.</summary>
    internal static string[] EngineTypesNamed(string source) =>
        EngineType.Matches(WithoutComments(source)).Select(m => m.Value).Distinct().ToArray();

    /// <summary>
    /// True when <paramref name="named"/> (a path under src/, with '/') is on <paramref name="allowed"/>: an exact path, or a pattern
    /// ending in <c>*.cs</c> that takes any file directly in that folder whose name starts with the part before the star.
    /// </summary>
    internal static bool IsAllowed(string named, IReadOnlyList<string> allowed) =>
        allowed.Any(a => a.EndsWith("*.cs", StringComparison.Ordinal)
            ? named.StartsWith(a[..^4], StringComparison.Ordinal) && named.EndsWith(".cs", StringComparison.Ordinal) && !named[(a.LastIndexOf('/') + 1)..].Contains('/')
            : string.Equals(named, a, StringComparison.Ordinal));

    /// <summary>Each file not on <paramref name="allowed"/> that names an engine type, as "path: the names".</summary>
    internal static string[] Offenders(IEnumerable<(string Named, string Source)> files, IReadOnlyList<string> allowed) =>
        files.Where(f => !IsAllowed(f.Named, allowed))
            .Select(f => (f.Named, Types: EngineTypesNamed(f.Source)))
            .Where(f => f.Types.Length > 0)
            .Select(f => $"{f.Named}: {string.Join(", ", f.Types)}")
            .Order(StringComparer.Ordinal).ToArray();

    /// <summary>Every .cs file under <paramref name="src"/> (bin and obj aside), as (path under src/ with '/', its text).</summary>
    internal static (string Named, string Source)[] SourceFiles(string src) =>
        Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (Path.GetRelativePath(src, f).Replace('\\', '/'), File.ReadAllText(f)))
            .ToArray();

    /// <summary>The exact entries of <paramref name="allowed"/> that no longer name an engine type, or are missing: the list must shrink with the code.</summary>
    internal static string[] StaleEntries(IEnumerable<(string Named, string Source)> files, IReadOnlyList<string> allowed)
    {
        var byName = files.ToDictionary(f => f.Named, f => f.Source, StringComparer.Ordinal);
        return allowed.Where(a => !a.EndsWith("*.cs", StringComparison.Ordinal))
            .Where(a => !byName.TryGetValue(a, out var source) || EngineTypesNamed(source).Length == 0)
            .ToArray();
    }
}
