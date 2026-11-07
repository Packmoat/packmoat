using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Application.Tests;

internal sealed class MovableClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>An in-memory token service with the same WHERE semantics as the SQLite one (tenant, owner, cap, expiry).</summary>
internal sealed class FakeTokens : ITokenService
{
    private readonly IClock _clock;
    private int _n;
    public FakeTokens(IClock clock) => _clock = clock;
    public List<TokenInfo> Rows { get; } = new();
    public List<string> Writes { get; } = new();

    public Task<IssuedToken> IssueAsync(string tenantId, string label, CancellationToken ct, TokenKind kind = TokenKind.Developer)
    {
        var id = $"tok_{++_n:x12}";
        Rows.Add(new TokenInfo(id, tenantId, "pmt_0000…", label, _clock.UtcNow, null, null, kind));
        return Task.FromResult(new IssuedToken(id, "pmt_secret_" + id, "pmt_0000…"));
    }

    public Task<IssuedToken?> IssueForMemberAsync(string tenantId, string ownerTid, string ownerOid, string label, DateTimeOffset expiresAt, TokenKind kind, int maxActive, CancellationToken ct)
    {
        Writes.Add("issue");
        var now = _clock.UtcNow;
        if (Rows.Count(r => r.TenantId == tenantId && r.OwnerTid == ownerTid && r.OwnerOid == ownerOid && r.IssuedVia == TokenIssuer.Self && r.IsActiveAt(now)) >= maxActive)
            return Task.FromResult<IssuedToken?>(null);
        var id = $"tok_{++_n:x12}";
        Rows.Add(new TokenInfo(id, tenantId, "pmt_0000…", label, now, null, null, kind, ownerTid, ownerOid, null, expiresAt, TokenIssuer.Self));
        return Task.FromResult<IssuedToken?>(new IssuedToken(id, "pmt_secret_" + id, "pmt_0000…"));
    }

    public Task<bool> RevokeAsync(string tokenId, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> RevokeAsync(string tenantId, string tokenId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<TokenInfo>> ListAsync(string tenantId, CancellationToken ct) => Task.FromResult<IReadOnlyList<TokenInfo>>(Rows.Where(r => r.TenantId == tenantId).ToList());
    public Task<IReadOnlyList<TokenInfo>> ListForMemberAsync(string tenantId, string ownerTid, string ownerOid, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TokenInfo>>(Rows.Where(r => r.TenantId == tenantId && r.OwnerTid == ownerTid && r.OwnerOid == ownerOid).ToList());

    public Task<bool> RevokeForMemberAsync(string tenantId, string ownerTid, string ownerOid, string tokenId, string revokedBy, CancellationToken ct) =>
        Task.FromResult(Revoke(r => r.TokenId == tokenId && r.TenantId == tenantId && r.OwnerTid == ownerTid && r.OwnerOid == ownerOid, revokedBy));

    public Task<bool> RevokeInTenantAsync(string tenantId, string tokenId, string revokedBy, CancellationToken ct) =>
        Task.FromResult(Revoke(r => r.TokenId == tokenId && r.TenantId == tenantId, revokedBy));

    private bool Revoke(Func<TokenInfo, bool> match, string by)
    {
        Writes.Add("revoke");
        var i = Rows.FindIndex(r => r.RevokedAt is null && match(r));
        if (i < 0) return false;
        Rows[i] = Rows[i] with { RevokedAt = _clock.UtcNow, RevokedBy = by };
        return true;
    }
}

internal sealed class FakeMembers : IMemberStore
{
    public Dictionary<(string, string), Member> Rows { get; } = new();
    public Task UpsertAsync(string azureTenantId, string objectId, string tenantId, string displayName, DateTimeOffset seenAt, CancellationToken ct)
    {
        Rows[(azureTenantId, objectId)] = Rows.TryGetValue((azureTenantId, objectId), out var m)
            ? m with { TenantId = tenantId, DisplayName = displayName, LastSeenAt = seenAt }
            : new Member(azureTenantId, objectId, tenantId, displayName, seenAt, seenAt);
        return Task.CompletedTask;
    }
    public Task<Member?> GetAsync(string azureTenantId, string objectId, CancellationToken ct) => Task.FromResult(Rows.GetValueOrDefault((azureTenantId, objectId)));
    public Task<IReadOnlyList<Member>> ListForTenantAsync(string tenantId, CancellationToken ct) => Task.FromResult<IReadOnlyList<Member>>(Rows.Values.Where(m => m.TenantId == tenantId).ToList());
}

internal sealed class FakeDirectories : ISignInDirectoryStore
{
    public Dictionary<string, DirectoryBinding> Rows { get; } = new();
    public Task<bool> BindAsync(DirectoryBinding binding, CancellationToken ct) => Task.FromResult(Rows.TryAdd(binding.AzureTenantId, binding));
    public Task<int?> UnbindAsync(string tenantId, string azureTenantId, DateTimeOffset at, string revokedBy, CancellationToken ct) =>
        Task.FromResult<int?>(Rows.TryGetValue(azureTenantId, out var b) && b.TenantId == tenantId && Rows.Remove(azureTenantId) ? 0 : null);
    public Task<DirectoryBinding?> GetAsync(string azureTenantId, CancellationToken ct) => Task.FromResult(Rows.GetValueOrDefault(azureTenantId));
    public Task<IReadOnlyList<DirectoryBinding>> ListForTenantAsync(string tenantId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DirectoryBinding>>(Rows.Values.Where(b => b.TenantId == tenantId).ToList());
}

internal sealed class FakeActionLog : IAdminActionLog
{
    public List<AdminActionRecord> Rows { get; } = new();
    public bool Fail { get; set; }
    public ValueTask AppendAsync(AdminActionRecord record, CancellationToken ct)
    {
        if (Fail) throw new InvalidOperationException("trail down");
        Rows.Add(record);
        return ValueTask.CompletedTask;
    }
    public Task<IReadOnlyList<AdminActionRecord>> LatestAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<AdminActionRecord>>(Rows.AsEnumerable().Reverse().Take(limit).ToList());
    public Task<AdminActionRecord?> LatestByAsync(string tenantId, string action, string actorTid, string actorOid, CancellationToken ct) =>
        Task.FromResult(Rows.LastOrDefault(r => r.TenantId == tenantId && r.Action == action && r.ActorTid == actorTid && r.ActorOid == actorOid));
    public Task<int> PruneAsync(DateTimeOffset before, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
}
