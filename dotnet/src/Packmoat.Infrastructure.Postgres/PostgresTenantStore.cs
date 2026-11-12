using System.Security.Cryptography;
using Npgsql;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The tenant store on PostgreSQL: tenants, their policy, waivers and upstream identity, as SQLite's tenant store keeps
/// them, with its SQL written again for PostgreSQL. Each port call is one transaction, with no catch inside it and
/// no SAVEPOINT: a refusal reaches the caller and the transaction rolls back. Where SQLite's BEGIN IMMEDIATE makes a check and a write see
/// the same rows, this takes the tenant's row FOR NO KEY UPDATE first (the waiver cap and duplicate check, the upstream identity), or
/// the <see cref="PostgresLock.TenantCreation"/> lock (a new tenant), so whatever else writes that tenant waits, and the lock order is
/// always the tenant row before its children. FOR NO KEY UPDATE, not FOR UPDATE: it serialises these writers just the
/// same, but lets another transaction's foreign-key check on a child row (FOR KEY SHARE) through, so no child insert elsewhere waits
/// on it and no lock upgrade can deadlock. Only the purge, which deletes the row, takes FOR UPDATE. Times are timestamptz, in UTC
/// to the microsecond (<see cref="PostgresTime"/>).
/// PACKMOAT_DB_ENGINE=postgres selects it.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresTenantStore : ITenantStore
{
    private readonly PostgresDatabase _db;
    private readonly IClock _clock;
    private readonly AesGcmSecretProtector _protector;
    private readonly IReadOnlyList<ITenantCreationParticipant> _participants;

    /// <param name="participants">
    /// What takes part in making a tenant, in its transaction (a module's own row). Required: pass
    /// <see cref="TenantLifecycle.Participants"/>, empty for none, so no caller can forget it.
    /// </param>
    public PostgresTenantStore(PostgresDatabase db, IClock clock, AesGcmSecretProtector protector, IReadOnlyList<ITenantCreationParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(participants);
        _db = db;
        _clock = clock;
        _protector = protector;
        _participants = participants;
    }

    public async Task<Tenant?> GetAsync(string tenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        return await LoadAsync(c, tenantId, requireActive: false, ct);
    }

    /// <summary>True when a tenant row exists (active or not). Reads only the tenants table.</summary>
    internal async Task<bool> ExistsAsync(string tenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM tenants WHERE id=@id", c);
        cmd.Parameters.AddWithValue("id", tenantId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>Loads a tenant only if it is active, on the caller's connection. Used by authentication.</summary>
    internal Task<Tenant?> GetActiveAsync(NpgsqlConnection c, string tenantId, CancellationToken ct) => LoadAsync(c, tenantId, requireActive: true, ct);

    public async Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        var ids = new List<string>();
        await using (var cmd = new NpgsqlCommand("SELECT id FROM tenants ORDER BY id", c))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids.Add(r.GetString(0));
        }
        var result = new List<Tenant>();
        foreach (var id in ids) if (await LoadAsync(c, id, false, ct) is { } t) result.Add(t);
        return result;
    }

    private async Task<Tenant?> LoadAsync(NpgsqlConnection c, string tenantId, bool requireActive, CancellationToken ct)
    {
        string name, upstreamBase, prefix, feed;
        int cooldown;
        long revision;
        bool active;
        await using (var cmd = new NpgsqlCommand(
            "SELECT name, upstream_base, allowed_path_prefix, feed_index_url, cooldown_days, policy_revision, active FROM tenants WHERE id=@id" + (requireActive ? " AND active=1" : ""), c))
        {
            cmd.Parameters.AddWithValue("id", tenantId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            (name, upstreamBase, prefix, feed, cooldown, revision, active) =
                (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), checked((int)r.GetInt64(4)), r.GetInt64(5), r.GetInt64(6) != 0);
        }

        var prefixes = await ReadList(c, "SELECT prefix FROM internal_prefixes WHERE tenant_id=@id ORDER BY prefix", tenantId, r => r.GetString(0), ct);
        var overrides = await ReadList(c, "SELECT package_id, cooldown_days FROM package_overrides WHERE tenant_id=@id ORDER BY package_id", tenantId,
            r => new PackageOverride(r.GetString(0), checked((int)r.GetInt64(1))), ct);
        var waivers = await ReadList(c, "SELECT id, package_id, version, expires_at, reason, created_by, source FROM waivers WHERE tenant_id=@id AND revoked_at IS NULL ORDER BY created_at",
            tenantId, r => new Waiver(r.GetString(0), r.GetString(1), r.GetString(2), Time(r, 3), r.GetString(4), r.GetString(5), ParseSource(r.GetString(6))), ct);

        return new Tenant(tenantId, name, upstreamBase, prefix, feed, new TenantPolicy(cooldown, prefixes, waivers, overrides, revision), active);
    }

    private static async Task<List<T>> ReadList<T>(NpgsqlConnection c, string sql, string tenantId, Func<NpgsqlDataReader, T> map, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("id", tenantId);
        var list = new List<T>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(map(r));
        return list;
    }

    public async Task CreateAsync(NewTenant t, CancellationToken ct)
    {
        if (!AdminCommands.IsValidTenantId(t.Id)) throw new ArgumentException("A tenant id is 1 to 63 letters, digits, '-' or '_', starting with a letter or digit.", nameof(t));
        if (t.CooldownDays < 0) throw new ArgumentOutOfRangeException(nameof(t), "Cooldown cannot be negative.");
        if (!t.UpstreamBase.EndsWith('/') && !(t.FeedSetup == FeedSetup.Customer && t.UpstreamBase.Length == 0))
            throw new ArgumentException("UpstreamBase must end with '/'.", nameof(t));
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await InsertAsync(c, tx, _participants, t, _clock.UtcNow, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// The one way a tenant row is written, inside the caller's transaction: first the <see cref="PostgresLock.TenantCreation"/> lock, held
    /// to the transaction's end, so tenant creations run one after another (as SQLite's BEGIN IMMEDIATE runs them) and a creation
    /// participant that checks what other tenants exist sees every creation before its own; then the tenant with its origin and creator;
    /// then each creation participant in order on the same transaction. A participant's veto
    /// (<see cref="TenantCreationRefusedException"/>) or failure throws, and the caller's transaction rolls back.
    /// <para>A caller must not hold a tenant row's lock or a directory's lock before calling this: the creation lock comes
    /// first, so that every creation takes its locks in the same order and none can wait in a cycle with another.</para>
    /// </summary>
    internal static async Task InsertAsync(NpgsqlConnection c, NpgsqlTransaction tx, IReadOnlyList<ITenantCreationParticipant> participants, NewTenant t,
        DateTimeOffset createdAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tx);
        var origin = t.Origin switch { TenantOrigin.Operator => "operator", TenantOrigin.Self => "self", _ => throw new ArgumentOutOfRangeException(nameof(t), "Unknown tenant origin.") };
        var feedSetup = FeedSetupText(t.FeedSetup);
        await PostgresLocks.TakeAsync(c, tx, PostgresLock.TenantCreation, ct);
        // feed_setup is written only when it is not the column's default ('operator'), so an operator-set tenant reads the default.
        await using (var cmd = new NpgsqlCommand(t.FeedSetup == FeedSetup.Operator
            ? """
              INSERT INTO tenants(id, name, upstream_base, allowed_path_prefix, feed_index_url, cooldown_days, created_at, origin, created_by_oid)
              VALUES(@id,@n,@b,@p,@f,@d,@t,@o,@by)
              """
            : """
              INSERT INTO tenants(id, name, upstream_base, allowed_path_prefix, feed_index_url, cooldown_days, created_at, origin, created_by_oid, feed_setup)
              VALUES(@id,@n,@b,@p,@f,@d,@t,@o,@by,@fs)
              """, c, tx))
        {
            cmd.Parameters.AddWithValue("id", t.Id);
            cmd.Parameters.AddWithValue("n", t.Name);
            cmd.Parameters.AddWithValue("b", t.UpstreamBase);
            cmd.Parameters.AddWithValue("p", t.AllowedPathPrefix);
            cmd.Parameters.AddWithValue("f", t.FeedIndexUrl);
            cmd.Parameters.AddWithValue("d", (long)t.CooldownDays);
            cmd.Parameters.AddWithValue("t", PostgresTime.ToDatabase(createdAt));
            cmd.Parameters.AddWithValue("o", origin);
            cmd.Parameters.AddWithValue("by", (object?)t.CreatedByOid ?? DBNull.Value);
            if (t.FeedSetup != FeedSetup.Operator) cmd.Parameters.AddWithValue("fs", feedSetup);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var participant in participants) await participant.OnCreatingAsync(tx, t, createdAt, ct);
    }

    public Task SetActiveAsync(string tenantId, bool active, CancellationToken ct) =>
        Change(tenantId, "UPDATE tenants SET active=@v, policy_revision=policy_revision+1 WHERE id=@id", ct, ("v", active ? 1L : 0L));

    public Task SetCooldownAsync(string tenantId, int days, CancellationToken ct) =>
        days < 0 ? throw new ArgumentOutOfRangeException(nameof(days)) : Change(tenantId, "UPDATE tenants SET cooldown_days=@v, policy_revision=policy_revision+1 WHERE id=@id", ct, ("v", (long)days));

    public Task AddInternalPrefixAsync(string tenantId, string prefix, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(prefix.Trim().TrimEnd('*')) ? throw new ArgumentException("An empty prefix would hide every package.", nameof(prefix))
        : ChangeThen(tenantId, "INSERT INTO internal_prefixes(tenant_id, prefix) VALUES(@id, @v) ON CONFLICT DO NOTHING", ct, ("v", prefix.Trim()));

    public Task RemoveInternalPrefixAsync(string tenantId, string prefix, CancellationToken ct) =>
        ChangeThen(tenantId, "DELETE FROM internal_prefixes WHERE tenant_id=@id AND prefix=@v", ct, ("v", prefix.Trim()));

    public Task SetOverrideAsync(string tenantId, string packageId, int days, CancellationToken ct) =>
        days < 0 ? throw new ArgumentOutOfRangeException(nameof(days))
        : ChangeThen(tenantId, "INSERT INTO package_overrides(tenant_id, package_id, cooldown_days) VALUES(@id,@p,@d) ON CONFLICT(tenant_id, package_id) DO UPDATE SET cooldown_days=@d", ct,
            ("p", packageId.Trim().ToLowerInvariant()), ("d", (long)days));

    public Task RemoveOverrideAsync(string tenantId, string packageId, CancellationToken ct) =>
        ChangeThen(tenantId, "DELETE FROM package_overrides WHERE tenant_id=@id AND package_id=@p", ct, ("p", packageId.Trim().ToLowerInvariant()));

    public async Task<Waiver> AddWaiverAsync(string tenantId, string packageId, string version, DateTimeOffset expiresAt, string reason, string createdBy, CancellationToken ct)
    {
        var result = await InsertWaiverAsync(tenantId, packageId, version, expiresAt, reason, createdBy, WaiverSource.Operator, maxActiveCustomer: null, ct);
        return result.Outcome == WaiverInsertOutcome.Added ? result.Waiver! : throw new WaiverRefusedException(WaiverRefusal.Duplicate);
    }

    public Task<CustomerWaiverInsert> AddCustomerWaiverAsync(string tenantId, string packageId, string version, DateTimeOffset expiresAt, string reason, string createdBy, int maxActive,
        CancellationToken ct) =>
        maxActive < 1 ? throw new ArgumentOutOfRangeException(nameof(maxActive))
        : InsertWaiverAsync(tenantId, packageId, version, expiresAt, reason, createdBy, WaiverSource.Customer, maxActive, ct);

    /// <summary>
    /// The one way a waiver row is written. The tenant's row is read FOR NO KEY UPDATE first, so a second insert for the same tenant waits until
    /// this one commits or rolls back, and then (read committed: each statement sees what committed before it) its duplicate check and cap
    /// count see this one's row. A refusal rolls back: no row, no revision bump.
    /// </summary>
    private async Task<CustomerWaiverInsert> InsertWaiverAsync(string tenantId, string packageId, string version, DateTimeOffset expiresAt, string reason, string createdBy,
        WaiverSource source, int? maxActiveCustomer, CancellationToken ct)
    {
        if (expiresAt <= _clock.UtcNow) throw new ArgumentException("A waiver must expire in the future.", nameof(expiresAt));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A waiver needs a reason.", nameof(reason));
        var waiver = new Waiver("wvr_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(), packageId.Trim().ToLowerInvariant(), version.Trim(), expiresAt,
            reason.Trim(), createdBy, source);
        var now = Now();

        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);

        TenantOrigin origin;
        await using (var lockTenant = new NpgsqlCommand("SELECT origin FROM tenants WHERE id=@id FOR NO KEY UPDATE", c, tx))   // the cap's lock
        {
            lockTenant.Parameters.AddWithValue("id", tenantId);
            if (await lockTenant.ExecuteScalarAsync(ct) is not string originText) throw new KeyNotFoundException($"No tenant '{tenantId}'.");
            origin = originText == "self" ? TenantOrigin.Self : TenantOrigin.Operator;   // anything else is named by its hash in an alert
        }

        // One active waiver per package and version: compared the way the policy engine compares them ("*" only matches "*").
        var key = VersionKey(waiver.Version);
        await using (var duplicates = new NpgsqlCommand("SELECT version FROM waivers WHERE tenant_id=@id AND package_id=@p AND revoked_at IS NULL AND expires_at > @now", c, tx))
        {
            duplicates.Parameters.AddWithValue("id", tenantId);
            duplicates.Parameters.AddWithValue("p", waiver.PackageId);
            duplicates.Parameters.AddWithValue("now", now);
            await using var r = await duplicates.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (VersionKey(r.GetString(0)) == key)
                    return new CustomerWaiverInsert(WaiverInsertOutcome.Duplicate, null, 0, origin);
        }

        var active = 0;
        if (source == WaiverSource.Customer)
        {
            await using var count = new NpgsqlCommand("SELECT COUNT(*) FROM waivers WHERE tenant_id=@id AND source='customer' AND revoked_at IS NULL AND expires_at > @now", c, tx);
            count.Parameters.AddWithValue("id", tenantId);
            count.Parameters.AddWithValue("now", now);
            active = checked((int)(long)(await count.ExecuteScalarAsync(ct))!);
            if (active >= maxActiveCustomer) return new CustomerWaiverInsert(WaiverInsertOutcome.CapReached, null, active, origin);
        }

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO waivers(id, tenant_id, package_id, version, expires_at, reason, created_by, created_at, source) VALUES(@w,@id,@p,@v,@e,@r,@b,@now,@s);
            UPDATE tenants SET policy_revision=policy_revision+1 WHERE id=@id
            """, c, tx))
        {
            insert.Parameters.AddWithValue("w", waiver.Id);
            insert.Parameters.AddWithValue("id", tenantId);
            insert.Parameters.AddWithValue("p", waiver.PackageId);
            insert.Parameters.AddWithValue("v", waiver.Version);
            insert.Parameters.AddWithValue("e", PostgresTime.ToDatabase(expiresAt));
            insert.Parameters.AddWithValue("r", waiver.Reason);
            insert.Parameters.AddWithValue("b", createdBy);
            insert.Parameters.AddWithValue("now", now);
            insert.Parameters.AddWithValue("s", SourceText(source));
            await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new CustomerWaiverInsert(WaiverInsertOutcome.Added, waiver, source == WaiverSource.Customer ? active + 1 : 0, origin);
    }

    private static string VersionKey(string version)
    {
        var t = version.Trim();
        return t == "*" ? "*" : NuGetVersionKey.Normalize(t);
    }

    private static string SourceText(WaiverSource source) => source switch
    {
        WaiverSource.Operator => "operator",
        WaiverSource.Customer => "customer",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    /// <summary>The stored waivers.source. Anything else fails closed (naming the column, never the value); the CHECK makes it unreachable.</summary>
    internal static WaiverSource ParseSource(string source) => source switch
    {
        "operator" => WaiverSource.Operator,
        "customer" => WaiverSource.Customer,
        _ => throw new InvalidDataException("waivers.source holds a source this build does not know."),
    };

    public async Task<IReadOnlyList<WaiverRecord>> ListWaiversAsync(string tenantId, int limit, CancellationToken ct)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT id, package_id, version, expires_at, reason, created_by, created_at, source, revoked_at, revoked_by
            FROM waivers WHERE tenant_id=@id
            ORDER BY (revoked_at IS NULL AND expires_at > @now) DESC, created_at DESC, id DESC LIMIT @n
            """, c);
        cmd.Parameters.AddWithValue("id", tenantId);
        cmd.Parameters.AddWithValue("now", Now());
        cmd.Parameters.AddWithValue("n", (long)limit);
        var list = new List<WaiverRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new WaiverRecord(r.GetString(0), r.GetString(1), r.GetString(2), Time(r, 3), r.GetString(4), r.GetString(5), Time(r, 6),
                ParseSource(r.GetString(7)), r.IsDBNull(8) ? null : Time(r, 8), r.IsDBNull(9) ? null : r.GetString(9)));
        return list;
    }

    /// <summary>A timestamptz column as a time with offset 0 (<see cref="PostgresTime.FromDatabase(DateTime)"/>).</summary>
    internal static DateTimeOffset Time(NpgsqlDataReader r, int i) => PostgresTime.FromDatabase(r.GetFieldValue<DateTime>(i));

    /// <summary>
    /// Revokes one waiver. The waiver's tenant row is read FOR NO KEY UPDATE first (none: nothing to revoke), so the revoke and the revision bump
    /// wait for, and are waited for by, any other write of that tenant's policy, always tenant row first.
    /// </summary>
    public async Task<bool> RevokeWaiverAsync(string? tenantId, string waiverId, string revokedBy, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        string owner;
        await using (var lockTenant = new NpgsqlCommand(
            "SELECT t.id FROM tenants t JOIN waivers w ON w.tenant_id = t.id WHERE w.id=@w" + (tenantId is null ? "" : " AND w.tenant_id=@tenant") + " FOR NO KEY UPDATE OF t", c, tx))
        {
            lockTenant.Parameters.AddWithValue("w", waiverId);
            if (tenantId is not null) lockTenant.Parameters.AddWithValue("tenant", tenantId);
            if (await lockTenant.ExecuteScalarAsync(ct) is not string found) return false;
            owner = found;
        }
        bool changed;
        await using (var revoke = new NpgsqlCommand("UPDATE waivers SET revoked_at=@t, revoked_by=@by WHERE id=@w AND tenant_id=@owner AND revoked_at IS NULL", c, tx))
        {
            revoke.Parameters.AddWithValue("t", Now());
            revoke.Parameters.AddWithValue("by", revokedBy);
            revoke.Parameters.AddWithValue("w", waiverId);
            revoke.Parameters.AddWithValue("owner", owner);
            changed = await revoke.ExecuteNonQueryAsync(ct) > 0;
        }
        if (changed)
        {
            await using var bump = new NpgsqlCommand("UPDATE tenants SET policy_revision=policy_revision+1 WHERE id=@owner", c, tx);
            bump.Parameters.AddWithValue("owner", owner);
            await bump.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return changed;
    }

    /// <summary>
    /// Sets the tenant's own app (its client id and encrypted certificate). The tenant's row is read FOR NO KEY UPDATE first: the identity is
    /// written after whatever else holds that tenant (the purge, a directory claim) and before the next.
    /// </summary>
    public async Task SetUpstreamIdentityAsync(string tenantId, UpstreamIdentity identity, CancellationToken ct)
    {
        var blob = _protector.Protect(identity.CertificatePem, TenantContext(tenantId));
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await LockTenantRowAsync(c, tx, tenantId, ct);
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO upstream_identities(tenant_id, kind, azure_tenant_id, client_id, cert_encrypted, updated_at) VALUES(@id,'own',@a,@c,@b,@t)
            ON CONFLICT(tenant_id) DO UPDATE SET kind='own', azure_tenant_id=@a, client_id=@c, cert_encrypted=@b, updated_at=@t
            """, c, tx))
        {
            cmd.Parameters.AddWithValue("id", tenantId);
            cmd.Parameters.AddWithValue("a", identity.AzureTenantId);
            cmd.Parameters.AddWithValue("c", identity.ClientId);
            cmd.Parameters.AddWithValue("b", blob);
            cmd.Parameters.AddWithValue("t", Now());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await TakeOverFeedSetupAsync(c, tx, tenantId, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// The tenant's row, FOR NO KEY UPDATE, for the rest of <paramref name="tx"/>: the identity binding's lock. A missing tenant is not an error
    /// here: the write after it is refused by the foreign key, as on SQLite.
    /// </summary>
    private static async Task LockTenantRowAsync(NpgsqlConnection c, NpgsqlTransaction tx, string tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM tenants WHERE id=@id FOR NO KEY UPDATE", c, tx);
        cmd.Parameters.AddWithValue("id", tenantId);
        await cmd.ExecuteScalarAsync(ct);
    }

    /// <summary>
    /// An operator write of the upstream or identity takes the feed over: feed_setup becomes 'operator' in the caller's
    /// transaction, so the wizard never replaces what the operator set.
    /// </summary>
    internal static async Task TakeOverFeedSetupAsync(NpgsqlConnection c, NpgsqlTransaction tx, string tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("UPDATE tenants SET feed_setup='operator' WHERE id=@id AND feed_setup<>'operator'", c, tx);
        cmd.Parameters.AddWithValue("id", tenantId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<TenantSetup?> GetSetupAsync(string tenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT origin, feed_setup FROM tenants WHERE id=@id", c);
        cmd.Parameters.AddWithValue("id", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new TenantSetup(r.GetString(0) == "self" ? TenantOrigin.Self : TenantOrigin.Operator, ParseFeedSetup(r.GetString(1)));
    }

    private static string FeedSetupText(FeedSetup setup) => setup switch
    {
        FeedSetup.Operator => "operator",
        FeedSetup.Customer => "customer",
        _ => throw new ArgumentOutOfRangeException(nameof(setup), "Unknown feed setup."),
    };

    /// <summary>The stored tenants.feed_setup. Anything else fails closed as 'operator' (the customer never gains the wizard by it); the CHECK makes it unreachable.</summary>
    internal static FeedSetup ParseFeedSetup(string setup) => setup == "customer" ? FeedSetup.Customer : FeedSetup.Operator;

    public async Task<UpstreamIdentity?> GetUpstreamIdentityAsync(string tenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT azure_tenant_id, client_id, cert_encrypted FROM upstream_identities WHERE tenant_id=@id AND kind='own'", c);
        cmd.Parameters.AddWithValue("id", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var blob = r["cert_encrypted"] as byte[] ?? throw new CryptographicException("Stored identity is not an encrypted blob.");
        return new UpstreamIdentity(r.GetString(0), r.GetString(1), _protector.Unprotect(blob, TenantContext(tenantId)));
    }

    /// <summary>
    /// Binds the tenant to the shared app in <paramref name="azureTenantId"/>. The tenant's row is read FOR NO KEY UPDATE first, as for
    /// <see cref="SetUpstreamIdentityAsync"/>.
    /// </summary>
    public async Task BindSharedIdentityAsync(string tenantId, string azureTenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await LockTenantRowAsync(c, tx, tenantId, ct);
        // The own-app client id and encrypted key are cleared in the same statement: a shared row never keeps a stale private key. Any
        // earlier passing onboarding check was for the old binding: it no longer counts.
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO upstream_identities(tenant_id, kind, azure_tenant_id, client_id, cert_encrypted, updated_at) VALUES(@id,'shared',@a,NULL,NULL,@t)
            ON CONFLICT(tenant_id) DO UPDATE SET kind='shared', azure_tenant_id=@a, client_id=NULL, cert_encrypted=NULL, updated_at=@t;
            DELETE FROM shared_app_verifications WHERE tenant_id=@id
            """, c, tx))
        {
            cmd.Parameters.AddWithValue("id", tenantId);
            cmd.Parameters.AddWithValue("a", azureTenantId);
            cmd.Parameters.AddWithValue("t", Now());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await TakeOverFeedSetupAsync(c, tx, tenantId, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<UpstreamBinding?> GetUpstreamBindingAsync(string tenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT kind, azure_tenant_id, client_id FROM upstream_identities WHERE tenant_id=@id", c);   // never the key column
        cmd.Parameters.AddWithValue("id", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var kind = ParseKind(r.GetString(0));
        return new UpstreamBinding(kind, r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
    }

    /// <summary>
    /// The stored upstream_identities.kind: 'own' or 'shared'. Anything else fails closed with <see cref="InvalidDataException"/> (naming
    /// the column, never the value): a kind this build does not know is never read as an own app. Whoever adds a kind adds it here.
    /// </summary>
    internal static IdentityKind ParseKind(string kind) => kind switch
    {
        "own" => IdentityKind.Own,
        "shared" => IdentityKind.Shared,
        _ => throw new InvalidDataException("upstream_identities.kind holds an identity kind this build does not know."),
    };

    /// <summary>
    /// A tenant's key is bound to its id, exactly as on SQLite (the tests hold the two equal): new ids cannot contain ':' (see
    /// <see cref="AdminCommands.IsValidTenantId"/>); this refuses an older row that does, so no tenant context can ever equal a shared-app
    /// context ("shared-app:active", "shared-app:next").
    /// </summary>
    public static string TenantContext(string tenantId) =>
        tenantId.Contains(':') ? throw new CryptographicException("This tenant id cannot hold an encrypted identity.") : tenantId;

    private DateTimeOffset Now() => PostgresTime.ToDatabase(_clock.UtcNow);

    /// <summary>Runs an UPDATE on one tenant (one statement: its own transaction) and fails if no such tenant exists.</summary>
    private async Task Change(string tenantId, string sql, CancellationToken ct, params (string, object)[] parameters)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("id", tenantId);
        foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new KeyNotFoundException($"No tenant '{tenantId}'.");
    }

    /// <summary>
    /// Bumps the tenant's policy revision, which takes its row's lock (the tenant row first, as everywhere here), then runs a statement,
    /// in one transaction.
    /// </summary>
    private async Task ChangeThen(string tenantId, string sql, CancellationToken ct, params (string, object)[] parameters)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await using (var bump = new NpgsqlCommand("UPDATE tenants SET policy_revision=policy_revision+1 WHERE id=@id", c, tx))
        {
            bump.Parameters.AddWithValue("id", tenantId);
            if (await bump.ExecuteNonQueryAsync(ct) == 0) throw new KeyNotFoundException($"No tenant '{tenantId}'.");
        }
        await using (var cmd = new NpgsqlCommand(sql, c, tx))
        {
            cmd.Parameters.AddWithValue("id", tenantId);
            foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
