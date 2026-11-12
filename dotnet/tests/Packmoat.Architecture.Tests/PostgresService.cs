using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// The PostgreSQL service the test jobs run beside, read from a workflow: every job that runs a whole solution's tests
/// (<c>dotnet test</c> with no <c>--filter</c>) has a <c>postgres</c> service on one image pinned by digest, with a pg_isready health check
/// and port 5432, and sets PACKMOAT_TEST_POSTGRES on each such step; a line that holds the throwaway password is marked gitleaks:allow.
/// Without the service those jobs would fail (CI=true turns a skipped Postgres test into a failure), so this check says why first.
/// Shared with the hosted twin (linked into Packmoat.Hosted.Architecture.Tests), which applies it to the root workflows.
/// </summary>
internal static class PostgresService
{
    public const string Variable = "PACKMOAT_TEST_POSTGRES";

    /// <summary>The one supported major version, pinned by the index digest (the official image's multi-platform index).</summary>
    public static readonly Regex PinnedImage = new(@"^postgres:17\.\d+@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The PgBouncer in transaction mode beside it, for the pooler test (PACKMOAT_TEST_PGBOUNCER): the image
    /// tools/pgbouncer-tests.sh runs, pinned by digest.
    /// </summary>
    public const string BouncerVariable = "PACKMOAT_TEST_PGBOUNCER";

    public static readonly Regex PinnedBouncerImage = new(@"^edoburu/pgbouncer@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private static YamlMappingNode? Map(YamlNode? node, string key) =>
        node is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v as YamlMappingNode : null;

    private static string? Text(YamlNode? node, string key) =>
        node is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s ? s.Value : null;

    private static bool RunsWholeTests(string? run) =>
        run is not null && Regex.IsMatch(run, @"\bdotnet test\b") && !run.Contains("--filter", StringComparison.Ordinal);

    /// <summary>The ids of the jobs in <paramref name="workflow"/> that run a whole solution's tests.</summary>
    public static string[] TestJobs(string workflow)
    {
        var jobs = Map(Load(workflow), "jobs");
        if (jobs is null) return [];
        return jobs.Children
            .Where(j => Steps(j.Value).Any(s => RunsWholeTests(Text(s, "run"))))
            .Select(j => ((YamlScalarNode)j.Key).Value!)
            .ToArray();
    }

    private static YamlMappingNode Load(string workflow)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(workflow));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static IEnumerable<YamlMappingNode> Steps(YamlNode job) =>
        job is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode("steps"), out var s) && s is YamlSequenceNode seq
            ? seq.OfType<YamlMappingNode>() : [];

