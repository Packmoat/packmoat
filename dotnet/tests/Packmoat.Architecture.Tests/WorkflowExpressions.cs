using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// Where a workflow's <c>${{ … }}</c> expressions may go. Actions pastes an expression's value into the text before it runs, so a
/// value an outsider wrote (a pull request title, a branch name) becomes code in a run script or a <c>script:</c> input. The safe route is
/// <c>env:</c>, which this does not check: the script then reads a quoted variable.
/// <list type="bullet">
/// <item>The workflow is read with a YAML parser, so quoting, flow collections, explicit keys, escapes, line continuations and
/// aliases all reach the check as the values Actions sees. A workflow the parser cannot read is a problem, never an empty result.</item>
/// <item>Code may hold only the expressions in <see cref="RunAllowed"/>; anything else fails, so a new form fails by default. Code is a
/// <c>run:</c> or <c>shell:</c> value, and the <c>script</c>, <c>args</c> and <c>entrypoint</c> inputs (actions/github-script, and a
/// container action's command line).</item>
/// <item><c>with:</c> must be a mapping of text values. Every other value in the workflow (the other inputs, a <c>working-directory:</c>,
/// a container or service <c>image:</c>, ...) may not hold the event payload, the head branch, dispatch inputs, or the whole
/// <c>github</c> context (<c>toJSON(github)</c>, <c>github['…']</c>, <c>github.*</c>). These only block an outsider's text read
/// directly: text passed on through a step's, a job's or env: outputs is not traced.</item>
/// <item><c>env:</c>, <c>if:</c> and <c>concurrency:</c> are not checked, but only where Actions reads them as those settings: the
/// workflow (<c>env</c>, <c>concurrency</c>), a job (all three), a step (<c>env</c>, <c>if</c>), and a job's container or a service
/// (<c>env</c>). The same name anywhere else (a job id, a matrix key, a service id) is checked like any other key.</item>
/// <item>Each value is checked as a whole, so an expression split across lines is still read. An expression with a quote in it fails:
/// a <c>}}</c> inside a string would end this reader's match early.</item>
/// <item>Two line checks stay as a second layer, in case GitHub's YAML reader differs from this one: a YAML escape for <c>$</c>
/// (<c>\x24</c>, <c>$</c>, <c>\U00000024</c>), and an anchor or alias (<c>&amp;a</c>, <c>*a</c>).</item>
/// </list>
/// Used by CoreWorkflowTests; the private repository also compiles this file into its own pipeline tests for its root workflows, so it
/// must depend on nothing but the base library and YamlDotNet, which both test projects reference at the same version.
/// </summary>
internal static class WorkflowExpressions
{
    /// <summary>The only expressions code may hold: values the workflow, not an outsider, decides.</summary>
    /// <remarks>Not <c>matrix.&lt;name&gt;</c>: a matrix can be built from another job's output, <c>matrix: ${{ fromJSON(…) }}</c>.</remarks>
    internal const string RunAllowedNames = "runner.temp, github.ref, github.sha, github.repository";
    private static readonly Regex RunAllowed = new(@"^(runner\.temp|github\.ref|github\.sha|github\.repository)$", RegexOptions.CultureInvariant);

