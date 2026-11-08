using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>One step of the retention pass, in the order the pass runs them.</summary>
public enum RetentionStepKind
{
    /// <summary>Feed checks' personal data blanked after <see cref="AzureSetupRules.ConfirmedAdminsRetention"/>.</summary>
    FeedChecksBlanked,
    /// <summary>Tenants due for purge, counted (nothing is deleted).</summary>
    TenantsDueForPurge,
    DecisionRecordsPruned,
    AdminActionsPruned,
    AuditEventsPruned,
    TokenActivityPruned,
}

/// <summary>What one step did: how many rows, or the failure (whose message is never shown: only its type).</summary>
public sealed record RetentionStep(RetentionStepKind Kind, int Count, Exception? Failure)
{
    public bool Failed => Failure is not null;
}

/// <summary>
/// The retention pass's steps (#64 I1, #107, #105, #123, #117, #136), moved out of the server's daily service (#233) so the operator's
/// command (packmoat-admin jobs run retention) runs the same steps where the server's background jobs are off. Each step is its own: one
/// that fails does not stop the next. Who is due for purge is the purge eligibility's to say (#194). It never deletes a tenant.
/// <para>Only the server's pass claims the day's purge nudge and alerts (<see cref="ITenantPurgeStore.TryClaimNudgeDayAsync"/>); the
/// command counts and prints, so a run by hand cannot take the day's nudge from the server.</para>
/// </summary>
public sealed class RetentionPass
{
    /// <summary>
    /// How much older than <see cref="AuditPolicy.Retention"/> an audit row must be before the prune selects it, so this server's clock
    /// and the database's 'now' (the floor the database enforces) cannot straddle a row and have the database refuse the whole chunk.
    /// </summary>
    public static readonly TimeSpan AuditPruneMargin = TimeSpan.FromMinutes(1);

    private readonly IFeedCheckStore? _checks;
    private readonly IClock _clock;
    private readonly ITenantPurgeStore? _purges;
    private readonly IOpsAlerts _alerts;
    private readonly IDecisionRecordLog? _decisions;
    private readonly IAdminActionLog? _actions;
    private readonly IAuditRetention? _audit;
    private readonly ITokenActivityRetention? _tokenActivity;

    /// <param name="checks">Where feed checks are blanked; nothing blanked when null.</param>
    /// <param name="purges">Where the tenants due for purge are counted (by its eligibility); 0 when null or when it has none.</param>
    /// <param name="alerts">Where the nudge is sent; only <see cref="NudgePurgesAsync"/> sends.</param>
    /// <param name="decisions">Where the decision records are pruned; no pruning when null.</param>
    /// <param name="actions">Where admin_actions is pruned; no pruning when null.</param>
    /// <param name="audit">Where audit_events is pruned; no pruning when null.</param>
    /// <param name="tokenActivity">Where token_activity is pruned; no pruning when null.</param>
    public RetentionPass(IFeedCheckStore? checks, IClock clock, ITenantPurgeStore? purges = null, IOpsAlerts? alerts = null,
        IDecisionRecordLog? decisions = null, IAdminActionLog? actions = null, IAuditRetention? audit = null, ITokenActivityRetention? tokenActivity = null)
    {
        _checks = checks;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _purges = purges;
        _alerts = alerts ?? NullOpsAlerts.Instance;
        _decisions = decisions;
        _actions = actions;
        _audit = audit;
        _tokenActivity = tokenActivity;
    }

    /// <summary>Blanks the personal data of feed checks older than 30 days. Returns how many.</summary>
    public Task<int> BlankFeedChecksAsync(CancellationToken ct) =>
        _checks is null ? Task.FromResult(0) : _checks.BlankPersonalDataAsync(_clock.UtcNow - AzureSetupRules.ConfirmedAdminsRetention, ct);

    /// <summary>Counts the tenants due for purge now (no payment-provider call, nothing deleted, no day claimed, no alert).</summary>
    public Task<int> CountDueAsync(CancellationToken ct) =>
        _purges is null ? Task.FromResult(0) : TenantPurgeCommands.CountDueAsync(_purges, _clock.UtcNow, ct);

