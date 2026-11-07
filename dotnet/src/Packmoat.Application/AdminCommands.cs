using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>
/// Who is asking. An operator may act on every tenant; a tenant actor may act on exactly one (for later customer self-service).
/// Every admin command and query takes one explicitly, so no caller can forget to say who it is.
/// </summary>
public abstract record ActorScope
{
    private ActorScope(string name) { Name = name; }

    /// <summary>
    /// Who this actor is, for the record (for example on a waiver's created-by). It comes from whoever authenticated the actor,
    /// never from a request field, so a tenant actor cannot name someone else.
    /// </summary>
    public string Name { get; }

    /// <summary>The Packmoat operator: all tenants.</summary>
    public static ActorScope Operator { get; } = new OperatorScope("operator");

    /// <summary>The operator, identified by name (the CLI passes its OS user).</summary>
    public static ActorScope OperatorAs(string name) =>
        string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("An actor name is required.", nameof(name)) : new OperatorScope(name.Trim());

    /// <summary>
    /// A customer actor: this one tenant and nothing else. <paramref name="name"/> defaults to "tenant:&lt;id&gt;".
    /// Once a tenant-facing entry point exists, the name MUST come from the authenticator (the verified identity), never from a request field.
    /// </summary>
    public static ActorScope ForTenant(string tenantId, string? name = null) =>
        string.IsNullOrWhiteSpace(tenantId) ? throw new ArgumentException("A tenant id is required.", nameof(tenantId))
        : new TenantScope(tenantId, string.IsNullOrWhiteSpace(name) ? "tenant:" + tenantId : name.Trim());

    public bool IsOperator => this is OperatorScope;

    /// <summary>The tenant a scoped actor is limited to, or null for the operator.</summary>
    public string? TenantId => (this as TenantScope)?.Tenant;

    public bool CanAccess(string? tenantId) => this is OperatorScope || (this is TenantScope t && string.Equals(t.Tenant, tenantId, StringComparison.Ordinal));

    private sealed record OperatorScope(string Label) : ActorScope(Label);
    private sealed record TenantScope(string Tenant, string Label) : ActorScope(Label);
}

/// <summary>The actor is not allowed to do this. The message never says whether the other tenant exists.</summary>
public sealed class AccessDeniedException : UnauthorizedAccessException
{
    public AccessDeniedException(string message) : base(message) { }
}

