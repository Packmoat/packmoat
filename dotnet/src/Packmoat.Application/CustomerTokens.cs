using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>
/// A person who signed in on the customer site: (directory id, object id), both lower-case canonical GUIDs, and the tenant their directory
/// was bound to when last seen. The display name is what Microsoft sent at the last sign-in.
/// </summary>
public sealed record Member(string AzureTenantId, string ObjectId, string TenantId, string DisplayName, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);

/// <summary>Who has signed in. Written at sign-in (and when a person issues a token), read to show who owns a token.</summary>
public interface IMemberStore
{
    /// <summary>Creates the person, or updates their tenant, display name and last-seen time. First-seen never changes.</summary>
    Task UpsertAsync(string azureTenantId, string objectId, string tenantId, string displayName, DateTimeOffset seenAt, CancellationToken cancellationToken);
    Task<Member?> GetAsync(string azureTenantId, string objectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Member>> ListForTenantAsync(string tenantId, CancellationToken cancellationToken);
}

/// <summary>Why a customer-site token action was refused. The page shows a fixed message for each; nothing a person typed is echoed.</summary>
public enum CustomerTokenRefusal
{
    /// <summary>The actor has no directory, or the directory is no longer bound to the actor's tenant.</summary>
    NotLinked,
    /// <summary>The tenant is suspended or no longer exists.</summary>
    TenantSuspended,
    /// <summary>
    /// The serving gate refused the tenant: it serves nothing, so it issues nothing. The gate's notice, if any, is
    /// <see cref="CustomerTokenRefusedException.Notice"/>; the customer site's module words the page from it.
    /// </summary>
    ServingRefused,
    /// <summary>A shared-app tenant without a passing onboarding check for its current binding: it serves nothing, so it issues nothing.</summary>
    TenantNotVerified,
    /// <summary>The tenant has no upstream yet (no app bound to reach the feed, or feed URLs that fail the rules for its binding): it serves nothing, so it issues nothing.</summary>
    TenantNotSetUp,
    /// <summary>Only a Packmoat.Admin may do this.</summary>
    AdminOnly,
    /// <summary>The person already holds the most active tokens allowed.</summary>
    TooManyTokens,
    /// <summary>The expiry asked for is outside the allowed range.</summary>
    BadExpiry,
    /// <summary>The label is empty, too long, or holds control or invisible characters.</summary>
    BadLabel,
    /// <summary>An admin-only write needs a sign-in within the last <see cref="CustomerTokenRules.FreshSignIn"/>; this session's is older (or unknown).</summary>
    FreshSignInRequired,
    /// <summary>Too many issues (or revokes) by this person in the last hour.</summary>
    TooManyRequests,
}

public sealed class CustomerTokenRefusedException : InvalidOperationException
{
    public CustomerTokenRefusedException(CustomerTokenRefusal reason, ServingNotice? notice = null) : base($"Refused: {reason}.")
    {
        Reason = reason;
        Notice = notice;
    }

    public CustomerTokenRefusal Reason { get; }

    /// <summary>For <see cref="CustomerTokenRefusal.ServingRefused"/>: the serving gate's notice with its refusal, or null. Never looked inside here.</summary>
    public ServingNotice? Notice { get; }
}

/// <summary>The rules for tokens people issue for themselves on the customer site.</summary>
public static class CustomerTokenRules
{
    /// <summary>A self-issued developer token always lasts this long; the person does not choose.</summary>
    public const int DeveloperTokenDays = 90;
    /// <summary>Active (not revoked, not expired) self-issued tokens one person may hold in a tenant, developer and automation together.</summary>
    public const int MaxActiveTokensPerMember = 5;
    public const int MinAutomationDays = 1;
    /// <summary>An automation token always expires; at most this many days from issue.</summary>
    public const int MaxAutomationDays = 365;
    public const int MaxLabel = 100;
    /// <summary>Admin-only writes (automation issue, admin revoke) need a sign-in at most this long ago.</summary>
    public static readonly TimeSpan FreshSignIn = TimeSpan.FromMinutes(15);
    /// <summary>Per person, per <see cref="RateWindow"/>: at most this many issue attempts, and separately this many revokes.</summary>
    public const int MaxIssuesPerWindow = 10;
    public const int MaxRevokesPerWindow = 10;
    public static readonly TimeSpan RateWindow = TimeSpan.FromHours(1);

