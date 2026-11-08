using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>
/// The core's part of the tenant purge's rules (#105, #81): how long the operator's evidence is kept. Who is due is a module's to say
/// (<see cref="IPurgeEligibility"/>), with its own rules.
/// </summary>
public static class TenantPurgePolicy
{
    /// <summary>
    /// Decision 2026-10-19 (#117), 12 months: how long admin_actions (the operator's evidence) is kept, rows about purged tenants
    /// included. A purge never deletes it; the daily retention pass prunes rows older than this (#117). Migration 18 makes the database
    /// refuse to delete a younger row, so this may be raised but never lowered below 365 days without a new migration.
    /// </summary>
    public static readonly TimeSpan AdminActionsRetention = TimeSpan.FromDays(365);
}

public enum PurgeVerdict
{
    /// <summary>Due now (the eligibility still confirms, for example with a payment provider, before anything is deleted).</summary>
    Due,
    /// <summary>Will be due at <see cref="PurgeCandidate.DueAt"/>.</summary>
    NotYet,
    /// <summary>Ended, but with no recorded date to count from: never purged; listed for a check by hand.</summary>
    Unknown,
    /// <summary>Never purged in this state.</summary>
    NotEligible,
}

/// <summary>
/// What an <see cref="IPurgeEligibility"/> decided about one tenant (#194). The core reads only these fields: the verdict and its
/// reason, when it is (or will be) due, the basis recorded with the purge, and the detail written to admin_actions. <see cref="State"/>
/// is the eligibility's own: handed back to it unchanged (to confirm, and to re-check where no other writer can change it before the delete), never read here.
/// </summary>
public sealed record PurgeCandidate(string TenantId, PurgeVerdict Verdict, string Reason, DateTimeOffset? DueAt, string Basis, string Detail)
{
    public object? State { get; init; }
}

