using System.Text.RegularExpressions;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// The scanner of <c>PostgresCatchTests</c>: every catch in C# source that can stop a database error.
/// Kept apart from the tests, as the other scanners are, so the hosted architecture tests compile it in for the hosted PostgreSQL adapter. Its self-tests are in <c>PostgresCatchTests</c>.
/// </summary>
internal static class PostgresCatchScan
{
    /// <summary>A catch clause's head: the keyword, its declaration and any filter, up to the block's brace.</summary>
    private static readonly Regex CatchHead = new(@"\bcatch\b[^{]*", RegexOptions.CultureInvariant);

    private static readonly Regex DatabaseError = new(@"\b(PostgresException|NpgsqlException|DbException)\b", RegexOptions.CultureInvariant);

    /// <summary>A head that catches a database error without naming it: bare, or of Exception or one of DbException's bases.</summary>
    private static readonly Regex Broad = new(
        @"^catch(?:$|\s*\(\s*(?:global::)?(?:System\.)?(?:Runtime\.InteropServices\.)?(?:Exception|SystemException|ExternalException)\b)", RegexOptions.CultureInvariant);

    private static readonly Regex Filter = new(@"\bwhen\b", RegexOptions.CultureInvariant);

    /// <summary>Every flagged catch (see <c>PostgresCatchTests</c>) in the files' code (comments aside), as "file: head".</summary>
    internal static string[] DatabaseCatches(IEnumerable<(string Named, string Source)> files) =>
        files.SelectMany(f => Catches(EngineTypeScan.WithoutComments(f.Source))
                .Where(c => DatabaseError.IsMatch(c.Head) || Broad.IsMatch(c.Head) && (Filter.IsMatch(c.Head) || !Rethrows(c.Body)))
                .Select(c => $"{f.Named}: {c.Head}"))
            .Order(StringComparer.Ordinal).ToArray();

    /// <summary>Each catch in <paramref name="code"/>: its head (spaces as one) and its block's text between the braces.</summary>
    private static IEnumerable<(string Head, string Body)> Catches(string code)
    {
        foreach (Match m in CatchHead.Matches(code))
        {
            var open = m.Index + m.Length;
            yield return (Regex.Replace(m.Value, @"\s+", " ").Trim(), open < code.Length && code[open] == '{' ? Block(code, open) : "");
        }
    }

    /// <summary>The text inside the block whose '{' is at <paramref name="open"/>, to its matching '}' (or the end).</summary>
    private static string Block(string code, int open)
    {
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[(open + 1)..i];
        }
        return code[(open + 1)..];
    }

    /// <summary>
    /// Whether a catch block always rethrows what it caught, unchanged: it ends with <c>throw;</c> and nothing before that can leave the
    /// block another way (<c>if (e is PostgresException) return false; throw;</c> swallows the error it ends by rethrowing).
    /// </summary>
    private static bool Rethrows(string body) =>
        Regex.IsMatch(body, @"\bthrow\s*;\s*$", RegexOptions.CultureInvariant) && !Regex.IsMatch(body, @"\b(?:return|break|continue|goto|yield)\b", RegexOptions.CultureInvariant);
}
