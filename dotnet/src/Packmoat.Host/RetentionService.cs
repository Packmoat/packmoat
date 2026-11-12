using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Host;

/// <summary>
/// The daily retention pass: blanks the personal data (confirmed admins, the checker's display name, result details) of every feed check older than
/// <see cref="AzureSetupRules.ConfirmedAdminsRetention"/>, so a server that runs for weeks without a restart or a recorded check still
/// keeps them no longer than 30 days (plus at most a day). The blanking at start-up and before each recorded check stays as it was.
/// Each pass also counts the tenants due for purge and, when there are any, sends one alert with the count only. It never deletes:
/// the purge is the operator's command (packmoat-admin tenant purge).
/// The first pass runs <see cref="StartupDelay"/> after start (never later than one interval), then one every interval: the server
/// restarts on most deploys, so waiting a whole day first could mean the nudge never runs. At most one nudge is sent per UTC day, however
/// often the server restarts: the day is claimed in the database (<see cref="ITenantPurgeStore.TryClaimNudgeDayAsync"/>) before the alert.
/// Each pass also deletes the per-version decision records not seen for <see cref="DecisionRecordPolicy.RetentionDays"/> days,
/// and the admin_actions rows older than <see cref="TenantPurgePolicy.AdminActionsRetention"/> (the database refuses younger ones).
/// It also deletes the audit_events rows older than <see cref="AuditPolicy.RetentionDays"/> days (in chunks; the database refuses younger
/// ones) and the token_activity days older than <see cref="AuditPolicy.TokenActivityRetentionDays"/>.
/// Who is due is the purge eligibility's to say: with none configured the nudge counts 0. A module's own sweep that changes who is
/// due runs as the server starts and then on the same interval, so it comes before the nudge.
/// A failed pass is logged by kind only and retried next time.
/// <para>The steps are <see cref="RetentionPass"/>'s, so the operator's command (packmoat-admin jobs run retention) runs the same
/// ones. This service runs only while the server's background jobs are on (PACKMOAT_BACKGROUND_JOBS).</para>
/// <para>Each pass takes the job lease <see cref="JobLeases.Retention"/> first, the same one the operator's command takes, so two
/// copies of the server, or a copy and the command, never run it at once: a copy that does not get it skips the pass and logs so. Each pass
/// that runs logs its duration and ends its lease.</para>
/// </summary>
public sealed class RetentionService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    /// <summary>How long after start the first pass runs: long enough for start-up to settle, short enough to run before the next deploy.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>The audit prune's margin: <see cref="RetentionPass.AuditPruneMargin"/>.</summary>
    public static readonly TimeSpan AuditPruneMargin = RetentionPass.AuditPruneMargin;

    private readonly ILogger _log;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _firstPass;
    private readonly RetentionPass _pass;
    private readonly IJobLease _lease;
    private readonly string _holder;

    /// <param name="interval">A test seam: production always uses <see cref="Interval"/>.</param>
    /// <param name="purges">Where the tenants due for purge are counted (by its eligibility); no nudge when null or when it has none.</param>
    /// <param name="firstPass">A test seam: when the first pass runs (production: <see cref="StartupDelay"/>, or the interval if that is shorter).</param>
    /// <param name="decisions">Where the decision records are pruned; no pruning when null.</param>
    /// <param name="actions">Where admin_actions is pruned; no pruning when null.</param>
    /// <param name="audit">Where audit_events is pruned; no pruning when null.</param>
    /// <param name="tokenActivity">Where token_activity is pruned; no pruning when null.</param>
    /// <param name="rates">Where the rate counter's ended windows are deleted; no pruning when null.</param>
    /// <param name="nonces">Where expired form nonces are deleted; no pruning when null.</param>
    /// <param name="scratch">Where the feed setup scratch's expired rows are deleted; no pruning when null.</param>
    /// <param name="lease">The job lease each pass takes (production: the database's); always granted when null.</param>
    /// <param name="holder">A test seam: this copy's holder id (production: <see cref="JobLeases.ProcessHolder"/>).</param>
    public RetentionService(IFeedCheckStore checks, IClock clock, ILogger log, TimeSpan? interval = null,
        ITenantPurgeStore? purges = null, IOpsAlerts? alerts = null, TimeSpan? firstPass = null,
        IDecisionRecordLog? decisions = null, IAdminActionLog? actions = null, IAuditRetention? audit = null,
        ITokenActivityRetention? tokenActivity = null, IJobLease? lease = null, string? holder = null, IRateCounter? rates = null, IFormNonceStore? nonces = null,
        IFeedSetupScratch? scratch = null)
    {
        _lease = lease ?? UnsharedJobLease.Instance;
        _holder = holder ?? JobLeases.ProcessHolder;
        _log = log;
        _interval = interval ?? Interval;
        _firstPass = firstPass ?? (_interval < StartupDelay ? _interval : StartupDelay);
        _pass = new RetentionPass(checks, clock, purges, alerts, decisions, actions, audit, tokenActivity, rates, nonces, scratch);
    }

    /// <summary>When the first pass runs after start (for tests of the wiring).</summary>
    internal TimeSpan FirstPass => _firstPass;

    /// <summary>The lease each pass takes (for tests of the wiring).</summary>
    internal IJobLease Lease => _lease;

    /// <summary>
    /// The blanking at start-up: runs once, synchronously, and never stops the server starting. A failure is logged by kind
    /// only and left to the daily pass. Returns how many checks were blanked (0 on failure or with no store). It runs whether or not the
    /// background jobs are on.
    /// </summary>
    public static int BlankAtStartup(IFeedCheckStore? checks, IClock clock, ILogger log)
    {
        if (checks is null) return 0;
        try { return checks.BlankPersonalDataAsync(clock.UtcNow - AzureSetupRules.ConfirmedAdminsRetention, default).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError("Retention at start-up failed ({Kind}); starting anyway, the daily pass tries again", ex.GetType().Name);
            return 0;
        }
    }

    /// <summary>One pass: returns how many checks had their personal data blanked.</summary>
    public Task<int> RunOnceAsync(CancellationToken ct) => _pass.BlankFeedChecksAsync(ct);

    /// <summary>
    /// The purge nudge: counts the tenants due now (no payment-provider call, nothing deleted) and, when there are any and today (UTC) has
    /// not had a nudge yet, alerts the count. Returns the count.
    /// </summary>
    public Task<int> NudgePurgesAsync(CancellationToken ct) => _pass.NudgePurgesAsync(LogDue, ct);

    /// <summary>Deletes the decision records last seen more than <see cref="DecisionRecordPolicy.RetentionDays"/> days ago. Returns how many.</summary>
    public Task<int> PruneDecisionsAsync(CancellationToken ct) => _pass.PruneDecisionsAsync(ct);

    /// <summary>
    /// Deletes the admin_actions rows older than <see cref="TenantPurgePolicy.AdminActionsRetention"/> and leaves one row saying how many
    /// (<see cref="IAdminActionLog.PruneAsync"/>). Returns how many.
    /// </summary>
    public Task<int> PruneAdminActionsAsync(CancellationToken ct) => _pass.PruneAdminActionsAsync(ct);

    /// <summary>Deletes the audit_events rows older than <see cref="AuditPolicy.RetentionDays"/> days (plus <see cref="AuditPruneMargin"/>). Returns how many.</summary>
    public Task<int> PruneAuditAsync(CancellationToken ct) => _pass.PruneAuditAsync(ct);

    /// <summary>
    /// Deletes the token_activity days before the last <see cref="AuditPolicy.TokenActivityRetentionDays"/> UTC days (today included: the
    /// longest window the active-developer count can ask for). Returns how many rows.
    /// </summary>
    public Task<int> PruneTokenActivityAsync(CancellationToken ct) => _pass.PruneTokenActivityAsync(ct);

    private void LogDue(int due) => _log.LogWarning("Retention: {Count} tenant(s) due for purge; run packmoat-admin tenant purge-due", due);

    /// <summary>One whole pass under the lease: skipped when another copy holds it. Returns whether it ran.</summary>
    internal Task<bool> PassAsync(CancellationToken ct) =>
        LeasedJobs.RunAsync(_lease, JobLeases.Retention, _holder, _log, c => _pass.RunAsync(nudge: true, Log, c, LogDue), ct);

    /// <summary>Each step's line, as before the steps moved: a count above 0, or a failure by kind only.</summary>
    private void Log(RetentionStep step)
    {
        if (step.Failure is { } ex)
        {
            var kind = ex.GetType().Name;
            switch (step.Kind)
            {
                case RetentionStepKind.FeedChecksBlanked: _log.LogError("Retention pass failed ({Kind}); next try in a day", kind); break;
                case RetentionStepKind.TenantsDueForPurge: _log.LogError("Retention: counting tenants due for purge failed ({Kind}); next try in a day", kind); break;
                case RetentionStepKind.DecisionRecordsPruned: _log.LogError("Retention: pruning decision records failed ({Kind}); next try in a day", kind); break;
                case RetentionStepKind.AdminActionsPruned: _log.LogError("Retention: pruning admin actions failed ({Kind}); next try in a day", kind); break;
                case RetentionStepKind.AuditEventsPruned: _log.LogError("Retention: pruning audit events failed ({Kind}); next try in a day", kind); break;
                case RetentionStepKind.TokenActivityPruned: _log.LogError("Retention: pruning the per-day activity record failed ({Kind}); next try in a day", kind); break;
                case RetentionStepKind.RateWindowsPruned: _log.LogError("Retention: pruning ended rate-limit windows failed ({Kind}); next try in a day", kind); break;
                case RetentionStepKind.FormNoncesPruned: _log.LogError("Retention: pruning expired form nonces failed ({Kind}); next try in a day", kind); break;
                case RetentionStepKind.FeedSetupScratchPruned: _log.LogError("Retention: pruning the feed setup wizard's expired results and drafts failed ({Kind}); next try in a day", kind); break;
            }
            return;
        }
        if (step.Count <= 0) return;
        switch (step.Kind)
        {
            case RetentionStepKind.FeedChecksBlanked: _log.LogInformation("Retention: personal data blanked on {Count} feed check(s) older than 30 days", step.Count); break;
            case RetentionStepKind.DecisionRecordsPruned: _log.LogInformation("Retention: {Count} decision record(s) not seen for {Days} days deleted", step.Count, DecisionRecordPolicy.RetentionDays); break;
            case RetentionStepKind.AdminActionsPruned: _log.LogInformation("Retention: {Count} admin action record(s) older than 12 months deleted", step.Count); break;
            case RetentionStepKind.AuditEventsPruned: _log.LogInformation("Retention: {Count} audit event(s) older than {Days} days deleted", step.Count, AuditPolicy.RetentionDays); break;
            case RetentionStepKind.TokenActivityPruned: _log.LogInformation("Retention: {Count} per-day activity row(s) older than {Days} days deleted", step.Count, AuditPolicy.TokenActivityRetentionDays); break;
            case RetentionStepKind.RateWindowsPruned: _log.LogInformation("Retention: {Count} ended rate-limit window(s) deleted", step.Count); break;
            case RetentionStepKind.FormNoncesPruned: _log.LogInformation("Retention: {Count} expired form nonce(s) deleted", step.Count); break;
            case RetentionStepKind.FeedSetupScratchPruned: _log.LogInformation("Retention: {Count} expired feed setup result(s) and draft(s) deleted", step.Count); break;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_firstPass, stoppingToken);
            await PassAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_interval, stoppingToken);
                await PassAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
