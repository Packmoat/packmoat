using System.Text.RegularExpressions;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// The core's own workflows, core/.github/workflows (#197 E): the public repository runs them with core/ as its root, so they may name only
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
    // First segments that name no folder in core-image.yml, and only there (#266): a registry host (ghcr.io/x/y, docker.io/x/y), a
    // platform (linux/amd64), a git ref (refs/heads/main) and the repository its guard names (Packmoat/packmoat, pinned by PublisherProblems).
    private static readonly string[] PublisherNotFolders = ["docker.io", "ghcr.io", "linux", "refs", "Packmoat"];
    private static readonly Regex PathKey = new(@"^\s*-?\s*(working-directory|global-json-file|cache-dependency-path|path):\s*['""]?([^'""\s]+)", RegexOptions.CultureInvariant);

    /// <summary>
    /// What a workflow names outside <paramref name="root"/>, one line each: a <c>..</c> segment; a relative path whose first segment is
    /// in neither the root nor one of the workflow's working directories; a working-directory or global-json-file that is not in the root;
    /// and the word "hosted" anywhere, comments included. Comments are otherwise not read, and neither is an action reference (<c>uses:</c>).
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
            if (Regex.IsMatch(line, @"^\s*-?\s*uses:")) continue;
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
    public void The_core_has_its_conformance_image_dotnet_gitleaks_and_helm_workflows() =>
        Assert.Equal(["conformance.yml", "core-image.yml", "dotnet.yml", "gitleaks.yml", "helm.yml"], Workflows().Select(w => w.Name).ToArray());

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
            // #266: in core-image.yml only, an image name, a platform, a git ref and the guard's repository are not paths.
            const string publisher = """
                        if: github.repository == 'Packmoat/packmoat' && github.ref == 'refs/heads/main'
                        env:
                          IMAGE: ghcr.io/packmoat/packmoat-core
                          image: docker.io/tonistiigi/binfmt:qemu
                          platforms: linux/amd64,linux/arm64
                        run: cosign verify --certificate-identity "https://github.com/${GITHUB_REPOSITORY}/.github/workflows/x.yml@refs/heads/main"
                """;
            Assert.Empty(OutsidePaths(publisher, root, PublisherNotFolders));
            Assert.Equal(7, OutsidePaths(publisher, root).Count);                         // in any other workflow each is a path outside
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
            Assert.Equal(2, OutsidePaths("        working-directory: core/dotnet\n", root).Count);   // the path, and the working directory
            Assert.Single(OutsidePaths("        run: dotnet build Packmoat.Hosted.sln\n", root));
            Assert.Single(OutsidePaths("        # the hosted image\n", root));
        }
        finally { Directory.Delete(root, true); }
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
        found.AddRange(triggers.Where(t => !AllowedTriggers.Contains(t)).Select(t => $"trigger {t} is not one of {string.Join(", ", AllowedTriggers)}"));

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

    /// <summary>The one workflow that publishes (#266): the signed core image, only from main in the public repository, never on a pull request.</summary>
    private const string Publisher = "core-image.yml";

    /// <summary>
    /// The hygiene problems <see cref="Publisher"/> may have, because publishing needs them: a write permission, an identity token, and the
    /// built-in token as the registry login's password. <see cref="PublisherProblems"/> pins each to exactly that use.
    /// </summary>
    private static bool PublisherMayHave(string problem) =>
        problem is "says : write" or "says id-token"
        || Regex.IsMatch(problem, @"^line \d+: a secret or token: password: \$\{\{ secrets\.GITHUB_TOKEN \}\}$", RegexOptions.CultureInvariant);

    [Fact]   // mutations, each in a copy of dotnet.yml: issue_comment; the PR title in a run step; ${{ env.TITLE }} in a run step; secrets['NPM_TOKEN']; github.token; cache: npm
             // (#266): packages: write in dotnet.yml; secrets.NPM_TOKEN in core-image.yml
    public void The_core_workflows_are_safe_for_outside_pull_requests()
    {
        var offenders = Workflows().SelectMany(w => HygieneProblems(w.Name, w.Text).Where(p => w.Name != Publisher || !PublisherMayHave(p))
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
        // #225: a run line may hold only runner.temp, github.ref, github.sha and github.repository (#236: not matrix.<name>).
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
    /// contents read, packages write and id-token write on the job; the built-in token only as the registry login's password; one image
    /// name, <see cref="CoreImage"/>; nothing tagged by the build or pushed by tag, and nothing that lets a failed command pass; and the
    /// steps in order: push by digest, pull that image, smoke it, check PORT, sign, verify as <see cref="SignerTemplate"/>, then tag.
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
        Require(Regex.IsMatch(text, @"(?m)^    permissions:\n      contents: read\n      packages: write\n      id-token: write\n(?!      )"),
            "the job's permissions are not exactly contents read, packages write and id-token write");
        Require(Regex.Matches(text, @"(?m)^\s*permissions:").Count == 2, "permissions are set somewhere other than the top and the job");
        Require(Regex.Matches(text, "packages: write").Count == 1 && Regex.Matches(text, "id-token").Count == 1, "packages: write or id-token is said more than once");

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
                          "cosign sign", "cosign verify", "imagetools create"];
        var at = order.Select(s => code.IndexOf(s, StringComparison.Ordinal)).ToArray();
        Require(at.All(i => i >= 0) && at.Zip(at.Skip(1)).All(p => p.First < p.Second),
            "the steps are not in order: push by digest, pull that image, smoke, PORT smoke, sign, verify, then tag");
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
        // #268 review: the image name overridden in a step, a second signature or push, the issuer as a pattern, credentials kept.
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
    }

    [Fact]   // mutations: the doc's signer in lower case (cosign compares it exactly); the doc's image under <owner>
    public void The_self_hosting_guide_names_the_published_image_and_its_signer()
    {
        var workflow = Workflows().Single(w => w.Name == Publisher).Text;
        var repository = Regex.Match(PublishGuard, "github.repository == '([^']+)'").Groups[1].Value;
        Assert.Contains($"IMAGE: {CoreImage}", workflow);
        Assert.Contains(SignerTemplate, workflow);
        var signer = SignerTemplate.Replace("${GITHUB_REPOSITORY}", repository, StringComparison.Ordinal);

        var doc = File.ReadAllText(Path.Combine(CoreRoot(), "docs", "self-hosting.md"));
        var identities = Regex.Matches(doc, @"--certificate-identity\s+(\S+)").Select(m => m.Groups[1].Value).ToArray();
        Assert.NotEmpty(identities);
        Assert.All(identities, i => Assert.Equal(signer, i));
        // The guide and the Helm chart (its values, its refusal message and the restore pod) name the published image and no other.
        foreach (var file in new[] { "docs/self-hosting.md", "deploy/helm/packmoat/values.yaml", "deploy/helm/packmoat/templates/_helpers.tpl", "deploy/helm/restore-pod.yaml" })
        {
            var images = Regex.Matches(File.ReadAllText(Path.Combine(CoreRoot(), file)), @"ghcr\.io/[^\s`:@,""')]+").Select(m => m.Value).ToArray();
            Assert.True(images.Length > 0, $"{file} names no ghcr.io image");
            Assert.All(images, i => Assert.Equal(CoreImage, i));
        }
    }

    [Fact]   // mutations: --strict dropped; --redact dropped; fetch-depth: 0 removed; (#268 review) || true after the hash check; set -- in the script;
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

    // The review cases for #228, #230, #234 and #236 (b, c, n, r, p), each a job header or a step added to dotnet.yml, as the reviews'
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
    // #228 review.
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
    // #230.
    [InlineData("n1_flow_sequence_steps", "", $"  second:\n    runs-on: ubuntu-24.04\n    steps: [ {{ run: \"echo ${{{{ {T} }}}}\" }} ]\n", InRun)]
    [InlineData("n2_flow_mapping_quoted_key", "", $"      - {{ name: x, \"run\": \"echo ${{{{ {T} }}}}\" }}\n", InRun)]
    [InlineData("n3_with_comment_first", "", GithubScript + "        with:\n              # an over-indented comment sets the key indent\n          script: |\n            return \"${{ env.TITLE }}\"\n", InScript)]
    [InlineData("n3b_with_comment_first_event", "", GithubScript + "        with:\n              # an over-indented comment sets the key indent\n          script: |\n            return \"${{ steps.x.outputs.title }}\"\n", InScript)]
    [InlineData("n4_flow_seq_with", "", "  third:\n    runs-on: ubuntu-24.04\n    steps:\n" + GithubScript + "        with: {script: \"return '${{ env.TITLE }}'\"}\n", InScript)]
    // #234 review.
    [InlineData("r1_multiline_flow_run_after_comma", "", $"      - {{\n          name: x, run: \"echo ${{{{ {T} }}}}\"\n        }}\n", InRun)]
    [InlineData("r2_multiline_flow_with_script", "", GithubScript + "        with:\n          {\n            script: \"return '${{ env.TITLE }}'\"\n          }\n", InScript)]
    [InlineData("r3_multiline_flow_seq", "", $"  second:\n    runs-on: ubuntu-24.04\n    steps: [\n      {{\n        name: x, run: \"echo ${{{{ {T} }}}}\"\n      }}\n    ]\n", InRun)]
    [InlineData("r4_with_blank_comment", "", GithubScript + "        with:\n\n              # comment\n          script: |\n            return \"${{ env.TITLE }}\"\n", InScript)]
    [InlineData("r5_comment_between_inputs", "", GithubScript + "        with:\n          result-encoding: string\n              # comment\n          script: |\n            return \"${{ env.TITLE }}\"\n", InScript)]
    // #236: what the parser itself must refuse, or walk into.
    [InlineData("p1_parse_error", "", $"      - run: \"echo ${{{{ {T} }}}}\n", NotYaml)]
    [InlineData("p2_duplicate_key", "", $"      - run: echo hi\n        run: echo ${{{{ {T} }}}}\n", NotYaml)]
    [InlineData("p3_with_not_a_mapping", "", GithubScript + "        with: x\n", "with: is not a mapping of inputs")]
    [InlineData("p4_input_not_text", "", GithubScript + "        with:\n          script: [ \"return '${{ env.TITLE }}'\" ]\n", "the with: input script is not text")]
    [InlineData("p5_defaults_run_shell", "    defaults:\n      run:\n        shell: bash -c \"${{ env.SHELL_LINE }} {0}\"\n", "",
                "(jobs.dotnet.defaults.run.shell): ${{ env.SHELL_LINE }} in a shell: value is not one of")]
    [InlineData("p6_input_name_case", "", GithubScript + "        with:\n          Script: return \"${{ env.TITLE }}\"\n", "in the with: input Script is not one of")]
    [InlineData("p7_merge_key", "", "      - <<: { run: \"echo ${{ env.TITLE }}\" }\n        name: x\n", ".<<.run): ${{ env.TITLE }} in a run: value is not one of")]
    // #236 review: env:, if: and concurrency: are exempt only where Actions reads them as settings.
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
        var dotnet = Workflows().Single(w => w.Name == "dotnet.yml").Text;
        Assert.Contains(JobLine, dotnet);
        var problems = WorkflowExpressions.Problems(dotnet.Replace(JobLine, JobLine + job) + step);
        if (expected.Length == 0) Assert.True(problems.Count == 0, $"{name} must pass, got:\n" + string.Join("\n", problems));
        foreach (var e in expected)
            Assert.True(problems.Any(p => p.Contains(e, StringComparison.Ordinal)), $"{name}: expected \"{e}\", got:\n" + string.Join("\n", problems));
    }
}
