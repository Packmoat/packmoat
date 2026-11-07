using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Application.Tests;

/// <summary>Records every call so a refused actor can be shown to have touched nothing.</summary>
internal sealed class FakeStore : ITenantStore, ITokenService, IAuditQuery, IActiveDeveloperQuery
{
    public List<string> Calls { get; } = new();
    /// <summary>Reads of tenant config; kept apart so a scope-first test can assert nothing at all was read.</summary>
    public List<string> Reads { get; } = new();
    public string? LastWaiverCreatedBy { get; private set; }
    public List<Tenant> Tenants { get; } = new();
    public List<TokenInfo> TokenRows { get; } = new();
    public DateTimeOffset? LastWaiverExpiry { get; private set; }
    public string? LastAuditTenant { get; private set; } = "(not called)";

    public Task CreateAsync(NewTenant t, CancellationToken ct) { Calls.Add("create " + t.Id); return Task.CompletedTask; }
    public Task AddInternalPrefixAsync(string tenantId, string prefix, CancellationToken ct) { Calls.Add($"prefix {tenantId} {prefix}"); return Task.CompletedTask; }
    public Task SetOverrideAsync(string tenantId, string packageId, int days, CancellationToken ct) { Calls.Add($"override {tenantId} {packageId} {days}"); return Task.CompletedTask; }
    public Task<Waiver> AddWaiverAsync(string tenantId, string packageId, string version, DateTimeOffset expiresAt, string reason, string createdBy, CancellationToken ct)
    {
        Calls.Add($"waiver {tenantId} {packageId}");
        LastWaiverExpiry = expiresAt;
        LastWaiverCreatedBy = createdBy;
        return Task.FromResult(new Waiver("w1", packageId, version, expiresAt, reason, createdBy));
    }
    /// <summary>What the next customer insert reports, and the count of active customer waivers it reports after it.</summary>
    public WaiverInsertOutcome NextCustomerOutcome { get; set; } = WaiverInsertOutcome.Added;
    public int ActiveCustomerAfter { get; set; } = 1;
    public TenantOrigin Origin { get; set; } = TenantOrigin.Operator;
    public Task<CustomerWaiverInsert> AddCustomerWaiverAsync(string tenantId, string packageId, string version, DateTimeOffset expiresAt, string reason, string createdBy, int maxActive, CancellationToken ct)
    {
        Calls.Add($"customer-waiver {tenantId} {packageId} {version} max={maxActive}");
        LastWaiverExpiry = expiresAt;
        LastWaiverCreatedBy = createdBy;
        var waiver = NextCustomerOutcome == WaiverInsertOutcome.Added ? new Waiver("w1", packageId, version, expiresAt, reason, createdBy, WaiverSource.Customer) : null;
        return Task.FromResult(new CustomerWaiverInsert(NextCustomerOutcome, waiver, ActiveCustomerAfter, Origin));
    }
    public List<WaiverRecord> WaiverRows { get; } = new();
    public Task<IReadOnlyList<WaiverRecord>> ListWaiversAsync(string tenantId, int limit, CancellationToken ct)
    {
        Reads.Add($"waivers {tenantId} {limit}");
        return Task.FromResult<IReadOnlyList<WaiverRecord>>(WaiverRows.Take(limit).ToList());
    }
    public Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct) { Calls.Add("list"); return Task.FromResult<IReadOnlyList<Tenant>>(Tenants.ToList()); }

    public TokenKind? LastKind { get; private set; }
    public Task<ActiveDevelopers> GetAsync(string tenantId, int days, CancellationToken ct) { Calls.Add($"active {tenantId} {days}"); return Task.FromResult(new ActiveDevelopers(tenantId, days, 0, Array.Empty<ActiveToken>())); }
    public Task<IssuedToken> IssueAsync(string tenantId, string label, CancellationToken ct, TokenKind kind = TokenKind.Developer) { LastKind = kind; Calls.Add($"issue {tenantId}"); return Task.FromResult(new IssuedToken("t-new", "secret", "pmt_")); }
    public Task<bool> RevokeAsync(string tokenId, CancellationToken ct) { Calls.Add("revoke " + tokenId); return Task.FromResult(true); }
    public Task<bool> RevokeAsync(string tenantId, string tokenId, CancellationToken ct) { Calls.Add($"revoke {tenantId} {tokenId}"); return Task.FromResult(true); }
    public Task<IReadOnlyList<TokenInfo>> ListAsync(string tenantId, CancellationToken ct) { Reads.Add("tokens " + tenantId); return Task.FromResult<IReadOnlyList<TokenInfo>>(TokenRows.Where(r => r.TenantId == tenantId).ToList()); }
    // The customer self-service methods are exercised against FakeTokens (CustomerTokenTests); the admin commands never call them.
    public Task<IssuedToken?> IssueForMemberAsync(string tenantId, string ownerTid, string ownerOid, string label, DateTimeOffset expiresAt, TokenKind kind, int maxActive, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<TokenInfo>> ListForMemberAsync(string tenantId, string ownerTid, string ownerOid, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> RevokeForMemberAsync(string tenantId, string ownerTid, string ownerOid, string tokenId, string revokedBy, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> RevokeInTenantAsync(string tenantId, string tokenId, string revokedBy, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<AuditEvent>> QueryAsync(string? tenantId, int limit, CancellationToken ct) { LastAuditTenant = tenantId; Calls.Add("audit"); return Task.FromResult<IReadOnlyList<AuditEvent>>(Array.Empty<AuditEvent>()); }

    public Task<Tenant?> GetAsync(string tenantId, CancellationToken ct) { Reads.Add("get " + tenantId); return Task.FromResult(Tenants.FirstOrDefault(t => t.Id == tenantId)); }
    public Task SetActiveAsync(string tenantId, bool active, CancellationToken ct) { Calls.Add($"active {tenantId} {active}"); return Task.CompletedTask; }
    public Task SetCooldownAsync(string tenantId, int days, CancellationToken ct) { Calls.Add($"cooldown {tenantId} {days}"); return Task.CompletedTask; }
    public Task RemoveInternalPrefixAsync(string tenantId, string prefix, CancellationToken ct) { Calls.Add($"unprefix {tenantId} {prefix}"); return Task.CompletedTask; }
    public Task RemoveOverrideAsync(string tenantId, string packageId, CancellationToken ct) { Calls.Add($"unoverride {tenantId} {packageId}"); return Task.CompletedTask; }
    public Task<bool> RevokeWaiverAsync(string? tenantId, string waiverId, string revokedBy, CancellationToken ct) { Calls.Add($"unwaive {tenantId ?? "*"} {waiverId} by={revokedBy}"); return Task.FromResult(true); }
    public Task SetUpstreamIdentityAsync(string tenantId, UpstreamIdentity identity, CancellationToken ct) { Calls.Add("identity-set " + tenantId); return Task.CompletedTask; }
    public Task<UpstreamIdentity?> GetUpstreamIdentityAsync(string tenantId, CancellationToken ct) { Reads.Add("identity " + tenantId); return Task.FromResult<UpstreamIdentity?>(new UpstreamIdentity("az", "cid", "PEM")); }
    public Dictionary<string, UpstreamBinding> Bindings { get; } = new();
    public UpstreamIdentity? LastIdentity { get; private set; }
    public Task BindSharedIdentityAsync(string tenantId, string azureTenantId, CancellationToken ct)
    {
        Calls.Add($"bind {tenantId} {azureTenantId}");
        Bindings[tenantId] = new UpstreamBinding(IdentityKind.Shared, azureTenantId, null);
        return Task.CompletedTask;
    }
    public Task<UpstreamBinding?> GetUpstreamBindingAsync(string tenantId, CancellationToken ct) { Reads.Add("binding " + tenantId); return Task.FromResult(Bindings.GetValueOrDefault(tenantId)); }
    /// <summary>Each tenant's origin and feed setup (#207); a tenant with no entry reads operator-made, operator-set.</summary>
    public Dictionary<string, TenantSetup> Setups { get; } = new();
    public Task<TenantSetup?> GetSetupAsync(string tenantId, CancellationToken ct)
    {
        Reads.Add("setup " + tenantId);
        return Task.FromResult(Setups.TryGetValue(tenantId, out var s) ? s
            : Tenants.Any(t => t.Id == tenantId) ? new TenantSetup(TenantOrigin.Operator, FeedSetup.Operator) : null);
    }
}

/// <summary>An in-memory shared app credential store that records calls.</summary>
internal sealed class FakeSharedStore : ISharedAppCredentialStore
{
    public Dictionary<SharedAppSlot, SharedAppCredential> Slots { get; } = new();
    public List<string> Calls { get; } = new();
    public Task SetAsync(SharedAppSlot slot, SharedAppCredential credential, CancellationToken ct) { Calls.Add("set " + slot); Slots[slot] = credential; return Task.CompletedTask; }
    public Task<SharedAppCredential?> GetAsync(SharedAppSlot slot, CancellationToken ct) { Calls.Add("get " + slot); return Task.FromResult(Slots.GetValueOrDefault(slot)); }
    public Task<bool> PromoteAsync(Action<SharedAppCredential> validate, CancellationToken ct)
    {
        Calls.Add("promote");
        if (!Slots.TryGetValue(SharedAppSlot.Next, out var next)) return Task.FromResult(false);
        validate(next);   // throws to refuse, before anything changes
        Slots.Remove(SharedAppSlot.Next);
        Slots[SharedAppSlot.Active] = next;
        return Task.FromResult(true);
    }
    public Dictionary<string, SharedAppVerification> Verifications { get; } = new();
    /// <summary>What the real store's in-transaction re-read would say: false means something changed since the check started.</summary>
    public bool StillCurrent { get; set; } = true;
    public Task<bool> RecordVerificationAsync(SharedAppVerification v, CancellationToken ct)
    {
        Calls.Add("verify " + v.TenantId);
        if (!StillCurrent) return Task.FromResult(false);
        Verifications[v.TenantId] = v;
        return Task.FromResult(true);
    }
    public Task<bool> DeleteVerificationAsync(SharedAppVerification v, CancellationToken ct) =>
        Task.FromResult(Verifications.TryGetValue(v.TenantId, out var current) && current == v && Verifications.Remove(v.TenantId));
    public Task<SharedAppVerification?> GetVerificationAsync(string tenantId, CancellationToken ct) => Task.FromResult(Verifications.GetValueOrDefault(tenantId));
}

/// <summary>Reads a fake PEM: "expired" in it means expired, "nokey" means no private key, "garbage" means unreadable.</summary>
internal sealed class FakeInspector : ICertificateInspector
{
    public CertificateFacts Inspect(string pem) =>
        pem.Contains("garbage") ? throw new ArgumentException("The PEM file does not hold a readable certificate.")
        : new CertificateFacts("THUMB", pem.Contains("expired") ? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) : new DateTimeOffset(2027, 10, 10, 0, 0, 0, TimeSpan.Zero), !pem.Contains("nokey"));
}

internal sealed class FixedClock : IClock
{
    public DateTimeOffset UtcNow { get; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
}
