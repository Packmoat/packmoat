using System.Text.RegularExpressions;
using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>The customer site's own limits on waiver changes (#157). The waiver limits themselves are <see cref="WaiverRules"/>.</summary>
public static class CustomerWaiverRules
{
    /// <summary>Per person, per <see cref="RateWindow"/>: at most this many add attempts, and separately this many revokes.</summary>
    public const int MaxAddsPerWindow = 10;
    public const int MaxRevokesPerWindow = 10;
    public static readonly TimeSpan RateWindow = TimeSpan.FromHours(1);

    private static readonly Regex IdShape = new(@"^wvr_[0-9a-f]{12}$", RegexOptions.CultureInvariant);

    /// <summary>True for a waiver id as the store makes them: "wvr_" and 12 lower-case hex digits, nothing else.</summary>
    public static bool IsWaiverId(string? id) => id is not null && IdShape.IsMatch(id);
}

/// <summary>
/// The customer site's waiver writes (#157). Packmoat.Admin only, with a sign-in within <see cref="CustomerTokenRules.FreshSignIn"/>:
/// checked HERE, first, because the core command receives an <see cref="ActorScope"/>, which carries neither a role nor a sign-in time.
/// Then the same guard as the token pages (<see cref="CustomerTokenGuard"/>: the directory still bound to the actor's tenant, the tenant
/// active); the tenant is ONLY <c>actor.TenantId</c>. The waiver limits are the core's (<see cref="AdminCommands.AddWaiverAsync"/> for a
/// tenant actor). Each write leaves admin_actions rows (site "app"): the intent row first, so a change that cannot be recorded does not
/// happen; then the new id or ".failed", best effort.
/// </summary>
public sealed class CustomerWaiverCommands
{
    private readonly IAdminActionLog _log;
    private readonly IClock _clock;
    private readonly AdminCommands _core;
    private readonly CustomerTokenGuard _guard;
    private readonly PersonRateLimiter _adds;
    private readonly PersonRateLimiter _revokes;

    /// <param name="alerts">Where the core raises <see cref="OpsAlert.WaiverCapReached"/>. The core commands are this instance's own (a
    /// singleton), so their per-tenant alert throttle lives as long as the site; the customer site never sees <see cref="AdminCommands"/>.</param>
    public CustomerWaiverCommands(ITenantStore tenants, ITokenService tokens, ISignInDirectoryStore directories, IAdminActionLog log, IClock clock, IOpsAlerts? alerts = null)
    {
        _log = log;
        _clock = clock;
        _core = new AdminCommands(tenants, tokens, clock, alerts: alerts);
        _guard = new CustomerTokenGuard(tenants, directories, shared: null);
        _adds = new PersonRateLimiter(clock, CustomerWaiverRules.MaxAddsPerWindow, CustomerWaiverRules.RateWindow);
        _revokes = new PersonRateLimiter(clock, CustomerWaiverRules.MaxRevokesPerWindow, CustomerWaiverRules.RateWindow);
    }

    /// <summary>
    /// Waives one exact version of a package in the actor's tenant for <paramref name="days"/> days. Bad input is refused
    /// (<see cref="WaiverRefusedException"/>) before anything is read or recorded; the internal-prefix, duplicate and cap refusals come from
    /// the core after the intent row, and leave a ".failed" row naming the reason.
    /// </summary>
    public async Task<Waiver> AddAsync(CustomerActor actor, string? packageId, string? version, int days, string? reason, CancellationToken ct)
    {
        CustomerTokenRules.RequireFreshAdmin(actor, _clock.UtcNow);
        var package = WaiverRules.CleanPackageId(packageId) ?? throw new WaiverRefusedException(WaiverRefusal.BadPackage);
        var exact = WaiverRules.CleanExactVersion(version) ?? throw new WaiverRefusedException(WaiverRefusal.BadVersion);
        if (days is < WaiverRules.MinCustomerDays or > WaiverRules.MaxCustomerDays) throw new WaiverRefusedException(WaiverRefusal.BadExpiry);
        var why = WaiverRules.CleanReason(reason) ?? throw new WaiverRefusedException(WaiverRefusal.BadReason);

        var (tid, oid, tenant) = await _guard.CheckLinkedAsync(actor, ct);
        if (!_adds.TryAcquire(tid + "/" + oid)) throw new CustomerTokenRefusedException(CustomerTokenRefusal.TooManyRequests);

        var detail = $"package={package}; version={exact}; days={days}; reason={CustomerTokenRules.Quote(why)}";   // the reason last and quoted
        await _log.AppendAsync(Record(actor, tid, oid, "waiver.add", tenant.Id, detail), ct);   // no record, no waiver

        Waiver waiver;
        try
        {
            waiver = await _core.AddWaiverAsync(ActorScope.ForTenant(tenant.Id, CustomerTokenRules.Who(actor)), tenant.Id, package, exact, days, why, ct);
        }
        catch (Exception e)
        {
            var code = e is WaiverRefusedException r ? Code(r.Reason) : "error";
            await TryAppendAsync(Record(actor, tid, oid, "waiver.add.failed", tenant.Id, $"reason={code}; {detail}"));
            throw;
        }
        await TryAppendAsync(Record(actor, tid, oid, "waiver.add.id", tenant.Id, "id=" + waiver.Id));
        return waiver;
    }

