using Npgsql;
using Packmoat.Application;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// Customer directory to tenant links on PostgreSQL, as SQLite's: directory ids are stored lower-case canonical, and the
/// primary key keeps one tenant per directory. A bind is one statement that never moves a directory already linked. The unbind is one
/// transaction: it reads the tenant's row FOR NO KEY UPDATE first (as the member token cap does), then removes the link and revokes the
/// directory's self-issued tokens in that tenant, so a member issue for the same tenant runs wholly before or wholly after it, and the lock
/// order is the tenant row before its children. No catch, no SAVEPOINT. Times are timestamptz, in UTC to the microsecond
/// (<see cref="PostgresTime"/>). PACKMOAT_DB_ENGINE=postgres selects it.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresSignInDirectoryStore : ISignInDirectoryStore
{
    private readonly PostgresDatabase _db;

    public PostgresSignInDirectoryStore(PostgresDatabase db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    private static string Canonical(string azureTenantId) =>
        CustomerSignIn.CanonicalDirectory(azureTenantId) ?? throw new ArgumentException("A directory id must be a GUID.");

    public async Task<bool> BindAsync(DirectoryBinding binding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var directory = Canonical(binding.AzureTenantId);
        await using var c = await _db.OpenAsync(ct);
        // ON CONFLICT DO NOTHING: a directory already linked (to any tenant) is never moved silently.
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO sign_in_directories(azure_tenant_id, tenant_id, bound_at, bound_by) VALUES(@d,@t,@at,@by) ON CONFLICT(azure_tenant_id) DO NOTHING", c);
        cmd.Parameters.AddWithValue("d", directory);
        cmd.Parameters.AddWithValue("t", binding.TenantId);
        cmd.Parameters.AddWithValue("at", PostgresTime.ToDatabase(binding.BoundAt));
        cmd.Parameters.AddWithValue("by", binding.BoundBy);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<int?> UnbindAsync(string tenantId, string azureTenantId, DateTimeOffset at, string revokedBy, CancellationToken ct)
    {
        var directory = Canonical(azureTenantId);
        var revokedAt = PostgresTime.ToDatabase(at);
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await using (var lockTenant = new NpgsqlCommand("SELECT 1 FROM tenants WHERE id=@t FOR NO KEY UPDATE", c, tx))   // the tenant's tokens' lock
        {
            lockTenant.Parameters.AddWithValue("t", tenantId);
            await lockTenant.ExecuteScalarAsync(ct);   // no tenant: no link either, so the delete below finds none
        }
        await using (var delete = new NpgsqlCommand("DELETE FROM sign_in_directories WHERE azure_tenant_id=@d AND tenant_id=@t", c, tx))
        {
            delete.Parameters.AddWithValue("d", directory);
            delete.Parameters.AddWithValue("t", tenantId);
            if (await delete.ExecuteNonQueryAsync(ct) != 1) return null;   // rolled back on dispose: nothing changed
        }
        // Same transaction: nobody from a directory that is no longer linked keeps a working self-issued token in this tenant.
        int revoked;
        await using (var revoke = new NpgsqlCommand(
            "UPDATE tokens SET revoked_at=@at, revoked_by=@by WHERE tenant_id=@t AND owner_tid=@d AND issued_via='self' AND revoked_at IS NULL", c, tx))
        {
            revoke.Parameters.AddWithValue("at", revokedAt);
            revoke.Parameters.AddWithValue("by", revokedBy);
            revoke.Parameters.AddWithValue("t", tenantId);
            revoke.Parameters.AddWithValue("d", directory);
            revoked = await revoke.ExecuteNonQueryAsync(ct);
        }
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
        await using var cmd = new NpgsqlCommand("SELECT azure_tenant_id, tenant_id, bound_at, bound_by FROM sign_in_directories " + where + " ORDER BY azure_tenant_id", c);
        cmd.Parameters.AddWithValue("p", value);
        var list = new List<DirectoryBinding>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new DirectoryBinding(r.GetString(0), r.GetString(1), PostgresTime.FromDatabase(r.GetFieldValue<DateTime>(2)), r.GetString(3)));
        return list;
    }
}