    /// <summary>
    /// What job <paramref name="jobId"/> of <paramref name="workflow"/> lacks, one line each (empty when nothing), and the image its postgres
    /// service names (null when none).
    /// </summary>
    public static (string? Image, IReadOnlyList<string> Problems) Check(string workflow, string jobId)
    {
        var found = new List<string>();
        var job = Map(Map(Load(workflow), "jobs"), jobId);
        if (job is null) return (null, [$"{jobId}: no such job"]);
        var service = Map(Map(job, "services"), "postgres");
        var image = Text(service, "image");
        if (service is null) found.Add($"{jobId}: no postgres service");
        else
        {
            if (image is null || !PinnedImage.IsMatch(image)) found.Add($"{jobId}: the postgres image is not postgres:17.x pinned by digest (postgres:17.x@sha256:<64 hex>): {image}");
            if (Text(service, "options") is not { } options || !Regex.IsMatch(options, @"--health-cmd\s+""pg_isready\b"))
                found.Add($"{jobId}: the postgres service has no pg_isready health check");
            var ports = Map(job, "services") is { } services && Map(services, "postgres") is { } p && p.Children.TryGetValue(new YamlScalarNode("ports"), out var pn)
                && pn is YamlSequenceNode seq ? seq.OfType<YamlScalarNode>().Select(n => n.Value).ToArray() : [];
            if (!ports.Contains("5432:5432")) found.Add($"{jobId}: the postgres service does not publish port 5432");
        }
        var bouncer = Map(Map(job, "services"), "pgbouncer");
        if (bouncer is null) found.Add($"{jobId}: no pgbouncer service");
        else
        {
            var bouncerImage = Text(bouncer, "image");
            if (bouncerImage is null || !PinnedBouncerImage.IsMatch(bouncerImage))
                found.Add($"{jobId}: the pgbouncer image is not edoburu/pgbouncer pinned by digest (edoburu/pgbouncer@sha256:<64 hex>): {bouncerImage}");
            if (Text(Map(bouncer, "env"), "POOL_MODE") != "transaction") found.Add($"{jobId}: the pgbouncer service is not in transaction mode (POOL_MODE: transaction)");
            if (Text(Map(bouncer, "env"), "DB_HOST") != "postgres") found.Add($"{jobId}: the pgbouncer service does not stand in front of the postgres service (DB_HOST: postgres)");
            if (Text(bouncer, "options") is not { } options || !Regex.IsMatch(options, @"--health-cmd\s+""pg_isready\b"))
                found.Add($"{jobId}: the pgbouncer service has no pg_isready health check");
            var ports = bouncer.Children.TryGetValue(new YamlScalarNode("ports"), out var pn) && pn is YamlSequenceNode seq
                ? seq.OfType<YamlScalarNode>().Select(n => n.Value).ToArray() : [];
            if (!ports.Contains("6432:5432")) found.Add($"{jobId}: the pgbouncer service does not publish port 6432");
        }
        var tests = Steps(job).Where(s => RunsWholeTests(Text(s, "run"))).ToArray();
        if (tests.Length == 0) found.Add($"{jobId}: no step runs dotnet test");
        foreach (var step in tests)
        {
            var value = Text(Map(step, "env"), Variable);
            if (value is null || !value.Contains("Host=localhost;Port=5432;", StringComparison.Ordinal))
                found.Add($"{jobId}: the step \"{Text(step, "name")}\" does not set {Variable} to the service (Host=localhost;Port=5432;...)");
            var bouncerValue = Text(Map(step, "env"), BouncerVariable);
            if (bouncerValue is null || !bouncerValue.Contains("Host=localhost;Port=6432;", StringComparison.Ordinal))
                found.Add($"{jobId}: the step \"{Text(step, "name")}\" does not set {BouncerVariable} to the pgbouncer service (Host=localhost;Port=6432;...)");
        }
        foreach (var line in workflow.Split('\n').Where(l => Regex.IsMatch(l, "POSTGRES_PASSWORD|DB_PASSWORD|Password=", RegexOptions.IgnoreCase)))
            if (!line.Contains("gitleaks:allow", StringComparison.Ordinal)) found.Add($"a line holds a password without gitleaks:allow: {line.Trim()}");
        return (image, found);
    }

    /// <summary>The image job <paramref name="jobId"/>'s pgbouncer service names, or null.</summary>
    public static string? BouncerImage(string workflow, string jobId) =>
        Text(Map(Map(Map(Map(Load(workflow), "jobs"), jobId), "services"), "pgbouncer"), "image");

    /// <summary>The PgBouncer image tools/pgbouncer-tests.sh runs (its <c>bouncer_image=</c> line).</summary>
    public static string? ScriptBouncerImage(string script) =>
        Regex.Match(script, @"(?m)^bouncer_image=""([^""]+)""") is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The image tools/postgres-tests.sh runs (its <c>image=</c> line).</summary>
    public static string? ScriptImage(string script) =>
        Regex.Match(script, @"(?m)^image=""([^""]+)""") is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary><paramref name="workflow"/> without its jobs' <c>services:</c> blocks, for a self-test that adds its own.</summary>
    public static string WithoutServices(string workflow) =>
        Regex.Replace(workflow.Replace("\r\n", "\n"), @"(?m)^    services:\n(?:(?:      .*|[ \t]*)\n)*", "");
}
