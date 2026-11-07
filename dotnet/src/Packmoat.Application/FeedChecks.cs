namespace Packmoat.Application;

/// <summary>How a run of the feed setup check ended: every required check passed, something failed, or something could not be verified.</summary>
public enum FeedCheckOutcome { Passed, Failed, Incomplete }

/// <summary>
/// What a check row is about (migration 14). <see cref="Settings"/>: an Admin's check of the feed's settings (the wizard's step-up check).
/// <see cref="Developer"/>: a developer's proof that they cannot read the feed directly (#64 part I2). Every row written before
/// migration 14 is a settings check.
/// </summary>
public enum FeedCheckKind { Settings, Developer }

/// <summary>
/// One run of the feed setup check for a tenant, as recorded: who ran it (directory, object id and display name), exactly what was
/// checked (directory, feed, shared client id), the outcome and every result. <c>ConfirmedAdmins</c> holds the display names of the
/// feed's administrators the person confirmed: personal data, kept under the 30-day retention. Never a token. <c>Id</c> is set by the
/// store (0 before it is recorded).
/// </summary>
public sealed record FeedCheck(
    string TenantId,
    DateTimeOffset CheckedAt,
    string CheckedByTid,
    string CheckedByOid,
    string CheckedByName,
    string AzureTenantId,
    string FeedIndexUrl,
    string ClientId,
    FeedCheckOutcome Outcome,
    IReadOnlyList<CheckResult> Results,
    IReadOnlyList<string> ConfirmedAdmins)
{
    public long Id { get; init; }
    /// <summary>Settings (the default) or developer.</summary>
    public FeedCheckKind Kind { get; init; } = FeedCheckKind.Settings;
    /// <summary>The names the Admin typed for a wizard settings check (migration 16, #119); null for every other row and every older one.</summary>
    public FeedNames? Names { get; init; }
}

/// <summary>
/// The feed check history (<c>feed_checks</c>). Append-only: a check is never changed once recorded, except that its personal data is
/// blanked after 30 days (<see cref="IFeedCheckStore.BlankPersonalDataAsync"/>).
/// </summary>
public interface IFeedCheckStore
{
    /// <summary>Records the check and returns its id. Throws when the tenant does not exist (nothing is written).</summary>
    Task<long> RecordAsync(FeedCheck check, CancellationToken cancellationToken);
    /// <summary>The tenant's newest check of this kind (by time, then by the order written), or null when it has none.</summary>
    Task<FeedCheck?> LatestAsync(string tenantId, FeedCheckKind kind, CancellationToken cancellationToken);
    /// <summary>
    /// The 30-day retention of personal data (#63 H2, #107). For every check made before <paramref name="checkedBefore"/>: empties
    /// <c>confirmed_admins</c>, blanks <c>checked_by_name</c> and redacts the results (<see cref="FeedCheckRedaction.Redact"/>), in one
    /// transaction. The checker's tid and oid are kept until the tenant purge (pseudonymous; they link to admin_actions), as are the
    /// outcome, kind and feed names. The rows themselves stay (a verification record may refer to one). Idempotent: returns how many
    /// rows changed, so 0 when run again.
    /// </summary>
    Task<int> BlankPersonalDataAsync(DateTimeOffset checkedBefore, CancellationToken cancellationToken);
}

/// <summary>
/// The 30-day redaction of a check's results (#107), pure. A detail can name a person (an admin, a member who holds a role), so every
/// detail is replaced, whatever the check: a future check that adds names is covered without a change here (fail closed). Each result's
/// id, title and status are kept, in order, so the history still says what was checked and how it went. A result's feed names are dropped.
/// </summary>
public static class FeedCheckRedaction
{
    public const string RemovedDetail = "Details removed after 30 days.";

    public static IReadOnlyList<CheckResult> Redact(IEnumerable<CheckResult> results) =>
        results.Select(r => new CheckResult(r.Id, r.Title, r.Status, RemovedDetail)).ToList();
}
