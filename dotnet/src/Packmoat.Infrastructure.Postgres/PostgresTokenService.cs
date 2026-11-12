using System.Security.Cryptography;
using Npgsql;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// Issues, verifies and revokes tenant tokens on PostgreSQL, as SQLite's token service does, with its SQL written again
/// for PostgreSQL. Only SHA-256 hashes are stored; the plaintext is shown once. A token past its <c>expires_at</c> does not authenticate,
/// and neither does a token of a tenant the serving gate refuses (see <see cref="IServingGate"/>; separate from the operator's
/// <c>tenants.active</c>). Times are timestamptz, in UTC to the microsecond (<see cref="PostgresTime"/>). Each port call is one transaction;
/// the per-member cap takes the tenant's row FOR NO KEY UPDATE first, and the last-used touch is retried on a deadlock
/// (<see cref="PostgresRetry"/>), each attempt one statement and so one whole transaction. PACKMOAT_DB_ENGINE=postgres selects it.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresTokenService : ITokenService, ITenantAuthenticator
{
    private const string Columns = """
        t.id, t.tenant_id, t.display_prefix, t.label, t.created_at, t.revoked_at, t.last_used_at, t.kind,
        t.owner_tid, t.owner_oid, m.display_name, t.expires_at, t.issued_via, t.revoked_by, m.last_seen_at
        """;
    private const string From = "FROM tokens t LEFT JOIN members m ON m.azure_tenant_id = t.owner_tid AND m.object_id = t.owner_oid";

    private readonly PostgresDatabase _db;
    private readonly PostgresTenantStore _tenants;
    private readonly IClock _clock;
    private readonly IServingGate _gate;

    /// <param name="gate">Asked on each lookup of an active tenant's token (behind the cache: a miss only), before the token is touched.
    /// Required: there is no default, so nothing builds a token service that serves by accident.</param>
    public PostgresTokenService(PostgresDatabase db, PostgresTenantStore tenants, IClock clock, IServingGate gate)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    private static (string Id, string Plaintext, string Prefix) NewToken()
    {
        var plaintext = "pmt_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(); // 192 random bits
        var id = "tok_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        return (id, plaintext, plaintext[..8] + "…");
    }

    private static string KindText(TokenKind kind) => kind == TokenKind.Automation ? "automation" : "developer";

    /// <summary>An operator's token. One statement: the insert happens only if the tenant row exists (active or not), else nothing is written.</summary>
    public async Task<IssuedToken> IssueAsync(string tenantId, string label, CancellationToken ct, TokenKind kind = TokenKind.Developer)
    {
        var (id, plaintext, prefix) = NewToken();
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO tokens(id, tenant_id, token_hash, display_prefix, label, created_at, kind)
            SELECT @id,@t,@h,@p,@l,@c,@k WHERE EXISTS (SELECT 1 FROM tenants WHERE id=@t)
            """, c);
        cmd.Parameters.AddWithValue("k", KindText(kind));
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("h", TokenHasher.Hash(plaintext));
        cmd.Parameters.AddWithValue("p", prefix);
        cmd.Parameters.AddWithValue("l", label);
        cmd.Parameters.AddWithValue("c", Now());
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new KeyNotFoundException($"No tenant '{tenantId}'.");
        return new IssuedToken(id, plaintext, prefix);
    }

    /// <summary>
    /// A member's own token, at most <paramref name="maxActive"/> live ones each. The tenant's row is read FOR NO KEY UPDATE first (none: no such
    /// tenant), so a second issue for the same tenant waits until this one ends, and its count then sees this one's token.
    /// </summary>
    public async Task<IssuedToken?> IssueForMemberAsync(string tenantId, string ownerTid, string ownerOid, string label, DateTimeOffset expiresAt, TokenKind kind, int maxActive,
        CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await using (var lockTenant = new NpgsqlCommand("SELECT 1 FROM tenants WHERE id=@t FOR NO KEY UPDATE", c, tx))   // the cap's lock
        {
            lockTenant.Parameters.AddWithValue("t", tenantId);
            if (await lockTenant.ExecuteScalarAsync(ct) is null) throw new KeyNotFoundException($"No tenant '{tenantId}'.");
        }
        if (expiresAt <= _clock.UtcNow) throw new ArgumentException("A token's expiry must be in the future.", nameof(expiresAt));
        var (id, plaintext, prefix) = NewToken();

        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO tokens(id, tenant_id, token_hash, display_prefix, label, created_at, kind, owner_tid, owner_oid, expires_at, issued_via)
            SELECT @id,@t,@h,@p,@l,@c,@k,@otid,@ooid,@e,'self'
            WHERE (SELECT COUNT(*) FROM tokens WHERE tenant_id=@t AND owner_tid=@otid AND owner_oid=@ooid AND issued_via='self'
                   AND revoked_at IS NULL AND (expires_at IS NULL OR expires_at > @c)) < @max
            """, c, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("h", TokenHasher.Hash(plaintext));
        cmd.Parameters.AddWithValue("p", prefix);
        cmd.Parameters.AddWithValue("l", label);
        cmd.Parameters.AddWithValue("c", Now());
        cmd.Parameters.AddWithValue("k", KindText(kind));
        cmd.Parameters.AddWithValue("otid", ownerTid);
        cmd.Parameters.AddWithValue("ooid", ownerOid);
        cmd.Parameters.AddWithValue("e", PostgresTime.ToDatabase(expiresAt));
        cmd.Parameters.AddWithValue("max", (long)maxActive);
        var inserted = await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return inserted == 1 ? new IssuedToken(id, plaintext, prefix) : null;
    }

    public Task<bool> RevokeAsync(string tokenId, CancellationToken ct) =>
        UpdateAsync("UPDATE tokens SET revoked_at=@t WHERE id=@id AND revoked_at IS NULL", ct, ("id", tokenId));

    public Task<bool> RevokeAsync(string tenantId, string tokenId, CancellationToken ct) =>
        UpdateAsync("UPDATE tokens SET revoked_at=@t WHERE id=@id AND tenant_id=@tenant AND revoked_at IS NULL", ct, ("id", tokenId), ("tenant", tenantId));

    public Task<bool> RevokeForMemberAsync(string tenantId, string ownerTid, string ownerOid, string tokenId, string revokedBy, CancellationToken ct) =>
        UpdateAsync("UPDATE tokens SET revoked_at=@t, revoked_by=@by WHERE id=@id AND tenant_id=@tenant AND owner_tid=@otid AND owner_oid=@ooid AND revoked_at IS NULL", ct,
            ("by", revokedBy), ("id", tokenId), ("tenant", tenantId), ("otid", ownerTid), ("ooid", ownerOid));

    public Task<bool> RevokeInTenantAsync(string tenantId, string tokenId, string revokedBy, CancellationToken ct) =>
        UpdateAsync("UPDATE tokens SET revoked_at=@t, revoked_by=@by WHERE id=@id AND tenant_id=@tenant AND revoked_at IS NULL", ct,
            ("by", revokedBy), ("id", tokenId), ("tenant", tenantId));

    /// <summary>One revoke: one statement, so one transaction, with @t the clock's now. True when a row changed.</summary>
    private async Task<bool> UpdateAsync(string sql, CancellationToken ct, params (string Name, string Value)[] args)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("t", Now());
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public Task<IReadOnlyList<TokenInfo>> ListAsync(string tenantId, CancellationToken ct) =>
        QueryAsync("WHERE t.tenant_id=@t", ct, ("t", tenantId));

    public Task<IReadOnlyList<TokenInfo>> ListForMemberAsync(string tenantId, string ownerTid, string ownerOid, CancellationToken ct) =>
        QueryAsync("WHERE t.tenant_id=@t AND t.owner_tid=@otid AND t.owner_oid=@ooid", ct, ("t", tenantId), ("otid", ownerTid), ("ooid", ownerOid));

    private async Task<IReadOnlyList<TokenInfo>> QueryAsync(string where, CancellationToken ct, params (string Name, string Value)[] args)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} {From} {where} ORDER BY t.created_at, t.id", c);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        var list = new List<TokenInfo>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new TokenInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), PostgresTenantStore.Time(r, 4),
                TimeOrNull(r, 5), TimeOrNull(r, 6), r.GetString(7) == "automation" ? TokenKind.Automation : TokenKind.Developer,
                StringOrNull(r, 8), StringOrNull(r, 9), StringOrNull(r, 10), TimeOrNull(r, 11),
                r.GetString(12) == "self" ? TokenIssuer.Self : TokenIssuer.Operator, StringOrNull(r, 13), TimeOrNull(r, 14)));
        return list;
    }

    public async ValueTask<AuthResult?> AuthenticateAsync(string token, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        string tokenId, tenantId;
        DateTimeOffset? expiresAt;
        await using (var cmd = new NpgsqlCommand("SELECT id, tenant_id, expires_at FROM tokens WHERE token_hash=@h AND revoked_at IS NULL", c))
        {
            cmd.Parameters.AddWithValue("h", TokenHasher.Hash(token));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            (tokenId, tenantId, expiresAt) = (r.GetString(0), r.GetString(1), TimeOrNull(r, 2));
        }
        if (expiresAt is { } e && e <= _clock.UtcNow) return null;   // expired: dead, exactly like revoked

        var tenant = await _tenants.GetActiveAsync(c, tenantId, ct);
        if (tenant is null) return null; // suspended or removed tenant: the token is dead

        // The serving gate, separately from the operator's switch, and before the touch: a refused token is not "used". A gate that throws
        // throws on (the proxy answers 503): never served.
        var answer = await _gate.AskAsync(tenantId, ct);
        // The two-argument rule on purpose, never the upstream one: the deploy canary has no upstream and its /v3/ping must keep answering,
        // and a tenant whose upstream is missing or unchecked already fails closed on each proxied request.
        if (ServingRule.Decide(tenant.Active, answer) != ServingDecision.Serve) return null;

        // The touch, with the retry: each attempt is this one statement, so one whole transaction of its own, and
        // nothing outside it is changed before it commits.
        var now = Now();
        var cutoff = PostgresTime.ToDatabase(_clock.UtcNow.AddMinutes(-1));
        await PostgresRetry.RunAsync(async attemptCt =>
        {
            await using var touch = new NpgsqlCommand("UPDATE tokens SET last_used_at=@now WHERE id=@id AND (last_used_at IS NULL OR last_used_at < @cutoff)", c);
            touch.Parameters.AddWithValue("now", now);
            touch.Parameters.AddWithValue("id", tokenId);
            touch.Parameters.AddWithValue("cutoff", cutoff);
            await touch.ExecuteNonQueryAsync(attemptCt);
        }, ct);
        // A gate's known end is exact: the result says so, so the cache in front never serves it past then.
        return new AuthResult(tenant, tokenId, expiresAt, answer.Until);
    }

    private DateTimeOffset Now() => PostgresTime.ToDatabase(_clock.UtcNow);
    private static DateTimeOffset? TimeOrNull(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : PostgresTenantStore.Time(r, i);
    private static string? StringOrNull(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
