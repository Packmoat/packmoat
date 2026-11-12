using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure;

/// <summary>
/// The shared multitenant app's credential, encrypted with the master key. Each slot has its own encryption context
/// ("shared-app:active", "shared-app:next"). A tenant id can never contain ':', so a tenant's blob can never decrypt as the
/// shared key or the other way round, and a "next" blob copied into "active" does not decrypt either: promote re-encrypts.
/// </summary>
public sealed class SqliteSharedAppCredentialStore : ISharedAppCredentialStore
{
    private readonly SqliteDatabase _db;
    private readonly IClock _clock;
    private readonly AesGcmSecretProtector _protector;

    public SqliteSharedAppCredentialStore(SqliteDatabase db, IClock clock, AesGcmSecretProtector protector)
    {
        _db = db;
        _clock = clock;
        _protector = protector;
    }

    public static string SlotName(SharedAppSlot slot) => slot == SharedAppSlot.Next ? "next" : "active";

    /// <summary>The encryption context of a slot. Contains ':', which no tenant id may.</summary>
    public static string Context(SharedAppSlot slot) => "shared-app:" + SlotName(slot);

    public async Task SetAsync(SharedAppSlot slot, SharedAppCredential credential, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        await Upsert(cmd, slot, credential, ct);
    }

    public async Task<SharedAppCredential?> GetAsync(SharedAppSlot slot, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        return await Read(cmd, slot, ct);
    }

    public async Task<bool> PromoteAsync(Action<SharedAppCredential> validate, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        if (await Read(cmd, SharedAppSlot.Next, ct) is not { } next) return false;
        validate(next);   // throws to refuse; the transaction is rolled back and nothing changes
        await Upsert(cmd, SharedAppSlot.Active, next, ct);
        cmd.Parameters.Clear();
        cmd.CommandText = "DELETE FROM shared_app_credentials WHERE slot='next'";
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> RecordVerificationAsync(SharedAppVerification v, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;

        // Re-read what the record vouches for, in this transaction: the binding, the feed URL and the active client id.
        cmd.CommandText = """
            SELECT i.kind, i.azure_tenant_id, t.feed_index_url, (SELECT client_id FROM shared_app_credentials WHERE slot='active')
            FROM tenants t JOIN upstream_identities i ON i.tenant_id = t.id WHERE t.id=@id
            """;
        cmd.Parameters.AddWithValue("@id", v.TenantId);
        UpstreamBinding binding;
        string feed, activeClient;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct) || r.IsDBNull(3)) return false;
            binding = new UpstreamBinding(SqliteTenantStore.ParseKind(r.GetString(0)), r.GetString(1), null);
            (feed, activeClient) = (r.GetString(2), r.GetString(3));
        }
        if (!v.Matches(binding, feed, activeClient)) return false;

        cmd.Parameters.Clear();
        // Operator-made only (the CLI's onboard check): a record replaced here never keeps a wizard record's 'wizard' or its check ids.
        cmd.CommandText = """
            INSERT INTO shared_app_verifications(tenant_id, azure_tenant_id, feed_index_url, client_id, verified_at, verified_by) VALUES(@id,@a,@f,@c,@t,@by)
            ON CONFLICT(tenant_id) DO UPDATE SET azure_tenant_id=@a, feed_index_url=@f, client_id=@c, verified_at=@t, verified_by=@by,
                verified_via='operator', feed_check_id=NULL, developer_check_id=NULL
            """;
        cmd.Parameters.AddWithValue("@id", v.TenantId);
        cmd.Parameters.AddWithValue("@a", v.AzureTenantId);
        cmd.Parameters.AddWithValue("@f", v.FeedIndexUrl);
        cmd.Parameters.AddWithValue("@c", v.ClientId);
        cmd.Parameters.AddWithValue("@t", v.VerifiedAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("@by", v.VerifiedBy);
        await cmd.ExecuteNonQueryAsync(ct);
        await SqliteTenantStore.TakeOverFeedSetupAsync(cmd, v.TenantId, ct);   // the operator's onboard check takes the feed over
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> DeleteVerificationAsync(SharedAppVerification v, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            DELETE FROM shared_app_verifications WHERE tenant_id=@id AND azure_tenant_id=@a AND feed_index_url=@f AND client_id=@c AND verified_at=@t AND verified_by=@by
            """;
        cmd.Parameters.AddWithValue("@id", v.TenantId);
        cmd.Parameters.AddWithValue("@a", v.AzureTenantId);
        cmd.Parameters.AddWithValue("@f", v.FeedIndexUrl);
        cmd.Parameters.AddWithValue("@c", v.ClientId);
        cmd.Parameters.AddWithValue("@t", v.VerifiedAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("@by", v.VerifiedBy);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<SharedAppVerification?> GetVerificationAsync(string tenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT azure_tenant_id, feed_index_url, client_id, verified_at, verified_by FROM shared_app_verifications WHERE tenant_id=@id";
        cmd.Parameters.AddWithValue("@id", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new SharedAppVerification(tenantId, r.GetString(0), r.GetString(1), r.GetString(2), DateTimeOffset.Parse(r.GetString(3), System.Globalization.CultureInfo.InvariantCulture), r.GetString(4));
    }

    private async Task Upsert(SqliteCommand cmd, SharedAppSlot slot, SharedAppCredential credential, CancellationToken ct)
    {
        cmd.Parameters.Clear();
        cmd.CommandText = """
            INSERT INTO shared_app_credentials(slot, client_id, cert_encrypted, updated_at) VALUES(@s,@c,@b,@t)
            ON CONFLICT(slot) DO UPDATE SET client_id=@c, cert_encrypted=@b, updated_at=@t
            """;
        cmd.Parameters.AddWithValue("@s", SlotName(slot));
        cmd.Parameters.AddWithValue("@c", credential.ClientId);
        cmd.Parameters.AddWithValue("@b", _protector.Protect(credential.CertificatePem, Context(slot)));
        cmd.Parameters.AddWithValue("@t", _clock.UtcNow.ToUniversalTime().ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<SharedAppCredential?> Read(SqliteCommand cmd, SharedAppSlot slot, CancellationToken ct)
    {
        cmd.Parameters.Clear();
        cmd.CommandText = "SELECT client_id, cert_encrypted FROM shared_app_credentials WHERE slot=@s";
        cmd.Parameters.AddWithValue("@s", SlotName(slot));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var blob = r["cert_encrypted"] as byte[] ?? throw new CryptographicException("Stored shared credential is not an encrypted blob.");
        return new SharedAppCredential(r.GetString(0), _protector.Unprotect(blob, Context(slot)));
    }
}