    /// <summary>True when <paramref name="signedInAt"/> is known, not in the future, and within <see cref="FreshSignIn"/> of <paramref name="now"/>.</summary>
    public static bool IsFresh(DateTimeOffset? signedInAt, DateTimeOffset now) =>
        signedInAt is { } t && t <= now + TimeSpan.FromMinutes(1) && now - t <= FreshSignIn;

    /// <summary>
    /// A value for an admin_actions detail, in double quotes with '\' and '"' escaped, so a label can never forge a "; key=value" field.
    /// Callers put quoted values last.
    /// </summary>
    public static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>The trimmed label, or null when it is empty, longer than <see cref="MaxLabel"/>, or holds control or invisible characters.</summary>
    public static string? CleanLabel(string? label)
    {
        var t = (label ?? "").Trim();
        if (t.Length == 0 || t.Length > MaxLabel) return null;
        foreach (var c in t)
            if (char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format
                    or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)
                return null;
        return t;
    }

    /// <summary>The person's identity, or a refusal: the directory must be a canonical GUID.</summary>
    internal static (string Tid, string Oid) Person(CustomerActor actor) =>
        CustomerSignIn.CanonicalDirectory(actor.DirectoryId) is { } tid && actor.ObjectId != Guid.Empty
            ? (tid, actor.ObjectId.ToString("D"))
            : throw new CustomerTokenRefusedException(CustomerTokenRefusal.NotLinked);

    /// <summary>
    /// The role and the fresh sign-in every admin-only customer-site write checks first (tokens, and waivers #157): Packmoat.Admin, and a
    /// sign-in within <see cref="FreshSignIn"/> of <paramref name="now"/>. Checked here, in the Application layer, because the scope the core
    /// commands receive (<see cref="ActorScope"/>) carries neither a role nor a sign-in time.
    /// </summary>
    internal static void RequireFreshAdmin(CustomerActor actor, DateTimeOffset now)
    {
        if (actor.Role != CustomerRole.Admin) throw new CustomerTokenRefusedException(CustomerTokenRefusal.AdminOnly);
        if (!IsFresh(actor.SignedInAt, now)) throw new CustomerTokenRefusedException(CustomerTokenRefusal.FreshSignInRequired);
    }

    /// <summary>"Display name (oid)": how a customer is named in revoked_by.</summary>
    internal static string Who(CustomerActor actor) => $"{actor.DisplayName} ({actor.ObjectId:D})";
}

/// <summary>
/// The checks every customer token action runs first. The tenant is ONLY <c>actor.TenantId</c>; it is never taken from input.
/// The actor's directory must still be bound to that tenant (read again now), and the tenant must be active. Writes that need the tenant to be
/// serving also go through the full <see cref="ServingRule"/>: the serving gate (the core's serves everyone), then the upstream
/// (<c>UpstreamReadiness.Of</c>: set up, and for the shared app checked).
/// </summary>
internal sealed class CustomerTokenGuard
{
    private readonly ITenantStore _tenants;
    private readonly ISignInDirectoryStore _directories;
    private readonly ISharedAppCredentialStore? _shared;

    public CustomerTokenGuard(ITenantStore tenants, ISignInDirectoryStore directories, ISharedAppCredentialStore? shared)
    {
        _tenants = tenants;
        _directories = directories;
        _shared = shared;
    }

