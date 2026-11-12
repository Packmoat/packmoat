using Packmoat.Application;

namespace Packmoat.Host.Admin;

/// <summary>
/// The admin action trail could not be written, so the action was not done. A module's handler lets it through; the core answers
/// "trail-failed". Never carries the underlying message.
/// </summary>
public sealed class AdminTrailException : Exception
{
    public AdminTrailException(string action) : base("The admin action trail could not be written for " + action + ".") => Action = action;

    public string Action { get; }
}

/// <summary>The trail guard a module's commands write through, so a failed write reaches the core as <see cref="AdminTrailException"/>.</summary>
public static class AdminTrail
{
    /// <summary>
    /// Wraps <paramref name="log"/>: any failure to append becomes <see cref="AdminTrailException"/>, logged with the action and the
    /// exception's type only (never its message). Reads pass through.
    /// </summary>
    public static IAdminActionLog Guard(IAdminActionLog log, ILogger logger) => new Guarded(log, logger);

    private sealed class Guarded(IAdminActionLog inner, ILogger logger) : IAdminActionLog
    {
        public async ValueTask AppendAsync(AdminActionRecord record, CancellationToken cancellationToken)
        {
            try { await inner.AppendAsync(record, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Admin action trail write failed for {Action} ({Kind})", record.Action, ex.GetType().Name);
                throw new AdminTrailException(record.Action);
            }
        }

        public Task<IReadOnlyList<AdminActionRecord>> LatestAsync(int limit, CancellationToken cancellationToken) => inner.LatestAsync(limit, cancellationToken);

        public Task<AdminActionRecord?> LatestByAsync(string tenantId, string action, string actorTid, string actorOid, CancellationToken cancellationToken) =>
            inner.LatestByAsync(tenantId, action, actorTid, actorOid, cancellationToken);

        public Task<int> PruneAsync(DateTimeOffset before, DateTimeOffset now, CancellationToken cancellationToken) => inner.PruneAsync(before, now, cancellationToken);
    }
}
