using System.Text.RegularExpressions;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// Architecture test 6's scanner: which source files write to the local disk. A copy of the server behind a load balancer
/// does not share its disk with the others, so a file write is state only that copy has; each one is declared on an allowlist. Comments
/// are stripped first by the C# comment reader (<see cref="CSharpSource"/>), so a write named in a comment does not count. Shared with the
/// hosted twin (linked into Packmoat.Hosted.Architecture.Tests).
/// <para>Known limits, accepted: a FileInfo's or DirectoryInfo's own writes (Delete, Create, MoveTo...) are seen only on one made inline
/// (<c>new FileInfo(p).Delete()</c>) or on a name the same file declares as one (a variable, field or parameter typed FileInfo,
/// DirectoryInfo or FileSystemInfo, or a <c>var</c> given <c>new FileInfo</c>, <c>new DirectoryInfo</c> or <c>Directory.Create*</c>).
/// One reached another way (returned by a method, or through a property) is not; it would come with a path made by a write this scan
/// does see, so far. File.OpenRead and File.OpenText only read, and are not counted.</para>
/// </summary>
internal static class FileWriteScan
{
    // File's writes, a FileStream or StreamWriter on a path, a temp file, making or removing a directory, the cookie keys kept in a folder,
    // and SQLite opening a database file it may write or create (a connection's default mode is ReadWriteCreate).
    private static readonly Regex Write = new(
        @"\bFile\.(?:Write\w*|AppendAll\w*|AppendText|Create\w*|Move|Copy|Replace|Delete|Open(?!Read\b|Text\b)\w*|SetUnixFileMode|SetAttributes|SetLastWriteTime\w*)\b" +
        @"|\bnew\s+(?:FileStream|StreamWriter)\b|\bPath\.GetTempFileName\b|\bDirectory\.(?:Create\w*|Delete|Move)\b" +
        @"|\bPersistKeysToFileSystem\b|\bReadWriteCreate\b|\bSqliteOpenMode\.ReadWrite\b|\bnew\s+SqliteConnection\b|\bBackupDatabase\b",
        RegexOptions.CultureInvariant);

    private const string InfoWrites = @"(?:Delete|Create|CreateSubdirectory|CreateText|AppendText|OpenWrite|Open|MoveTo|CopyTo|Replace|Encrypt|Decrypt)";

    // A FileInfo or DirectoryInfo made and written in one expression: new FileInfo(path).Delete()
    private static readonly Regex InlineInfoWrite = new(
        @"\bnew\s+(?:FileInfo|DirectoryInfo)\s*\([^()]*(?:\([^()]*\)[^()]*)*\)\s*\.\s*(?<m>" + InfoWrites + @")\s*\(",
        RegexOptions.CultureInvariant);

    // A name the file declares as a FileInfo, DirectoryInfo or FileSystemInfo: typed, or a var given one.
    private static readonly Regex InfoName = new(
        @"\b(?:FileInfo|DirectoryInfo|FileSystemInfo)\??\s+(?<n>[A-Za-z_]\w*)\s*(?:[=;,)]|=>)" +
        @"|\bvar\s+(?<n>[A-Za-z_]\w*)\s*=\s*(?:new\s+(?:FileInfo|DirectoryInfo)\b|Directory\.Create\w*\s*\()",
        RegexOptions.CultureInvariant);

    /// <summary>The file writes <paramref name="source"/>'s code (comments aside) makes, in order, each once.</summary>
    internal static string[] WritesNamed(string source)
    {
        var code = CSharpSource.WithoutComments(source);
        var found = Write.Matches(code).Select(m => (m.Index, Name: Regex.Replace(m.Value, @"\s+", " "))).ToList();
        found.AddRange(InlineInfoWrite.Matches(code).Select(m => (m.Index, Name: "FileSystemInfo." + m.Groups["m"].Value)));
        foreach (var name in InfoName.Matches(code).Select(m => m.Groups["n"].Value).Distinct())
            found.AddRange(Regex.Matches(code, $@"(?<![\w.]){Regex.Escape(name)}\s*\.\s*(?<m>{InfoWrites})\s*\(")
                .Select(m => (m.Index, Name: "FileSystemInfo." + m.Groups["m"].Value)));
        return found.OrderBy(f => f.Index).Select(f => f.Name).Distinct().ToArray();
    }

    /// <summary>Each file not on <paramref name="allowed"/> that writes, as "path: the writes".</summary>
    internal static string[] Offenders(IEnumerable<(string Named, string Source)> files, IReadOnlyDictionary<string, string> allowed) =>
        files.Where(f => !allowed.ContainsKey(f.Named))
            .Select(f => (f.Named, Writes: WritesNamed(f.Source)))
            .Where(f => f.Writes.Length > 0)
            .Select(f => $"{f.Named}: {string.Join(", ", f.Writes)}")
            .Order(StringComparer.Ordinal).ToArray();

    /// <summary>The entries of <paramref name="allowed"/> that no longer write, or are missing: the list shrinks with the code.</summary>
    internal static string[] StaleEntries(IEnumerable<(string Named, string Source)> files, IReadOnlyDictionary<string, string> allowed)
    {
        var byName = files.ToDictionary(f => f.Named, f => f.Source, StringComparer.Ordinal);
        return allowed.Keys.Where(a => !byName.TryGetValue(a, out var source) || WritesNamed(source).Length == 0)
            .Order(StringComparer.Ordinal).ToArray();
    }
}
