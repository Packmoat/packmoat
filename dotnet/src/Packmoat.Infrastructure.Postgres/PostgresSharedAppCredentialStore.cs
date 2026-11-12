using System.Security.Cryptography;
using Npgsql;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The shared multitenant app's credential, and the operator's records that a tenant's shared binding was verified, on PostgreSQL, the twin of <see cref="SqliteSharedAppCredentialStore"/>: each slot encrypted with the master key under its own context
/// (<see cref="Context"/>), so a blob copied between slots, or between a slot and a tenant, does not decrypt.
/// Times are timestamptz, in UTC to the microsecond (<see cref="PostgresTime"/>). PACKMOAT_DB_ENGINE=postgres selects it.
/// <para><b>Locks.</b> The two slots are one rule across every tenant, so a write of a slot takes the named lock
/// <see cref="PostgresLock.SharedAppSlots"/> first: a set is one transaction (the lock, then the upsert), and the promote is one transaction
/// (the lock, then the read of "next", the caller's check, the upsert of "active" and the delete of "next"). So two promotes never both move
/// "next", and a "next" set while a promote runs is never deleted unread. Recording a verification is one transaction: it reads the
/// tenant's row FOR NO KEY UPDATE (the binding's lock, as the operator's bind and the wizard's Connect take it), then the active slot's row
/// FOR SHARE, then writes: so the binding, the feed and the active client id it checks cannot change before it commits. No catch, no
/// SAVEPOINT: a refusal (the caller's check, a constraint) rolls the whole transaction back and reaches the caller.</para>
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresSharedAppCredentialStore : ISharedAppCredentialStore
{
    private readonly PostgresDatabase _db;
    private readonly IClock _clock;
    private readonly AesGcmSecretProtector _protector;

    public PostgresSharedAppCredentialStore(PostgresDatabase db, IClock clock, AesGcmSecretProtector protector)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    /// <summary>A slot's stored name, as SQLite's (the tests hold the two equal).</summary>
    public static string SlotName(SharedAppSlot slot) => slot == SharedAppSlot.Next ? "next" : "active";

    /// <summary>
    /// The encryption context of a slot, as SQLite's (the tests hold the two equal), so a key written by either engine's store decrypts with
    /// the other's. Contains ':', which no tenant id may.
    /// </summary>
    public static string Context(SharedAppSlot slot) => "shared-app:" + SlotName(slot);

    public async Task SetAsync(SharedAppSlot slot, SharedAppCredential credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credential);
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await PostgresLocks.TakeAsync(c, tx, PostgresLock.SharedAppSlots, ct);
        await UpsertAsync(c, tx, slot, credential, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<SharedAppCredential?> GetAsync(SharedAppSlot slot, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        return await ReadAsync(c, null, slot, ct);
    }

    /// <summary>
    /// Moves "next" to "active" under <see cref="PostgresLock.SharedAppSlots"/>, in one transaction: false when there is no "next".
    /// <paramref name="validate"/> throws to refuse; the transaction is rolled back and nothing changes.
    /// </summary>
    public async Task<bool> PromoteAsync(Action<SharedAppCredential> validate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(validate);
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await PostgresLocks.TakeAsync(c, tx, PostgresLock.SharedAppSlots, ct);   // before the read: a second promote waits, then finds no "next"
        if (await ReadAsync(c, tx, SharedAppSlot.Next, ct) is not { } next) return false;
        validate(next);   // throws to refuse; the transaction is rolled back on dispose and nothing changes
        await UpsertAsync(c, tx, SharedAppSlot.Active, next, ct);
        await using (var delete = new NpgsqlCommand("DELETE FROM shared_app_credentials WHERE slot='next'", c, tx))
            await delete.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> RecordVerificationAsync(SharedAppVerification v, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(v);
        var verifiedAt = PostgresTime.ToDatabase(v.VerifiedAt);
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);

        // Re-read what the record vouches for, in this transaction and locked: the binding and the feed URL (the tenant's row, the binding's
        // lock), then the active client id (its row FOR SHARE, which a promote's write of "active" waits for).
        string kind, directory, feed;
        await using (var read = new NpgsqlCommand(
            "SELECT i.kind, i.azure_tenant_id, t.feed_index_url FROM tenants t JOIN upstream_identities i ON i.tenant_id = t.id WHERE t.id=@id FOR NO KEY UPDATE OF t",
            c, tx))
        {
            read.Parameters.AddWithValue("id", v.TenantId);
            await using var r = await read.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return false;
            (kind, directory, feed) = (r.GetString(0), r.GetString(1), r.GetString(2));
        }
        string? activeClient;
        await using (var active = new NpgsqlCommand("SELECT client_id FROM shared_app_credentials WHERE slot='active' FOR SHARE", c, tx))
            activeClient = await active.ExecuteScalarAsync(ct) as string;
        if (activeClient is null) return false;
        var binding = new UpstreamBinding(PostgresTenantStore.ParseKind(kind), directory, null);
        if (!v.Matches(binding, feed, activeClient)) return false;

        // Operator-made only (the CLI's onboard check): a record replaced here never keeps a wizard record's 'wizard' or its check ids.
        await using (var write = new NpgsqlCommand(
            """
            INSERT INTO shared_app_verifications(tenant_id, azure_tenant_id, feed_index_url, client_id, verified_at, verified_by) VALUES(@id,@a,@f,@c,@t,@by)
            ON CONFLICT(tenant_id) DO UPDATE SET azure_tenant_id=@a, feed_index_url=@f, client_id=@c, verified_at=@t, verified_by=@by,
                verified_via='operator', feed_check_id=NULL, developer_check_id=NULL
            """, c, tx))
        {
            write.Parameters.AddWithValue("id", v.TenantId);
            write.Parameters.AddWithValue("a", v.AzureTenantId);
            write.Parameters.AddWithValue("f", v.FeedIndexUrl);
            write.Parameters.AddWithValue("c", v.ClientId);
            write.Parameters.AddWithValue("t", verifiedAt);
            write.Parameters.AddWithValue("by", v.VerifiedBy);
            await write.ExecuteNonQueryAsync(ct);
        }
        await PostgresTenantStore.TakeOverFeedSetupAsync(c, tx, v.TenantId, ct);   // the operator's onboard check takes the feed over
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> DeleteVerificationAsync(SharedAppVerification v, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(v);
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM shared_app_verifications WHERE tenant_id=@id AND azure_tenant_id=@a AND feed_index_url=@f AND client_id=@c AND verified_at=@t AND verified_by=@by", c);
        AddExact(cmd, v);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>The parameters that name exactly one verification record (@id, @a, @f, @c, @t, @by), as a delete of it compares them.</summary>
    internal static void AddExact(NpgsqlCommand cmd, SharedAppVerification v)
    {
        cmd.Parameters.AddWithValue("id", v.TenantId);
        cmd.Parameters.AddWithValue("a", v.AzureTenantId);
        cmd.Parameters.AddWithValue("f", v.FeedIndexUrl);
        cmd.Parameters.AddWithValue("c", v.ClientId);
        cmd.Parameters.AddWithValue("t", PostgresTime.ToDatabase(v.VerifiedAt));
        cmd.Parameters.AddWithValue("by", v.VerifiedBy);
    }

    public async Task<SharedAppVerification?> GetVerificationAsync(string tenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT azure_tenant_id, feed_index_url, client_id, verified_at, verified_by FROM shared_app_verifications WHERE tenant_id=@id", c);
        cmd.Parameters.AddWithValue("id", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new SharedAppVerification(tenantId, r.GetString(0), r.GetString(1), r.GetString(2), PostgresTenantStore.Time(r, 3), r.GetString(4));
    }

    private async Task UpsertAsync(NpgsqlConnection c, NpgsqlTransaction tx, SharedAppSlot slot, SharedAppCredential credential, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO shared_app_credentials(slot, client_id, cert_encrypted, updated_at) VALUES(@s,@c,@b,@t)
            ON CONFLICT(slot) DO UPDATE SET client_id=@c, cert_encrypted=@b, updated_at=@t
            """, c, tx);
        cmd.Parameters.AddWithValue("s", SlotName(slot));
        cmd.Parameters.AddWithValue("c", credential.ClientId);
        cmd.Parameters.AddWithValue("b", _protector.Protect(credential.CertificatePem, Context(slot)));
        cmd.Parameters.AddWithValue("t", PostgresTime.ToDatabase(_clock.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<SharedAppCredential?> ReadAsync(NpgsqlConnection c, NpgsqlTransaction? tx, SharedAppSlot slot, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT client_id, cert_encrypted FROM shared_app_credentials WHERE slot=@s", c, tx);
        cmd.Parameters.AddWithValue("s", SlotName(slot));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var blob = r["cert_encrypted"] as byte[] ?? throw new CryptographicException("Stored shared credential is not an encrypted blob.");
        return new SharedAppCredential(r.GetString(0), _protector.Unprotect(blob, Context(slot)));
    }
}