    // Expressions are case-insensitive in Actions, so the blocklist is too. A bare "github" (not followed by a dot) is the whole context,
    // as in toJSON(github), or a bracket form, as in github['head_ref'] or github["event"]. github.* is an object filter over the whole
    // context, as in github.*.head_ref or toJSON(github.*).
    private static readonly Regex Blocked = new(
        @"\bgithub\s*\.\s*event\b|\bgithub\s*\.\s*head_ref\b|\bgithub\s*\.\s*\*|\binputs\s*[.\[]|\bgithub\b(?!\s*\.)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Singleline: a value is checked whole, so an expression may run across lines.
    private static readonly Regex Expression = new(@"\$\{\{(.*?)\}\}", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>The keys whose values are code, and the inputs that are. Actions reads input names without regard to case, so these
    /// compare that way.</summary>
    private static readonly string[] CodeKeys = ["run", "shell"], CodeInputs = ["script", "args", "entrypoint"];

    /// <summary>The mappings whose <c>env:</c>, <c>if:</c> or <c>concurrency:</c> are settings, by path, and which of the three each
    /// has. A job id, a step and a service id are one segment each; nothing else is exempt.</summary>
    private static readonly (Regex Path, string[] Keys)[] Unchecked =
    [
        (new(@"^$"), ["env", "concurrency"]),
        (new(@"^jobs\.[^.\[]+$"), ["env", "if", "concurrency"]),
        (new(@"^jobs\.[^.\[]+\.steps\[\d+\]$"), ["env", "if"]),
        (new(@"^jobs\.[^.\[]+\.container$"), ["env"]),
        (new(@"^jobs\.[^.\[]+\.services\.[^.\[]+$"), ["env"]),
    ];

    private static bool IsUnchecked(string path, string name) => Unchecked.Any(u => u.Path.IsMatch(path) && u.Keys.Contains(name, StringComparer.Ordinal));

    /// <summary>Deeper than any workflow goes; a guard against a cycle of aliases.</summary>
    private const int MaxDepth = 64;

    private static bool Is(string name, string[] names) => names.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>A value on one line, for a message.</summary>
    private static string Show(string text)
    {
        var one = Regex.Replace(text.Trim(), @"\s+", " ");
        return one.Length <= 160 ? one : one[..160] + " …";
    }

    private static string At(YamlNode node, string path) => $"line {node.Start.Line} ({(path.Length == 0 ? "the document" : path)})";

    /// <summary>The problems with one text value: <paramref name="code"/> takes the allow-list, anything else the blocklist.</summary>
    private static IEnumerable<string> Check(YamlScalarNode node, string path, string where, bool code)
    {
        var text = node.Value ?? "";
        foreach (var e in Expression.Matches(text).Select(m => m.Groups[1].Value.Trim()))
        {
            if (e.Contains('\'') || e.Contains('"'))
                yield return $"{At(node, path)}: ${{{{ {Show(e)} }}}} in {where} has a quote in it, which this check cannot read: {Show(text)}";
            else if (code && !RunAllowed.IsMatch(e))
                yield return $"{At(node, path)}: ${{{{ {Show(e)} }}}} in {where} is not one of {RunAllowedNames} (pass it through env: and quote it): {Show(text)}";
            else if (!code && Blocked.IsMatch(e))
                yield return $"{At(node, path)}: an outsider's text in {where} (pass it through env:): {Show(text)}";
        }
        if (Expression.Replace(text, "").Contains("${{", StringComparison.Ordinal))
            yield return $"{At(node, path)}: an expression not closed, in {where}: {Show(text)}";
    }

    /// <summary>
    /// Every value under <paramref name="node"/>, at every depth, aliases resolved. A text value takes the rule of the key it is under
    /// (<paramref name="where"/>, <paramref name="code"/>); a sequence passes that rule to its items; a mapping sets the rule again for each
    /// of its keys.
    /// </summary>
    private static void Walk(YamlNode node, string path, string where, bool code, int depth, List<string> found)
    {
        if (depth > MaxDepth) { found.Add($"{At(node, path)}: nested deeper than {MaxDepth}, which this check cannot read"); return; }
        switch (node)
        {
            case YamlScalarNode scalar:
                found.AddRange(Check(scalar, path, where, code));
                break;
            case YamlSequenceNode sequence:
                foreach (var (item, i) in sequence.Children.Select((n, i) => (n, i))) Walk(item, $"{path}[{i}]", where, code, depth + 1, found);
                break;
            case YamlMappingNode mapping:
                foreach (var (key, value) in mapping.Children)
                {
                    if (key is not YamlScalarNode { Value: { } name })
                    {
                        found.Add($"{At(key, path)}: a key that is not text, which this check cannot read");
                        continue;
                    }
                    var under = path.Length == 0 ? name : $"{path}.{name}";
                    if (IsUnchecked(path, name)) continue;
                    if (name.Equals("with", StringComparison.OrdinalIgnoreCase)) With(value, under, found);
                    else Walk(value, under, $"a {name}: value", Is(name, CodeKeys), depth + 1, found);
                }
                break;
        }
    }

    /// <summary>A <c>with:</c> block: a mapping of text inputs; <see cref="CodeInputs"/> are code, the rest take the blocklist.</summary>
    private static void With(YamlNode node, string path, List<string> found)
    {
        if (node is not YamlMappingNode inputs)
        {
            found.Add($"{At(node, path)}: with: is not a mapping of inputs, which this check cannot read");
            return;
        }
        foreach (var (key, value) in inputs.Children)
        {
            if (key is not YamlScalarNode { Value: { } name }) { found.Add($"{At(key, path)}: a key that is not text, which this check cannot read"); continue; }
            var under = $"{path}.{name}";
            if (value is YamlScalarNode text) found.AddRange(Check(text, under, $"the with: input {name}", Is(name, CodeInputs)));
            else found.Add($"{At(value, under)}: the with: input {name} is not text, which this check cannot read");
        }
    }

    /// <summary>Every expression in a workflow that is somewhere it may not be, and every form this check cannot read, one line each.</summary>
    internal static IReadOnlyList<string> Problems(string workflow)
    {
        var found = new List<string>();
        foreach (var (line, number) in workflow.Split('\n').Select((l, i) => (l.TrimEnd('\r'), i + 1)))
        {
            void Add(string what) => found.Add($"line {number}: {what}, which this check cannot read: {line.Trim()}");
            if (Regex.IsMatch(line, @"\\(x24|u0024|U00000024)", RegexOptions.IgnoreCase)) Add("a YAML escape for $");
            if (Regex.IsMatch(line, @"(^\s*(-\s+)?|:\s+|^\s*\?\s+|[\[{,]\s*)[&*][\w-]+")) Add("a YAML anchor or alias");
        }

        var stream = new YamlStream();
        try { stream.Load(new StringReader(workflow)); }
        catch (YamlException e)
        {
            found.Add($"line {e.Start.Line}: the workflow is not YAML this check can read: {Show(e.InnerException?.Message ?? e.Message)}");
            return found;
        }
        if (stream.Documents.Count != 1) found.Add($"{stream.Documents.Count} YAML documents, not 1, which this check cannot read");
        foreach (var document in stream.Documents) Walk(document.RootNode, "", "the workflow", code: false, 0, found);
        return found;
    }
}
