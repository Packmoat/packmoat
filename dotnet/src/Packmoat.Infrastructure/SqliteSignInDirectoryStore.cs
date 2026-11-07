using Microsoft.Data.Sqlite;
using Packmoat.Application;

namespace Packmoat.Infrastructure;

/// <summary>Customer directory -> tenant links (migration 7). Directory ids are stored lower-case canonical; the primary key keeps one tenant per directory.</summary>
public sealed class SqliteSignInDirectoryStore : ISignInDirectoryStore
{
    private readonly SqliteDatabase _db;

    public SqliteSignInDirectoryStore(SqliteDatabase db) => _db = db;

    private static string Canonical(string azureTenantId) =>
        CustomerSignIn.CanonicalDirectory(azureTenantId) ?? throw new ArgumentException("A directory id must be a GUID.");

    public async Task<bool> BindAsync(DirectoryBinding binding, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        // ON CONFLICT DO NOTHING: a directory already linked (to any tenant) is never moved silently.
        cmd.CommandText = "INSERT INTO sign_in_directories(azure_tenant_id, tenant_id, bound_at, bound_by) VALUES(@d,@t,@at,@by) ON CONFLICT(azure_tenant_id) DO NOTHING";
        cmd.Parameters.AddWithValue("@d", Canonical(binding.AzureTenantId));
        cmd.Parameters.AddWithValue("@t", binding.TenantId);
        cmd.Parameters.AddWithValue("@at", binding.BoundAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("@by", binding.BoundBy);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<int?> UnbindAsync(string tenantId, string azureTenantId, DateTimeOffset at, string revokedBy, CancellationToken ct)
    {
        var directory = Canonical(azureTenantId);
        await using var c = await _db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM sign_in_directories WHERE azure_tenant_id=@d AND tenant_id=@t";
        cmd.Parameters.AddWithValue("@d", directory);
        cmd.Parameters.AddWithValue("@t", tenantId);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) return null;   // rolled back on dispose: nothing changed
        // Same transaction: nobody from a directory that is no longer linked keeps a working self-issued token in this tenant.
        cmd.CommandText = "UPDATE tokens SET revoked_at=@at, revoked_by=@by WHERE tenant_id=@t AND owner_tid=@d AND issued_via='self' AND revoked_at IS NULL";
        cmd.Parameters.AddWithValue("@at", at.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@by", revokedBy);
        var revoked = await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return revoked;
    }

    public async Task<DirectoryBinding?> GetAsync(string azureTenantId, CancellationToken ct)
    {
        if (CustomerSignIn.CanonicalDirectory(azureTenantId) is not { } d) return null;
        var list = await Query("WHERE azure_tenant_id=@p", d, ct);
        return list.Count == 1 ? list[0] : null;
    }

    public Task<IReadOnlyList<DirectoryBinding>> ListForTenantAsync(string tenantId, CancellationToken ct) => Query("WHERE tenant_id=@p", tenantId, ct);

    private async Task<IReadOnlyList<DirectoryBinding>> Query(string where, string value, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT azure_tenant_id, tenant_id, bound_at, bound_by FROM sign_in_directories " + where + " ORDER BY azure_tenant_id";
        cmd.Parameters.AddWithValue("@p", value);
        var list = new List<DirectoryBinding>();
        await using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new DirectoryBinding(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind), r.GetString(3)));
        return list;
    }
}
