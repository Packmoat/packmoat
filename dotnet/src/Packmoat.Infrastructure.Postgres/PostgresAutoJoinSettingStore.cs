using Npgsql;
using Packmoat.Application;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The auto-join setting on PostgreSQL, as SQLite's: one row per tenant, no row means on. No cache: every call reads the
/// database, and a failed read throws. Each call is one statement, so one transaction; the write is an upsert, and a tenant that does not
/// exist is refused by the foreign key. PACKMOAT_DB_ENGINE=postgres selects it.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresAutoJoinSettingStore : IAutoJoinSettingStore
{
    private readonly PostgresDatabase _db;

    public PostgresAutoJoinSettingStore(PostgresDatabase db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<bool> IsOnAsync(string tenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT enabled FROM auto_join_settings WHERE tenant_id=@t", c);
        cmd.Parameters.AddWithValue("t", tenantId);
        return await cmd.ExecuteScalarAsync(ct) is not long enabled || enabled == 1;
    }

    public async Task SetAsync(string tenantId, bool enabled, DateTimeOffset at, string changedBy, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO auto_join_settings(tenant_id, enabled, changed_at, changed_by) VALUES(@t, @e, @a, @b)
            ON CONFLICT(tenant_id) DO UPDATE SET enabled=excluded.enabled, changed_at=excluded.changed_at, changed_by=excluded.changed_by
            """, c);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("e", enabled ? 1L : 0L);
        cmd.Parameters.AddWithValue("a", PostgresTime.ToDatabase(at));
        cmd.Parameters.AddWithValue("b", changedBy);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
