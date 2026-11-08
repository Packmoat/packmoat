namespace Packmoat.Architecture.Tests;

/// <summary>
/// Reads C# source as the compiler sees its comments (#270, shared since #278): // to the end of the line, and /* to */. String literals
/// (regular, verbatim, interpolated with their holes, raw) and char literals are stepped over, so data, and a URL or a quote in data, is
/// never taken for a comment, and a comment marker in data never hides the code after it. Linked into the hosted architecture tests.
/// </summary>
internal static class CSharpSource
{
    /// <summary>The comments, as (line, text), one entry for each line a comment covers.</summary>
    internal static List<(int Line, string Text)> Comments(string text) => new CommentReader(text).Read().Found;

    /// <summary>Each comment whole, in order: a // comment to the end of its line, a /* comment to its */ (line breaks kept).</summary>
    internal static List<string> CommentTexts(string text) =>
        new CommentReader(text).Read().Spans.Select(s => text[s.Start..s.End]).ToList();

    /// <summary>The source with every comment taken out (a // comment's line break is kept); string and char literals are kept whole.</summary>
    internal static string WithoutComments(string text)
    {
        var spans = new CommentReader(text).Read().Spans;
        var code = new System.Text.StringBuilder(text.Length);
        var at = 0;
        foreach (var (start, end) in spans)
        {
            code.Append(text, at, start - at);
            at = end;
        }
        return code.Append(text, at, text.Length - at).ToString();
    }

    private sealed class CommentReader(string text)
    {
        private readonly List<(int, string)> _found = [];
        private readonly List<(int Start, int End)> _spans = [];
        private int _i;
        private int _line = 1;

        public (List<(int, string)> Found, List<(int Start, int End)> Spans) Read()
        {
            Code(inHole: false);
            return (_found, _spans);
        }

        private char At(int k) => k < text.Length ? text[k] : '\0';

        // Code, to the end, or (in an interpolation hole) to the } that closes the hole.
        private void Code(bool inHole)
        {
            var depth = 0;
            while (_i < text.Length)
            {
                var c = text[_i];
                if (c == '\n') { _line++; _i++; }
                else if (c == '/' && At(_i + 1) == '/')
                {
                    var start = _i;
                    while (_i < text.Length && text[_i] != '\n') _i++;
                    _found.Add((_line, text[start.._i]));
                    _spans.Add((start, _i));
                }
                else if (c == '/' && At(_i + 1) == '*')
                {
                    var start = _i;
                    var first = _line;
                    _i += 2;
                    while (_i < text.Length && !(text[_i] == '*' && At(_i + 1) == '/')) { if (text[_i] == '\n') _line++; _i++; }
                    _i = Math.Min(text.Length, _i + 2);
                    var lines = text[start.._i].Split('\n');
                    for (var k = 0; k < lines.Length; k++) _found.Add((first + k, lines[k]));
                    _spans.Add((start, _i));
                }
                else if (c == '\'') CharLiteral();
                else if (c == '"' || (c is '$' or '@' && StringAfterPrefix())) StringLiteral();
                else if (inHole && c == '{') { depth++; _i++; }
                else if (inHole && c == '}') { _i++; if (depth-- == 0) return; }
                else _i++;
            }
        }

        private bool StringAfterPrefix()
        {
            var k = _i;
            while (At(k) is '$' or '@') k++;
            return At(k) == '"';
        }

        private void CharLiteral()
        {
            _i++;
            _i += At(_i) == '\\' ? 2 : 1;
            while (_i < text.Length && text[_i] != '\'' && text[_i] != '\n') _i++;
            if (At(_i) == '\'') _i++;
        }

        private void StringLiteral()
        {
            var dollars = 0;
            var verbatim = false;
            while (At(_i) is '$' or '@') { if (text[_i] == '$') dollars++; else verbatim = true; _i++; }
            var quotes = 0;
            while (At(_i + quotes) == '"') quotes++;
            if (quotes >= 3 && !verbatim) { RawString(quotes, dollars); return; }   // @"""" is a verbatim string holding one quote
            _i++;   // the opening quote
            while (_i < text.Length)
            {
                var c = text[_i];
                if (c == '\n') { _line++; _i++; if (!verbatim) return; }   // a regular string cannot span lines
                else if (verbatim && c == '"') { _i++; if (At(_i) != '"') return; _i++; }   // "" is a quote inside
                else if (!verbatim && c == '\\') _i += At(_i + 1) == '\n' ? 1 : 2;
                else if (!verbatim && c == '"') { _i++; return; }
                else if (dollars > 0 && c == '{') { if (At(_i + 1) == '{') _i += 2; else { _i++; Code(inHole: true); } }
                else _i++;
            }
        }

        private void RawString(int quotes, int dollars)
        {
            _i += quotes;
            while (_i < text.Length)
            {
                var c = text[_i];
                if (c == '"')
                {
                    var run = 0;
                    while (At(_i + run) == '"') run++;
                    _i += run;
                    if (run >= quotes) return;
                }
                else if (dollars > 0 && c == '{')
                {
                    var run = 0;
                    while (At(_i + run) == '{') run++;
                    _i += run;
                    if (run >= dollars) Code(inHole: true);
                }
                else { if (c == '\n') _line++; _i++; }
            }
        }
    }
}
