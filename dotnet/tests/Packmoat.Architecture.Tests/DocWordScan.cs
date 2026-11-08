using System.Text.RegularExpressions;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// Architecture test 9's scanner (#278 step 0, A6): how many times a file's doc comments (<c>///</c>) name a storage mechanism instead of
/// the guarantee it gives: SQLite or a <c>Sqlite*</c> type, IMMEDIATE, WAL, PRAGMA, rowid, a trigger, a write lock (or write-locked), an
/// exclusive lock, the journal, a savepoint, busy_timeout. Both kinds of doc comment are read: <c>///</c> and <c>/** … */</c>. A port's doc says what
/// a caller can rely on ("no other writer can change it between the check and the write"); how one engine does it belongs to the adapter.
/// Comments are found by the C# comment reader (<see cref="CSharpSource"/>), so a word in a string is not counted. Shared with the hosted
/// twin (linked into Packmoat.Hosted.Architecture.Tests).
/// </summary>
internal static class DocWordScan
{
    private static readonly Regex Mechanism = new(
        @"(?i:sqlite\w*)|\bIMMEDIATE\b|\bWAL\b|(?i:\bpragma\b)|(?i:\browid\b)|(?i:\btriggers?\b)|(?i:\bwrite[- ]lock(?:s|ed)?\b)" +
        @"|(?i:\bexclusive[- ]locks?\b)|(?i:\bjournal\w*)|(?i:\bsavepoints?\b)|(?i:\bbusy_timeout\b)",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The mechanism words in <paramref name="source"/>'s doc comments, in order (one entry per occurrence). A doc comment is one that
    /// starts with exactly three slashes, or with <c>/**</c> (not the empty <c>/**/</c>), as the compiler reads them.
    /// </summary>
    internal static string[] MechanismWords(string source) =>
        CSharpSource.CommentTexts(source)
            .Where(IsDoc)
            .SelectMany(c => Mechanism.Matches(c).Select(m => m.Value))
            .ToArray();

    private static bool IsDoc(string comment) =>
        (comment.StartsWith("///", StringComparison.Ordinal) && !comment.StartsWith("////", StringComparison.Ordinal))
        || (comment.StartsWith("/**", StringComparison.Ordinal) && !comment.StartsWith("/**/", StringComparison.Ordinal));

    /// <summary>
    /// Each file whose count differs from <paramref name="pinned"/> (a file not on it is pinned at zero), as "path: found N, pinned M (the
    /// words)". Above the pin: say the guarantee instead. Below it: lower the pin, so it cannot creep back.
    /// </summary>
    internal static string[] OffPin(IEnumerable<(string Named, string Source)> files, IReadOnlyDictionary<string, int> pinned) =>
        files.Select(f => (f.Named, Words: MechanismWords(f.Source), Pin: pinned.GetValueOrDefault(f.Named)))
            .Where(f => f.Words.Length != f.Pin)
            .Select(f => $"{f.Named}: found {f.Words.Length}, pinned {f.Pin} ({string.Join(", ", f.Words)})")
            .Order(StringComparer.Ordinal).ToArray();

    /// <summary>The pins for files that are not among <paramref name="files"/> (moved or deleted): the list shrinks with the code.</summary>
    internal static string[] MissingPins(IEnumerable<(string Named, string Source)> files, IReadOnlyDictionary<string, int> pinned)
    {
        var have = files.Select(f => f.Named).ToHashSet(StringComparer.Ordinal);
        return pinned.Keys.Where(k => !have.Contains(k)).Order(StringComparer.Ordinal).ToArray();
    }
}