/// <summary>
/// Who is due for purge, and the last word before a purge (#194). The core has none: with none, nobody is listed and every purge is
/// refused. A module gives one through its tenant lifecycle.
/// </summary>
public interface IPurgeEligibility
{
    /// <summary>Every tenant it assessed at <paramref name="now"/>, in any order. Reads only: no payment provider, nothing deleted.</summary>
    Task<IReadOnlyList<PurgeCandidate>> ListAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The tenant assessed at <paramref name="now"/>, or null when there is no such tenant.</summary>
    Task<PurgeCandidate?> AssessAsync(string tenantId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Asked once a due candidate's purge is recorded and before the delete, outside the delete's transaction (it may call a payment
    /// provider). Throws <see cref="PurgeRefusedException"/> for anything in doubt; returning means go ahead.
    /// </summary>
    Task ConfirmAsync(PurgeCandidate candidate, CancellationToken cancellationToken);
}

/// <summary>A purge was refused. Nothing was deleted. The message names the reason and ids only.</summary>
public sealed class PurgeRefusedException : InvalidOperationException
{
    public PurgeRefusedException(string message) : base(message) { }
}

/// <summary>A purge that happened: when, why and by whom. No personal fields.</summary>
public sealed record TenantPurgeRecord(string TenantId, DateTimeOffset PurgedAt, string Reason, string Actor);

/// <summary>How many rows one table lost in a purge.</summary>
public sealed record PurgedTable(string Table, int Rows);

/// <summary>The result of <see cref="TenantPurgeCommands.PurgeAsync"/>: done now (with per-table counts), or already done earlier (no counts).</summary>
public sealed record PurgeOutcome(bool AlreadyPurged, DateTimeOffset PurgedAt, IReadOnlyList<PurgedTable> Counts);

/// <summary>Where the purge does its one delete (Infrastructure), and the eligibility that decides and re-checks it.</summary>
public interface ITenantPurgeStore
{
    /// <summary>
    /// Who is due, or null when none is configured (then nobody is listed and every purge is refused). The same object re-checks the
    /// candidate inside <see cref="PurgeAsync"/>.
    /// </summary>
    IPurgeEligibility? Eligibility { get; }
    /// <summary>The purge record for this tenant id, or null when it was never purged.</summary>
    Task<TenantPurgeRecord?> FindPurgeAsync(string tenantId, CancellationToken cancellationToken);
    /// <summary>
    /// In ONE write transaction (taken before the first read): refuses (<see cref="PurgeRefusedException"/>, nothing deleted) unless the
    /// tenant still exists and the eligibility still finds <paramref name="candidate"/> due as it was at <see cref="TenantPurgeRecord.PurgedAt"/>;
    /// then deletes the tenant's rows, records <paramref name="record"/>, and deletes its audit events. Returns the rows deleted per table.
    /// </summary>
    Task<IReadOnlyList<PurgedTable>> PurgeAsync(PurgeCandidate candidate, TenantPurgeRecord record, CancellationToken cancellationToken);
    /// <summary>
    /// The daily nudge's once-a-day guard, kept in the database so restarts cannot repeat it: true (and <paramref name="day"/> recorded)
    /// when no nudge was recorded for that day yet; false when one was.
    /// </summary>
    Task<bool> TryClaimNudgeDayAsync(DateOnly day, CancellationToken cancellationToken);
}

/// <summary>
/// The operator's purge (the CLI only; nothing in the server deletes a tenant). Irreversible, so it fails closed at every step: an
/// eligibility must be configured; the tenant must be due (<see cref="IPurgeEligibility.AssessAsync"/>); then a record in admin_actions;
/// then the eligibility's last word (<see cref="IPurgeEligibility.ConfirmAsync"/>: it may ask a payment provider); then one
/// transaction that checks again and deletes.
/// </summary>
public sealed class TenantPurgeCommands
{
    public const string Action = "tenant.purge";

    /// <summary>The refusal when no eligibility is configured (no module says who is due).</summary>
    public const string NoEligibility = "No purge eligibility is configured. Nothing was deleted.";

    private readonly ITenantPurgeStore _store;
    private readonly IAdminActionLog _log;
    private readonly IClock _clock;

    public TenantPurgeCommands(ITenantPurgeStore store, IAdminActionLog log, IClock clock)
    {
        _store = store;
        _log = log;
        _clock = clock;
    }

    /// <summary>How many tenants are due now (no payment-provider call): for the daily nudge. 0 with no eligibility.</summary>
    public static async Task<int> CountDueAsync(ITenantPurgeStore store, DateTimeOffset now, CancellationToken ct) =>
        store.Eligibility is { } eligibility ? (await eligibility.ListAsync(now, ct)).Count(c => c.Verdict == PurgeVerdict.Due) : 0;

    /// <summary>
    /// Operator only. Tenants due now and ended ones with no date (for a check by hand), due first, each ordered by tenant id. None with
    /// no eligibility.
    /// </summary>
    public async Task<IReadOnlyList<PurgeCandidate>> ListDueAsync(ActorScope actor, CancellationToken ct)
    {
        AdminCommands.RequireOperator(actor);
        if (_store.Eligibility is not { } eligibility) return Array.Empty<PurgeCandidate>();
        var now = _clock.UtcNow;
        return (await eligibility.ListAsync(now, ct))
            .Where(c => c.Verdict is PurgeVerdict.Due or PurgeVerdict.Unknown)
            .OrderBy(c => c.Verdict == PurgeVerdict.Due ? 0 : 1).ThenBy(c => c.TenantId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Operator only. Deletes everything Packmoat holds for the tenant, except what the purge keeps (see the store). <paramref name="confirm"/>
    /// must repeat the tenant id exactly. With no eligibility it is refused before anything is read or recorded. A tenant id purged before
    /// answers <see cref="PurgeOutcome.AlreadyPurged"/> and changes nothing. Throws <see cref="KeyNotFoundException"/> for an unknown id and
    /// <see cref="PurgeRefusedException"/> for anything not due (nothing deleted).
    /// </summary>
    public async Task<PurgeOutcome> PurgeAsync(ActorScope actor, string tenantId, string? confirm, CancellationToken ct, string site = "cli")
    {
        AdminCommands.RequireOperator(actor);
        if (string.IsNullOrEmpty(tenantId) || !string.Equals(confirm, tenantId, StringComparison.Ordinal))
            throw new ArgumentException("--confirm must repeat the tenant id exactly. Nothing was deleted.");
        var eligibility = _store.Eligibility ?? throw new PurgeRefusedException(NoEligibility);
        if (await _store.FindPurgeAsync(tenantId, ct) is { } earlier) return new PurgeOutcome(true, earlier.PurgedAt, Array.Empty<PurgedTable>());

        var now = _clock.UtcNow;
        var candidate = await eligibility.AssessAsync(tenantId, now, ct) ?? throw new KeyNotFoundException($"No tenant '{tenantId}'.");
        if (candidate.TenantId != tenantId) throw new PurgeRefusedException($"The purge eligibility answered for another tenant. Nothing was deleted.");
        if (candidate.Verdict != PurgeVerdict.Due)
            throw new PurgeRefusedException($"Tenant {tenantId} is not due for purge ({candidate.Reason}{(candidate.DueAt is { } at ? $"; due {at:u}" : "")}). Nothing was deleted.");

        var intent = new AdminActionRecord(now, "", "", actor.Name, Action, tenantId, candidate.Detail, site);
        await _log.AppendAsync(intent, ct);   // no record, no purge
        try
        {
            await eligibility.ConfirmAsync(candidate, ct);
            var record = new TenantPurgeRecord(tenantId, now, candidate.Basis, actor.Name);
            var counts = await _store.PurgeAsync(candidate, record, ct);
            return new PurgeOutcome(false, now, counts);
        }
        catch
        {
            try { await _log.AppendAsync(intent with { Time = _clock.UtcNow, Action = Action + ".failed" }, CancellationToken.None); }
            catch { /* best effort: the intent row already stands */ }
            throw;
        }
    }
}