    /// <summary>Binding and active tenant: what every customer token action checks first. No serving gate is asked.</summary>
    public async Task<(string Tid, string Oid, Tenant Tenant)> CheckLinkedAsync(CustomerActor actor, CancellationToken ct)
    {
        var (tid, oid) = CustomerTokenRules.Person(actor);
        if (string.IsNullOrWhiteSpace(actor.TenantId)) throw new CustomerTokenRefusedException(CustomerTokenRefusal.NotLinked);
        var binding = await _directories.GetAsync(tid, ct);
        if (binding is null || binding.AzureTenantId != tid || !string.Equals(binding.TenantId, actor.TenantId, StringComparison.Ordinal))
            throw new CustomerTokenRefusedException(CustomerTokenRefusal.NotLinked);
        var tenant = await _tenants.GetAsync(actor.TenantId, ct);
        if (tenant is null || !tenant.Active) throw new CustomerTokenRefusedException(CustomerTokenRefusal.TenantSuspended);
        return (tid, oid, tenant);
    }

    /// <summary>
    /// <see cref="CheckLinkedAsync"/>, then a gate that serves and an upstream that is ready (set up, and for a shared-app tenant a passing
    /// onboarding check), for issuing. A gate that throws refuses (the exception goes on: fail closed).
    /// </summary>
    public async Task<(string Tid, string Oid, Tenant Tenant)> CheckServingAsync(CustomerActor actor, IServingGate gate, CancellationToken ct)
    {
        var (tid, oid, tenant) = await CheckLinkedAsync(actor, ct);
        var answer = await gate.AskAsync(tenant.Id, ct);
        // The upstream must be set up (a binding, and URLs that pass the rules for it) and, for the shared app, checked. Fail closed: no store to ask, no
        // active credential or no matching check all mean "not verified".
        var upstream = await _tenants.GetUpstreamBindingAsync(tenant.Id, ct);
        SharedAppVerification? verification = null;
        string? activeClientId = null;
        if (upstream is { Kind: IdentityKind.Shared } && _shared is not null)
        {
            activeClientId = (await _shared.GetAsync(SharedAppSlot.Active, ct))?.ClientId;
            verification = await _shared.GetVerificationAsync(tenant.Id, ct);
        }
        return ServingRule.Decide(tenant.Active, answer, UpstreamReadiness.Of(tenant, upstream, verification, activeClientId)) switch
        {
            ServingDecision.Serve => (tid, oid, tenant),
            ServingDecision.Refused => throw new CustomerTokenRefusedException(CustomerTokenRefusal.ServingRefused, answer.Notice),
            ServingDecision.NotSetUp => throw new CustomerTokenRefusedException(CustomerTokenRefusal.TenantNotSetUp),
            ServingDecision.NotVerified => throw new CustomerTokenRefusedException(CustomerTokenRefusal.TenantNotVerified),
            _ => throw new CustomerTokenRefusedException(CustomerTokenRefusal.TenantSuspended),
        };
    }
}

/// <summary>
/// The customer site's token writes. Every one runs <see cref="CustomerTokenGuard"/> first; the tenant comes only from the actor.
/// The plaintext of a new token is returned from the issue call and nowhere else: it is never stored, logged or written to admin_actions.
/// Each write leaves admin_actions rows (site "app"; actor tid, oid and display name; never a token value), following the admin site's
/// pattern: the intent row is written first, so a write that cannot be recorded does not happen; then ".failed" or the new id, best effort.
/// </summary>
public sealed class CustomerTokenCommands
{
    public const string Site = "app";

    private readonly ITokenService _tokens;
    private readonly IMemberStore _members;
    private readonly IAdminActionLog _log;
    private readonly IClock _clock;
    private readonly CustomerTokenGuard _guard;
    private readonly IServingGate _gate;
    private readonly PersonRateLimiter _issues;
    private readonly PersonRateLimiter _revokes;

    public CustomerTokenCommands(ITenantStore tenants, ITokenService tokens, IMemberStore members, ISignInDirectoryStore directories,
        IAdminActionLog log, IClock clock, ISharedAppCredentialStore? shared, IServingGate gate)
    {
        _tokens = tokens;
        _members = members;
        _log = log;
        _clock = clock;
        _guard = new CustomerTokenGuard(tenants, directories, shared);
        _gate = gate;
        _issues = new PersonRateLimiter(clock, CustomerTokenRules.MaxIssuesPerWindow, CustomerTokenRules.RateWindow);
        _revokes = new PersonRateLimiter(clock, CustomerTokenRules.MaxRevokesPerWindow, CustomerTokenRules.RateWindow);
    }

