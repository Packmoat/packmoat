using System.Globalization;
using Microsoft.Data.Sqlite;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure;

/// <summary>
/// The audit trail of package downloads (#136). The table refuses UPDATE, and refuses DELETE of a row less than 90 days old by the
/// database's own clock unless the tenant's purge is running (migration 19). The server writes through <see cref="QueuedAuditLog"/>,
/// which calls <see cref="AppendBatchAsync"/>; the daily retention pass calls <see cref="PruneAsync"/>.
/// </summary>
public sealed class SqliteAuditLog : IAuditLog, IAuditBatchLog, IAuditRetention, IAuditQuery
{
    /// <summary>The most rows one prune statement deletes, so the write lock is held only briefly.</summary>
    public const int DefaultPruneChunk = 5_000;

    private readonly SqliteDatabase _db;
    private readonly int _pruneChunk;

    /// <param name="pruneChunk">A test seam: production uses <see cref="DefaultPruneChunk"/>.</param>
    public SqliteAuditLog(SqliteDatabase db, int pruneChunk = DefaultPruneChunk)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pruneChunk, 1);
        _db = db;
        _pruneChunk = pruneChunk;
    }

    /// <summary>Writes one event and waits for it. For the admin tool and tests; the server queues (<see cref="QueuedAuditLog"/>).</summary>
    public async ValueTask AppendAsync(AuditEvent e, CancellationToken ct) => await AppendBatchAsync(new[] { e }, ct);

    public async Task AppendBatchAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct)
    {
        if (events.Count == 0) return;
        await using var c = await _db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO audit_events(time, request_id, tenant_id, token_id, kind, package_id, version, decision, reason, waiver_id)
            VALUES(@t,@r,@tn,@tk,@k,@p,@v,@d,@why,@w)
            """;
        var time = cmd.Parameters.Add("@t", SqliteType.Text);
        var request = cmd.Parameters.Add("@r", SqliteType.Text);
        var tenant = cmd.Parameters.Add("@tn", SqliteType.Text);
        var token = cmd.Parameters.Add("@tk", SqliteType.Text);
        var kind = cmd.Parameters.Add("@k", SqliteType.Text);
        var package = cmd.Parameters.Add("@p", SqliteType.Text);
        var version = cmd.Parameters.Add("@v", SqliteType.Text);
        var decision = cmd.Parameters.Add("@d", SqliteType.Text);
        var reason = cmd.Parameters.Add("@why", SqliteType.Text);
        var waiver = cmd.Parameters.Add("@w", SqliteType.Text);
        foreach (var e in events)
        {
            time.Value = Format(e.Time);
            request.Value = e.RequestId;
            tenant.Value = e.TenantId;
            token.Value = e.TokenId;
            kind.Value = e.Kind.ToString();
            package.Value = e.PackageId;
            version.Value = e.Version;
            decision.Value = e.Decision.ToString();
            reason.Value = e.Reason.ToString();
            waiver.Value = (object?)e.WaiverId ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);   // a purged tenant's row is dropped by its trigger
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// One prune chunk (<c>@b</c> the cutoff, <c>@n</c> the chunk size). The text comparison lets ix_audit_time find the old rows (a test
    /// pins the plan). The other two conditions select only what the trigger allows: a time written as a date and time (not, say, a bare
    /// number, which SQLite reads as a Julian day) that julianday can read. So a row the trigger would refuse can never block the prune.
    /// </summary>
    public const string PruneSql = """
        DELETE FROM audit_events WHERE id IN (
          SELECT id FROM audit_events
          WHERE time < @b AND time GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T*' AND julianday(time) < julianday(@b)
          LIMIT @n)
        """;

    public async Task<int> PruneAsync(DateTimeOffset before, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        // Each chunk is its own statement and its own short write: when the database refuses a chunk (a clock running ahead), the chunks
        // before it stay deleted and the error is thrown.
        cmd.CommandText = PruneSql;
        cmd.Parameters.AddWithValue("@b", Format(before));
        cmd.Parameters.AddWithValue("@n", _pruneChunk);
        var total = 0;
        while (true)
        {
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            total += deleted;
            if (deleted < _pruneChunk) return total;
        }
    }

    private static string Format(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Newest first. For the admin tool and tests.</summary>
    public async Task<IReadOnlyList<AuditEvent>> QueryAsync(string? tenantId, int limit, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT time, request_id, tenant_id, token_id, kind, package_id, version, decision, reason, waiver_id FROM audit_events "
                          + (tenantId is null ? "" : "WHERE tenant_id=@t ") + "ORDER BY id DESC LIMIT @l";
        if (tenantId is not null) cmd.Parameters.AddWithValue("@t", tenantId);
        cmd.Parameters.AddWithValue("@l", limit);
        var list = new List<AuditEvent>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AuditEvent(DateTimeOffset.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3),
                Enum.Parse<AuditKind>(r.GetString(4)), r.GetString(5), r.GetString(6), Enum.Parse<Decision>(r.GetString(7)),
                Enum.Parse<VerdictReason>(r.GetString(8)), r.IsDBNull(9) ? null : r.GetString(9)));
        return list;
    }
}
