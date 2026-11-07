using System.Globalization;
using Packmoat.Application;

namespace Packmoat.Infrastructure;

/// <summary>The auto-join setting (migration 11). No row means on. No cache: every call reads the database, and a failed read throws.</summary>
public sealed class SqliteAutoJoinSettingStore : IAutoJoinSettingStore
{
    private readonly SqliteDatabase _db;

    public SqliteAutoJoinSettingStore(SqliteDatabase db) => _db = db;

    public async Task<bool> IsOnAsync(string tenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT enabled FROM auto_join_settings WHERE tenant_id=@t";
        cmd.Parameters.AddWithValue("@t", tenantId);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull || Convert.ToInt64(value, CultureInfo.InvariantCulture) == 1;
    }

    public async Task SetAsync(string tenantId, bool enabled, DateTimeOffset at, string changedBy, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO auto_join_settings(tenant_id, enabled, changed_at, changed_by) VALUES(@t, @e, @a, @b)
            ON CONFLICT(tenant_id) DO UPDATE SET enabled=excluded.enabled, changed_at=excluded.changed_at, changed_by=excluded.changed_by
            """;
        cmd.Parameters.AddWithValue("@t", tenantId);
        cmd.Parameters.AddWithValue("@e", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@a", at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@b", changedBy);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