    /// <summary>Admin-only writes need a recent sign-in, so a left-open session cannot mint automation tokens or revoke other people's.</summary>
    private void RequireFreshAdmin(CustomerActor actor) => CustomerTokenRules.RequireFreshAdmin(actor, _clock.UtcNow);

    /// <summary>
    /// Records that this person signed in (directory, oid, tenant, display name, seen times). Call it right after a sign-in is allowed.
    /// Needs only the binding: a suspended tenant's people may still sign in to learn that it is suspended.
    /// </summary>
    public async Task RecordSignInAsync(CustomerActor actor, CancellationToken ct)
    {
        var (tid, oid) = CustomerTokenRules.Person(actor);
        try
        {
            await _guard.CheckLinkedAsync(actor, ct);
        }
        catch (CustomerTokenRefusedException e) when (e.Reason == CustomerTokenRefusal.TenantSuspended) { /* still record the person */ }
        await _members.UpsertAsync(tid, oid, actor.TenantId, actor.DisplayName, _clock.UtcNow, ct);
    }

    /// <summary>A developer token for the signed-in person, any role, lasting <see cref="CustomerTokenRules.DeveloperTokenDays"/> days.</summary>
    public Task<IssuedToken> IssueDeveloperTokenAsync(CustomerActor actor, string label, CancellationToken ct) =>
        IssueAsync(actor, label, TokenKind.Developer, CustomerTokenRules.DeveloperTokenDays, ct);

    /// <summary>
    /// An automation token (build agents; never counted as a developer). Packmoat.Admin only. It always expires: <paramref name="days"/> from
    /// <see cref="CustomerTokenRules.MinAutomationDays"/> to <see cref="CustomerTokenRules.MaxAutomationDays"/>. Owned by the admin who issued it.
    /// </summary>
    public Task<IssuedToken> IssueAutomationTokenAsync(CustomerActor actor, string label, int days, CancellationToken ct)
    {
        RequireFreshAdmin(actor);
        if (days is < CustomerTokenRules.MinAutomationDays or > CustomerTokenRules.MaxAutomationDays) throw new CustomerTokenRefusedException(CustomerTokenRefusal.BadExpiry);
        return IssueAsync(actor, label, TokenKind.Automation, days, ct);
    }

    private async Task<IssuedToken> IssueAsync(CustomerActor actor, string label, TokenKind kind, int days, CancellationToken ct)
    {
        var clean = CustomerTokenRules.CleanLabel(label) ?? throw new CustomerTokenRefusedException(CustomerTokenRefusal.BadLabel);
        var (tid, oid, tenant) = await _guard.CheckServingAsync(actor, _gate, ct);
        if (!_issues.TryAcquire(tid + "/" + oid)) throw new CustomerTokenRefusedException(CustomerTokenRefusal.TooManyRequests);
        var now = _clock.UtcNow;
        var expires = now.AddDays(days);

        // A quick check for a clean refusal; the store checks the cap again in the same statement as the insert.
        var mine = await _tokens.ListForMemberAsync(tenant.Id, tid, oid, ct);
        if (mine.Count(t => t.IssuedVia == TokenIssuer.Self && t.IsActiveAt(now)) >= CustomerTokenRules.MaxActiveTokensPerMember)
            throw new CustomerTokenRefusedException(CustomerTokenRefusal.TooManyTokens);

        await _members.UpsertAsync(tid, oid, tenant.Id, actor.DisplayName, now, ct);   // so the owner's name is known
        var detail = $"kind={kind}; expires={expires:u}; label={CustomerTokenRules.Quote(clean)}";   // never the token itself; the label last and quoted
        await _log.AppendAsync(Record(actor, tid, oid, "token.issue", tenant.Id, detail), ct);   // no record, no token

        IssuedToken? issued;
        try
        {
            issued = await _tokens.IssueForMemberAsync(tenant.Id, tid, oid, clean, expires, kind, CustomerTokenRules.MaxActiveTokensPerMember, ct);
        }
        catch
        {
            await TryAppendAsync(Record(actor, tid, oid, "token.issue.failed", tenant.Id, detail));
            throw;
        }
        if (issued is null)
        {
            await TryAppendAsync(Record(actor, tid, oid, "token.issue.failed", tenant.Id, $"reason=cap; {detail}"));
            throw new CustomerTokenRefusedException(CustomerTokenRefusal.TooManyTokens);
        }
        await TryAppendAsync(Record(actor, tid, oid, "token.issue.id", tenant.Id, "id=" + issued.TokenId));
        return issued;
    }

