using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// The core's own workflows, core/.github/workflows: the public repository runs them with core/ as its root, so they may name only
/// paths inside core/. They cannot run from a subfolder here, so these checks stand in until they do. They also keep the pipeline rules
/// the private repository's workflows keep: actions pinned to full commit SHAs, a read-only token, no secrets, no untrusted triggers.
/// </summary>
public class CoreWorkflowTests
{
    /// <summary>core/: the folder that holds dotnet/ (found by dotnet/Packmoat.sln).</summary>
    private static string CoreRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Packmoat.sln"))) dir = dir.Parent;
        return dir?.Parent?.FullName ?? throw new InvalidOperationException("Packmoat.sln not found above the test output folder.");
    }

    // Every file in the folder, whatever its extension: Actions runs .yaml as well as .yml, and the exact-list test fails on anything new.
    private static IEnumerable<(string Name, string Text)> Workflows() =>
        Directory.GetFiles(Path.Combine(CoreRoot(), ".github", "workflows")).Order(StringComparer.Ordinal)
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)));

    private static bool InCore(string root, string relative) =>
        File.Exists(Path.Combine(root, relative)) || Directory.Exists(Path.Combine(root, relative));

    // A relative path: a name, a slash and more, not part of a variable ($RUNNER_TEMP/x), an expression (${{ runner.temp }}/x), a URL or
    // an absolute path (/app/host/x).
    private static readonly Regex RelativePath = new(@"(?<![\w.$}{/~:-])(?:\./)?([\w.-]+)/[\w./*-]*", RegexOptions.CultureInvariant);
    // A .. segment: two dots with no other dot or name character on either side, so "cd ..;", "..&&", "../x" and "$X/.." are all caught.
    private static readonly Regex ParentSegment = new(@"(?<![\w.-])\.\.(?![\w.-])", RegexOptions.CultureInvariant);
    // First segments that name no folder in core-image.yml, and only there: a registry host (ghcr.io/x/y, docker.io/x/y), a
    // platform (linux/amd64), a git ref (refs/heads/main) and the repository its guard names (Packmoat/packmoat, pinned by PublisherProblems).
    private static readonly string[] PublisherNotFolders = ["docker.io", "ghcr.io", "linux", "refs", "Packmoat"];
    private static readonly Regex PathKey = new(@"^\s*-?\s*(working-directory|global-json-file|cache-dependency-path|path):\s*['""]?([^'""\s]+)", RegexOptions.CultureInvariant);

    /// <summary>
    /// What a workflow names outside <paramref name="root"/>, one line each: a <c>..</c> segment; a relative path whose first segment is
    /// in neither the root nor one of the workflow's working directories; a working-directory or global-json-file that is not in the root;
    /// and the word "hosted" anywhere, comments included. Comments are otherwise not read, and neither is an action reference (<c>uses:</c>) or a service's container image (<c>image:</c>).
    /// A first segment in <paramref name="notFolders"/> is not a path; only <see cref="Publisher"/> is given any (<see cref="PublisherNotFolders"/>).
    /// </summary>
    internal static IReadOnlyList<string> OutsidePaths(string workflow, string root, IReadOnlyCollection<string>? notFolders = null)
    {
        notFolders ??= [];
        var found = new List<string>();
        // A step's paths are relative to its working directory. Every working directory must itself be in the root, with no .. segment,
        // so a path found under one is in the root too.
        var bases = workflow.Split('\n').Select(l => PathKey.Match(l))
            .Where(m => m.Success && m.Groups[1].Value == "working-directory" && !m.Groups[2].Value.StartsWith('$'))
            .Select(m => m.Groups[2].Value).Prepend("").Distinct().ToArray();
        foreach (var (raw, number) in workflow.Split('\n').Select((l, i) => (l.TrimEnd('\r'), i + 1)))
        {
            if (raw.Contains("hosted", StringComparison.OrdinalIgnoreCase)) found.Add($"line {number}: names the hosted service: {raw.Trim()}");
            var line = Regex.Replace(raw, @"(^|\s)#.*$", "");
            if (Regex.IsMatch(line, @"^\s*-?\s*(uses|image):")) continue;   // an action or a container image, not a path
            if (ParentSegment.IsMatch(line)) found.Add($"line {number}: a .. segment: {line.Trim()}");
            foreach (Match m in RelativePath.Matches(line))
                if (m.Groups[1].Value != ".." && !notFolders.Contains(m.Groups[1].Value) &&!bases.Any(b => InCore(Path.Combine(root, b), m.Groups[1].Value))) found.Add($"line {number}: {m.Value} is not in the repository");
            var key = PathKey.Match(line);
            if (key.Success && !key.Groups[2].Value.StartsWith('$'))
            {
                var value = key.Groups[2].Value.StartsWith("./", StringComparison.Ordinal) ? key.Groups[2].Value[2..] : key.Groups[2].Value;
                if (!InCore(root, value)) found.Add($"line {number}: {key.Groups[1].Value} {value} is not in the repository");
            }
        }
        return found;
    }

    [Fact]   // mutation: a release.yml added
    public void The_core_has_its_codeql_conformance_image_dotnet_gitleaks_helm_and_scorecard_workflows() =>
        Assert.Equal(["codeql.yml", "conformance.yml", "core-image.yml", "dotnet.yml", "gitleaks.yml", "helm.yml", "scorecard.yml"],
            Workflows().Select(w => w.Name).ToArray());

    [Fact]   // mutation: working-directory: ../hosted/dotnet in core/.github/workflows/dotnet.yml
    public void The_core_workflows_name_only_paths_inside_core()
    {
        var root = CoreRoot();
        var offenders = Workflows().SelectMany(w => OutsidePaths(w.Text, root, w.Name == Publisher ? PublisherNotFolders : null)
            .Select(o => $"{w.Name} {o}")).ToArray();
        Assert.True(offenders.Length == 0, "core/.github/workflows runs with core/ as the repository root; these name something outside it:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_path_check_catches_a_path_outside_and_accepts_the_paths_inside()
    {
        var root = Directory.CreateTempSubdirectory("packmoat-workflows-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "dotnet"));
            Directory.CreateDirectory(Path.Combine(root, "spec", "check"));
            Directory.CreateDirectory(Path.Combine(root, ".github", "scripts"));
            File.WriteAllText(Path.Combine(root, "dotnet", "global.json"), "{}");
            Directory.CreateDirectory(Path.Combine(root, "dotnet", "tests"));

            const string inside = """
                      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
                      - uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68 # see ../elsewhere and deploy/ in a comment
                        with:
                          global-json-file: dotnet/global.json
                      - name: The image builds from dotnet/ (not pushed)
                        working-directory: dotnet
                        run: dotnet test tests/x.csproj
                      - working-directory: ./spec/check
                        run: dotnet test x --results-directory "$RUNNER_TEMP/results" && cp a ${{ runner.temp }}/out/b
                      - run: .github/scripts/image-smoke.sh packmoat-core:ci /app/host/Packmoat.Server.dll && curl https://example.com/a/b
                        working-directory: ${{ runner.temp }}/x
                """;
            Assert.Empty(OutsidePaths(inside, root));
            // In core-image.yml only, an image name, a platform, a git ref and the guard's repository are not paths.
            const string publisher = """
                        if: github.repository == 'Packmoat/packmoat' && github.ref == 'refs/heads/main'
                        env:
                          IMAGE: ghcr.io/packmoat/packmoat-core
                          image: docker.io/tonistiigi/binfmt:qemu
                          platforms: linux/amd64,linux/arm64
                        run: cosign verify --certificate-identity "https://github.com/${GITHUB_REPOSITORY}/.github/workflows/x.yml@refs/heads/main"
                """;
            Assert.Empty(OutsidePaths(publisher, root, PublisherNotFolders));
            Assert.Equal(6, OutsidePaths(publisher, root).Count);                         // in any other workflow each is a path outside, but the image: line
            // An image: line names a container image (a service's, or an action's input), never a path.
            Assert.Empty(OutsidePaths("        image: edoburu/pgbouncer@sha256:8638aa563607256d3c7440437534195f1066a92b68015d89ae9d6b935b444a6d\n", root));
            Assert.Single(OutsidePaths("        run: docker run edoburu/pgbouncer\n", root));
            Assert.Single(OutsidePaths("        run: dotnet build linux/Packmoat.sln\n", root));
            Assert.Single(OutsidePaths("        if: hashFiles('../x/**') != ''\n", root), o => o.Contains("a .. segment"));   // a condition is read too
            Assert.Single(OutsidePaths("        if: hashFiles('../x/**') != ''\n", root, PublisherNotFolders), o => o.Contains("a .. segment"));
            Assert.Single(OutsidePaths("        if: hashFiles('deploy/x') != ''\n", root));

            Assert.Single(OutsidePaths("        working-directory: ../hosted\n", root), o => o.Contains("a .. segment"));
            Assert.Single(OutsidePaths("        run: cp -R ../x .\n", root));
            Assert.Single(OutsidePaths("        run: dotnet build ./../x\n", root));
            Assert.Single(OutsidePaths("        run: cd ..; x\n", root));
            Assert.Single(OutsidePaths("        run: cd ..&& x\n", root));
            Assert.Single(OutsidePaths("        run: (cd ..)\n", root));
            Assert.Single(OutsidePaths("        run: cd \"$RUNNER_TEMP\"/.. && ls\n", root));
            Assert.Empty(OutsidePaths("        - name: Wait... then a...b and x..y\n", root));
            Assert.Single(OutsidePaths("        global-json-file: global.json\n", root));                 // the root's, not dotnet/'s
            Assert.Single(OutsidePaths("        run: docker build -f deploy/Dockerfile .\n", root));
            Assert.Single(OutsidePaths("        run: dotnet test tests/x.csproj\n", root));               // no working directory holds tests/
            // The private repository's folder name, put together so that this file names no such path itself (CorePathTests).
            Assert.Equal(2, OutsidePaths("        working-directory: " + "core" + "/dotnet\n", root).Count);   // the path, and the working directory
            Assert.Single(OutsidePaths("        run: dotnet build Packmoat.Hosted.sln\n", root));
            Assert.Single(OutsidePaths("        # the hosted image\n", root));
        }
        catch when (TempDir.Failing(root)) { throw; }
        finally { TempDir.Delete(root); }
    }

    [Fact]
    public void Every_action_in_a_core_workflow_is_pinned_to_a_full_commit_sha()
    {
        var offenders = Workflows().SelectMany(w =>
            Regex.Matches(w.Text, @"^\s*-?\s*uses:\s*(\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value)
                .Where(u => !u.StartsWith("./", StringComparison.Ordinal) && !Regex.IsMatch(u, @"@[0-9a-f]{40}$")).Select(u => $"{w.Name}: {u}")).ToArray();
        Assert.True(offenders.Length == 0, "Unpinned actions:\n" + string.Join("\n", offenders));
    }

    /// <summary>The only events a core workflow may run on. Anything else (issue_comment, pull_request_target, workflow_run, ...) can run
    /// with more than a pull request's read-only token, or on text an outsider wrote.</summary>
    private static readonly string[] AllowedTriggers = ["pull_request", "push", "workflow_dispatch"];

    /// <summary>
    /// The two scanners' extra events, neither of which an outsider can start: <c>schedule</c> runs main's workflow, and
    /// <c>branch_protection_rule</c> runs it when an administrator changes a rule. <see cref="ScannerProblems"/> pins each scanner's whole list.
    /// </summary>
    private static readonly Dictionary<string, string[]> ExtraTriggers = new()
    {
        ["codeql.yml"] = ["schedule"],
        ["scorecard.yml"] = ["schedule", "branch_protection_rule"],
    };

    /// <summary>Workflows allowed a cache (actions/cache, or a setup action's cache input). None: a cache is shared state an outside pull
    /// request could poison, and the builds are small.</summary>
    private static readonly string[] AllowedCaches = [];

    private static readonly Regex SecretReference = new(@"\bsecrets\s*(\.|\[)|\bgithub\s*\.\s*token\b|secrets:\s*inherit", RegexOptions.CultureInvariant);

    /// <summary>The events in a workflow's <c>on:</c>, in any of its three forms (a name, a list, or a map of names).</summary>
    internal static IReadOnlyList<string> Triggers(string workflow)
    {
        var lines = workflow.Split('\n').Select(l => Regex.Replace(l.TrimEnd('\r'), @"(^|\s)#.*$", "")).ToArray();
        var at = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^[""']?on[""']?\s*:"));
        if (at < 0) return [];
        var inline = lines[at][(lines[at].IndexOf(':') + 1)..].Trim();
        if (inline.Length > 0) return Regex.Matches(inline, @"[\w-]+").Select(m => m.Value).ToArray();
        var events = new List<string>();
        int? indent = null;
        foreach (var line in lines.Skip(at + 1))
        {
            if (line.Trim().Length == 0) continue;
            var width = line.Length - line.TrimStart().Length;
            if (width == 0) break;
            indent ??= width;
            if (width != indent) continue;
            var m = Regex.Match(line, @"^\s*-?\s*[""']?([\w-]+)[""']?\s*:?");
            if (m.Success) events.Add(m.Groups[1].Value);
        }
        return events;
    }

    /// <summary>
    /// What a workflow does that an outside pull request could abuse, one line each: a trigger not in <see cref="AllowedTriggers"/>; any
    /// secret, the built-in token included; an expression where an outsider's text could become code
    /// (<see cref="WorkflowExpressions.Problems"/>); a cache, unless the workflow is in <see cref="AllowedCaches"/>; and more than a read-only token.
    /// </summary>
    internal static IReadOnlyList<string> HygieneProblems(string name, string workflow)
    {
        var found = new List<string>();
        var triggers = Triggers(workflow);
        if (triggers.Count == 0) found.Add("no on: triggers found");
        var allowed = AllowedTriggers.Concat(ExtraTriggers.GetValueOrDefault(name, [])).ToArray();
        found.AddRange(triggers.Where(t => !allowed.Contains(t)).Select(t => $"trigger {t} is not one of {string.Join(", ", allowed)}"));

        if (!Regex.IsMatch(workflow, @"^permissions:\s*\n\s+contents:\s*read\s*\n(?!\s)", RegexOptions.Multiline)) found.Add("the top-level permissions are not exactly contents: read");
        foreach (var bad in new[] { ": write", "write-all", "id-token", "push: true" })   // push: true: core-image.yml publishes, by digest only
            if (workflow.Contains(bad, StringComparison.Ordinal)) found.Add($"says {bad}");

        foreach (var (raw, number) in workflow.Split('\n').Select((l, i) => (l.TrimEnd('\r'), i + 1)))
        {
            if (SecretReference.IsMatch(raw)) found.Add($"line {number}: a secret or token: {raw.Trim()}");
            if (AllowedCaches.Contains(name)) continue;
            if (Regex.IsMatch(raw, @"^\s*-?\s*cache:")) found.Add($"line {number}: a cache: {raw.Trim()}");
            if (Regex.IsMatch(raw, @"^\s*-?\s*uses:\s*actions/cache\b")) found.Add($"line {number}: actions/cache: {raw.Trim()}");
        }
        found.AddRange(WorkflowExpressions.Problems(workflow));
        return found;
    }

    /// <summary>The one workflow that publishes: the signed core image, only from main in the public repository, never on a pull request.</summary>
    private const string Publisher = "core-image.yml";

    /// <summary>
    /// The hygiene problems <see cref="Publisher"/> may have, because publishing needs them: a write permission, an identity token, and the
    /// built-in token as the registry login's password. <see cref="PublisherProblems"/> pins each to exactly that use.
    /// </summary>
    private static bool PublisherMayHave(string problem) =>
        problem is "says : write" or "says id-token"
        || Regex.IsMatch(problem, @"^line \d+: a secret or token: password: \$\{\{ secrets\.GITHUB_TOKEN \}\}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The workflows that may write anything, each with the one job that may and that job's whole permissions block, exactly: the
    /// publisher (packages for the image, an identity token to sign, attestations for its SBOM and provenance); CodeQL (security events, to
    /// upload its findings); and Scorecard (security events, and an identity token to publish its results). See <see cref="WriterProblems"/>.
    /// </summary>
    private static readonly Dictionary<string, (string Job, string[] Permissions)> Writers = new()
    {
        [Publisher] = ("publish", ["contents: read", "packages: write", "id-token: write", "attestations: write"]),
        ["codeql.yml"] = ("codeql", ["contents: read", "security-events: write"]),
        ["scorecard.yml"] = ("scorecard", ["contents: read", "security-events: write", "id-token: write"]),
    };

    /// <summary>
    /// What a workflow in <see cref="Writers"/> may write beyond its entry, one line each: permissions anywhere but the top (exactly
    /// contents read) and its one job; that job's block not exactly its entry; a write or an identity token said anywhere else, comments
    /// included. Any other workflow may write nothing at all (<see cref="HygieneProblems"/>).
    /// </summary>
    internal static IReadOnlyList<string> WriterProblems(string name, string workflow)
    {
        if (!Writers.TryGetValue(name, out var writer)) return [];
        var found = new List<string>();
        var lines = workflow.Replace("\r\n", "\n").Split('\n');
        var text = string.Join('\n', lines);
        if (!Regex.IsMatch(text, @"(?m)^permissions:\n  contents: read\n(?![ \t])")) found.Add("the top-level permissions are not exactly contents: read");
        if (Regex.Matches(text, @"(?m)^\s*permissions:").Count != 2) found.Add("permissions are set somewhere other than the top and one job");
        var at = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^    permissions:\s*$"));
        if (at < 0) found.Add($"the {writer.Job} job sets no permissions of its own");
        else
        {
            var job = lines.Take(at).LastOrDefault(l => Regex.IsMatch(l, @"^  [\w-]+:\s*$"))?.Trim().TrimEnd(':');
            if (job != writer.Job) found.Add($"the job with permissions is {job}, not {writer.Job}");
            var block = lines.Skip(at + 1).TakeWhile(l => l.StartsWith("      ", StringComparison.Ordinal)).Select(l => l.Trim()).ToArray();
            if (!block.SequenceEqual(writer.Permissions))
                found.Add($"the {writer.Job} job's permissions are not exactly {string.Join(", ", writer.Permissions)}: they are {string.Join(", ", block)}");
        }
        if (Regex.Matches(text, ": write").Count != writer.Permissions.Count(p => p.EndsWith(": write", StringComparison.Ordinal)))
            found.Add("a write is said somewhere other than the job's permissions");
        if (Regex.Matches(text, "id-token").Count != writer.Permissions.Count(p => p.StartsWith("id-token:", StringComparison.Ordinal)))
            found.Add("id-token is said somewhere other than the job's permissions");
        if (text.Contains("write-all", StringComparison.Ordinal)) found.Add("says write-all");
        return found;
    }

    /// <summary>The hygiene problems a workflow in <see cref="Writers"/> may have, because <see cref="WriterProblems"/> pins them: a write,
    /// an identity token if its entry has one, and for the publisher the registry login's password (<see cref="PublisherMayHave"/>).</summary>
    private static bool MayHave(string name, string problem) =>
        Writers.TryGetValue(name, out var w)
        && (problem == "says : write" || (problem == "says id-token" && w.Permissions.Contains("id-token: write")) || (name == Publisher && PublisherMayHave(problem)));

    [Fact]   // mutations: attestations: write in codeql.yml; contents: write in scorecard.yml; id-token: write in codeql.yml
    public void Only_the_publisher_and_the_scanners_may_write_and_each_exactly_what_its_one_job_needs() =>
        Assert.All(Workflows(), w => Assert.True(WriterProblems(w.Name, w.Text).Count == 0, $"{w.Name}:\n" + string.Join("\n", WriterProblems(w.Name, w.Text))));

    [Fact]
    public void The_writer_check_catches_each_wider_permission()
    {
        foreach (var (name, (job, permissions)) in Writers)
        {
            var text = Workflows().Single(w => w.Name == name).Text.Replace("\r\n", "\n");
            Assert.Empty(WriterProblems(name, text));
            void Catches(string from, string to, string expect) => CatchesIn(t => WriterProblems(name, t), text, from, to, expect);
            var last = "      " + permissions[^1] + "\n";
            Catches(last, last + "      actions: write\n", "permissions are not exactly");
            Catches(last, last + "      attestations: read\n", "permissions are not exactly");
            Catches("      contents: read\n", "      contents: write\n", "permissions are not exactly");
            Catches("      contents: read\n", "", "permissions are not exactly");
            Catches("permissions:\n  contents: read\n", "permissions:\n  contents: read\n  security-events: write\n", "the top-level permissions");
            Catches("permissions:\n  contents: read\n", "permissions: write-all\n", "write-all");
            Catches("jobs:\n", "jobs:\n  second:\n    runs-on: ubuntu-24.04\n    permissions:\n      contents: read\n", "permissions are set somewhere other");
            Catches($"\n  {job}:\n", "\n  renamed:\n", $"not {job}");
            Catches("      - uses: actions/checkout@", "      - env:\n          X: \"id-token\"\n        uses: actions/checkout@", "id-token is said");
            Catches("      - uses: actions/checkout@", "      # attestations: write\n      - uses: actions/checkout@", "a write is said");
        }
        // Any other workflow may write nothing, and only the scanners may run on a schedule or on a rule change: the hygiene check says so.
        var dotnet = Workflows().Single(w => w.Name == "dotnet.yml").Text.Replace("\r\n", "\n");
        Assert.Contains("says : write", HygieneProblems("dotnet.yml", dotnet.Replace("  contents: read\n", "  contents: read\n  attestations: write\n")));
        Assert.Contains("says id-token", HygieneProblems("gitleaks.yml", dotnet.Replace("  contents: read\n", "  contents: read\n  id-token: write\n")));
        Assert.Contains("says id-token", HygieneProblems("codeql.yml", dotnet.Replace("  contents: read\n", "  contents: read\n  id-token: write\n")).Where(p => !MayHave("codeql.yml", p)));
        Assert.Contains(HygieneProblems("dotnet.yml", dotnet.Replace("  workflow_dispatch:\n", "  workflow_dispatch:\n  schedule:\n")), p => p.StartsWith("trigger schedule", StringComparison.Ordinal));
        Assert.Contains(HygieneProblems("codeql.yml", dotnet.Replace("  workflow_dispatch:\n", "  workflow_dispatch:\n  branch_protection_rule:\n")),
            p => p.StartsWith("trigger branch_protection_rule", StringComparison.Ordinal));
    }

    [Fact]   // mutations, each in a copy of dotnet.yml: issue_comment; the PR title in a run step; ${{ env.TITLE }} in a run step; secrets['NPM_TOKEN']; github.token; cache: npm
             // and packages: write in dotnet.yml; secrets.NPM_TOKEN in core-image.yml
    public void The_core_workflows_are_safe_for_outside_pull_requests()
    {
        var offenders = Workflows().SelectMany(w => HygieneProblems(w.Name, w.Text).Where(p => !MayHave(w.Name, p))
            .Select(p => $"{w.Name}: {p}")).ToArray();
        Assert.True(offenders.Length == 0, "The public repository takes outside pull requests; these workflows would let one do too much:\n" + string.Join("\n", offenders));
    }

    private const string Safe = """
        name: x
        on:
          pull_request:
          push:
            branches: [main]
          workflow_dispatch:
        permissions:
          contents: read
        jobs:
          x:
            steps:
              - uses: actions/setup-node@949feb2413d6458794dcd2491c4babbbce0c15c1 # v7.1.0
                with:
                  node-version: 22
                  package-manager-cache: false
              - env:
                  TITLE: ${{ github.event.pull_request.title }}
                run: |
                  echo "$TITLE" ${{ runner.temp }}
              - run: echo ${{ github.ref }} ${{ github.sha }} ${{github.repository}}
              - uses: actions/github-script@ed597411d8f924073f98dfc5c65a23a2325f34cd # v8.0.0
                with:
                  result-encoding: string
                  script: |
                    return "${{ github.sha }}" + process.env.TITLE
              - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
                with:
                  ref: ${{ github.sha }}
                  path: ${{ runner.temp }}/x-${{ env.IMAGE }}-${{ steps.a.outputs.b }}
                  repository: ${{ github.repository }}
                  token: ${{ github.actor }}
        """;

    [Fact]
    public void The_hygiene_check_catches_each_risk_and_accepts_a_safe_workflow()
    {
        Assert.Empty(HygieneProblems("x.yml", Safe));
        Assert.Equal(["pull_request", "push", "workflow_dispatch"], Triggers(Safe));
        Assert.Equal(["push", "issue_comment"], Triggers("on: [push, issue_comment]\n"));
        Assert.Equal(["issue_comment"], Triggers("\"on\": issue_comment\n"));

        static void Catches(string from, string to, string expect) => CatchesIn(w => HygieneProblems("x.yml", w), Safe, from, to, expect);
        Catches("  workflow_dispatch:\n", "  workflow_dispatch:\n  issue_comment:\n", "trigger issue_comment");
        Catches("  pull_request:\n", "  pull_request_target:\n", "trigger pull_request_target");
        // A run line may hold only runner.temp, github.ref, github.sha and github.repository (not matrix.<name>).
        const string Run = "echo ${{ github.ref }}", InRun = "in a run: value";
        Catches(Run, "echo \"${{ github.event.pull_request.title }}\"", InRun);
        Catches(Run, "git checkout ${{ github.head_ref }}", InRun);
        Catches(Run, "echo ${{inputs.name}}", InRun);
        Catches("echo \"$TITLE\" ${{ runner.temp }}", "echo \"$TITLE\" ${{ github.event.issue.body }}", InRun);   // in a | block
        Catches(Run, "echo \"${{ env.TITLE }}\"", InRun);
        Catches(Run, "echo ${{ steps.x.outputs.y }}", InRun);
        Catches(Run, "echo ${{ needs.a.outputs.b }}", InRun);
        Catches(Run, "git checkout ${{ github['head_ref'] }}", InRun);
        Catches(Run, "echo ${{ github[\"event\"].pull_request.title }}", InRun);
        Catches(Run, "echo '${{ toJSON(github) }}'", InRun);
        Catches(Run, "echo ${{ vars.X }}", InRun);
        Catches(Run, "echo ${{ format('{0}', github.event.pull_request.title) }}", InRun);
        Catches(Run, "echo ${{ matrix.os }}", InRun);
        Catches("${{github.repository}}", "${{github.repository", "an expression not closed");
        // A with: value may not hold an outsider's text, in any form; a script: input is code, so it gets the run line's allow-list.
        const string Script = "return \"${{ github.sha }}\"", InScript = "in the with: input script", Ref = "ref: ${{ github.sha }}", InRef = "in the with: input ref";
        Catches(Script, "return \"${{ github.event.pull_request.title }}\"", InScript);
        Catches(Script, "return \"${{ env.TITLE }}\"", InScript);
        Catches(Ref, "ref: ${{ github.event.pull_request.head.ref }}", InRef);
        Catches(Ref, "ref: ${{ github.head_ref }}", InRef);
        Catches(Ref, "ref: ${{ GitHub.Head_Ref }}", InRef);                 // expressions are case-insensitive
        Catches(Ref, "ref: ${{ github['head_ref'] }}", InRef);
        Catches(Ref, "ref: ${{ github[\"event\"].pull_request.title }}", InRef);
        Catches(Ref, "ref: ${{ inputs.ref }}", InRef);
        Catches("token: ${{ github.actor }}", "token: ${{ toJSON(github) }}", "an outsider's text in the with: input token");
        Catches("result-encoding: string", "result-encoding: ${{ github.event.comment.body }}", "an outsider's text in the with: input result-encoding");
        Catches("echo ${{ github.ref }}", "echo ${{ secrets['NPM_TOKEN'] }}", "a secret or token");
        Catches("echo ${{ github.ref }}", "echo ${{ secrets.GITHUB_TOKEN }}", "a secret or token");
        Catches("echo ${{ github.ref }}", "echo ${{ github.token }}", "a secret or token");
        Catches("package-manager-cache: false", "cache: npm", "a cache");
        Catches("actions/setup-node@", "actions/cache@", "actions/cache");
        Catches("contents: read", "contents: write", "permissions");
    }

    /// <summary>Applies one mutation to <paramref name="text"/> (<paramref name="from"/>, which must be there, becomes <paramref name="to"/>)
    /// and asserts that <paramref name="check"/> then reports a problem containing <paramref name="expect"/>.</summary>
    private static void CatchesIn(Func<string, IReadOnlyList<string>> check, string text, string from, string to, string expect)
    {
        Assert.Contains(from, text);
        var problems = check(text.Replace(from, to));
        Assert.True(problems.Any(p => p.Contains(expect, StringComparison.Ordinal)), $"{to}: expected \"{expect}\", got:\n" + string.Join("\n", problems));
    }

    /// <summary>The image the public repository publishes. A registry name must be lower case, so it is written out, not built from
    /// github.repository_owner (Packmoat, with a capital P).</summary>
    private const string CoreImage = "ghcr.io/packmoat/packmoat-core";

    /// <summary>The job's one guard: only the public repository's main branch publishes, so a fork, or a run by hand from a branch, does nothing.</summary>
    private const string PublishGuard = "if: github.repository == 'Packmoat/packmoat' && github.ref == 'refs/heads/main'";

    /// <summary>The identity the image is signed with, as the workflow writes it; GITHUB_REPOSITORY is Packmoat/packmoat when it runs.</summary>
    private const string SignerTemplate = "https://github.com/${GITHUB_REPOSITORY}/.github/workflows/core-image.yml@refs/heads/main";

    /// <summary>The issuer of GitHub Actions' identity tokens, which signed it.</summary>
    private const string Issuer = "https://token.actions.githubusercontent.com";

    /// <summary>
    /// What <see cref="Publisher"/> does beyond publishing the signed core image from main, one line each. It runs only on a push to main or
    /// by hand, never on a pull request; one job, under the one guard <see cref="PublishGuard"/>; a read-only token at the top and only
    /// contents read, packages write, id-token write and attestations write on the job; the built-in token only as the registry login's
    /// password; one image name, <see cref="CoreImage"/>; nothing tagged by the build or pushed by tag, and nothing that lets a failed
    /// command pass; each platform's SBOM attested to that platform's digest and the build provenance to the index digest, with
    /// nothing more pushed to the registry; and the steps in order: push by digest, pull that image, smoke it, check PORT, sign, verify as
    /// <see cref="SignerTemplate"/>, the SBOMs, the attestations, then tag. The attestations come before the tags, as the signature does,
    /// so a tag never names an image without its SBOM and provenance; a failure there leaves the image untagged, as a failed signature does.
    /// </summary>
    internal static IReadOnlyList<string> PublisherProblems(string workflow)
    {
        var found = new List<string>();
        var text = workflow.Replace("\r\n", "\n");
        var code = Regex.Replace(text, @"(?m)^\s*#.*$", "");
        void Require(bool ok, string problem) { if (!ok) found.Add(problem); }

        Require(Regex.IsMatch(text, @"(?m)^on:\n  push:\n    branches: \[main\]\n  workflow_dispatch:\n\npermissions:"),
            "the triggers are not exactly a push to main and workflow_dispatch");
        Require(!text.Contains("pull_request", StringComparison.Ordinal), "the triggers name pull_request");

        var jobs = text.IndexOf("\njobs:\n", StringComparison.Ordinal);
        Require(jobs >= 0 && Regex.Matches(text[jobs..], @"(?m)^  [\w-]+:\s*$").Count == 1, "not exactly one job");
        Require(Regex.Matches(code, @"(?m)^\s*-?\s*if:").Count == 1, "not exactly one if: line (the guard)");
        Require(Regex.IsMatch(text, "(?m)^    " + Regex.Escape(PublishGuard) + "$"), $"the job's guard is not exactly {PublishGuard}");

        Require(Regex.IsMatch(text, @"(?m)^permissions:\n  contents: read\n(?![ \t])"), "the top-level permissions are not exactly contents: read");
        Require(Regex.IsMatch(text, @"(?m)^    permissions:\n      contents: read\n      packages: write\n      id-token: write\n      attestations: write\n(?!      )"),
            "the job's permissions are not exactly contents read, packages write, id-token write and attestations write");
        Require(Regex.Matches(text, @"(?m)^\s*permissions:").Count == 2, "permissions are set somewhere other than the top and the job");
        Require(Regex.Matches(text, "packages: write").Count == 1 && Regex.Matches(text, "id-token").Count == 1 && Regex.Matches(text, "attestations").Count == 1,
            "packages: write, id-token or attestations is said more than once");

        // Two SBOMs (syft), one per platform, each attested to its platform's digest; one build provenance, attested to the index digest
        // the tags name. The attestations are kept by GitHub, not pushed to the registry.
        Require(Regex.Matches(code, @"uses: anchore/sbom-action@").Count == 2 && Regex.Matches(code, @"uses: actions/attest@").Count == 3,
            "not exactly two SBOM steps and three attest steps");
        Require(Regex.Matches(code, @"(?m)^\s+subject-name: \$\{\{ env\.IMAGE \}\}$").Count == 3 && Regex.Matches(code, @"subject-(name|path|checksums):").Count == 3,
            "an attestation names a subject other than IMAGE by digest");
        foreach (var arch in new[] { "amd64", "arm64" })
        {
            Require(code.Contains($"          image: registry:${{{{ env.IMAGE }}}}@${{{{ steps.platforms.outputs.{arch} }}}}\n", StringComparison.Ordinal)
                    && code.Contains($"          SYFT_PLATFORM: linux/{arch}\n", StringComparison.Ordinal)
                    && code.Contains($"          output-file: ${{{{ runner.temp }}}}/sbom-{arch}.spdx.json\n", StringComparison.Ordinal),
                $"the linux/{arch} SBOM is not made from that platform's image in the registry");
            Require(code.Contains($"          subject-digest: ${{{{ steps.platforms.outputs.{arch} }}}}\n          sbom-path: ${{{{ runner.temp }}}}/sbom-{arch}.spdx.json\n", StringComparison.Ordinal),
                $"the linux/{arch} SBOM is not attested to that platform's digest");
        }
        Require(Regex.Matches(code, @"subject-digest:").Count == 3 && Regex.Matches(code, @"sbom-path:").Count == 2
                && Regex.IsMatch(code, @"\n          subject-digest: \$\{\{ steps\.build\.outputs\.digest \}\}\n\s*- name: Tag"),
            "the build provenance is not attested to the index digest, as the last step before the tags");
        Require(!Regex.IsMatch(code, @"push-to-registry|predicate"), "an attestation is pushed to the registry, or is not the SBOM or the build provenance");

        var lines = text.Split('\n');
        var secretLines = lines.Select((l, i) => (l, i)).Where(x => SecretReference.IsMatch(x.l)).ToArray();
        Require(secretLines.Length == 1 && secretLines[0].l == "          password: ${{ secrets.GITHUB_TOKEN }}"
                && lines.Take(secretLines[0].i).Last(l => Regex.IsMatch(l, @"^\s*-?\s*uses:")).Contains("uses: docker/login-action@", StringComparison.Ordinal),
            "a secret or token other than secrets.GITHUB_TOKEN once, as the docker/login-action password");

        Require(text.Contains($"    env:\n      IMAGE: {CoreImage}\n", StringComparison.Ordinal), $"the job's IMAGE is not {CoreImage}");
        Require(Regex.Matches(code, @"ghcr\.io/").Count == 1, "an image name other than IMAGE's (ghcr.io/ more than once)");
        Require(Regex.Matches(code, @"(?m)^\s*-?\s*IMAGE\s*:").Count == 1, "IMAGE is set more than once (a step's env: can override the job's)");
        Require(text.Contains("        with:\n          persist-credentials: false\n", StringComparison.Ordinal)
                && Regex.Matches(code, @"uses: actions/checkout@").Count == 1, "the checkout keeps its credentials (persist-credentials: false is missing)");

        // The image reaches the registry only by build-push-action's digest push and the one imagetools create after the signature.
        Require(!Regex.IsMatch(code, @"\bdocker\s+(image\s+)?push\b"), "docker push");
        Require(!Regex.IsMatch(code, @"\bdocker\s+(image\s+)?tag\s+\S+\s+""?\$\{?IMAGE\b"), "docker tag to ${IMAGE}");
        Require(!Regex.IsMatch(code, @"\b(crane|oras|skopeo|regctl)\b"), "another registry tool (crane, oras, skopeo or regctl)");
        Require(Regex.Matches(code, @"imagetools\s+create").Count == 1, "imagetools create more than once");
        Require(Regex.Matches(code, @"\bcosign\s+sign").Count == 1, "a cosign sign other than the one by digest");

        Require(!Regex.IsMatch(code, @"(?m)^\s+tags:"), "the build tags the image");
        Require(!Regex.IsMatch(code, @"(?m)^\s+push: true"), "the build pushes by tag (push: true)");
        Require(code.Contains("push-by-digest=true", StringComparison.Ordinal), "the build does not push by digest");
        Require(!code.Contains("continue-on-error", StringComparison.Ordinal), "continue-on-error");
        Require(!Regex.IsMatch(code, @"\|\|\s*(true|:)\b|\|\|\s*:|\|\|\s*echo\b|set \+e"), "a failed command can pass (|| true, || :, || echo or set +e)");

        Require(code.Contains("cosign sign --yes --recursive \"${IMAGE}@${DIGEST}\"", StringComparison.Ordinal), "the index and its images are not signed by digest");
        Require(Regex.IsMatch(code, @"cosign verify \\\n(?:\s+--[^\n]*\\\n){2}\s+""\$\{IMAGE\}@\$\{d\}"" > /dev/null\n"), "the verify is not the whole command, ending > /dev/null");
        Require(code.Contains($"--certificate-identity \"{SignerTemplate}\"", StringComparison.Ordinal), $"the verify does not demand {SignerTemplate}");
        Require(code.Contains($"--certificate-oidc-issuer \"{Issuer}\"", StringComparison.Ordinal), $"the verify does not demand the issuer {Issuer}");
        Require(Regex.Matches(code, @"--certificate-identity\b").Count == 1 && Regex.Matches(code, @"--certificate-oidc-issuer\b").Count == 1
                && !Regex.IsMatch(code, @"--certificate-(identity|oidc-issuer)-regexp"), "the verify's identity or issuer is given twice, or as a pattern (-regexp)");
        Require(code.Contains("if [[ \"${got}\" != \"${DIGEST}\" ]]; then", StringComparison.Ordinal), "the tags are not checked against the signed digest");

        string[] order = ["id: build", "docker pull \"${IMAGE}@${amd64}\"", "image-smoke.sh packmoat-core:smoke", "port-smoke.sh packmoat-core:smoke",
                          "cosign sign", "cosign verify", "id: platforms", "uses: anchore/sbom-action@", "uses: actions/attest@", "imagetools create"];
        var at = order.Select(s => code.IndexOf(s, StringComparison.Ordinal)).ToArray();
        Require(at.All(i => i >= 0) && at.Zip(at.Skip(1)).All(p => p.First < p.Second),
            "the steps are not in order: push by digest, pull that image, smoke, PORT smoke, sign, verify, SBOMs, attestations, then tag");
        // Every SBOM and attest step, not only the first, is between the signature's check and the tags.
        var verify = code.IndexOf("cosign verify", StringComparison.Ordinal);
        var tag = code.IndexOf("imagetools create", StringComparison.Ordinal);
        Require(Regex.Matches(code, @"uses: (anchore/sbom-action|actions/attest)@").All(m => m.Index > verify && m.Index < tag),
            "an SBOM or attest step is not between the signature's check and the tags");
        return found;
    }

    [Fact]   // mutations: IMAGE ghcr.io/${{ github.repository_owner }}/packmoat-core; pull_request: added; the guard widened or dropped; tag before sign;
             // cosign verify ... || true (each is a case in the self-test below, applied to the real core-image.yml)
    public void The_core_image_is_published_only_from_main_signed_and_then_tagged() =>
        Assert.Empty(PublisherProblems(Workflows().Single(w => w.Name == Publisher).Text));

    [Fact]
    public void The_publisher_check_catches_each_way_to_publish_more_or_unchecked()
    {
        var text = Workflows().Single(w => w.Name == Publisher).Text.Replace("\r\n", "\n");
        Assert.Empty(PublisherProblems(text));
        void Catches(string from, string to, string expect) => CatchesIn(PublisherProblems, text, from, to, expect);

        Catches($"IMAGE: {CoreImage}\n", "IMAGE: ghcr.io/${{ github.repository_owner }}/packmoat-core\n", "the job's IMAGE is not");
        Catches("  workflow_dispatch:\n", "  workflow_dispatch:\n  pull_request:\n", "the triggers");
        Catches("    branches: [main]\n", "    branches: [main]\n    paths: ['dotnet/**']\n", "the triggers are not exactly");
        Catches(PublishGuard, PublishGuard + " || github.event_name == 'workflow_dispatch'", "the job's guard");
        Catches(PublishGuard, "if: github.ref == 'refs/heads/main'", "the job's guard");
        Catches("    " + PublishGuard + "\n", "", "not exactly one if: line");
        Catches("      - name: Summary\n", "      - if: always()\n        name: Summary\n", "not exactly one if: line");
        Catches("permissions:\n  contents: read\n", "permissions:\n  contents: write\n", "the top-level permissions");
        Catches("      id-token: write\n", "      id-token: write\n      actions: write\n", "the job's permissions");
        Catches("      - name: Summary\n", "      - name: Summary\n        env:\n          X: ${{ secrets.NPM_TOKEN }}\n", "a secret or token");
        Catches("      - name: Summary\n", "      - name: Summary\n        env:\n          X: ${{ github.token }}\n", "a secret or token");
        Catches("          provenance: false\n", "          provenance: false\n          tags: x\n", "the build tags the image");
        Catches("          provenance: false\n", "          provenance: false\n          push: true\n", "pushes by tag");
        Catches("push-by-digest=true", "push-by-digest=false", "push by digest");
        Catches("> /dev/null\n", "> /dev/null || true\n", "a failed command can pass");
        Catches("    timeout-minutes: 45\n", "    timeout-minutes: 45\n    continue-on-error: true\n", "continue-on-error");
        Catches("      - uses: sigstore/cosign-installer@", "      - run: docker buildx imagetools create --tag \"${IMAGE}:main\" \"${IMAGE}@${DIGEST}\"\n      - uses: sigstore/cosign-installer@",
            "the steps are not in order");
        Catches("core-image.yml@refs/heads/main\" \\\n", "cd.yml@refs/heads/main\" \\\n", "the verify does not demand");
        Catches("jobs:\n", "jobs:\n  second:\n    runs-on: ubuntu-24.04\n    steps:\n      - run: echo hi\n", "not exactly one job");
        // The image name overridden in a step, a second signature or push, the issuer as a pattern, credentials kept.
        const string Sign = "          cosign sign --yes --recursive \"${IMAGE}@${DIGEST}\"\n";
        Catches("        env:\n          DIGEST: ${{ steps.build.outputs.digest }}\n        run: |\n          cosign sign",
            "        env:\n          IMAGE: docker.io/evil/core\n          DIGEST: ${{ steps.build.outputs.digest }}\n        run: |\n          cosign sign", "IMAGE is set more than once");
        Catches(Sign, Sign + "          cosign sign --yes \"${IMAGE}:latest\"\n", "a cosign sign other than");
        Catches(Sign, Sign + "          cosign sign-blob --yes x\n", "a cosign sign other than");
        Catches("--certificate-oidc-issuer \"https://token.actions.githubusercontent.com\"", "--certificate-oidc-issuer-regexp \".*\"", "the verify does not demand the issuer");
        Catches("--certificate-oidc-issuer \"https://token.actions.githubusercontent.com\" \\\n",
            "--certificate-oidc-issuer \"https://token.actions.githubusercontent.com\" \\\n              --certificate-identity-regexp \".*\" \\\n", "as a pattern");
        const string Pull = "          docker tag \"${IMAGE}@${amd64}\" packmoat-core:smoke\n";
        Catches(Pull, Pull + "          docker tag packmoat-core:smoke \"${IMAGE}:main\"\n", "docker tag to ${IMAGE}");
        Catches(Pull, Pull + "          docker push \"${IMAGE}:main\"\n", "docker push");
        Catches(Pull, Pull + "          docker image push \"${IMAGE}:main\"\n", "docker push");
        Catches(Pull, Pull + "          crane copy \"${IMAGE}@${amd64}\" \"${IMAGE}:main\"\n", "another registry tool");
        Catches(Pull, Pull + "          oras copy x y\n", "another registry tool");
        Catches(Pull, Pull + "          docker buildx imagetools create --tag \"${IMAGE}:early\" \"${IMAGE}@${DIGEST}\"\n", "imagetools create more than once");
        Catches("          persist-credentials: false\n", "", "the checkout keeps its credentials");
        // The SBOMs and the provenance, each to the right digest, kept by GitHub, between the signature's check and the tags.
        Catches("      attestations: write\n", "", "the job's permissions");
        Catches("          sbom-path: ${{ runner.temp }}/sbom-arm64.spdx.json\n", "          sbom-path: ${{ runner.temp }}/sbom-arm64.spdx.json\n          push-to-registry: true\n",
            "pushed to the registry");
        Catches("          sbom-path: ${{ runner.temp }}/sbom-amd64.spdx.json\n", "          sbom-path: ${{ runner.temp }}/sbom-arm64.spdx.json\n", "the linux/amd64 SBOM is not attested");
        Catches("          SYFT_PLATFORM: linux/arm64\n", "          SYFT_PLATFORM: linux/amd64\n", "the linux/arm64 SBOM is not made");
        Catches("          image: registry:${{ env.IMAGE }}@${{ steps.platforms.outputs.amd64 }}\n", "          image: ${{ env.IMAGE }}:main\n", "the linux/amd64 SBOM is not made");
        Catches("          subject-digest: ${{ steps.build.outputs.digest }}\n", "          subject-digest: ${{ steps.platforms.outputs.amd64 }}\n", "the build provenance is not attested");
        Catches("          subject-name: ${{ env.IMAGE }}\n          subject-digest: ${{ steps.build.outputs.digest }}\n",
            "          subject-name: ghcr.io/evil/core\n          subject-digest: ${{ steps.build.outputs.digest }}\n", "an attestation names a subject");
        const string Summary = "      - name: Summary\n";
        Catches(Summary, "      - uses: actions/attest@1e69f48acb82d1966a394da916b4c1698aa569d6 # v4.2.2\n        with:\n          subject-path: x\n" + Summary,
            "three attest steps");
        // An attestation moved after the tags: the provenance step, cut and put back after the tag step.
        var provenance = Regex.Match(text, @"      - name: Attest the build provenance[^\n]*\n(?:        [^\n]*\n)+").Value;
        Assert.NotEmpty(provenance);
        Catches(provenance, "", "not exactly two SBOM steps and three attest steps");
        Assert.Contains(PublisherProblems(text.Replace(provenance, "").Replace(Summary, provenance + Summary)),
            p => p.StartsWith("an SBOM or attest step is not between", StringComparison.Ordinal));
    }

    [Fact]   // mutations: the guide's signer in lower case (cosign compares it exactly); the guide's image under <owner>; the image name changed
             // in the Kubernetes guide
    public void The_deploy_guides_name_the_published_image_and_its_signer()
    {
        var workflow = Workflows().Single(w => w.Name == Publisher).Text;
        var repository = Regex.Match(PublishGuard, "github.repository == '([^']+)'").Groups[1].Value;
        Assert.Contains($"IMAGE: {CoreImage}", workflow);
        Assert.Contains(SignerTemplate, workflow);
        var signer = SignerTemplate.Replace("${GITHUB_REPOSITORY}", repository, StringComparison.Ordinal);

        // Every doc: each cosign identity is the signer, and the single-server guide, which shows the check, has at least one.
        var docs = SelfHostingDocTests.MarkdownFiles(CoreRoot());
        var identities = docs.SelectMany(d => Regex.Matches(File.ReadAllText(Path.Combine(CoreRoot(), d)), @"--certificate-identity\s+(\S+)").Select(m => (Doc: d, Identity: m.Groups[1].Value))).ToArray();
        Assert.Contains(identities, x => x.Doc == "docs/deploy/single-server.md");
        Assert.All(identities, x => Assert.Equal(signer, x.Identity));
        // Every doc and the Helm chart (its values, its refusal message and the restore pod) name the published image and no other; the
        // two guides that run it, and the chart, must name it.
        static string[] Images(string text) => Regex.Matches(text, @"ghcr\.io/[^\s`:@,""')]+").Select(m => m.Value).ToArray();
        foreach (var file in new[] { "docs/deploy/single-server.md", "docs/deploy/kubernetes.md", "deploy/helm/packmoat/values.yaml",
                     "deploy/helm/packmoat/templates/_helpers.tpl", "deploy/helm/restore-pod.yaml" })
            Assert.True(Images(File.ReadAllText(Path.Combine(CoreRoot(), file))).Length > 0, $"{file} names no ghcr.io image");
        foreach (var file in docs.Concat(["deploy/helm/packmoat/values.yaml", "deploy/helm/packmoat/templates/_helpers.tpl", "deploy/helm/restore-pod.yaml"]))
            Assert.All(Images(File.ReadAllText(Path.Combine(CoreRoot(), file))), i => Assert.True(i == CoreImage, $"{file} names {i}, not {CoreImage}"));
    }

    [Fact]   // mutations: --strict dropped; --redact dropped; fetch-depth: 0 removed; || true after the hash check; set -- in the script;
             // continue-on-error in gitleaks.yml
    public void The_secret_scan_runs_a_pinned_checked_release_over_the_whole_history_and_never_prints_a_secret()
    {
        var script = File.ReadAllText(Path.Combine(CoreRoot(), ".github", "scripts", "gitleaks.sh")).Replace("\r\n", "\n");
        Assert.Matches(@"(?m)^version=\d+\.\d+\.\d+$", script);
        Assert.Matches(@"(?m)^sha256=[0-9a-f]{64}$", script);
        Assert.Matches(@"(?m)^set -euo pipefail$", script);
        // The whole line: nothing after the check (|| true, ; true, & ...) can let a wrong download through.
        Assert.Matches(@"(?m)^echo ""\$\{sha256\}  \$dir/gitleaks\.tar\.gz"" \| sha256sum --check --strict -$", script);
        Assert.Matches(@"(?m)^""\$dir/gitleaks"" ""\$@"" --redact --no-banner$", script);
        // Nothing may swallow a failure or change the arguments before they reach gitleaks (set -- x, shift, args=...).
        Assert.DoesNotMatch(@"\|\||set\s+\+e|(?m)^\s*(set\s+--?(\s|$)|shift\b)", script);

        var workflow = Workflows().Single(w => w.Name == "gitleaks.yml").Text.Replace("\r\n", "\n");
        Assert.Contains("          fetch-depth: 0\n", workflow);
        Assert.Contains("          persist-credentials: false\n", workflow);
        Assert.Matches(@"(?m)^\s+run: \.github/scripts/gitleaks\.sh git \.$", workflow);
        var code = Regex.Replace(workflow, @"(?m)^\s*#.*$", "");
        Assert.DoesNotContain("continue-on-error", code);
        Assert.DoesNotMatch(@"\|\||set\s+\+e", code);
    }

    /// <summary>The actions each scanner may use. Scorecard publishes its results only from a workflow whose job uses its approved
    /// actions, and neither scanner needs more.</summary>
    private static readonly Dictionary<string, string[]> ScannerActions = new()
    {
        ["codeql.yml"] = ["actions/checkout", "actions/setup-dotnet", "github/codeql-action/init", "github/codeql-action/analyze"],
        ["scorecard.yml"] = ["actions/checkout", "ossf/scorecard-action", "github/codeql-action/upload-sarif"],
    };

    /// <summary>
    /// What a scanner does beyond scanning, one line each. CodeQL: on a pull request, a push to main, weekly and by hand; C# read
    /// without a build (build-mode none); no cache (TRAP or dependency). Scorecard: on a push to main, weekly and on a branch rule change,
    /// never a pull request; publishes its results; no env: or defaults: at the top or in its job, no container or service (Scorecard's
    /// own rules for publishing). Both: one job, the checkout keeps no credentials, only the actions in <see cref="ScannerActions"/>, and
    /// no run: step, and nothing that lets a failure pass. Their permissions are <see cref="WriterProblems"/>'.
    /// </summary>
    internal static IReadOnlyList<string> ScannerProblems(string name, string workflow)
    {
        var found = new List<string>();
        var text = workflow.Replace("\r\n", "\n");
        var code = Regex.Replace(text, @"(?m)^\s*#.*$", "");
        void Require(bool ok, string problem) { if (!ok) found.Add(problem); }
        var jobs = text.IndexOf("\njobs:\n", StringComparison.Ordinal);
        Require(jobs >= 0 && Regex.Matches(text[jobs..], @"(?m)^  [\w-]+:\s*$").Count == 1, "not exactly one job");
        Require(Regex.Matches(code, "persist-credentials: false").Count == 1 && Regex.Matches(code, "uses: actions/checkout@").Count == 1,
            "the checkout keeps its credentials (persist-credentials: false is missing)");
        var uses = Regex.Matches(code, @"(?m)^\s*-?\s*uses:\s*([^@\s]+)@").Select(m => m.Groups[1].Value).Distinct().ToArray();
        Require(uses.All(ScannerActions[name].Contains) && ScannerActions[name].All(uses.Contains),
            $"the actions are not exactly {string.Join(", ", ScannerActions[name])}: {string.Join(", ", uses)}");
        Require(!code.Contains("continue-on-error", StringComparison.Ordinal) && !Regex.IsMatch(code, @"\|\||set \+e"), "a failure can pass");
        // Only actions, no commands of their own: a run: step would run with the job's write (and, in Scorecard, its identity token), and
        // Scorecard refuses to publish from a job with one.
        Require(!Regex.IsMatch(code, @"(?m)^\s*-?\s*[""']?run[""']?\s*:"), "a run: step");
        Require(Regex.IsMatch(text, @"(?m)^    branches: \[main\]$") && Regex.Matches(text, "branches").Count == 1, "the push is not exactly to main");
        Require(Regex.IsMatch(text, @"(?m)^  schedule:\n    - cron: '\d+ \d+ \* \* [0-6]'\n"), "not weekly (one cron, on one day of the week)");
        if (name == "codeql.yml")
        {
            Require(Triggers(text).SequenceEqual(["pull_request", "push", "schedule", "workflow_dispatch"]),
                "the triggers are not exactly a pull request, a push to main, weekly and by hand");
            Require(Regex.IsMatch(code, @"(?m)^          languages: csharp\n          build-mode: none\n          trap-caching: false\n          dependency-caching: false\n"),
                "CodeQL does not read C# with build-mode none and no cache");
        }
        else
        {
            Require(Triggers(text).SequenceEqual(["push", "schedule", "branch_protection_rule"]),
                "the triggers are not exactly a push to main, weekly and a branch rule change");
            Require(code.Contains("          publish_results: true\n", StringComparison.Ordinal), "the results are not published");
            Require(!Regex.IsMatch(code, @"(?m)^(env|defaults):") && !Regex.IsMatch(code, @"(?m)^    (env|defaults|container|services):"),
                "env:, defaults:, a container or a service, which Scorecard refuses to publish from");
            Require(Regex.IsMatch(code, @"(?m)^    runs-on: ubuntu-[\d.]+$"), "the job does not run on an Ubuntu runner");
        }
        return found;
    }

    [Fact]   // mutations: scorecard on pull_request; codeql with build-mode autobuild; an unpinned github/codeql-action/upload-sarif@v4
    public void The_scanners_scan_and_do_nothing_more() =>
        Assert.All(ScannerActions.Keys, n => Assert.Empty(ScannerProblems(n, Workflows().Single(w => w.Name == n).Text)));

    [Fact]
    public void The_scanner_check_catches_each_way_to_do_more()
    {
        var codeql = Workflows().Single(w => w.Name == "codeql.yml").Text.Replace("\r\n", "\n");
        var scorecard = Workflows().Single(w => w.Name == "scorecard.yml").Text.Replace("\r\n", "\n");
        void Catches(string name, string text, string from, string to, string expect) => CatchesIn(t => ScannerProblems(name, t), text, from, to, expect);
        foreach (var (name, text) in new[] { ("codeql.yml", codeql), ("scorecard.yml", scorecard) })
        {
            Assert.Empty(ScannerProblems(name, text));
            Catches(name, text, "  schedule:\n", "  schedule:\n  issues:\n", "the triggers are not exactly");
            Catches(name, text, "    branches: [main]\n", "    branches: [main, dev]\n", "the push is not exactly to main");
            Catches(name, text, "  schedule:\n    - cron: '", "  schedule:\n    - cron: '*/5 * * * *'\n    - cron: '", "not weekly");
            Catches(name, text, "          persist-credentials: false\n", "", "the checkout keeps its credentials");
            Catches(name, text, "      - uses: actions/checkout@", "      - uses: actions/cache@0400d5f644dc74513175e3cd8d07132dd4860809 # v4\n      - uses: actions/checkout@", "the actions are not exactly");
            Catches(name, text, "    timeout-minutes:", "    continue-on-error: true\n    timeout-minutes:", "a failure can pass");
            Catches(name, text, "      - uses: actions/checkout@", "      - run: curl -fsSL https://example.com/x.sh | sh\n      - uses: actions/checkout@", "a run: step");
            Catches(name, text, "      - uses: actions/checkout@", "      - name: x\n        run: echo hi\n      - uses: actions/checkout@", "a run: step");
            Catches(name, text, "jobs:\n", "jobs:\n  second:\n    runs-on: ubuntu-24.04\n    steps:\n      - run: echo hi\n", "not exactly one job");
        }
        Catches("codeql.yml", codeql, "build-mode: none", "build-mode: autobuild", "build-mode none");
        Catches("codeql.yml", codeql, "trap-caching: false", "trap-caching: true", "no cache");
        Catches("codeql.yml", codeql, "languages: csharp", "languages: csharp,javascript", "C#");
        Catches("scorecard.yml", scorecard, "on:\n", "on:\n  pull_request:\n", "the triggers are not exactly");
        Catches("scorecard.yml", scorecard, "publish_results: true", "publish_results: false", "not published");
        Catches("scorecard.yml", scorecard, "\njobs:\n", "\nenv:\n  X: y\n\njobs:\n", "env:, defaults:");
        Catches("scorecard.yml", scorecard, "    timeout-minutes:", "    services:\n      x:\n        image: alpine\n    timeout-minutes:", "env:, defaults:");
        Catches("scorecard.yml", scorecard, "    runs-on: ubuntu-24.04\n", "    runs-on: windows-2025\n", "Ubuntu");
    }

    /// <summary>The ecosystems Dependabot keeps up to date, each with its folder: the workflows' actions, the .NET packages, the
    /// schema check's npm packages, and the Dockerfile's base images (pinned by digest).</summary>
    private static readonly string[] DependabotEcosystems = ["github-actions /", "nuget /dotnet", "npm /spec/check", "docker /dotnet"];

    /// <summary>
    /// The one cooldown exception: Microsoft's .NET base images, whose rebuilds carry the OS and runtime security fixes and
    /// which Dependabot has no security advisories for, so a cooldown would only hold those fixes back. Dependabot names a Docker image by
    /// its repository; the docs do not say whether the registry host is part of the name, so both forms are listed. Nothing else is excluded.
    /// </summary>
    private static readonly string?[] DockerCooldownExclude = ["dotnet/*", "mcr.microsoft.com/dotnet/*"];

    /// <summary>
    /// What .github/dependabot.yml leaves out, one line each: exactly the ecosystems in <see cref="DependabotEcosystems"/>, each weekly,
    /// each with a cooldown of at least 7 days (a release waits a week before it is proposed, as dotnet/aspire does; security updates are not
    /// held back), minor and patch updates grouped into one pull request, and at most 5 open pull requests.
    /// </summary>
    internal static IReadOnlyList<string> DependabotProblems(string yaml)
    {
        var found = new List<string>();
        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            using (var reader = new StringReader(yaml)) stream.Load(reader);
            root = (YamlMappingNode)stream.Documents.Single().RootNode;
        }
        catch (Exception e) when (e is YamlException or InvalidCastException or InvalidOperationException) { return [$"not YAML this check can read: {e.Message}"]; }

        static YamlNode? Get(YamlNode? node, string key) =>
            node is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;
        static string? Text(YamlNode? node) => (node as YamlScalarNode)?.Value;

        if (Text(Get(root, "version")) != "2") found.Add("version is not 2");
        var updates = (Get(root, "updates") as YamlSequenceNode)?.Children ?? [];
        var seen = updates.Select(u => $"{Text(Get(u, "package-ecosystem"))} {Text(Get(u, "directory"))}").ToArray();
        if (!seen.Order(StringComparer.Ordinal).SequenceEqual(DependabotEcosystems.Order(StringComparer.Ordinal)))
            found.Add($"the ecosystems are not exactly {string.Join(", ", DependabotEcosystems)}: {string.Join(", ", seen)}");
        foreach (var (u, id) in updates.Zip(seen))
        {
            if (Text(Get(Get(u, "schedule"), "interval")) != "weekly") found.Add($"{id}: not weekly");
            if (!int.TryParse(Text(Get(Get(u, "cooldown"), "default-days")), out var days) || days < 7) found.Add($"{id}: no cooldown of at least 7 days (default-days)");
            if (Get(Get(u, "cooldown"), "include") is not null) found.Add($"{id}: a cooldown include: list (the cooldown must cover every dependency)");
            var exclude = Get(Get(u, "cooldown"), "exclude") is { } ex ? (ex as YamlSequenceNode)?.Children.Select(Text).ToArray() ?? ["(not a list)"] : [];
            var expected = id == "docker /dotnet" ? DockerCooldownExclude : [];
            if (!exclude.SequenceEqual(expected))
                found.Add($"{id}: the cooldown's exclude: is not exactly [{string.Join(", ", expected)}]: [{string.Join(", ", exclude)}]");
            if (!int.TryParse(Text(Get(u, "open-pull-requests-limit")), out var limit) || limit < 1 || limit > 5) found.Add($"{id}: open-pull-requests-limit is not 1 to 5");
            static string?[] List(YamlNode? node) => (node as YamlSequenceNode)?.Children.Select(Text).Order(StringComparer.Ordinal).ToArray() ?? [];
            var groups = (Get(u, "groups") as YamlMappingNode)?.Children.Values ?? [];
            if (!groups.Any(g => List(Get(g, "update-types")).SequenceEqual(["minor", "patch"]) && List(Get(g, "patterns")).SequenceEqual(["*"])))
                found.Add($"{id}: minor and patch updates are not grouped (a group of patterns [\"*\"] and update-types minor and patch)");
        }
        return found;
    }

    private static string DependabotFile => Path.Combine(CoreRoot(), ".github", "dependabot.yml");

    [Fact]   // mutations: the docker entry dropped; a cooldown removed; a pip entry added; the npm folder misspelt
    public void Dependabot_updates_the_four_ecosystems_weekly_after_a_cooldown()
    {
        Assert.True(File.Exists(DependabotFile), ".github/dependabot.yml is missing");
        var problems = DependabotProblems(File.ReadAllText(DependabotFile));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        // Each folder is there (Dependabot reports a missing one only in its own logs) and holds what it updates.
        Assert.True(File.Exists(Path.Combine(CoreRoot(), "dotnet", "Dockerfile")));
        Assert.True(File.Exists(Path.Combine(CoreRoot(), "dotnet", "Packmoat.sln")));
        Assert.True(File.Exists(Path.Combine(CoreRoot(), "spec", "check", "package-lock.json")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(CoreRoot(), ".github", "workflows")));
    }

    [Fact]
    public void The_dependabot_check_catches_each_gap()
    {
        var text = File.ReadAllText(DependabotFile).Replace("\r\n", "\n");
        Assert.Empty(DependabotProblems(text));
        void Catches(string from, string to, string expect) => CatchesIn(DependabotProblems, text, from, to, expect);
        var cooldown = Regex.Match(text, @"\n    cooldown:\n      default-days: 7\n").Value;
        Assert.NotEmpty(cooldown);
        // One ecosystem's cooldown removed (the first), the others left: that one is named.
        Assert.Contains(DependabotProblems(new Regex(Regex.Escape(cooldown)).Replace(text, "\n", 1)), p => p == "github-actions /: no cooldown of at least 7 days (default-days)");
        Catches("default-days: 7", "default-days: 3", "no cooldown");
        Catches("directory: /spec/check", "directory: /spec", "the ecosystems are not exactly");
        Catches("package-ecosystem: docker", "package-ecosystem: pip", "the ecosystems are not exactly");
        Catches("interval: weekly", "interval: daily", "not weekly");
        Catches("open-pull-requests-limit: 3", "open-pull-requests-limit: 50", "open-pull-requests-limit");
        Catches("          - minor\n          - patch\n", "          - major\n          - minor\n          - patch\n", "not grouped");
        Catches("version: 2", "version: 1", "version is not 2");
        // The docker entry's exclusion is exactly the .NET images: not widened, not dropped, and no other entry has one.
        const string Exclude = "        - \"mcr.microsoft.com/dotnet/*\"\n";
        Catches(Exclude, "        - \"*\"\n", "docker /dotnet: the cooldown's exclude: is not exactly");
        Catches(Exclude, Exclude + "        - \"mcr.microsoft.com/*\"\n", "docker /dotnet: the cooldown's exclude: is not exactly");
        Catches("        - \"dotnet/*\"\n" + Exclude, "", "docker /dotnet: the cooldown's exclude: is not exactly");
        Catches("  - package-ecosystem: nuget\n    directory: /dotnet\n    schedule:\n      interval: weekly\n    cooldown:\n      default-days: 7\n",
            "  - package-ecosystem: nuget\n    directory: /dotnet\n    schedule:\n      interval: weekly\n    cooldown:\n      default-days: 7\n      exclude:\n        - \"Microsoft.*\"\n",
            "nuget /dotnet: the cooldown's exclude: is not exactly");
        Catches("  - package-ecosystem: npm\n    directory: /spec/check\n    schedule:\n      interval: weekly\n    cooldown:\n      default-days: 7\n",
            "  - package-ecosystem: npm\n    directory: /spec/check\n    schedule:\n      interval: weekly\n    cooldown:\n      default-days: 7\n      include:\n        - \"ajv\"\n",
            "npm /spec/check: a cooldown include:");
        Catches("updates:", "updates: [", "not YAML");
    }

    // The review cases (b, c, n, r, p), each a job header or a step added to dotnet.yml, as the reviews'
    // generator built them. The b, n and r cases each got an outsider's text into code past an earlier version of the check; the c cases
    // are the controls. A case that must fail names what the check must say, so it is caught for the right reason; one with nothing must pass.
    private const string T = "github.event.pull_request.title";
    private const string GithubScript = "      - uses: actions/github-script@ed597411d8f924073f98dfc5c65a23a2325f34cd # v8.0.0\n";
    private const string InRun = "in a run: value is not one of", InScript = "in the with: input script is not one of", InArgs = "in the with: input args is not one of";
    private const string Deep = "[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[x]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]";
    private const string Escape = "a YAML escape for $", Anchor = "a YAML anchor or alias", NotYaml = "the workflow is not YAML this check can read";

    [Theory]   // mutations: skip sequence items; don't walk into with:; args not code; no cross-line matching; nothing on a parse error; no image blocklist;
               // exempt env/if/concurrency anywhere; matrix.<name> allowed; no github.* in the blocklist; With: by case; entrypoint not code;
               // no document count; walk only the first document; accept keys that are not text; no depth guard
    // Controls: an outsider's text in code, in each form the check must see.
    [InlineData("c01_inline_run_title", "", $"      - run: echo ${{{{ {T} }}}}\n", InRun)]
    [InlineData("c02_block_later_line", "", $"      - run: |\n          echo hi\n          echo ${{{{ {T} }}}}\n", InRun)]
    [InlineData("c03_folded", "", $"      - run: >\n          echo hi\n          ${{{{ {T} }}}}\n", InRun)]
    [InlineData("c04_split_expr", "", "      - run: |\n          echo ${{ github.event.\n          pull_request.title }}\n", InRun)]
    [InlineData("c05_format", "", $"      - run: echo ${{{{ format('{{0}}', {T}) }}}}\n", "in a run: value has a quote in it")]
    [InlineData("c06_fromjson", "", "      - run: echo ${{ fromJSON(toJSON(github)).event.pull_request.title }}\n", InRun)]
    [InlineData("c07_event_path_run", "", "      - run: echo ${{ github.event_path }}\n", InRun)]
    // Controls that must pass: the env: route, and text that is not an expression to Actions (it needs the ASCII "${{" with no space).
    [InlineData("base", "", "")]
    [InlineData("c08_env_route", "", $"      - env:\n          TITLE: ${{{{ {T} }}}}\n        run: echo \"$TITLE\"\n")]
    [InlineData("c08b_job_env_route", $"    env:\n      TITLE: ${{{{ {T} }}}}\n", "      - run: echo \"$TITLE\"\n")]
    [InlineData("c09_unicode_dollar", "", $"      - run: echo \uff04{{{{ {T} }}}}\n")]
    [InlineData("c09b_spaced", "", $"      - run: echo $ {{{{ {T} }}}}\n")]
    [InlineData("b01_yaml_escape_x24", "", $"      - run: \"echo \\x24{{{{ {T} }}}}\"\n", InRun, Escape)]
    [InlineData("b02_yaml_escape_u0024", "", $"      - run: \"echo \\u0024{{{{ {T} }}}}\"\n", InRun, Escape)]
    [InlineData("b02b_yaml_escape_U00000024", "", $"      - run: \"echo \\U00000024{{{{ {T} }}}}\"\n", InRun, Escape)]
    [InlineData("b03_dq_line_continuation", "", $"      - run: \"echo $\\\n          {{{{ {T} }}}}\"\n", InRun)]
    [InlineData("b04_space_before_colon", "", $"      - run : echo ${{{{ {T} }}}}\n", InRun)]
    [InlineData("b05_flow_mapping", "", $"      - {{ name: x, run: \"echo ${{{{ {T} }}}}\" }}\n", InRun)]
    [InlineData("b06_with_comment_script", "", GithubScript + $"        with: # inputs\n          script: |\n            return \"${{{{ {T} }}}}\"\n", InScript)]
    [InlineData("b07_with_close_brace_literal", "", "      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1\n        with:\n" +
                "          ref: ${{ format('}}{0}', github.head_ref) }}\n", "in the with: input ref has a quote in it")]
    [InlineData("b07b_run_close_brace_literal", "", $"      - run: echo ${{{{ format('}}}}{{0}}', {T}) }}}}\n", "in a run: value has a quote in it")]
    [InlineData("b08_shell_override", "", $"      - shell: bash -c \"echo ${{{{ {T} }}}}; bash {{0}}\"\n        run: echo hi\n", "in a shell: value is not one of")]
    [InlineData("b09_working_directory", "", "      - working-directory: ${{ github.head_ref }}\n        run: echo hi\n", "an outsider's text in a working-directory: value")]
    [InlineData("b10_docker_args", "", "      - uses: docker://alpine:3.20\n        with:\n          entrypoint: sh\n          args: -c \"echo ${{ env.TITLE }}\"\n", InArgs)]
    [InlineData("b10b_docker_args_steps", "", "      - uses: docker://alpine:3.20\n        with:\n          entrypoint: sh\n          args: -c \"echo ${{ steps.meta.outputs.title }}\"\n", InArgs)]
    [InlineData("b11_explicit_key_script", "", GithubScript + "        with:\n          ? script\n          : return \"${{ env.TITLE }}\"\n", InScript)]
    [InlineData("b11b_explicit_key_event", "", GithubScript + $"        with:\n          ? script\n          : return \"${{{{ {T} }}}}\"\n", InScript)]
    [InlineData("b12_anchor_alias", $"    env:\n      X: &s echo ${{{{ {T} }}}}\n", "      - run: *s\n", InRun, Anchor)]
    [InlineData("b13_with_space_before_colon", "", GithubScript + $"        with :\n          script: return \"${{{{ {T} }}}}\"\n", InScript)]
    [InlineData("b14_script_space_key", "", GithubScript + "        with:\n          script : |\n            return \"${{ env.TITLE }}\"\n", InScript)]
    [InlineData("b15_with_anchor", "", GithubScript + "        with: &w\n          script: return \"${{ env.TITLE }}\"\n", InScript, Anchor)]
    [InlineData("b16_container_image", "    container:\n      image: ${{ github.head_ref }}\n    services:\n      s:\n        image: ${{ github.event.pull_request.title }}\n", "",
                "(jobs.dotnet.container.image): an outsider's text in a image: value", "(jobs.dotnet.services.s.image): an outsider's text in a image: value")]
    [InlineData("n1_flow_sequence_steps", "", $"  second:\n    runs-on: ubuntu-24.04\n    steps: [ {{ run: \"echo ${{{{ {T} }}}}\" }} ]\n", InRun)]
    [InlineData("n2_flow_mapping_quoted_key", "", $"      - {{ name: x, \"run\": \"echo ${{{{ {T} }}}}\" }}\n", InRun)]
    [InlineData("n3_with_comment_first", "", GithubScript + "        with:\n              # an over-indented comment sets the key indent\n          script: |\n            return \"${{ env.TITLE }}\"\n", InScript)]
    [InlineData("n3b_with_comment_first_event", "", GithubScript + "        with:\n              # an over-indented comment sets the key indent\n          script: |\n            return \"${{ steps.x.outputs.title }}\"\n", InScript)]
    [InlineData("n4_flow_seq_with", "", "  third:\n    runs-on: ubuntu-24.04\n    steps:\n" + GithubScript + "        with: {script: \"return '${{ env.TITLE }}'\"}\n", InScript)]
    [InlineData("r1_multiline_flow_run_after_comma", "", $"      - {{\n          name: x, run: \"echo ${{{{ {T} }}}}\"\n        }}\n", InRun)]
    [InlineData("r2_multiline_flow_with_script", "", GithubScript + "        with:\n          {\n            script: \"return '${{ env.TITLE }}'\"\n          }\n", InScript)]
    [InlineData("r3_multiline_flow_seq", "", $"  second:\n    runs-on: ubuntu-24.04\n    steps: [\n      {{\n        name: x, run: \"echo ${{{{ {T} }}}}\"\n      }}\n    ]\n", InRun)]
    [InlineData("r4_with_blank_comment", "", GithubScript + "        with:\n\n              # comment\n          script: |\n            return \"${{ env.TITLE }}\"\n", InScript)]
    [InlineData("r5_comment_between_inputs", "", GithubScript + "        with:\n          result-encoding: string\n              # comment\n          script: |\n            return \"${{ env.TITLE }}\"\n", InScript)]
    // What the parser itself must refuse, or walk into.
    [InlineData("p1_parse_error", "", $"      - run: \"echo ${{{{ {T} }}}}\n", NotYaml)]
    [InlineData("p2_duplicate_key", "", $"      - run: echo hi\n        run: echo ${{{{ {T} }}}}\n", NotYaml)]
    [InlineData("p3_with_not_a_mapping", "", GithubScript + "        with: x\n", "with: is not a mapping of inputs")]
    [InlineData("p4_input_not_text", "", GithubScript + "        with:\n          script: [ \"return '${{ env.TITLE }}'\" ]\n", "the with: input script is not text")]
    [InlineData("p5_defaults_run_shell", "    defaults:\n      run:\n        shell: bash -c \"${{ env.SHELL_LINE }} {0}\"\n", "",
                "(jobs.dotnet.defaults.run.shell): ${{ env.SHELL_LINE }} in a shell: value is not one of")]
    [InlineData("p6_input_name_case", "", GithubScript + "        with:\n          Script: return \"${{ env.TITLE }}\"\n", "in the with: input Script is not one of")]
    [InlineData("p7_merge_key", "", "      - <<: { run: \"echo ${{ env.TITLE }}\" }\n        name: x\n", ".<<.run): ${{ env.TITLE }} in a run: value is not one of")]
    // Env:, if: and concurrency: are exempt only where Actions reads them as settings.
    [InlineData("q1_job_id_env", "", $"  env:\n    runs-on: ubuntu-24.04\n    steps:\n      - run: echo \"${{{{ {T} }}}}\"\n", "(jobs.env.steps[0].run): ${{ " + T + " }} in a run: value is not one of")]
    [InlineData("q1b_job_id_if", "", $"  if:\n    runs-on: ubuntu-24.04\n    steps:\n      - run: echo \"${{{{ {T} }}}}\"\n", "(jobs.if.steps[0].run): ${{ " + T + " }} in a run: value is not one of")]
    [InlineData("q1c_job_id_concurrency", "", $"  concurrency:\n    runs-on: ubuntu-24.04\n    steps:\n      - run: echo \"${{{{ {T} }}}}\"\n",
                "(jobs.concurrency.steps[0].run): ${{ " + T + " }} in a run: value is not one of")]
    [InlineData("q2_matrix_key_env", $"    strategy:\n      matrix:\n        env: [\"${{{{ {T} }}}}\"]\n", "      - run: echo ${{ matrix.env }}\n",
                "(jobs.dotnet.strategy.matrix.env[0]): an outsider's text in a env: value", "${{ matrix.env }} in a run: value is not one of")]
    [InlineData("q3_service_id_env", "    services:\n      env:\n        image: ${{ github.head_ref }}\n", "", "(jobs.dotnet.services.env.image): an outsider's text in a image: value")]
    [InlineData("q4_matrix_from_json", "    strategy:\n      matrix: ${{ fromJSON(needs.a.outputs.m) }}\n", "      - run: echo ${{ matrix.x }}\n", "${{ matrix.x }} in a run: value is not one of")]
    [InlineData("q5_object_filter", "", "      - working-directory: ${{ toJSON(github.*) }}\n        run: echo hi\n", "an outsider's text in a working-directory: value")]
    [InlineData("q5b_object_filter_head_ref", "", "      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1\n        with:\n          ref: ${{ github.*.head_ref }}\n",
                "an outsider's text in the with: input ref")]
    [InlineData("q6_with_capitalised", "", GithubScript + "        With:\n          script: return \"${{ env.TITLE }}\"\n", InScript)]
    [InlineData("q7_entrypoint", "", "      - uses: docker://alpine:3.20\n        with:\n          entrypoint: ${{ env.X }}\n", "${{ env.X }} in the with: input entrypoint is not one of")]
    [InlineData("q8_two_documents", "", $"---\nname: second\non: push\njobs:\n  a:\n    runs-on: ubuntu-24.04\n    steps:\n      - run: echo ${{{{ {T} }}}}\n",
                "2 YAML documents, not 1", "(jobs.a.steps[0].run): ${{ " + T + " }} in a run: value is not one of")]
    [InlineData("q9_key_not_text", "", "      - ? [a, b]\n        : x\n        run: echo hi\n", "a key that is not text")]
    [InlineData("q10_max_depth", "", "      - run: echo hi\n        x: " + Deep + "\n", "nested deeper than 64")]
    public void The_expression_check_catches_each_review_case_for_the_right_reason(string name, string job, string step, params string[] expected)
    {
        const string JobLine = "    timeout-minutes: 30\n";
        // Without the postgres service: some cases add a services: of their own, and a job may have only one.
        var dotnet = PostgresService.WithoutServices(Workflows().Single(w => w.Name == "dotnet.yml").Text);
        Assert.Contains(JobLine, dotnet);
        var problems = WorkflowExpressions.Problems(dotnet.Replace(JobLine, JobLine + job) + step);
        if (expected.Length == 0) Assert.True(problems.Count == 0, $"{name} must pass, got:\n" + string.Join("\n", problems));
        foreach (var e in expected)
            Assert.True(problems.Any(p => p.Contains(e, StringComparison.Ordinal)), $"{name}: expected \"{e}\", got:\n" + string.Join("\n", problems));
    }

    // ---- The PostgreSQL service the test job runs beside, and dotnet/tools/postgres-tests.sh, on one image.

    private static string Script() => File.ReadAllText(Path.Combine(CoreRoot(), "dotnet", "tools", "postgres-tests.sh")).Replace("\r\n", "\n");

    private static string BouncerScript() => File.ReadAllText(Path.Combine(CoreRoot(), "dotnet", "tools", "pgbouncer-tests.sh")).Replace("\r\n", "\n");

    // mutations: the digest dropped from dotnet.yml's image; PACKMOAT_TEST_POSTGRES removed from its test step; the script on another digest
    [Fact]
    public void The_test_job_runs_beside_a_postgres_service_pinned_by_digest_on_the_scripts_image()
    {
        var dotnet = Workflows().Single(w => w.Name == "dotnet.yml").Text.Replace("\r\n", "\n");
        Assert.Equal(["dotnet"], PostgresService.TestJobs(dotnet));
        var (image, problems) = PostgresService.Check(dotnet, "dotnet");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.Equal(image, PostgresService.ScriptImage(Script()));
        // The PgBouncer beside it runs the image dotnet/tools/pgbouncer-tests.sh runs, and that script runs the same postgres.
        var bouncer = PostgresService.BouncerImage(dotnet, "dotnet");
        Assert.Matches(PostgresService.PinnedBouncerImage, bouncer);
        Assert.Equal(bouncer, PostgresService.ScriptBouncerImage(BouncerScript()));
        Assert.Contains($"\npg_image=\"{image}\"\n", BouncerScript());
        // No other core workflow runs a whole solution's tests, so none needs the service.
        Assert.All(Workflows().Where(w => w.Name != "dotnet.yml"), w => Assert.Empty(PostgresService.TestJobs(w.Text)));
    }

    [Fact]
    public void The_postgres_service_check_catches_each_gap()
    {
        var dotnet = Workflows().Single(w => w.Name == "dotnet.yml").Text.Replace("\r\n", "\n");
        var image = PostgresService.Check(dotnet, "dotnet").Image!;
        void Catches(string from, string to, string expect)
        {
            Assert.Contains(from, dotnet);
            var problems = PostgresService.Check(dotnet.Replace(from, to), "dotnet").Problems;
            Assert.True(problems.Any(p => p.Contains(expect, StringComparison.Ordinal)), $"{to.Trim()}: expected \"{expect}\", got:\n" + string.Join("\n", problems));
        }
        Catches($"image: {image}", $"image: {image[..image.IndexOf('@')]}", "not postgres:17.x pinned by digest");
        Catches($"image: {image}", $"image: {image.Replace("postgres:17.", "postgres:16.")}", "not postgres:17.x pinned by digest");
        Catches($"image: {image}", $"image: {image[..^1]}", "not postgres:17.x pinned by digest");
        Catches("--health-cmd \"pg_isready", "--health-cmd \"true", "no pg_isready health check");
        Catches("          - 5432:5432\n", "          - 5433:5432\n", "does not publish port 5432");
        Catches("          PACKMOAT_TEST_POSTGRES: ", "          PACKMOAT_TEST_POSTGRESQL: ", "does not set PACKMOAT_TEST_POSTGRES");
        Catches("pm-ci-throwaway   # gitleaks:allow\n", "pm-ci-throwaway\n", "a password without gitleaks:allow");
        Catches("    services:\n      postgres:\n", "    services:\n      pg:\n", "no postgres service");
        // The PgBouncer service and its variable.
        var bouncer = PostgresService.BouncerImage(dotnet, "dotnet")!;
        Catches("      pgbouncer:\n", "      bouncer:\n", "no pgbouncer service");
        Catches($"image: {bouncer}", $"image: {bouncer[..bouncer.IndexOf('@')]}:latest", "not edoburu/pgbouncer pinned by digest");
        Catches("          POOL_MODE: transaction\n", "          POOL_MODE: session\n", "not in transaction mode");
        Catches("          DB_HOST: postgres\n", "          DB_HOST: localhost\n", "does not stand in front of the postgres service");
        Catches("--health-cmd \"pg_isready -h 127.0.0.1 -p 5432", "--health-cmd \"true", "the pgbouncer service has no pg_isready health check");
        Catches("          - 6432:5432\n", "          - 6433:5432\n", "does not publish port 6432");
        Catches("          PACKMOAT_TEST_PGBOUNCER: ", "          PACKMOAT_TEST_BOUNCER: ", "does not set PACKMOAT_TEST_PGBOUNCER");
        Catches("          DB_PASSWORD: pm-ci-throwaway   # gitleaks:allow\n", "          DB_PASSWORD: pm-ci-throwaway\n", "a password without gitleaks:allow");
        Assert.Equal("edoburu/pgbouncer@sha256:x", PostgresService.ScriptBouncerImage("# x\nbouncer_image=\"edoburu/pgbouncer@sha256:x\"\n"));
        Assert.Null(PostgresService.ScriptImage("image=postgres:17\n"));
        Assert.Equal("postgres:17.1@sha256:x", PostgresService.ScriptImage("# x\nimage=\"postgres:17.1@sha256:x\"\n"));
    }

    // mutations: the trap removed; --rm dropped; the wait cut to 60; dotnet called directly (not through DOTNET)
    [Fact]
    public void The_postgres_tests_script_runs_the_image_throwaway_waits_and_stops_it()
    {
        var script = Script();
        Assert.StartsWith("#!/usr/bin/env bash\n", script);
        Assert.Contains("\nset -euo pipefail\n", script);
        Assert.Contains("docker run --rm -d -p \"127.0.0.1:${port}:5432\"", script);
        Assert.Contains("\nport=55432\n", script);
        Assert.Contains("\ntrap 'docker stop \"$container\" > /dev/null' EXIT\n", script);
        Assert.Contains("for _ in $(seq 1 120); do", script);
        Assert.Contains("pg_isready -h 127.0.0.1", script);
        Assert.Contains("\nexport PACKMOAT_TEST_POSTGRES=", script);
        Assert.Contains("\n\"${DOTNET:-dotnet}\" test \"$dotnet_dir/Packmoat.sln\" --filter Category=Postgres \"$@\"\n", script);
        Assert.All(script.Split('\n').Where(l => l.Contains("password=\"", StringComparison.Ordinal) || l.Contains("Password=", StringComparison.Ordinal)),
            l => Assert.Contains("gitleaks:allow", l));
    }

    // ---- The image's PostgreSQL smoke, on the services' image, run where the image is built and where it is published.

    private static string SmokeScript() =>
        File.ReadAllText(Path.Combine(CoreRoot(), ".github", "scripts", "image-postgres-smoke.sh")).Replace("\r\n", "\n");

    // mutations: the smoke on another digest; its step dropped from dotnet.yml or core-image.yml; the trap that removes its containers removed;
    // the superuser refusal or the app role's grants left out
    [Fact]
    public void The_image_postgres_smoke_runs_the_services_image_where_the_image_is_built_and_published()
    {
        var script = SmokeScript();
        var dotnet = Workflows().Single(w => w.Name == "dotnet.yml").Text.Replace("\r\n", "\n");
        var service = PostgresService.Check(dotnet, "dotnet").Image!;
        Assert.Equal(service, Regex.Match(script, @"(?m)^postgres=""([^""]+)""").Groups[1].Value);
        Assert.StartsWith("#!/usr/bin/env bash\n", script);
        Assert.Contains("\nset -euo pipefail\n", script);
        Assert.Contains("docker rm -f \"${server}\" \"${db}\"", script[..script.IndexOf("docker run", StringComparison.Ordinal)]);   // the trap, before any container
        Assert.Contains("admin pm_owner db migrate --grant-to pm_app", script);
        Assert.Contains("run_server pm_app", script);
        Assert.Contains("run_server postgres", script);
        Assert.Contains("is not safe to serve from. Refusing to start", script);
        Assert.Contains("run: .github/scripts/image-postgres-smoke.sh packmoat-core:ci\n", dotnet);
        var publisher = Workflows().Single(w => w.Name == Publisher).Text.Replace("\r\n", "\n");
        var smoke = publisher.IndexOf("image-postgres-smoke.sh packmoat-core:smoke", StringComparison.Ordinal);
        Assert.True(smoke > publisher.IndexOf("port-smoke.sh packmoat-core:smoke", StringComparison.Ordinal) && smoke < publisher.IndexOf("cosign sign", StringComparison.Ordinal),
            "the published image's PostgreSQL smoke must run after the PORT smoke and before the image is signed");
    }
}