    /// <summary>
    /// Revokes a not-yet-revoked waiver of the actor's tenant (operator-made ones too: that only tightens). False, and no record, for an id
    /// that is not the shape, unknown, already revoked, or another tenant's: all look the same.
    /// </summary>
    public async Task<bool> RevokeAsync(CustomerActor actor, string? waiverId, CancellationToken ct)
    {
        CustomerTokenRules.RequireFreshAdmin(actor, _clock.UtcNow);
        var (tid, oid, tenant) = await _guard.CheckLinkedAsync(actor, ct);
        if (!_revokes.TryAcquire(tid + "/" + oid)) throw new CustomerTokenRefusedException(CustomerTokenRefusal.TooManyRequests);
        if (!CustomerWaiverRules.IsWaiverId(waiverId)) return false;
        var waiver = tenant.Policy.Waivers.FirstOrDefault(w => string.Equals(w.Id, waiverId, StringComparison.Ordinal));   // this tenant's only
        if (waiver is null) return false;

        var detail = $"waiver={waiver.Id}; package={waiver.PackageId}; version={waiver.Version}; source={(waiver.Source == WaiverSource.Customer ? "customer" : "operator")}";
        await _log.AppendAsync(Record(actor, tid, oid, "waiver.revoke", tenant.Id, detail), ct);
        bool done;
        try
        {
            done = await _core.RevokeWaiverAsync(ActorScope.ForTenant(tenant.Id, CustomerTokenRules.Who(actor)), waiver.Id, ct);   // AND tenant_id in the store
        }
        catch (AccessDeniedException) { done = false; }   // gone between the read and the write
        if (!done) await TryAppendAsync(Record(actor, tid, oid, "waiver.revoke.failed", tenant.Id, detail));
        return done;
    }

    private static string Code(WaiverRefusal reason) => reason switch
    {
        WaiverRefusal.InternalPrefix => "internal-prefix",
        WaiverRefusal.Duplicate => "duplicate",
        WaiverRefusal.TooManyWaivers => "cap",
        _ => "refused",
    };

    private AdminActionRecord Record(CustomerActor actor, string tid, string oid, string action, string tenantId, string detail) =>
        new(_clock.UtcNow, tid, oid, actor.DisplayName, action, tenantId, detail, CustomerTokenCommands.Site);

    private async Task TryAppendAsync(AdminActionRecord record)
    {
        try { await _log.AppendAsync(record, CancellationToken.None); }
        catch { /* best effort: the intent row already stands */ }
    }
}

/// <summary>
/// What the Waivers page shows. <c>Active</c>: not revoked and not expired, newest first. <c>History</c>: ended ones, newest first, for
/// Admins only (empty for a Developer). Reasons and names are untrusted text.
/// </summary>
public sealed record CustomerWaiverList(IReadOnlyList<WaiverRecord> Active, IReadOnlyList<WaiverRecord> History, bool IsAdmin);

/// <summary>The customer site's waiver reads: any role, the same guard as the writes; the tenant comes only from the actor.</summary>
public sealed class CustomerWaiverQueries
{
    /// <summary>How the operator is named on customer pages: never the operator's own name (the CLI's --by, or the admin site's sign-in name).</summary>
    public const string OperatorName = "Packmoat";

    private static readonly Regex CustomerName = new(@"^(.+) \(([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\)$", RegexOptions.CultureInvariant);

    private readonly ITenantStore _tenants;
    private readonly IMemberStore _members;
    private readonly IClock _clock;
    private readonly CustomerTokenGuard _guard;

    public CustomerWaiverQueries(ITenantStore tenants, ISignInDirectoryStore directories, IMemberStore members, IClock clock)
    {
        _tenants = tenants;
        _members = members;
        _clock = clock;
        _guard = new CustomerTokenGuard(tenants, directories, shared: null);
    }

    /// <summary>
    /// The tenant's waivers. The store lists active waivers first (#161 review), so no active one is lost behind
    /// <see cref="AdminQueries.WaiverListLimit"/> rows of history. <c>CreatedBy</c> and <c>RevokedBy</c> come back ready to show: a
    /// person's display name only when the stored value is exactly "Name (object id)" (how the customer site records a person) AND that
    /// object id is a member of this tenant; anything else, and every maker of an operator waiver, is <see cref="OperatorName"/>.
    /// No kind of actor is stored with a revoke, so the member check is what tells a customer from the operator.
    /// </summary>
    public async Task<CustomerWaiverList> ListAsync(CustomerActor actor, CancellationToken ct)
    {
        var (_, _, tenant) = await _guard.CheckLinkedAsync(actor, ct);
        var rows = await _tenants.ListWaiversAsync(tenant.Id, AdminQueries.WaiverListLimit, ct);
        var members = (await _members.ListForTenantAsync(tenant.Id, ct)).Select(m => m.ObjectId).ToHashSet(StringComparer.Ordinal);
        string Shown(string? stored) =>
            stored is not null && CustomerName.Match(stored) is { Success: true } m && members.Contains(m.Groups[2].Value) ? m.Groups[1].Value : OperatorName;
        var shown = rows.Select(w => w with
        {
            CreatedBy = w.Source == WaiverSource.Operator ? OperatorName : Shown(w.CreatedBy),
            RevokedBy = w.RevokedBy is null ? null : Shown(w.RevokedBy),
        }).ToList();

        var now = _clock.UtcNow;
        var admin = actor.Role == CustomerRole.Admin;
        return new CustomerWaiverList(
            shown.Where(w => w.IsActiveAt(now)).ToList(),
            admin ? shown.Where(w => !w.IsActiveAt(now)).ToList() : Array.Empty<WaiverRecord>(),
            admin);
    }
}