/// <summary>Read side for the audit trail. Newest first. <paramref name="tenantId"/> null means every tenant.</summary>
public interface IAuditQuery
{
    Task<IReadOnlyList<AuditEvent>> QueryAsync(string? tenantId, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// The admin write operations. The CLI and any later web UI are thin callers of this. The scope check always runs first,
/// before anything is read or written, so a refused actor learns nothing about other tenants.
/// </summary>
public sealed class AdminCommands
{
    private readonly ITenantStore _tenants;
    private readonly ITokenService _tokens;
    private readonly IClock _clock;
    private readonly ISharedAppCredentialStore? _shared;
    private readonly ICertificateInspector? _inspector;
    private readonly IOpsAlerts _alerts;

    public AdminCommands(ITenantStore tenants, ITokenService tokens, IClock clock, ISharedAppCredentialStore? shared = null, ICertificateInspector? inspector = null,
        IOpsAlerts? alerts = null)
    {
        _tenants = tenants;
        _tokens = tokens;
        _clock = clock;
        _shared = shared;
        _inspector = inspector;
        _alerts = alerts ?? NullOpsAlerts.Instance;
        _capAlerts = new PersonRateLimiter(clock, 1, CapAlertInterval);
    }

    /// <summary>
    /// At most one <see cref="OpsAlert.WaiverCapReached"/> per tenant in this long (#161 review): a revoke then an add back to the cap
    /// would otherwise alert the operator every time. In memory, so a restart may send one more.
    /// </summary>
    public static readonly TimeSpan CapAlertInterval = TimeSpan.FromHours(1);
    private readonly PersonRateLimiter _capAlerts;

    /// <summary>
    /// Letters, digits, '-' and '_' only. Besides keeping ids tidy, this is what guarantees a tenant id can never equal the
    /// encryption context of the shared app's key ("shared-app:active"), which contains a ':'.
    /// </summary>
    public static bool IsValidTenantId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 63 && char.IsAsciiLetterOrDigit(id[0]) && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Operator only: a tenant does not exist yet, so no tenant actor can own it.</summary>
    public Task CreateTenantAsync(ActorScope actor, NewTenant tenant, CancellationToken ct)
    {
        CheckNewTenant(actor, tenant);
        return _tenants.CreateAsync(tenant, ct);
    }

    /// <summary>
    /// What the operator's create checks before anything is written: the operator, the id, and the upstream URLs. Also used by the
    /// command line's create-and-link (#195), which writes through the organisation store instead.
    /// </summary>
    public static void CheckNewTenant(ActorScope actor, NewTenant tenant)
    {
        if (!actor.IsOperator) throw new AccessDeniedException("Only the operator can create tenants.");
        if (!IsValidTenantId(tenant.Id)) throw new ArgumentException("A tenant id is 1 to 63 letters, digits, '-' or '_', starting with a letter or digit.", nameof(tenant));
        // A tenant its customer connects (#207) has no upstream URLs at all until Connect writes them; only an operator-made one.
        if (tenant.FeedSetup == FeedSetup.Customer)
        {
            if (tenant.Origin != TenantOrigin.Operator || tenant.UpstreamBase.Length + tenant.AllowedPathPrefix.Length + tenant.FeedIndexUrl.Length != 0)
                throw new ArgumentException("A tenant its customer connects is operator-made and has no upstream URLs.", nameof(tenant));
            return;
        }
        // Own-app tenants may still use an http test host; the prefix and feed-under-prefix rules apply to every tenant. Binding to the shared app is stricter.
        if (TenantUrlRules.Check(tenant.UpstreamBase, tenant.AllowedPathPrefix, tenant.FeedIndexUrl, requireHttps: false, requirePrefix: false) is { } problem)
            throw new ArgumentException(problem, nameof(tenant));
    }

    /// <summary>A GUID in its usual 8-4-4-4-12 form, returned lower-case; anything else is refused, naming the option (never echoing a secret).</summary>
    public static string RequireGuid(string? value, string what) =>
        Guid.TryParseExact(value?.Trim(), "D", out var g) ? g.ToString("D") : throw new ArgumentException($"{what} must be a GUID like 00000000-0000-0000-0000-000000000000.");

    public Task AddPrefixAsync(ActorScope actor, string tenantId, string prefix, CancellationToken ct)
    {
        Require(actor, tenantId);
        return _tenants.AddInternalPrefixAsync(tenantId, prefix, ct);
    }

    public Task AddOverrideAsync(ActorScope actor, string tenantId, string packageId, int days, CancellationToken ct)
    {
        Require(actor, tenantId);
        return _tenants.SetOverrideAsync(tenantId, packageId, days, ct);
    }

    public const int MinWaiverDays = 1;
    public const int MaxWaiverDays = 3650;

    /// <summary>
    /// The waiver lasts <paramref name="days"/> days from now. Created-by is the actor's name. A duplicate of an active waiver (same package
    /// and version) is refused for everyone.
    /// <list type="bullet">
    /// <item>The operator: 1 to 3650 days, checked here and only here; an exact version or "*"; never capped.</item>
    /// <item>A tenant actor (#157): the customer limits in <see cref="WaiverRules"/>, all checked before anything is written: one exact version,
    /// <see cref="WaiverRules.MinCustomerDays"/> to <see cref="WaiverRules.MaxCustomerDays"/> days, a reason of
    /// <see cref="WaiverRules.MinReason"/> to <see cref="WaiverRules.MaxReason"/> plain characters, no package under an internal prefix, and at
    /// most <see cref="WaiverRules.MaxActiveCustomerWaivers"/> active customer waivers (checked by the store in the insert's transaction).
    /// The insert that reaches the cap exactly raises <see cref="OpsAlert.WaiverCapReached"/>, at most once per tenant per
    /// <see cref="CapAlertInterval"/>. Refusals throw <see cref="WaiverRefusedException"/>.</item>
    /// </list>
    /// </summary>
    public Task<Waiver> AddWaiverAsync(ActorScope actor, string tenantId, string packageId, string version, int days, string reason, CancellationToken ct)
    {
        Require(actor, tenantId);
        if (!actor.IsOperator) return AddCustomerWaiverAsync(actor, tenantId, packageId, version, days, reason, ct);
        if (days is < MinWaiverDays or > MaxWaiverDays) throw new ArgumentException($"days must be a whole number from {MinWaiverDays} to {MaxWaiverDays}.", nameof(days));
        return _tenants.AddWaiverAsync(tenantId, packageId, version, _clock.UtcNow.AddDays(days), reason, actor.Name, ct);
    }

    private async Task<Waiver> AddCustomerWaiverAsync(ActorScope actor, string tenantId, string packageId, string version, int days, string reason, CancellationToken ct)
    {
        var package = WaiverRules.CleanPackageId(packageId) ?? throw new WaiverRefusedException(WaiverRefusal.BadPackage);
        var exact = WaiverRules.CleanExactVersion(version) ?? throw new WaiverRefusedException(WaiverRefusal.BadVersion);
        if (days is < WaiverRules.MinCustomerDays or > WaiverRules.MaxCustomerDays) throw new WaiverRefusedException(WaiverRefusal.BadExpiry);
        var why = WaiverRules.CleanReason(reason) ?? throw new WaiverRefusedException(WaiverRefusal.BadReason);
        var tenant = await _tenants.GetAsync(tenantId, ct) ?? throw new KeyNotFoundException($"No tenant '{tenantId}'.");
        if (InternalPrefixes.Matches(tenant.Policy.InternalPrefixes, package)) throw new WaiverRefusedException(WaiverRefusal.InternalPrefix);

        var result = await _tenants.AddCustomerWaiverAsync(tenantId, package, exact, _clock.UtcNow.AddDays(days), why, actor.Name, WaiverRules.MaxActiveCustomerWaivers, ct);
        switch (result.Outcome)
        {
            case WaiverInsertOutcome.Duplicate: throw new WaiverRefusedException(WaiverRefusal.Duplicate);
            case WaiverInsertOutcome.CapReached: throw new WaiverRefusedException(WaiverRefusal.TooManyWaivers);
        }
        var waiver = result.Waiver ?? throw new InvalidOperationException("The store reported a waiver added but returned none.");
        if (result.ActiveCustomerWaivers == WaiverRules.MaxActiveCustomerWaivers && _capAlerts.TryAcquire(tenantId))
            _alerts.Raise(new OpsAlert.WaiverCapReached(TenantRef.For(tenantId, result.Origin), WaiverRules.MaxActiveCustomerWaivers));
        return waiver;
    }

    public Task RemovePrefixAsync(ActorScope actor, string tenantId, string prefix, CancellationToken ct)
    {
        Require(actor, tenantId);
        return _tenants.RemoveInternalPrefixAsync(tenantId, prefix, ct);
    }

    public Task RemoveOverrideAsync(ActorScope actor, string tenantId, string packageId, CancellationToken ct)
    {
        Require(actor, tenantId);
        return _tenants.RemoveOverrideAsync(tenantId, packageId, ct);
    }

    public Task SuspendTenantAsync(ActorScope actor, string tenantId, CancellationToken ct)
    {
        Require(actor, tenantId);
        return _tenants.SetActiveAsync(tenantId, false, ct);
    }

    public Task ActivateTenantAsync(ActorScope actor, string tenantId, CancellationToken ct)
    {
        Require(actor, tenantId);
        return _tenants.SetActiveAsync(tenantId, true, ct);
    }

    public Task SetCooldownAsync(ActorScope actor, string tenantId, int days, CancellationToken ct)
    {
        Require(actor, tenantId);
        return _tenants.SetCooldownAsync(tenantId, days, ct);
    }

    /// <summary>
    /// True when a waiver was revoked; revoked-by is the actor's name. A tenant actor may revoke only an active waiver of its own tenant
    /// (operator-made ones included: that only tightens); an unknown id and another tenant's id are refused the same way. (The operator
    /// names no tenant, so the operator revokes by id alone.)
    /// </summary>
    public async Task<bool> RevokeWaiverAsync(ActorScope actor, string waiverId, CancellationToken ct)
    {
        if (actor.TenantId is { } own)
        {
            var tenant = await _tenants.GetAsync(own, ct);
            if (tenant is null || !tenant.Policy.Waivers.Any(w => w.Id == waiverId)) throw new AccessDeniedException("That waiver is not in your tenant.");
            return await _tenants.RevokeWaiverAsync(own, waiverId, actor.Name, ct);   // and the store re-checks the tenant in the same statement
        }
        return await _tenants.RevokeWaiverAsync(null, waiverId, actor.Name, ct);
    }

    /// <summary>Operator only: the upstream identity holds the customer's private key.</summary>
    public Task SetUpstreamIdentityAsync(ActorScope actor, string tenantId, UpstreamIdentity identity, CancellationToken ct)
    {
        RequireOperator(actor);
        var checkedIdentity = identity with
        {
            AzureTenantId = RequireGuid(identity.AzureTenantId, "--azure-tenant (the customer's directory id)"),
            ClientId = RequireGuid(identity.ClientId, "--client-id (the application id)"),
        };
        if (string.IsNullOrWhiteSpace(identity.CertificatePem)) throw new ArgumentException("The certificate file is empty.");
        return _tenants.SetUpstreamIdentityAsync(tenantId, checkedIdentity, ct);
    }

    /// <summary>
    /// Operator only. Binds a tenant to Packmoat's shared app in the customer's directory. Refused when:
    /// <list type="bullet">
    /// <item>the tenant's URLs do not pass the strict rules (https; a path prefix ending with '/'; the feed under base + prefix):
    /// with one app for many customers, the prefix is what keeps a tenant inside its own organisation;</item>
    /// <item>another SHARED tenant is bound to the same directory: never allowed (two tenants would share one service principal,
    /// so one could use what the other granted; the database enforces this too);</item>
    /// <item>an own-app tenant uses the same directory, or another row holds a directory that is not a GUID and so cannot be compared,
    /// unless <paramref name="allowSharedDirectory"/>.</item>
    /// </list>
    /// Binding (even again to the same directory) removes any earlier passing onboarding check: run it again before the tenant serves.
    /// </summary>
    public async Task BindSharedIdentityAsync(ActorScope actor, string tenantId, string azureTenantId, bool allowSharedDirectory, CancellationToken ct)
    {
        RequireOperator(actor);
        var directory = RequireGuid(azureTenantId, "--azure-tenant (the customer's directory id)");
        var tenant = await _tenants.GetAsync(tenantId, ct) ?? throw new KeyNotFoundException($"No tenant '{tenantId}'.");
        if (TenantUrlRules.Check(tenant.UpstreamBase, tenant.AllowedPathPrefix, tenant.UpstreamFeedIndexUrl, requireHttps: true, requirePrefix: true) is { } problem)
            throw new ArgumentException($"Tenant '{tenantId}' cannot use the shared app: {problem}");

        foreach (var other in await _tenants.ListAsync(ct))
        {
            if (other.Id == tenantId) continue;
            var binding = await _tenants.GetUpstreamBindingAsync(other.Id, ct);
            if (binding is null) continue;
            var comparable = Guid.TryParse(binding.AzureTenantId.Trim(), out var g);
            if (comparable && g.ToString("D") == directory && binding.Kind == IdentityKind.Shared)
                throw new ArgumentException($"Directory {directory} is already bound to the shared app by tenant '{other.Id}'. Two shared tenants can never share a directory.");
            if (allowSharedDirectory) continue;
            if (comparable && g.ToString("D") == directory)
                throw new ArgumentException($"Directory {directory} is already used by tenant '{other.Id}' (own app). If both really are the same customer's directory, pass --allow-shared-directory.");
            if (!comparable)
                throw new ArgumentException($"Tenant '{other.Id}' stores its directory as a name, not a GUID, so it cannot be compared with {directory}. Fix that tenant's identity, or pass --allow-shared-directory if you have checked it is a different directory.");
        }
        await _tenants.BindSharedIdentityAsync(tenantId, directory, ct);
    }

    /// <summary>What a shared tenant's onboarding check is about: read once at its start, and recorded exactly as read.</summary>
    public sealed record SharedCheckSubject(string TenantId, string AzureTenantId, string FeedIndexUrl, string ClientId);

    /// <summary>Operator only: the directory, feed URL and active shared client id of a shared tenant, as they are now.</summary>
    public async Task<SharedCheckSubject> ReadSharedCheckSubjectAsync(ActorScope actor, string tenantId, CancellationToken ct)
    {
        RequireOperator(actor);
        var tenant = await _tenants.GetAsync(tenantId, ct) ?? throw new KeyNotFoundException($"No tenant '{tenantId}'.");
        var binding = await _tenants.GetUpstreamBindingAsync(tenantId, ct);
        if (binding is not { Kind: IdentityKind.Shared }) throw new ArgumentException($"Tenant '{tenantId}' does not use the shared app.");
        var active = await SharedStore().GetAsync(SharedAppSlot.Active, ct)
                     ?? throw new ArgumentException("The tenant uses the shared app but no active shared credential is set (identity shared set).");
        return new SharedCheckSubject(tenantId, binding.AzureTenantId, tenant.UpstreamFeedIndexUrl, active.ClientId);
    }

    /// <summary>
    /// Operator only: records that the onboarding check passed for exactly <paramref name="checkedSubject"/> (what the check read at its
    /// start). The store re-reads the binding, feed URL and active client id in the same transaction and refuses if any of them changed
    /// meanwhile, so a record can never vouch for something that was not checked. Without a record the tenant serves no traffic.
    /// </summary>
    public async Task<SharedAppVerification> RecordSharedVerificationAsync(ActorScope actor, SharedCheckSubject checkedSubject, CancellationToken ct)
    {
        RequireOperator(actor);
        var record = new SharedAppVerification(checkedSubject.TenantId, checkedSubject.AzureTenantId, checkedSubject.FeedIndexUrl, checkedSubject.ClientId, _clock.UtcNow, actor.Name);
        if (!await SharedStore().RecordVerificationAsync(record, ct))
            throw new ArgumentException("The tenant's directory or feed URL, or the active shared client id, changed while the check ran (or the tenant no longer uses the shared app), so nothing was recorded. Run the check again.");
        return record;
    }

    /// <summary>Operator only: removes <paramref name="record"/> if it is still the tenant's record (one made later is left alone). True when removed.</summary>
    public Task<bool> WithdrawSharedVerificationAsync(ActorScope actor, SharedAppVerification record, CancellationToken ct)
    {
        RequireOperator(actor);
        return SharedStore().DeleteVerificationAsync(record, ct);
    }

    /// <summary>
    /// Operator only: stores the shared app's client id and certificate + key, as the active one or as the next one (rotation).
    /// The PEM must hold a readable certificate with its private key and must not have expired.
    /// </summary>
    public Task SetSharedAppCredentialAsync(ActorScope actor, SharedAppSlot slot, SharedAppCredential credential, CancellationToken ct)
    {
        RequireOperator(actor);
        if (string.IsNullOrWhiteSpace(credential.CertificatePem)) throw new ArgumentException("The certificate file is empty.");
        var clientId = RequireGuid(credential.ClientId, "--client-id (the shared app's application id)");
        CheckUsable(credential);
        return SharedStore().SetAsync(slot, credential with { ClientId = clientId }, ct);
    }

    /// <summary>Operator only: the next credential becomes the active one, after the same checks as set. False when there is no next one.</summary>
    public Task<bool> PromoteSharedAppCredentialAsync(ActorScope actor, CancellationToken ct)
    {
        RequireOperator(actor);
        _ = Inspector();   // refuse before touching the store if nothing can check the certificate
        return SharedStore().PromoteAsync(CheckUsable, ct);
    }

    /// <summary>Throws <see cref="ArgumentException"/> unless the PEM has a readable, unexpired certificate and its private key.</summary>
    private void CheckUsable(SharedAppCredential credential)
    {
        var facts = Inspector().Inspect(credential.CertificatePem);
        if (!facts.HasPrivateKey) throw new ArgumentException("The certificate has no private key matching it; use the .pem from 'identity shared new-cert'.");
        if (facts.NotAfter <= _clock.UtcNow) throw new ArgumentException($"The certificate expired on {facts.NotAfter:u}.");
    }

    private ISharedAppCredentialStore SharedStore() => _shared ?? throw new InvalidOperationException("The shared app credential store is not configured here.");
    private ICertificateInspector Inspector() => _inspector ?? throw new InvalidOperationException("No certificate inspector is configured here, so a shared credential cannot be checked.");

    /// <summary>Refuses any tenant actor. Call it before an operator-only action that does not go through a command here.</summary>
    public static void RequireOperator(ActorScope actor)
    {
        if (!actor.IsOperator) throw new AccessDeniedException("Only the operator can do this.");
    }

    public Task<IssuedToken> IssueTokenAsync(ActorScope actor, string tenantId, string label, CancellationToken ct, TokenKind kind = TokenKind.Developer)
    {
        Require(actor, tenantId);
        return _tokens.IssueAsync(tenantId, label, ct, kind);
    }

    /// <summary>
    /// True when a token was revoked. A tenant actor may revoke only a token that belongs to its own tenant; an unknown id
    /// and another tenant's id are refused the same way, so ids cannot be probed.
    /// </summary>
    public async Task<bool> RevokeTokenAsync(ActorScope actor, string tokenId, CancellationToken ct)
    {
        if (actor.TenantId is { } own)
        {
            var mine = await _tokens.ListAsync(own, ct);
            if (!mine.Any(t => t.TokenId == tokenId)) throw new AccessDeniedException("That token is not in your tenant.");
        }
        return actor.TenantId is { } t ? await _tokens.RevokeAsync(t, tokenId, ct) : await _tokens.RevokeAsync(tokenId, ct);
    }

    private static void Require(ActorScope actor, string? tenantId)
    {
        if (!actor.CanAccess(tenantId)) throw new AccessDeniedException("You can only act on your own tenant.");
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("A tenant id is required.", nameof(tenantId));
    }
}

/// <summary>
/// A tenant and, for the operator only, how it signs in upstream: <c>Binding</c> (no key material) and, in own-app mode,
/// <c>Identity</c> (with the private key), and <c>Setup</c>: who made it and who sets up its feed (#207).
/// </summary>
public sealed record TenantDetail(Tenant Tenant, UpstreamIdentity? Identity, UpstreamBinding? Binding = null)
{
    public TenantSetup? Setup { get; init; }
}

/// <summary>The admin read operations, scoped the same way as <see cref="AdminCommands"/>.</summary>
public sealed class AdminQueries
{
    private readonly ITenantStore _tenants;
    private readonly IAuditQuery _audit;
    private readonly ITokenService _tokens;
    private readonly IActiveDeveloperQuery _active;
    private readonly ISharedAppCredentialStore? _shared;
    private readonly IDecisionRecordQuery? _decisions;

    public AdminQueries(ITenantStore tenants, IAuditQuery audit, ITokenService tokens, IActiveDeveloperQuery active, ISharedAppCredentialStore? shared = null,
        IDecisionRecordQuery? decisions = null)
    {
        _shared = shared;
        _decisions = decisions;
        _tenants = tenants;
        _audit = audit;
        _tokens = tokens;
        _active = active;
    }

    public const int MinActiveDays = 1;
    /// <summary>No further back than token_activity is kept (#136).</summary>
    public const int MaxActiveDays = AuditPolicy.TokenActivityRetentionDays;

    /// <summary>Distinct developer tokens used in the last <paramref name="days"/> days (1 to <see cref="MaxActiveDays"/>, checked here only). Token labels may be personal data.</summary>
    public Task<ActiveDevelopers> ActiveDevelopersAsync(ActorScope actor, string tenantId, int days, CancellationToken ct)
    {
        Require(actor, tenantId);
        if (days is < MinActiveDays or > MaxActiveDays) throw new ArgumentException($"days must be a whole number from {MinActiveDays} to {MaxActiveDays}.", nameof(days));
        return _active.GetAsync(tenantId, days, ct);
    }

    /// <summary>One tenant, or null when it does not exist. A tenant actor naming another tenant is refused before the store is read.</summary>
    public Task<Tenant?> GetTenantAsync(ActorScope actor, string tenantId, CancellationToken ct)
    {
        Require(actor, tenantId);
        return _tenants.GetAsync(tenantId, ct);
    }

    /// <summary>
    /// A tenant with its upstream identity. The identity (it carries the private key) is read for the operator only;
    /// for a tenant actor it is always null, whether or not one is set.
    /// </summary>
    public async Task<TenantDetail?> ShowTenantAsync(ActorScope actor, string tenantId, CancellationToken ct)
    {
        var tenant = await GetTenantAsync(actor, tenantId, ct);
        if (tenant is null) return null;
        if (!actor.IsOperator) return new TenantDetail(tenant, null);
        return new TenantDetail(tenant, await _tenants.GetUpstreamIdentityAsync(tenantId, ct), await _tenants.GetUpstreamBindingAsync(tenantId, ct))
        {
            Setup = await _tenants.GetSetupAsync(tenantId, ct),
        };
    }

    /// <summary>Operator only: which app the tenant uses (own or shared) and in which directory. Reads no key material.</summary>
    public Task<UpstreamBinding?> GetUpstreamBindingAsync(ActorScope actor, string tenantId, CancellationToken ct)
    {
        AdminCommands.RequireOperator(actor);
        Require(actor, tenantId);
        return _tenants.GetUpstreamBindingAsync(tenantId, ct);
    }

    /// <summary>Operator only: the recorded passing onboarding check of a shared tenant, if any (it may be for an older binding).</summary>
    public Task<SharedAppVerification?> SharedVerificationAsync(ActorScope actor, string tenantId, CancellationToken ct)
    {
        AdminCommands.RequireOperator(actor);
        Require(actor, tenantId);
        return _shared is null ? Task.FromResult<SharedAppVerification?>(null) : _shared.GetVerificationAsync(tenantId, ct);
    }

    /// <summary>Operator only: the shared app's credentials by slot (only slots that are set). They hold the private key: callers print only derived facts.</summary>
    public async Task<IReadOnlyList<(SharedAppSlot Slot, SharedAppCredential Credential)>> SharedAppCredentialsAsync(ActorScope actor, CancellationToken ct)
    {
        AdminCommands.RequireOperator(actor);
        if (_shared is null) return Array.Empty<(SharedAppSlot, SharedAppCredential)>();
        var list = new List<(SharedAppSlot, SharedAppCredential)>();
        foreach (var slot in new[] { SharedAppSlot.Active, SharedAppSlot.Next })
            if (await _shared.GetAsync(slot, ct) is { } c) list.Add((slot, c));
        return list;
    }

    /// <summary>Operator only: every tenant with its identity, for certificate expiry.</summary>
    public async Task<IReadOnlyList<TenantDetail>> ListIdentitiesAsync(ActorScope actor, CancellationToken ct)
    {
        AdminCommands.RequireOperator(actor);
        var list = new List<TenantDetail>();
        foreach (var t in await _tenants.ListAsync(ct)) list.Add(new TenantDetail(t, await _tenants.GetUpstreamIdentityAsync(t.Id, ct), await _tenants.GetUpstreamBindingAsync(t.Id, ct)));
        return list;
    }

    public Task<IReadOnlyList<TokenInfo>> ListTokensAsync(ActorScope actor, string tenantId, CancellationToken ct)
    {
        Require(actor, tenantId);
        return _tokens.ListAsync(tenantId, ct);
    }

    /// <summary>How many waivers the list returns: active ones and the history after them.</summary>
    public const int WaiverListLimit = 200;

    /// <summary>The tenant's waivers, active ones first, then expired and revoked, newest first within each (at most <see cref="WaiverListLimit"/>). Reasons and names are untrusted text.</summary>
    public Task<IReadOnlyList<WaiverRecord>> ListWaiversAsync(ActorScope actor, string tenantId, CancellationToken ct)
    {
        Require(actor, tenantId);
        return _tenants.ListWaiversAsync(tenantId, WaiverListLimit, ct);
    }

    private static void Require(ActorScope actor, string? tenantId)
    {
        if (!actor.CanAccess(tenantId)) throw new AccessDeniedException("You can only read your own tenant.");
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("A tenant id is required.", nameof(tenantId));
    }

    /// <summary>The operator sees every tenant; a tenant actor sees only its own (never an error, never another tenant).</summary>
    public async Task<IReadOnlyList<Tenant>> ListTenantsAsync(ActorScope actor, CancellationToken ct)
    {
        var all = await _tenants.ListAsync(ct);
        return actor.IsOperator ? all : all.Where(t => actor.CanAccess(t.Id)).ToList();
    }

    /// <summary>True when this server can read the decision record (it has the database).</summary>
    public bool HasDecisionRecord => _decisions is not null;

    /// <summary>
    /// Operator only: one tenant's decision record (#123), newest last-seen first, at most the query's limit (1 to
    /// <see cref="DecisionRecordQuery.MaxLimit"/>). The package id is trimmed and lower-cased and the version normalised, as the recorder
    /// stores them; a version needs a package; a prefix must not be empty; neither may be longer than the recorder keeps. All checked
    /// here, before the store is read. Package ids and versions in the rows are whatever clients asked for: callers treat them as untrusted.
    /// </summary>
    public Task<DecisionRecordPage> DecisionsAsync(ActorScope actor, DecisionRecordQuery query, CancellationToken ct)
    {
        AdminCommands.RequireOperator(actor);
        Require(actor, query.TenantId);
        if (query.Limit is < 1 or > DecisionRecordQuery.MaxLimit)
            throw new ArgumentException($"The limit must be a whole number from 1 to {DecisionRecordQuery.MaxLimit}.", nameof(query));
        var package = string.IsNullOrWhiteSpace(query.PackageId) ? null : query.PackageId.Trim().ToLowerInvariant();
        var version = string.IsNullOrWhiteSpace(query.Version) ? null : NuGetVersionKey.Normalize(query.Version);
        if (version is not null && package is null) throw new ArgumentException("A version needs a package.", nameof(query));
        if (package is null && query.PackagePrefix) throw new ArgumentException("A package prefix must not be empty.", nameof(query));
        if (package?.Length > RecordingPackageGate.MaxFieldLength || version?.Length > RecordingPackageGate.MaxFieldLength)
            throw new ArgumentException($"A package id or version is at most {RecordingPackageGate.MaxFieldLength} characters.", nameof(query));
        if (_decisions is null) throw new InvalidOperationException("The decision record is not available on this server.");
        return _decisions.QueryAsync(query with { PackageId = package, Version = version }, ct);
    }

    /// <summary>
    /// Newest first. The operator may pass a tenant id or null for all. A tenant actor with null gets its own tenant;
    /// naming any other tenant is refused.
    /// </summary>
    public Task<IReadOnlyList<AuditEvent>> AuditTailAsync(ActorScope actor, string? tenantId, int limit, CancellationToken ct)
    {
        if (actor.TenantId is { } own)
        {
            tenantId ??= own;
            if (!actor.CanAccess(tenantId)) throw new AccessDeniedException("You can only read your own tenant's audit trail.");
        }
        return _audit.QueryAsync(tenantId, limit, ct);
    }
}
