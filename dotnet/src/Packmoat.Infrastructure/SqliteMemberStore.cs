using System.Globalization;
using Microsoft.Data.Sqlite;
using Packmoat.Application;

namespace Packmoat.Infrastructure;

/// <summary>People who signed in on the customer site (migration 8). Ids are stored lower-case canonical; the schema refuses anything else.</summary>
public sealed class SqliteMemberStore : IMemberStore
{
    private readonly SqliteDatabase _db;

    public SqliteMemberStore(SqliteDatabase db) => _db = db;

    private static string Canonical(string id) =>
        Guid.TryParse(id?.Trim(), out var g) && g != Guid.Empty ? g.ToString("D") : throw new ArgumentException("A directory or object id must be a GUID.");

    public async Task UpsertAsync(string azureTenantId, string objectId, string tenantId, string displayName, DateTimeOffset seenAt, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO members(azure_tenant_id, object_id, tenant_id, display_name, first_seen_at, last_seen_at) VALUES(@d,@o,@t,@n,@s,@s)
            ON CONFLICT(azure_tenant_id, object_id) DO UPDATE SET tenant_id=excluded.tenant_id, display_name=excluded.display_name, last_seen_at=excluded.last_seen_at
            """;
        cmd.Parameters.AddWithValue("@d", Canonical(azureTenantId));
        cmd.Parameters.AddWithValue("@o", Canonical(objectId));
        cmd.Parameters.AddWithValue("@t", tenantId);
        cmd.Parameters.AddWithValue("@n", displayName);
        cmd.Parameters.AddWithValue("@s", seenAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Member?> GetAsync(string azureTenantId, string objectId, CancellationToken ct)
    {
        if (!Guid.TryParse(azureTenantId, out var d) || !Guid.TryParse(objectId, out var o)) return null;
        var list = await Query("WHERE azure_tenant_id=@a AND object_id=@b", ct, ("@a", d.ToString("D")), ("@b", o.ToString("D")));
        return list.Count == 1 ? list[0] : null;
    }

    public Task<IReadOnlyList<Member>> ListForTenantAsync(string tenantId, CancellationToken ct) => Query("WHERE tenant_id=@a", ct, ("@a", tenantId));

    private async Task<IReadOnlyList<Member>> Query(string where, CancellationToken ct, params (string Name, string Value)[] args)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT azure_tenant_id, object_id, tenant_id, display_name, first_seen_at, last_seen_at FROM members " + where + " ORDER BY display_name, object_id";
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        var list = new List<Member>();
        await using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new Member(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Parse(r.GetString(4)), Parse(r.GetString(5))));
        return list;
    }

    private static DateTimeOffset Parse(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