    /// <summary>
    /// Revokes one of the person's own active tokens. False (and no record) when the id is not an active token this person owns in this
    /// tenant: someone else's, another tenant's and an unknown id all look the same.
    /// </summary>
    public async Task<bool> RevokeOwnTokenAsync(CustomerActor actor, string tokenId, CancellationToken ct)
    {
        var (tid, oid, tenant) = await _guard.CheckLinkedAsync(actor, ct);
        if (!_revokes.TryAcquire(tid + "/" + oid)) throw new CustomerTokenRefusedException(CustomerTokenRefusal.TooManyRequests);
        var token = (await _tokens.ListForMemberAsync(tenant.Id, tid, oid, ct)).FirstOrDefault(t => t.TokenId == tokenId && t.RevokedAt is null);
        if (token is null) return false;
        var detail = $"token={token.TokenId}; kind={token.Kind}; owner=self; label={CustomerTokenRules.Quote(token.Label)}";
        await _log.AppendAsync(Record(actor, tid, oid, "token.revoke", tenant.Id, detail), ct);
        var done = await _tokens.RevokeForMemberAsync(tenant.Id, tid, oid, token.TokenId, CustomerTokenRules.Who(actor), ct);
        if (!done) await TryAppendAsync(Record(actor, tid, oid, "token.revoke.failed", tenant.Id, detail));
        return done;
    }

    /// <summary>
    /// Packmoat.Admin only: revokes any not-yet-revoked token of the actor's own tenant (anyone's, operator-issued ones too), recording who.
    /// False (and no record) for an id that is not in this tenant: another tenant's token is never touched.
    /// </summary>
    public async Task<bool> RevokeTenantTokenAsync(CustomerActor actor, string tokenId, CancellationToken ct)
    {
        RequireFreshAdmin(actor);
        var (tid, oid, tenant) = await _guard.CheckLinkedAsync(actor, ct);
        if (!_revokes.TryAcquire(tid + "/" + oid)) throw new CustomerTokenRefusedException(CustomerTokenRefusal.TooManyRequests);
        var token = (await _tokens.ListAsync(tenant.Id, ct)).FirstOrDefault(t => t.TokenId == tokenId && t.RevokedAt is null);
        if (token is null || token.TenantId != tenant.Id) return false;
        var detail = $"token={token.TokenId}; kind={token.Kind}; owner={token.OwnerOid ?? "operator-issued"}; label={CustomerTokenRules.Quote(token.Label)}";
        await _log.AppendAsync(Record(actor, tid, oid, "token.revoke", tenant.Id, detail), ct);
        var done = await _tokens.RevokeInTenantAsync(tenant.Id, token.TokenId, CustomerTokenRules.Who(actor), ct);
        if (!done) await TryAppendAsync(Record(actor, tid, oid, "token.revoke.failed", tenant.Id, detail));
        return done;
    }

    private AdminActionRecord Record(CustomerActor actor, string tid, string oid, string action, string tenantId, string detail) =>
        new(_clock.UtcNow, tid, oid, actor.DisplayName, action, tenantId, detail, Site);