    /// <summary>
    /// The server's purge nudge: counts the tenants due now and, when there are any and today (UTC) has not had a nudge yet, claims the
    /// day and alerts the count. <paramref name="onDue"/> hears the count first, when it is above 0. Returns the count.
    /// </summary>
    public async Task<int> NudgePurgesAsync(Action<int>? onDue, CancellationToken ct)
    {
        if (_purges is null) return 0;
        var now = _clock.UtcNow;
        var due = await TenantPurgeCommands.CountDueAsync(_purges, now, ct);
        if (due > 0)
        {
            onDue?.Invoke(due);
            if (await _purges.TryClaimNudgeDayAsync(DateOnly.FromDateTime(now.UtcDateTime), ct))
            {
                try { _alerts.Notify(new OpsAlert.PurgeDue(due)); }
                catch (Exception) { /* best effort by contract: an alert never fails the pass */ }
            }
        }
        return due;
    }

    /// <summary>Deletes the decision records last seen more than <see cref="DecisionRecordPolicy.RetentionDays"/> days ago. Returns how many.</summary>
    public Task<int> PruneDecisionsAsync(CancellationToken ct) =>
        _decisions is null ? Task.FromResult(0) : _decisions.PruneAsync(_clock.UtcNow - DecisionRecordPolicy.Retention, ct);

    /// <summary>
    /// Deletes the admin_actions rows older than <see cref="TenantPurgePolicy.AdminActionsRetention"/> and leaves one row saying how many
    /// (<see cref="IAdminActionLog.PruneAsync"/>). Returns how many.
    /// </summary>
    public Task<int> PruneAdminActionsAsync(CancellationToken ct)
    {
        if (_actions is null) return Task.FromResult(0);
        var now = _clock.UtcNow;
        return _actions.PruneAsync(now - TenantPurgePolicy.AdminActionsRetention, now, ct);
    }

    /// <summary>Deletes the audit_events rows older than <see cref="AuditPolicy.RetentionDays"/> days (plus <see cref="AuditPruneMargin"/>). Returns how many.</summary>
    public Task<int> PruneAuditAsync(CancellationToken ct) =>
        _audit is null ? Task.FromResult(0) : _audit.PruneAsync(_clock.UtcNow - AuditPolicy.Retention - AuditPruneMargin, ct);

    /// <summary>
    /// Deletes the token_activity days before the last <see cref="AuditPolicy.TokenActivityRetentionDays"/> UTC days (today included: the
    /// longest window the active-developer count can ask for). Returns how many rows.
    /// </summary>
    public Task<int> PruneTokenActivityAsync(CancellationToken ct) =>
        _tokenActivity is null ? Task.FromResult(0)
            : _tokenActivity.PruneAsync(DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime).AddDays(-(AuditPolicy.TokenActivityRetentionDays - 1)), ct);

    /// <summary>
    /// Every step, in order, each reported to <paramref name="report"/> as it ends. A failed step is reported and the next runs; a
    /// cancellation stops the pass. With <paramref name="nudge"/> the purge step is <see cref="NudgePurgesAsync"/> (the server's);
    /// without, <see cref="CountDueAsync"/> (the command's: no day claimed, no alert).
    /// </summary>
    public async Task<IReadOnlyList<RetentionStep>> RunAsync(bool nudge, Action<RetentionStep>? report, CancellationToken ct, Action<int>? onDue = null)
    {
        var steps = new List<RetentionStep>();
        async Task Step(RetentionStepKind kind, Func<CancellationToken, Task<int>> run)
        {
            RetentionStep step;
            try { step = new RetentionStep(kind, await run(ct), null); }
            catch (Exception ex) when (ex is not OperationCanceledException) { step = new RetentionStep(kind, 0, ex); }
            steps.Add(step);
            report?.Invoke(step);
        }

        await Step(RetentionStepKind.FeedChecksBlanked, BlankFeedChecksAsync);
        await Step(RetentionStepKind.TenantsDueForPurge, c => nudge ? NudgePurgesAsync(onDue, c) : CountDueAsync(c));
        await Step(RetentionStepKind.DecisionRecordsPruned, PruneDecisionsAsync);
        await Step(RetentionStepKind.AdminActionsPruned, PruneAdminActionsAsync);
        await Step(RetentionStepKind.AuditEventsPruned, PruneAuditAsync);
        await Step(RetentionStepKind.TokenActivityPruned, PruneTokenActivityAsync);
        return steps;
    }
}
