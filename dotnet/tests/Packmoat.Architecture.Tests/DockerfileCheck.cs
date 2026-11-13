using System.Text.RegularExpressions;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// An image's check on what its build stage published, and the place that check must hold. A Dockerfile can keep the check's text and
/// still not run it, or run it and then undo it, so the text alone is not enough. This reads the Dockerfile and returns one line per
/// problem (empty when the check is sound): the check is on a line of its own, nothing can change how a RUN is run (SHELL, ONBUILD),
/// only blank and comment lines sit between the last publish and the check and between the check and the runtime stage, the build stage
/// is the one the runtime stage copies from, and the runtime stage holds only allowed instructions and copies exactly the two published folders (a drift guard, not a security boundary).
/// Shared with the hosted twin (linked into Packmoat.Hosted.Architecture.Tests), which applies it to the hosted image's Dockerfile.
/// </summary>
internal static class DockerfileCheck
{
    private const string RuntimeFrom = "\nFROM mcr.microsoft.com/dotnet/aspnet:";

    private static readonly Regex PublishStep = new(@"(?m)^[ \t]*(?:RUN\b.*\b|&&[ \t]+)dotnet publish ", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Allowed = ["FROM", "ARG", "ENV", "LABEL", "USER", "VOLUME", "EXPOSE", "HEALTHCHECK", "ENTRYPOINT", "CMD", "COPY", "RUN"];

    /// <summary>A command that removes, moves, rewrites or replaces a file (a cleanup of a package list is one too, and is let through when it names no /app path and has no cd).</summary>
    private static readonly Regex ChangesFiles = new(@"(?<![\w-])(rm|unlink|find|mv|cp|truncate|shred|dd|tee|ln|install|rsync|sed|perl|python\d*)(?![\w-])|-delete\b", RegexOptions.CultureInvariant);

    /// <summary>The instructions of a stage: lines ending in a backslash joined to the next, comment lines dropped.</summary>
    private static List<string> Instructions(string stage)
    {
        var result = new List<string>();
        var current = "";
        foreach (var raw in stage.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#')) continue;
            if (line.EndsWith('\\')) { current += line[..^1].TrimEnd() + " "; continue; }
            current += line;
            if (current.Trim().Length > 0) result.Add(current.Trim());
            current = "";
        }
        if (current.Trim().Length > 0) result.Add(current.Trim());
        return result;
    }

    public static string[] Problems(string dockerfile, string check)
    {
        var text = dockerfile.Replace("\r\n", "\n");
        var problems = new List<string>();
        var at = text.IndexOf(check, StringComparison.Ordinal);
        if (at < 0) return ["the Dockerfile no longer holds the check, word for word:\n" + check];
        if (text.LastIndexOf(check, StringComparison.Ordinal) != at) problems.Add("the check appears more than once");

        // The last publish step: a RUN line that publishes, or a chained "&& dotnet publish" line. A comment that says "dotnet publish " is not one.
        var publish = PublishStep.Matches(text).LastOrDefault()?.Index ?? -1;
        var runtime = text.IndexOf(RuntimeFrom, StringComparison.Ordinal);
        if (publish < 0 || runtime < 0) { problems.Add("the Dockerfile has no publish or no runtime stage"); return problems.ToArray(); }
        if (!(publish < at && at < runtime)) { problems.Add("the check must run in the build stage, after the last publish and before the runtime stage"); return problems.ToArray(); }

        if (at == 0 || text[at - 1] != '\n') problems.Add("the check does not start its own line (something before it on the line, such as ONBUILD, changes what it is)");

        foreach (Match m in Regex.Matches(text, @"(?im)^[ \t]*(SHELL|ONBUILD)\b.*$"))
            problems.Add("an instruction that changes how a RUN runs or defers it: " + m.Value.Trim());

        var buildFrom = Regex.Matches(text[..publish], @"(?m)^FROM\b.*$").LastOrDefault()?.Value;
        if (buildFrom is null || !Regex.IsMatch(buildFrom, @"\sAS build$")) problems.Add("the last publish is not in the stage named build, which the runtime stage copies from");

        var publishEnd = text.IndexOf('\n', publish);
        if (publishEnd < 0 || publishEnd >= at) problems.Add("the last publish line runs into the check");
        else
        {
            if (text[..publishEnd].TrimEnd().EndsWith('\\')) problems.Add("the last publish line continues onto the next line");
            Gap("between the last publish and the check", text[(publishEnd + 1)..at]);
        }

        var afterCheck = at + check.Length;
        Gap("between the check and the runtime stage", text[afterCheck..Math.Max(afterCheck, runtime)]);

        RuntimeStage(text[(runtime + 1)..]);
        return problems.ToArray();

        // A drift guard, not a security boundary: it catches the edits that would undo the check by accident or by a plain mistake.
        // The runtime stage is read as instructions (continuations joined, comments dropped, kinds in any case). Only the kinds in
        // Allowed may appear (WORKDIR and ADD are left out on purpose: neither is used, and ADD can fetch and unpack into /app).
        void RuntimeStage(string stage)
        {
            var froms = 0;
            var buildCopies = new List<string>();
            foreach (var instruction in Instructions(stage))
            {
                var kind = instruction.Split([' ', '\t'], 2)[0].ToUpperInvariant();
                var words = instruction.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (!Allowed.Contains(kind)) { problems.Add($"the runtime stage uses an instruction that is not on the allowed list ({string.Join(", ", Allowed)}): {instruction}"); continue; }
                if (kind == "FROM") froms++;
                if (kind == "COPY")
                {
                    var fromFlags = words.Where(w => w.StartsWith("--from", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (fromFlags.Length == 0)
                    {
                        var destination = words[^1].TrimEnd('/');
                        if (destination == "/app/host" || destination == "/app/admin" || destination.StartsWith("/app/host/", StringComparison.Ordinal) || destination.StartsWith("/app/admin/", StringComparison.Ordinal))
                            problems.Add("the runtime stage copies something other than the published output into the published folders: " + instruction);
                    }
                    else if (fromFlags.Length == 1 && string.Equals(fromFlags[0], "--from=build", StringComparison.OrdinalIgnoreCase)) buildCopies.Add(string.Join(' ', words).Replace("--FROM", "--from", StringComparison.OrdinalIgnoreCase));
                    else problems.Add("the runtime stage copies from a stage other than build: " + instruction);
                }
                if (kind == "RUN")
                {
                    if (instruction.Contains("<<", StringComparison.Ordinal)) problems.Add("the runtime stage has a RUN with a heredoc, which this reader cannot follow: " + instruction);
                    else if (ChangesFiles.IsMatch(instruction) && (instruction.Contains("/app", StringComparison.Ordinal) || Regex.IsMatch(instruction, @"\bcd\b")))
                        problems.Add("the runtime stage has a RUN that may change the published files: " + instruction);
                }
            }
            if (froms != 1) problems.Add($"there must be exactly one FROM after the check (the runtime stage, last); there are {froms}");
            string[] expected = ["COPY --from=build /out/host /app/host", "COPY --from=build /out/admin /app/admin"];
            if (!buildCopies.SequenceEqual(expected)) problems.Add("the runtime stage must copy from the build stage exactly " + string.Join(" and ", expected) + "; it has: " + string.Join(" | ", buildCopies));
        }

        void Gap(string where, string lines)
        {
            foreach (var l in lines.Split('\n').Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')))
                problems.Add($"a step {where}: {l.Trim()}");
        }
    }

    /// <summary>Ordinary edits that must stay legal: a package-list cleanup in the runtime stage, and a comment that mentions a publish.</summary>
    public static (string Name, string Text)[] LegalEdits(string dockerfile)
    {
        var tail = dockerfile.Replace("\r\n", "\n").TrimEnd('\n') + "\n";
        return
        [
            ("an apt cleanup RUN in the runtime stage", tail + "RUN apt-get update && apt-get install -y --no-install-recommends curl \\\n && rm -rf /var/lib/apt/lists/*\n"),
            ("a comment in the runtime stage that mentions dotnet publish ", tail + "# the build stage runs dotnet publish for both entry points\n"),
        ];
    }

    /// <summary>
    /// The holes the check closes, each made in a copy of the real Dockerfile. The check's text survives in every one but the first three, which
    /// is why a reader that only looks for the text lets them through. Throws if a change did not change the file (the Dockerfile moved on).
    /// </summary>
    public static (string Name, string Text)[] Holes(string dockerfile, string check)
    {
        var text = dockerfile.Replace("\r\n", "\n");
        var runtimeFrom = text.IndexOf(RuntimeFrom, StringComparison.Ordinal) + 1;
        var afterRuntimeFrom = text.IndexOf('\n', runtimeFrom) + 1;
        var without = text.Replace(check, "");
        var tail = text.TrimEnd('\n') + "\n";
        return
        [
            ("the check deleted", Changed(text, without)),
            ("the check moved above the publish", Changed(text, without.Insert(Line(without, "RUN dotnet publish "), check))),
            ("the check moved into the runtime stage", Changed(text, without.Insert(without.IndexOf('\n', without.IndexOf(RuntimeFrom, StringComparison.Ordinal) + 1) + 1, check))),
            ("ONBUILD before the check", Changed(text, text.Replace(check, "ONBUILD " + check))),
            ("SHELL set to /bin/true after the publish", Changed(text, text.Replace(check, "SHELL [\"/bin/true\"]\n" + check))),
            ("a lowercase shell instruction after the publish", Changed(text, text.Replace(check, "shell [\"/bin/true\"]\n" + check))),
            ("a stage opened between the publish and the check", Changed(text, text.Replace(check, "FROM build AS verify\n" + check))),
            ("a RUN deleting a DLL after the check", Changed(text, text.Replace(check, check + "RUN rm /out/host/Npgsql.dll\n"))),
            ("a RUN deleting a DLL in the runtime stage", Changed(text, text.TrimEnd('\n') + "\nRUN rm -f /app/host/Npgsql.dll\n")),
            ("a third folder copied into the runtime stage", Changed(text, text.Insert(afterRuntimeFrom, "COPY --from=build /src /src\n"))),
            ("the publish line continued onto the check", Changed(text, text.Insert(text.IndexOf('\n', text.LastIndexOf("dotnet publish ", StringComparison.Ordinal)), " \\"))),
            ("a find -delete of a DLL in the runtime stage", Changed(text, tail + "RUN find /app/host -name Npgsql.dll -delete\n")),
            ("a COPY with the flag in capitals, over a DLL", Changed(text, tail + "COPY --FROM=build /src/other.dll /app/host/Npgsql.dll\n")),
            ("a COPY from the build context over a DLL", Changed(text, tail + "COPY evil.dll /app/host/Npgsql.dll\n")),
            ("a COPY from a stage by number", Changed(text, tail + "COPY --from=0 /src/other.dll /app/host/Npgsql.dll\n")),
            ("a heredoc RUN moving a DLL", Changed(text, tail + "RUN <<EOF\nmv /app/host/Npgsql.dll /root/\nEOF\n")),
            ("an extra final stage that copies only the admin folder", Changed(text, tail + "FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final\nCOPY --from=build /out/admin /app/admin\n")),
        ];
        static int Line(string s, string start) => s.IndexOf(start, StringComparison.Ordinal) is var i and >= 0 ? i : throw new InvalidOperationException("no line starts " + start);
        static string Changed(string before, string after) => after == before ? throw new InvalidOperationException("a hole did not change the Dockerfile") : after;
    }
}
