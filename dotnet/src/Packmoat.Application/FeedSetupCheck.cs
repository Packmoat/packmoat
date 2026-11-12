namespace Packmoat.Application;

/// <summary>One check's outcome. <see cref="Skip"/> means it could not run (shown as NOT VERIFIED), which is never good enough.</summary>
public enum CheckStatus { Pass, Fail, Warn, Skip }

/// <summary>
/// One item of the customer isolation checklist: its id (for example "permissions"), title, status, and a detail that never holds a
/// credential. <paramref name="Names"/>: the feeds a result is about, when a page words them itself (the bypass advisory's); null otherwise.
/// </summary>
public sealed record CheckResult(string Id, string Title, CheckStatus Status, string Detail, IReadOnlyList<string>? Names = null);

/// <summary>
/// The feed setup check: reads a customer's Azure DevOps feed settings and probes access, one result per item of the isolation checklist.
/// The port for the operator CLI's <c>onboard check</c> (and later the setup wizard); the HTTP lives in Infrastructure.
/// </summary>
public interface IFeedSetupCheck
{
    /// <summary>Every check, once (own-app tenants).</summary>
    Task<IReadOnlyList<CheckResult>> RunAsync(CancellationToken cancellationToken);
    /// <summary>Shared-app phase 1: every check except 'proxy', which cannot pass until a pass is recorded.</summary>
    Task<IReadOnlyList<CheckResult>> RunWithoutProxyAsync(CancellationToken cancellationToken);
    /// <summary>Shared-app phase 2: the 'proxy' check alone.</summary>
    Task<CheckResult> RunProxyAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The one honest verdict over a set of results. "Isolated" is only ever said when EVERY required check passed;
/// a warning or a check that could not run is never good enough.
/// </summary>
public static class OnboardingSummary
{
    public static readonly string[] Required = { "feed", "upstreams", "permissions", "views", "developer", "private", "proxy" };

    public sealed record Verdict(int ExitCode, string Text);

    /// <summary>
    /// Required whenever they were run: 'identity' runs only for a shared-app tenant, and 'project' only when asked for (the setup wizard)
    /// on a feed in a project; then they must pass too.
    /// </summary>
    public static readonly string[] RequiredWhenPresent = { "identity", "project" };

    /// <summary>
    /// Shared-app phase 1: nothing failed, and every required check except 'proxy' (including 'identity' when it ran) PASSED.
    /// Only then is a pass recorded, so the server will serve the tenant and the 'proxy' check can run.
    /// </summary>
    public static bool PhaseOnePassed(IReadOnlyList<CheckResult> results) =>
        results.All(r => r.Status != CheckStatus.Fail)
        && Required.Where(id => id != "proxy").Concat(RequiredWhenPresent.Where(id => results.Any(r => r.Id == id)))
            .All(id => results.FirstOrDefault(r => r.Id == id)?.Status == CheckStatus.Pass);

    public static string Label(CheckStatus status) => status == CheckStatus.Skip ? "NOT VERIFIED" : status.ToString().ToUpperInvariant();

    public static Verdict Evaluate(IReadOnlyList<CheckResult> results)
    {
        var failed = results.Count(r => r.Status == CheckStatus.Fail);
        if (failed > 0) return new Verdict(1, $"{failed} check(s) FAILED. Do not go live until they pass.");

        var notPassed = Required.Concat(RequiredWhenPresent.Where(id => results.Any(r => r.Id == id))).Where(id => results.FirstOrDefault(r => r.Id == id)?.Status != CheckStatus.Pass).ToArray();
        if (notPassed.Length > 0)
            return new Verdict(2, $"NOT VERIFIED: {notPassed.Length} required check(s) did not pass cleanly ({string.Join(", ", notPassed)}). Do not treat the customer as isolated until they do.");

        return new Verdict(0, "All required checks passed. These checks do not test the customer's own network; blocking direct access to nuget.org there is the customer's job (see their checklist).");
    }
}
