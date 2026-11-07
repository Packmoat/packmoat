using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>
/// Whether people with no Packmoat role in a linked directory join a tenant automatically, as Developer (#80). One setting per tenant.
/// No row means ON. There is no cache: every sign-in and every click of an auto-joined session reads it again. A read that fails throws,
/// so the caller fails closed (no session).
/// </summary>
public interface IAutoJoinSettingStore
{
    /// <summary>True when auto-join is on for the tenant (no row: on). Throws when the setting cannot be read.</summary>
    Task<bool> IsOnAsync(string tenantId, CancellationToken cancellationToken);
    /// <summary>Stores the setting for the tenant, with when and by whom. The tenant must exist.</summary>
    Task SetAsync(string tenantId, bool enabled, DateTimeOffset at, string changedBy, CancellationToken cancellationToken);
}

/// <summary>
/// The customer site's auto-join switch (/team). Packmoat.Admin only, with a sign-in in the last 15 minutes. The tenant is ONLY
/// <c>actor.TenantId</c>, checked by the same guard as the token writes. The intent row in admin_actions is written before the change,
/// so a change that cannot be recorded does not happen; a ".failed" row follows a change that fails.
/// </summary>
public sealed class AutoJoinCommands
{
    /// <summary>Changes one person may make in <see cref="CustomerTokenRules.RateWindow"/>.</summary>
    public const int MaxChangesPerWindow = 10;
    public const string Action = "team.autojoin.set";

    private readonly IAutoJoinSettingStore _store;
    private readonly IAdminActionLog _log;
    private readonly IClock _clock;
    private readonly CustomerTokenGuard _guard;
    private readonly PersonRateLimiter _changes;

    public AutoJoinCommands(ITenantStore tenants, ISignInDirectoryStore directories, IAutoJoinSettingStore store, IAdminActionLog log, IClock clock)
    {
        _store = store;
        _log = log;
        _clock = clock;
        _guard = new CustomerTokenGuard(tenants, directories, shared: null);
        _changes = new PersonRateLimiter(clock, MaxChangesPerWindow, CustomerTokenRules.RateWindow);
    }

    /// <summary>Packmoat.Admin only: whether auto-join is on for the actor's tenant.</summary>
    public async Task<bool> IsOnAsync(CustomerActor actor, CancellationToken ct)
    {
        if (actor.Role != CustomerRole.Admin) throw new CustomerTokenRefusedException(CustomerTokenRefusal.AdminOnly);
        var (_, _, tenant) = await _guard.CheckLinkedAsync(actor, ct);
        return await _store.IsOnAsync(tenant.Id, ct);
    }

    /// <summary>
    /// Turns auto-join on or off for the actor's tenant. In order: a fresh Admin; the guard (binding and active tenant, not the serving gate: an
    /// admin may always narrow who gets in); the rate limit; no change (false, nothing written) when it is already so; then the intent
    /// row, then the change. True when it changed.
    /// </summary>
    public async Task<bool> SetAutoJoinAsync(CustomerActor actor, bool enabled, CancellationToken ct)
    {
        if (actor.Role != CustomerRole.Admin) throw new CustomerTokenRefusedException(CustomerTokenRefusal.AdminOnly);
        if (!CustomerTokenRules.IsFresh(actor.SignedInAt, _clock.UtcNow)) throw new CustomerTokenRefusedException(CustomerTokenRefusal.FreshSignInRequired);
        var (tid, oid, tenant) = await _guard.CheckLinkedAsync(actor, ct);
        if (!_changes.TryAcquire(tid + "/" + oid)) throw new CustomerTokenRefusedException(CustomerTokenRefusal.TooManyRequests);
        var was = await _store.IsOnAsync(tenant.Id, ct);
        if (was == enabled) return false;

        var detail = $"enabled={OnOff(enabled)}; was={OnOff(was)}";
        await _log.AppendAsync(Record(actor, tid, oid, Action, tenant.Id, detail), ct);   // no record, no change
        try
        {
            await _store.SetAsync(tenant.Id, enabled, _clock.UtcNow, CustomerTokenRules.Who(actor), ct);
        }
        catch
        {
            try { await _log.AppendAsync(Record(actor, tid, oid, Action + ".failed", tenant.Id, detail), CancellationToken.None); }
            catch { /* best effort: the intent row already stands */ }
            throw;
        }
        return true;
    }

    private static string OnOff(bool on) => on ? "on" : "off";

    private AdminActionRecord Record(CustomerActor actor, string tid, string oid, string action, string tenantId, string detail) =>
        new(_clock.UtcNow, tid, oid, actor.DisplayName, action, tenantId, detail, CustomerTokenCommands.Site);
}