    private async Task TryAppendAsync(AdminActionRecord record)
    {
        try { await _log.AppendAsync(record, CancellationToken.None); }
        catch { /* best effort: the intent row already stands */ }
    }
}

/// <summary>
/// Why issuing a token would be refused right now (#200): the refusal the issue POST would answer with, and for
/// <see cref="CustomerTokenRefusal.ServingRefused"/> the serving gate's notice.
/// </summary>
public sealed record CustomerTokenBlocker(CustomerTokenRefusal Reason, ServingNotice? Notice);

/// <summary>The customer site's token reads. Same guard as the writes (binding and active tenant); the tenant comes only from the actor.</summary>
public sealed class CustomerTokenQueries
{
    private readonly ITokenService _tokens;
    private readonly CustomerTokenGuard _guard;
    private readonly IServingGate _gate;

    public CustomerTokenQueries(ITenantStore tenants, ITokenService tokens, ISignInDirectoryStore directories, ISharedAppCredentialStore? shared, IServingGate gate)
    {
        _tokens = tokens;
        _guard = new CustomerTokenGuard(tenants, directories, shared);
        _gate = gate;
    }

    /// <summary>
    /// Why issuing a token would be refused now, or null when it would not be. The same rule the issue commands run
    /// (<see cref="CustomerTokenGuard.CheckServingAsync"/>: the binding and the operator's switch, the serving gate, then the upstream), so the
    /// token pages can show the refusal in place of a create form the POST would refuse. The POST still checks: this is only for the page.
    /// The per-request parts of an issue (the label, the rate limit, the token cap, an admin's fresh sign-in) are not asked here.
    /// </summary>
    public async Task<CustomerTokenBlocker?> IssueBlockerAsync(CustomerActor actor, CancellationToken ct)
    {
        try
        {
            await _guard.CheckServingAsync(actor, _gate, ct);
            return null;
        }
        catch (CustomerTokenRefusedException e) { return new CustomerTokenBlocker(e.Reason, e.Notice); }
    }

    /// <summary>The signed-in person's own tokens in their tenant (active, expired and revoked), oldest first.</summary>
    public async Task<IReadOnlyList<TokenInfo>> MineAsync(CustomerActor actor, CancellationToken ct)
    {
        var (tid, oid, tenant) = await _guard.CheckLinkedAsync(actor, ct);
        return await _tokens.ListForMemberAsync(tenant.Id, tid, oid, ct);
    }

    /// <summary>Packmoat.Admin only: every token of the actor's tenant, with its owner. Never another tenant's.</summary>
    public async Task<IReadOnlyList<TokenInfo>> TenantAsync(CustomerActor actor, CancellationToken ct)
    {
        if (actor.Role != CustomerRole.Admin) throw new CustomerTokenRefusedException(CustomerTokenRefusal.AdminOnly);
        var (_, _, tenant) = await _guard.CheckLinkedAsync(actor, ct);
        return (await _tokens.ListAsync(tenant.Id, ct)).Where(t => t.TenantId == tenant.Id).ToList();
    }
}

/// <summary>
/// At most <c>max</c> attempts per key (a person) in a fixed window, in memory. A restart forgets the counts, which only makes the limit
/// looser for one window. Bounded: old windows are dropped when the table grows.
/// </summary>
public sealed class PersonRateLimiter
{
    private readonly IClock _clock;
    private readonly int _max;
    private readonly TimeSpan _window;
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _counts = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public PersonRateLimiter(IClock clock, int max, TimeSpan window)
    {
        _clock = clock;
        _max = max;
        _window = window;
    }

    /// <summary>Counts this attempt; false when the key already used all of this window's attempts.</summary>
    public bool TryAcquire(string key)
    {
        var now = _clock.UtcNow;
        lock (_lock)
        {
            if (!_counts.TryGetValue(key, out var e) || now - e.Start >= _window || now < e.Start) e = (now, 0);
            if (e.Count >= _max) { _counts[key] = e; return false; }
            _counts[key] = (e.Start, e.Count + 1);
            if (_counts.Count > 20_000)
                foreach (var k in _counts.Where(kv => now - kv.Value.Start >= _window).Select(kv => kv.Key).ToList()) _counts.Remove(k);
            return true;
        }
    }
}
