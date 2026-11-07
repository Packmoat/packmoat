using System.Globalization;
using Microsoft.Data.Sqlite;
using Packmoat.Application;

namespace Packmoat.Infrastructure;

/// <summary>
/// Append-only record of admin-site writes and of the CLI's directory bindings. The table refuses UPDATE always, and DELETE of any row
/// less than 365 days old (triggers; migration 18), so the retention prune is the only delete that can succeed.
/// </summary>
public sealed class SqliteAdminActionLog : IAdminActionLog
{
    private readonly SqliteDatabase _db;

    public SqliteAdminActionLog(SqliteDatabase db) => _db = db;

    public async ValueTask AppendAsync(AdminActionRecord r, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO admin_actions(time, actor_tid, actor_oid, actor_name, action, tenant_id, detail, site) VALUES(@t,@tid,@oid,@n,@a,@tn,@d,@s)";
        cmd.Parameters.AddWithValue("@t", r.Time.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("@tid", r.ActorTid);
        cmd.Parameters.AddWithValue("@oid", r.ActorOid);
        cmd.Parameters.AddWithValue("@n", r.ActorName);
        cmd.Parameters.AddWithValue("@a", r.Action);
        cmd.Parameters.AddWithValue("@tn", r.TenantId);
        cmd.Parameters.AddWithValue("@d", r.Detail);
        cmd.Parameters.AddWithValue("@s", r.Site);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AdminActionRecord>> LatestAsync(int limit, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT time, actor_tid, actor_oid, actor_name, action, tenant_id, detail, site FROM admin_actions ORDER BY id DESC LIMIT @l";
        cmd.Parameters.AddWithValue("@l", limit);
        var list = new List<AdminActionRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AdminActionRecord(DateTimeOffset.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7)));
        return list;
    }

    public async Task<int> PruneAsync(DateTimeOffset before, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        var cutoff = before.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        // Compared as the trigger compares (julianday), so a row whose time cannot be read is never selected and cannot block the prune.
        cmd.CommandText = "DELETE FROM admin_actions WHERE julianday(time) < julianday(@b)";
        cmd.Parameters.AddWithValue("@b", cutoff);
        var deleted = await cmd.ExecuteNonQueryAsync(ct);   // the trigger aborts the lot if any row is under 365 days old
        if (deleted > 0)
        {
            cmd.Parameters.Clear();
            cmd.CommandText = "INSERT INTO admin_actions(time, actor_tid, actor_oid, actor_name, action, tenant_id, detail, site) VALUES(@t,'','','retention',@a,'',@d,'server')";
            cmd.Parameters.AddWithValue("@t", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@a", IAdminActionLog.PruneAction);
            cmd.Parameters.AddWithValue("@d", $"rows={deleted}; before={cutoff}");
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return deleted;
    }

    public async Task<AdminActionRecord?> LatestByAsync(string tenantId, string action, string actorTid, string actorOid, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT time, actor_tid, actor_oid, actor_name, action, tenant_id, detail, site FROM admin_actions " +
                          "WHERE tenant_id=@tn AND action=@a AND actor_tid=@tid AND actor_oid=@oid ORDER BY id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@tn", tenantId);
        cmd.Parameters.AddWithValue("@a", action);
        cmd.Parameters.AddWithValue("@tid", actorTid);
        cmd.Parameters.AddWithValue("@oid", actorOid);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new AdminActionRecord(DateTimeOffset.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7))
            : null;
    }
}
