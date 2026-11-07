using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Infrastructure;

public sealed class SqliteTenantStore : ITenantStore
{
    private readonly SqliteDatabase _db;
    private readonly IClock _clock;
    private readonly AesGcmSecretProtector _protector;
    private readonly IReadOnlyList<ITenantCreationParticipant> _participants;

    /// <param name="participants">
    /// What takes part in making a tenant, in its transaction (#194; a module's own row). Required: pass
    /// <see cref="TenantLifecycle.Participants"/>, empty for none, so no caller can forget it.
    /// </param>
    public SqliteTenantStore(SqliteDatabase db, IClock clock, AesGcmSecretProtector protector, IReadOnlyList<ITenantCreationParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        _db = db;
        _clock = clock;
        _protector = protector;
        _participants = participants;
    }

    private const string KeyCheckName = "key_check";
    internal const string KeyCheckPlaintext = "packmoat-master-key-check-v1";

    /// <summary>
    /// Makes sure the master key in use is the one this database was made with. The first run records an encrypted marker;
    /// every later run must be able to read it. A wrong key stops the server instead of failing later, one customer at a time.
    /// </summary>
    public async Task EnsureKeyCheckAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var read = c.CreateCommand();
        read.CommandText = "SELECT value FROM meta WHERE key=@k";
        read.Parameters.AddWithValue("@k", KeyCheckName);
        if (await read.ExecuteScalarAsync(ct) is byte[] stored)
        {
            if (!SqliteBackup.KeyMatches(_protector, stored))
                throw new InvalidOperationException("The master key does not match this database. Refusing to continue: customer identities would not decrypt.");
            return;
        }
        await using var write = c.CreateCommand();
        write.CommandText = "INSERT OR IGNORE INTO meta(key, value) VALUES(@k, @v)";
        write.Parameters.AddWithValue("@k", KeyCheckName);
        write.Parameters.AddWithValue("@v", _protector.Protect(KeyCheckPlaintext, KeyCheckName));
        await write.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// True when the master key in use reads this database's key marker (#233: the readiness check). Read only: unlike
    /// <see cref="EnsureKeyCheckAsync"/> it never records a marker, and a database with none is false (the start always records one).
    /// </summary>
    public async Task<bool> KeyMatchesAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenReadOnlyAsync(ct);
        await using var read = c.CreateCommand();
        read.CommandText = "SELECT value FROM meta WHERE key=@k";
        read.Parameters.AddWithValue("@k", KeyCheckName);
        return await read.ExecuteScalarAsync(ct) is byte[] stored && SqliteBackup.KeyMatches(_protector, stored);
    }

    public async Task<Tenant?> GetAsync(string tenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        return await LoadAsync(c, tenantId, requireActive: false, ct);
    }

    /// <summary>True when a tenant row exists (active or not). Reads only the tenants table, so it does not depend on the policy tables' columns.</summary>
    internal async Task<bool> ExistsAsync(string tenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM tenants WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", tenantId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>Loads a tenant only if it is active. Used by authentication.</summary>
    internal async Task<Tenant?> GetActiveAsync(SqliteConnection c, string tenantId, CancellationToken ct) =>
        await LoadAsync(c, tenantId, requireActive: true, ct);

    public async Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        var ids = new List<string>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM tenants ORDER BY id";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids.Add(r.GetString(0));
        }
        var result = new List<Tenant>();
        foreach (var id in ids) if (await LoadAsync(c, id, false, ct) is { } t) result.Add(t);
        return result;
    }

    private async Task<Tenant?> LoadAsync(SqliteConnection c, string tenantId, bool requireActive, CancellationToken ct)
    {
        string name, upstreamBase, prefix, feed;
        int cooldown;
        long revision;
        bool active;
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name, upstream_base, allowed_path_prefix, feed_index_url, cooldown_days, policy_revision, active FROM tenants WHERE id=@id" + (requireActive ? " AND active=1" : "");
            cmd.Parameters.AddWithValue("@id", tenantId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            (name, upstreamBase, prefix, feed, cooldown, revision, active) = (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt64(5), r.GetInt32(6) != 0);
        }

        var prefixes = await ReadList(c, "SELECT prefix FROM internal_prefixes WHERE tenant_id=@id ORDER BY prefix", tenantId, r => r.GetString(0), ct);
        var overrides = await ReadList(c, "SELECT package_id, cooldown_days FROM package_overrides WHERE tenant_id=@id ORDER BY package_id", tenantId, r => new PackageOverride(r.GetString(0), r.GetInt32(1)), ct);
        var waivers = await ReadList(c, "SELECT id, package_id, version, expires_at, reason, created_by, source FROM waivers WHERE tenant_id=@id AND revoked_at IS NULL ORDER BY created_at",
            tenantId, r => new Waiver(r.GetString(0), r.GetString(1), r.GetString(2), DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture), r.GetString(4), r.GetString(5),
                ParseSource(r.GetString(6))), ct);

        return new Tenant(tenantId, name, upstreamBase, prefix, feed, new TenantPolicy(cooldown, prefixes, waivers, overrides, revision), active);
    }

    private static async Task<List<T>> ReadList<T>(SqliteConnection c, string sql, string tenantId, Func<SqliteDataReader, T> map, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", tenantId);
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
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        await InsertAsync(cmd, _participants, t, _clock.UtcNow, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// The one way a tenant row is written: the tenant with its origin and creator, on the caller's command (inside the caller's
    /// transaction), then each creation participant in order on the same transaction (#194). A participant's veto
    /// (<see cref="TenantCreationRefusedException"/>) or failure throws, and the caller's transaction rolls back.
    /// </summary>
    internal static async Task InsertAsync(SqliteCommand cmd, IReadOnlyList<ITenantCreationParticipant> participants, NewTenant t, DateTimeOffset createdAt, CancellationToken ct)
    {
        var origin = t.Origin switch { TenantOrigin.Operator => "operator", TenantOrigin.Self => "self", _ => throw new ArgumentOutOfRangeException(nameof(t), "Unknown tenant origin.") };
        var feedSetup = FeedSetupText(t.FeedSetup);
        var tx = cmd.Transaction ?? throw new InvalidOperationException("A tenant is written only inside a transaction.");
        cmd.Parameters.Clear();
        // feed_setup is written only when it is not the column's default ('operator'), so an operator-set tenant reads the default (#207).
        cmd.CommandText = t.FeedSetup == FeedSetup.Operator
            ? """
              INSERT INTO tenants(id, name, upstream_base, allowed_path_prefix, feed_index_url, cooldown_days, created_at, origin, created_by_oid)
              VALUES(@id,@n,@b,@p,@f,@d,@t,@o,@by)
              """
            : """
              INSERT INTO tenants(id, name, upstream_base, allowed_path_prefix, feed_index_url, cooldown_days, created_at, origin, created_by_oid, feed_setup)
              VALUES(@id,@n,@b,@p,@f,@d,@t,@o,@by,@fs)
              """;
        cmd.Parameters.AddWithValue("@id", t.Id);
        cmd.Parameters.AddWithValue("@n", t.Name);
        cmd.Parameters.AddWithValue("@b", t.UpstreamBase);
        cmd.Parameters.AddWithValue("@p", t.AllowedPathPrefix);
        cmd.Parameters.AddWithValue("@f", t.FeedIndexUrl);
        cmd.Parameters.AddWithValue("@d", t.CooldownDays);
        cmd.Parameters.AddWithValue("@t", createdAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@o", origin);
        cmd.Parameters.AddWithValue("@by", (object?)t.CreatedByOid ?? DBNull.Value);
        if (t.FeedSetup != FeedSetup.Operator) cmd.Parameters.AddWithValue("@fs", feedSetup);
        await cmd.ExecuteNonQueryAsync(ct);
        foreach (var participant in participants) await participant.OnCreatingAsync(tx, t, createdAt, ct);
    }

    public Task SetActiveAsync(string tenantId, bool active, CancellationToken ct) =>
        Change(tenantId, "UPDATE tenants SET active=@v, policy_revision=policy_revision+1 WHERE id=@id", ct, ("@v", active ? 1 : 0));

    public Task SetCooldownAsync(string tenantId, int days, CancellationToken ct) =>
        days < 0 ? throw new ArgumentOutOfRangeException(nameof(days)) : Change(tenantId, "UPDATE tenants SET cooldown_days=@v, policy_revision=policy_revision+1 WHERE id=@id", ct, ("@v", days));

    public Task AddInternalPrefixAsync(string tenantId, string prefix, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(prefix.Trim().TrimEnd('*')) ? throw new ArgumentException("An empty prefix would hide every package.", nameof(prefix))
        : ChangeThen(tenantId, "INSERT OR IGNORE INTO internal_prefixes(tenant_id, prefix) VALUES(@id, @v)", ct, ("@v", prefix.Trim()));

    public Task RemoveInternalPrefixAsync(string tenantId, string prefix, CancellationToken ct) =>
        ChangeThen(tenantId, "DELETE FROM internal_prefixes WHERE tenant_id=@id AND prefix=@v", ct, ("@v", prefix.Trim()));

    public Task SetOverrideAsync(string tenantId, string packageId, int days, CancellationToken ct) =>
        days < 0 ? throw new ArgumentOutOfRangeException(nameof(days))
        : ChangeThen(tenantId, "INSERT INTO package_overrides(tenant_id, package_id, cooldown_days) VALUES(@id,@p,@d) ON CONFLICT(tenant_id, package_id) DO UPDATE SET cooldown_days=@d", ct, ("@p", packageId.Trim().ToLowerInvariant()), ("@d", days));

    public Task RemoveOverrideAsync(string tenantId, string packageId, CancellationToken ct) =>
        ChangeThen(tenantId, "DELETE FROM package_overrides WHERE tenant_id=@id AND package_id=@p", ct, ("@p", packageId.Trim().ToLowerInvariant()));

    public async Task<Waiver> AddWaiverAsync(string tenantId, string packageId, string version, DateTimeOffset expiresAt, string reason, string createdBy, CancellationToken ct)
    {
        var result = await InsertWaiverAsync(tenantId, packageId, version, expiresAt, reason, createdBy, WaiverSource.Operator, maxActiveCustomer: null, ct);
        return result.Outcome == WaiverInsertOutcome.Added ? result.Waiver! : throw new WaiverRefusedException(WaiverRefusal.Duplicate);
    }

    public Task<CustomerWaiverInsert> AddCustomerWaiverAsync(string tenantId, string packageId, string version, DateTimeOffset expiresAt, string reason, string createdBy, int maxActive, CancellationToken ct) =>
        maxActive < 1 ? throw new ArgumentOutOfRangeException(nameof(maxActive))
        : InsertWaiverAsync(tenantId, packageId, version, expiresAt, reason, createdBy, WaiverSource.Customer, maxActive, ct);

    /// <summary>
    /// The one way a waiver row is written. BEGIN IMMEDIATE (the default for a non-deferred transaction here) takes the write lock before the
    /// first read, so the tenant read, the duplicate check, the cap count and the insert all see the same rows and no concurrent insert can
    /// slip between them. A refusal rolls back: no row, no revision bump.
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
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);   // BEGIN IMMEDIATE: the checks and the insert see the same rows
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("@id", tenantId);
        cmd.Parameters.AddWithValue("@p", waiver.PackageId);
        cmd.Parameters.AddWithValue("@now", now);

        cmd.CommandText = "SELECT origin FROM tenants WHERE id=@id";
        if (await cmd.ExecuteScalarAsync(ct) is not string originText) throw new KeyNotFoundException($"No tenant '{tenantId}'.");
        var origin = originText == "self" ? TenantOrigin.Self : TenantOrigin.Operator;   // anything else is named by its hash in an alert

        // One active waiver per package and version: compared the way the policy engine compares them ("*" only matches "*").
        cmd.CommandText = "SELECT version FROM waivers WHERE tenant_id=@id AND package_id=@p AND revoked_at IS NULL AND expires_at > @now";
        var key = VersionKey(waiver.Version);
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                if (VersionKey(r.GetString(0)) == key)
                    return new CustomerWaiverInsert(WaiverInsertOutcome.Duplicate, null, 0, origin);

        var active = 0;
        if (source == WaiverSource.Customer)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM waivers WHERE tenant_id=@id AND source='customer' AND revoked_at IS NULL AND expires_at > @now";
            active = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            if (active >= maxActiveCustomer) return new CustomerWaiverInsert(WaiverInsertOutcome.CapReached, null, active, origin);
        }

        cmd.CommandText = "INSERT INTO waivers(id, tenant_id, package_id, version, expires_at, reason, created_by, created_at, source) VALUES(@w,@id,@p,@v,@e,@r,@b,@now,@s)";
        cmd.Parameters.AddWithValue("@w", waiver.Id);
        cmd.Parameters.AddWithValue("@v", waiver.Version);
        cmd.Parameters.AddWithValue("@e", expiresAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("@r", waiver.Reason);
        cmd.Parameters.AddWithValue("@b", createdBy);
        cmd.Parameters.AddWithValue("@s", SourceText(source));
        await cmd.ExecuteNonQueryAsync(ct);
        cmd.CommandText = "UPDATE tenants SET policy_revision=policy_revision+1 WHERE id=@id";
        await cmd.ExecuteNonQueryAsync(ct);
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
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, package_id, version, expires_at, reason, created_by, created_at, source, revoked_at, revoked_by
            FROM waivers WHERE tenant_id=@id
            ORDER BY (revoked_at IS NULL AND expires_at > @now) DESC, created_at DESC, id DESC LIMIT @n
            """;
        cmd.Parameters.AddWithValue("@id", tenantId);
        cmd.Parameters.AddWithValue("@now", Now());
        cmd.Parameters.AddWithValue("@n", limit);
        var list = new List<WaiverRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new WaiverRecord(r.GetString(0), r.GetString(1), r.GetString(2), Time(r.GetString(3)), r.GetString(4), r.GetString(5), Time(r.GetString(6)),
                ParseSource(r.GetString(7)), r.IsDBNull(8) ? null : Time(r.GetString(8)), r.IsDBNull(9) ? null : r.GetString(9)));
        return list;
    }

    private static DateTimeOffset Time(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public async Task<bool> RevokeWaiverAsync(string? tenantId, string waiverId, string revokedBy, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE waivers SET revoked_at=@t, revoked_by=@by WHERE id=@w AND revoked_at IS NULL" + (tenantId is null ? "" : " AND tenant_id=@tenant");
        cmd.Parameters.AddWithValue("@t", Now());
        cmd.Parameters.AddWithValue("@by", revokedBy);
        cmd.Parameters.AddWithValue("@w", waiverId);
        if (tenantId is not null) cmd.Parameters.AddWithValue("@tenant", tenantId);
        var changed = await cmd.ExecuteNonQueryAsync(ct) > 0;
        if (changed)
        {
            cmd.CommandText = "UPDATE tenants SET policy_revision=policy_revision+1 WHERE id=(SELECT tenant_id FROM waivers WHERE id=@w)";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return changed;
    }

    public async Task SetUpstreamIdentityAsync(string tenantId, UpstreamIdentity identity, CancellationToken ct)
    {
        var blob = _protector.Protect(identity.CertificatePem, TenantContext(tenantId));
        await using var c = await _db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO upstream_identities(tenant_id, kind, azure_tenant_id, client_id, cert_encrypted, updated_at) VALUES(@id,'own',@a,@c,@b,@t)
            ON CONFLICT(tenant_id) DO UPDATE SET kind='own', azure_tenant_id=@a, client_id=@c, cert_encrypted=@b, updated_at=@t
            """;
        cmd.Parameters.AddWithValue("@id", tenantId);
        cmd.Parameters.AddWithValue("@a", identity.AzureTenantId);
        cmd.Parameters.AddWithValue("@c", identity.ClientId);
        cmd.Parameters.AddWithValue("@b", blob);
        cmd.Parameters.AddWithValue("@t", Now());
        await cmd.ExecuteNonQueryAsync(ct);
        await TakeOverFeedSetupAsync(cmd, tenantId, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// An operator write of the upstream or identity takes the feed over (#207): feed_setup becomes 'operator' on the caller's command, in
    /// its transaction, so the wizard never replaces what the operator set. Clears the command's parameters.
    /// </summary>
    internal static async Task TakeOverFeedSetupAsync(SqliteCommand cmd, string tenantId, CancellationToken ct)
    {
        cmd.Parameters.Clear();
        cmd.CommandText = "UPDATE tenants SET feed_setup='operator' WHERE id=@id AND feed_setup<>'operator'";
        cmd.Parameters.AddWithValue("@id", tenantId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<TenantSetup?> GetSetupAsync(string tenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT origin, feed_setup FROM tenants WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", tenantId);
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
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT azure_tenant_id, client_id, cert_encrypted FROM upstream_identities WHERE tenant_id=@id AND kind='own'";
        cmd.Parameters.AddWithValue("@id", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var blob = r["cert_encrypted"] as byte[] ?? throw new CryptographicException("Stored identity is not an encrypted blob.");
        return new UpstreamIdentity(r.GetString(0), r.GetString(1), _protector.Unprotect(blob, TenantContext(tenantId)));
    }

    public async Task BindSharedIdentityAsync(string tenantId, string azureTenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        // The own-app client id and encrypted key are cleared in the same statement: a shared row never keeps a stale private key.
        cmd.CommandText = """
            INSERT INTO upstream_identities(tenant_id, kind, azure_tenant_id, client_id, cert_encrypted, updated_at) VALUES(@id,'shared',@a,NULL,NULL,@t)
            ON CONFLICT(tenant_id) DO UPDATE SET kind='shared', azure_tenant_id=@a, client_id=NULL, cert_encrypted=NULL, updated_at=@t
            """;
        cmd.Parameters.AddWithValue("@id", tenantId);
        cmd.Parameters.AddWithValue("@a", azureTenantId);
        cmd.Parameters.AddWithValue("@t", Now());
        await cmd.ExecuteNonQueryAsync(ct);
        // Any earlier passing onboarding check was for the old binding: it no longer counts.
        cmd.CommandText = "DELETE FROM shared_app_verifications WHERE tenant_id=@id";
        await cmd.ExecuteNonQueryAsync(ct);
        await TakeOverFeedSetupAsync(cmd, tenantId, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<UpstreamBinding?> GetUpstreamBindingAsync(string tenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT kind, azure_tenant_id, client_id FROM upstream_identities WHERE tenant_id=@id";   // never the key column
        cmd.Parameters.AddWithValue("@id", tenantId);
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
    /// A tenant's key is bound to its id. New ids cannot contain ':' (see <see cref="AdminCommands.IsValidTenantId"/>); this refuses an
    /// older row that does, so no tenant context can ever equal a shared-app context ("shared-app:active", "shared-app:next").
    /// </summary>
    public static string TenantContext(string tenantId) =>
        tenantId.Contains(':') ? throw new CryptographicException("This tenant id cannot hold an encrypted identity.") : tenantId;

    private string Now() => _clock.UtcNow.ToUniversalTime().ToString("O");

    /// <summary>Runs an UPDATE on one tenant and fails if no such tenant exists.</summary>
    private async Task Change(string tenantId, string sql, CancellationToken ct, params (string, object)[] parameters)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", tenantId);
        foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new KeyNotFoundException($"No tenant '{tenantId}'.");
    }

    /// <summary>Runs a statement and, in the same transaction, bumps the tenant's policy revision.</summary>
    private async Task ChangeThen(string tenantId, string sql, CancellationToken ct, params (string, object)[] parameters)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE tenants SET policy_revision=policy_revision+1 WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", tenantId);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new KeyNotFoundException($"No tenant '{tenantId}'.");
        cmd.CommandText = sql;
        foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }
}
