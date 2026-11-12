using Npgsql;
using Packmoat.Application;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// People who signed in on the customer site, on PostgreSQL, as SQLite's: ids are stored lower-case canonical, and the
/// schema refuses anything else. Each call is one statement, so one transaction. A tenant's list is sorted by display name, then object id,
/// by their bytes as SQLite's BINARY collation sorts them: the columns are COLLATE "C", so "Zed" comes before "ann" and "ann" before
/// "Émile" whatever the database's own collation. Times are timestamptz, in UTC to the microsecond (<see cref="PostgresTime"/>). PACKMOAT_DB_ENGINE=postgres selects it.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresMemberStore : IMemberStore
{
    private readonly PostgresDatabase _db;

    public PostgresMemberStore(PostgresDatabase db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    private static string Canonical(string id) =>
        Guid.TryParse(id?.Trim(), out var g) && g != Guid.Empty ? g.ToString("D") : throw new ArgumentException("A directory or object id must be a GUID.");

    public async Task UpsertAsync(string azureTenantId, string objectId, string tenantId, string displayName, DateTimeOffset seenAt, CancellationToken ct)
    {
        var (directory, objectIdCanonical, seen) = (Canonical(azureTenantId), Canonical(objectId), PostgresTime.ToDatabase(seenAt));
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO members(azure_tenant_id, object_id, tenant_id, display_name, first_seen_at, last_seen_at) VALUES(@d,@o,@t,@n,@s,@s)
            ON CONFLICT(azure_tenant_id, object_id) DO UPDATE SET tenant_id=excluded.tenant_id, display_name=excluded.display_name, last_seen_at=excluded.last_seen_at
            """, c);
        cmd.Parameters.AddWithValue("d", directory);
        cmd.Parameters.AddWithValue("o", objectIdCanonical);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("n", displayName);
        cmd.Parameters.AddWithValue("s", seen);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Member?> GetAsync(string azureTenantId, string objectId, CancellationToken ct)
    {
        if (!Guid.TryParse(azureTenantId, out var d) || !Guid.TryParse(objectId, out var o)) return null;
        var list = await Query("WHERE azure_tenant_id=@a AND object_id=@b", ct, ("a", d.ToString("D")), ("b", o.ToString("D")));
        return list.Count == 1 ? list[0] : null;
    }

    public Task<IReadOnlyList<Member>> ListForTenantAsync(string tenantId, CancellationToken ct) => Query("WHERE tenant_id=@a", ct, ("a", tenantId));

    private async Task<IReadOnlyList<Member>> Query(string where, CancellationToken ct, params (string Name, string Value)[] args)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT azure_tenant_id, object_id, tenant_id, display_name, first_seen_at, last_seen_at FROM members " + where + " ORDER BY display_name, object_id", c);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        var list = new List<Member>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new Member(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), PostgresTime.FromDatabase(r.GetFieldValue<DateTime>(4)),
                PostgresTime.FromDatabase(r.GetFieldValue<DateTime>(5))));
        return list;
    }
}
