using System.Globalization;
using Npgsql;
using Packmoat.Application;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The admin action log on PostgreSQL, as SQLite's: append-only, read newest first by id. The table refuses UPDATE and
/// TRUNCATE always, and DELETE of any row less than 365 days old by the database's clock (the admin_actions_no_delete trigger), so the
/// retention prune is the only delete that can succeed. The prune is one transaction with no catch and no SAVEPOINT: the delete and its
/// record row commit together, and when the trigger refuses a row the refusal reaches the caller and nothing is deleted. Times are
/// timestamptz, in UTC to the microsecond (<see cref="PostgresTime"/>). PACKMOAT_DB_ENGINE=postgres selects it.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresAdminActionLog : IAdminActionLog
{
    private const string Columns = "time, actor_tid, actor_oid, actor_name, action, tenant_id, detail, site";

    private readonly PostgresDatabase _db;

    public PostgresAdminActionLog(PostgresDatabase db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    public async ValueTask AppendAsync(AdminActionRecord r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"INSERT INTO admin_actions({Columns}) VALUES(@t,@tid,@oid,@n,@a,@tn,@d,@s)", c);
        cmd.Parameters.AddWithValue("t", PostgresTime.ToDatabase(r.Time));
        cmd.Parameters.AddWithValue("tid", r.ActorTid);
        cmd.Parameters.AddWithValue("oid", r.ActorOid);
        cmd.Parameters.AddWithValue("n", r.ActorName);
        cmd.Parameters.AddWithValue("a", r.Action);
        cmd.Parameters.AddWithValue("tn", r.TenantId);
        cmd.Parameters.AddWithValue("d", r.Detail);
        cmd.Parameters.AddWithValue("s", r.Site);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AdminActionRecord>> LatestAsync(int limit, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM admin_actions ORDER BY id DESC LIMIT @l", c);
        cmd.Parameters.AddWithValue("l", (long)limit);
        var list = new List<AdminActionRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    /// <summary>
    /// The retention prune, in one transaction: deletes the rows written before <paramref name="before"/> (cut to the microsecond, as every
    /// time is stored) and, when it deleted any, appends the <see cref="IAdminActionLog.PruneAction"/> row in the same transaction. A row the
    /// database still holds young (the app's clock running ahead) makes the trigger refuse the DELETE: the refusal reaches the caller and
    /// the transaction rolls back, so no row is deleted and no record row is written.
    /// </summary>
    public async Task<int> PruneAsync(DateTimeOffset before, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = PostgresTime.ToDatabase(before);
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        int deleted;
        await using (var delete = new NpgsqlCommand("DELETE FROM admin_actions WHERE time < @b", c, tx))
        {
            delete.Parameters.AddWithValue("b", cutoff);
            deleted = await delete.ExecuteNonQueryAsync(ct);   // the trigger refuses the lot if any row is under 365 days old
        }
        if (deleted > 0)
        {
            await using var record = new NpgsqlCommand($"INSERT INTO admin_actions({Columns}) VALUES(@t,'','','retention',@a,'',@d,'server')", c, tx);
            record.Parameters.AddWithValue("t", PostgresTime.ToDatabase(now));
            record.Parameters.AddWithValue("a", IAdminActionLog.PruneAction);
            record.Parameters.AddWithValue("d", $"rows={deleted}; before={cutoff.ToString("O", CultureInfo.InvariantCulture)}");
            await record.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return deleted;
    }

    public async Task<AdminActionRecord?> LatestByAsync(string tenantId, string action, string actorTid, string actorOid, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM admin_actions WHERE tenant_id=@tn AND action=@a AND actor_tid=@tid AND actor_oid=@oid ORDER BY id DESC LIMIT 1", c);
        cmd.Parameters.AddWithValue("tn", tenantId);
        cmd.Parameters.AddWithValue("a", action);
        cmd.Parameters.AddWithValue("tid", actorTid);
        cmd.Parameters.AddWithValue("oid", actorOid);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    private static AdminActionRecord Read(NpgsqlDataReader r) =>
        new(PostgresTime.FromDatabase(r.GetFieldValue<DateTime>(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
            r.GetString(6), r.GetString(7));
}
