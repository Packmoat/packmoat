using Npgsql;
using NpgsqlTypes;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The audit trail of package downloads on PostgreSQL, as SQLite's. The table refuses UPDATE and TRUNCATE always,
/// and DELETE of a row less than 90 days old by the database's own clock unless the tenant's purge is running (the audit_no_delete trigger);
/// an event for a purged tenant is dropped by the audit_purged_tenant trigger, which takes that tenant id's lock shared. Times are
/// timestamptz, in UTC to the microsecond (<see cref="PostgresTime"/>). PACKMOAT_DB_ENGINE=postgres selects it.
/// <para><b>The batch</b> is one transaction. It first takes <c>lock_tenant_id(id, false)</c> for each of its distinct tenants, in
/// ordinal order (<see cref="PostgresTenantLocks"/>, the decision-record chunks' order too), and only then inserts, so every batch takes its tenant locks in the same order and holds no row while it waits for one: a
/// purge holding a tenant's lock exclusively makes a batch wait before it has written anything. The whole transaction is retried on a
/// deadlock or a serialization failure (<see cref="PostgresRetry"/>): each attempt opens its own connection, begins, locks, inserts and
/// commits, and touches nothing outside that transaction, so a failed attempt leaves no trace and the next one starts clean.</para>
/// <para><b>The prune</b> deletes in chunks of at most <c>pruneChunk</c> rows, each chunk its own statement and so its own transaction, as
/// SQLite's: when the database refuses a chunk (the app's clock running ahead of the database's), the chunks before it stay deleted and
/// the refusal reaches the caller. The cutoff is the caller's, from the app's clock; the database's clock only judges, through the trigger.
/// The prune never retries.</para>
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresAuditLog : IAuditLog, IAuditBatchLog, IAuditRetention, IAuditQuery
{
    /// <summary>The most rows one prune statement deletes, so each chunk's transaction is short (SQLite's number).</summary>
    public const int DefaultPruneChunk = 5_000;

    private const string Columns = "time, request_id, tenant_id, token_id, kind, package_id, version, decision, reason, waiver_id";

    /// <summary>
    /// One prune chunk: at most <c>@n</c> rows written before the cutoff <c>@b</c>, oldest first (ix_audit_time finds them in that order, as
    /// SQLite's plan does), so a chunk the database refuses holds the youngest rows and the older ones are already gone.
    /// </summary>
    internal const string PruneSql = "DELETE FROM audit_events WHERE id IN (SELECT id FROM audit_events WHERE time < @b ORDER BY time LIMIT @n)";

    private readonly PostgresDatabase _db;
    private readonly int _pruneChunk;

    /// <param name="pruneChunk">A test seam: production uses <see cref="DefaultPruneChunk"/>.</param>
    public PostgresAuditLog(PostgresDatabase db, int pruneChunk = DefaultPruneChunk)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pruneChunk, 1);
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _pruneChunk = pruneChunk;
    }

    /// <summary>Writes one event and waits for it. For the admin tool and tests; the server queues (<see cref="QueuedAuditLog"/>).</summary>
    public async ValueTask AppendAsync(AuditEvent e, CancellationToken ct) => await AppendBatchAsync(new[] { e }, ct);

    public async Task AppendBatchAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) return;
        // Worked out once, before any attempt: what is written, and the tenants to lock in lock order. Neither changes between attempts.
        var rows = events.Select(Row.Of).ToArray();
        var tenants = PostgresTenantLocks.InLockOrder(rows.Select(r => r.Tenant));
        await PostgresRetry.RunAsync(attemptCt => AttemptAsync(rows, tenants, attemptCt), ct);
    }

    /// <summary>One attempt of the batch: one whole transaction, on a connection of its own, with nothing done outside it.</summary>
    private async Task AttemptAsync(Row[] rows, string[] tenants, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await PostgresTenantLocks.TakeSharedAsync(c, tx, tenants, ct);   // lock order, every lock before any row
        await using (var insert = new NpgsqlCommand($"INSERT INTO audit_events({Columns}) VALUES(@t,@r,@tn,@tk,@k,@p,@v,@d,@why,@w)", c, tx))
        {
            var time = insert.Parameters.Add("t", NpgsqlDbType.TimestampTz);
            var request = insert.Parameters.Add("r", NpgsqlDbType.Text);
            var tenant = insert.Parameters.Add("tn", NpgsqlDbType.Text);
            var token = insert.Parameters.Add("tk", NpgsqlDbType.Text);
            var kind = insert.Parameters.Add("k", NpgsqlDbType.Text);
            var package = insert.Parameters.Add("p", NpgsqlDbType.Text);
            var version = insert.Parameters.Add("v", NpgsqlDbType.Text);
            var decision = insert.Parameters.Add("d", NpgsqlDbType.Text);
            var reason = insert.Parameters.Add("why", NpgsqlDbType.Text);
            var waiver = insert.Parameters.Add("w", NpgsqlDbType.Text);
            foreach (var row in rows)
            {
                time.Value = row.Time;
                request.Value = row.Event.RequestId;
                tenant.Value = row.Tenant;
                token.Value = row.Event.TokenId;
                kind.Value = row.Event.Kind.ToString();
                package.Value = row.Event.PackageId;
                version.Value = row.Event.Version;
                decision.Value = row.Event.Decision.ToString();
                reason.Value = row.Event.Reason.ToString();
                waiver.Value = (object?)row.Event.WaiverId ?? DBNull.Value;
                await insert.ExecuteNonQueryAsync(ct);   // a purged tenant's row is dropped by its trigger
            }
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>An event as it is written: its time already in UTC, cut to the microsecond (a time PostgreSQL cannot hold is refused here).</summary>
    private sealed record Row(AuditEvent Event, DateTimeOffset Time)
    {
        public string Tenant => Event.TenantId;

        public static Row Of(AuditEvent e)
        {
            ArgumentNullException.ThrowIfNull(e);
            return new(e, PostgresTime.ToDatabase(e.Time));
        }
    }

    public async Task<int> PruneAsync(DateTimeOffset before, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        // Each chunk is its own statement and so its own transaction: when the database refuses a chunk (a clock running ahead), the chunks
        // before it stay deleted and the refusal is thrown.
        await using var cmd = new NpgsqlCommand(PruneSql, c);
        cmd.Parameters.AddWithValue("b", PostgresTime.ToDatabase(before));
        cmd.Parameters.AddWithValue("n", (long)_pruneChunk);
        var total = 0;
        while (true)
        {
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            total += deleted;
            if (deleted < _pruneChunk) return total;
        }
    }

    /// <summary>Newest first. For the admin tool and tests.</summary>
    public async Task<IReadOnlyList<AuditEvent>> QueryAsync(string? tenantId, int limit, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM audit_events " + (tenantId is null ? "" : "WHERE tenant_id=@t ") + "ORDER BY id DESC LIMIT @l", c);
        if (tenantId is not null) cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("l", (long)limit);
        var list = new List<AuditEvent>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AuditEvent(PostgresTime.FromDatabase(r.GetFieldValue<DateTime>(0)), r.GetString(1), r.GetString(2), r.GetString(3),
                Enum.Parse<AuditKind>(r.GetString(4)), r.GetString(5), r.GetString(6), Enum.Parse<Decision>(r.GetString(7)),
                Enum.Parse<VerdictReason>(r.GetString(8)), r.IsDBNull(9) ? null : r.GetString(9)));
        return list;
    }
}
