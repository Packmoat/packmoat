using Npgsql;
using NpgsqlTypes;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The per-version decision record (<c>decision_records</c>) on PostgreSQL, the twin of SQLite's: the same upsert (first
/// seen kept, last seen the latest, counts added, the policy facts of the latest sighting), the same chunks and the same prune. SQLite's
/// two-argument MIN and MAX are LEAST and GREATEST here. Times are timestamptz, in UTC to the microsecond (<see cref="PostgresTime"/>); enum
/// values are stored by name. A record for a purged tenant is dropped by the decision_records_purged_tenant trigger, which takes that
/// tenant id's lock shared. PACKMOAT_DB_ENGINE=postgres selects it.
/// <para><b>The chunks.</b> A batch is written in chunks of at most <see cref="DefaultChunkSize"/> records, the same chunks as SQLite's, one
/// transaction each: a failed chunk is rolled back and the error thrown; the chunks before it are kept and the rest are not written. Each
/// chunk is sorted by its conflict key (tenant, ecosystem, package, version, decision, reason; ordinal, stable, so records with one key keep
/// their order) before it is written, so two chunks that touch the same rows lock them in one order. In its transaction a chunk first takes
/// <c>lock_tenant_id(id, false)</c> for each of its distinct tenants, in the audit batch's order (<see cref="PostgresTenantLocks"/>), and
/// only then upserts, so one that waits for a tenant the purge holds has written nothing. A chunk that ends in a deadlock or a
/// serialization failure is retried whole (<see cref="PostgresRetry"/>): each attempt opens its own connection, begins, locks, upserts and
/// commits, and reads only the chunk worked out before the first attempt, so a failed attempt leaves no trace.</para>
/// <para><b>The prune</b> is one statement, as SQLite's, and never retries.</para>
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresDecisionRecordLog : IDecisionRecordLog
{
    /// <summary>Records per chunk, and so per transaction: SQLite's number (the tests check they are equal).</summary>
    public const int DefaultChunkSize = 1000;

    private const string UpsertSql = """
        INSERT INTO decision_records(tenant_id, ecosystem, package_id, version, decision, reason, waiver_id, cooldown_days, published_at,
                                     policy_revision, first_seen_at, last_seen_at, seen_count)
        VALUES(@t, @e, @p, @v, @d, @r, @w, @c, @pub, @rev, @first, @last, @n)
        ON CONFLICT(tenant_id, ecosystem, package_id, version, decision, reason) DO UPDATE SET
            first_seen_at = LEAST(decision_records.first_seen_at, excluded.first_seen_at),
            seen_count = decision_records.seen_count + excluded.seen_count,
            last_seen_at = GREATEST(decision_records.last_seen_at, excluded.last_seen_at),
            waiver_id = CASE WHEN excluded.last_seen_at >= decision_records.last_seen_at THEN excluded.waiver_id ELSE decision_records.waiver_id END,
            cooldown_days = CASE WHEN excluded.last_seen_at >= decision_records.last_seen_at THEN excluded.cooldown_days ELSE decision_records.cooldown_days END,
            published_at = CASE WHEN excluded.last_seen_at >= decision_records.last_seen_at THEN excluded.published_at ELSE decision_records.published_at END,
            policy_revision = CASE WHEN excluded.last_seen_at >= decision_records.last_seen_at THEN excluded.policy_revision ELSE decision_records.policy_revision END
        """;

    private readonly PostgresDatabase _db;
    private readonly int _chunkSize;

    /// <param name="chunkSize">A test seam: production uses <see cref="DefaultChunkSize"/>.</param>
    public PostgresDecisionRecordLog(PostgresDatabase db, int chunkSize = DefaultChunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _chunkSize = chunkSize;
    }

    /// <summary>
    /// Writes in chunks, one transaction each (see <see cref="PostgresDecisionRecordLog"/>). A failed chunk is rolled back and the error
    /// thrown: the chunks before it are kept, the rest are not written.
    /// </summary>
    public async Task UpsertAsync(IReadOnlyList<DecisionRecord> records, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        for (var start = 0; start < records.Count; start += _chunkSize)
        {
            // Worked out once, before any attempt: the chunk's rows in conflict-key order, and its tenants in lock order.
            var rows = records.Skip(start).Take(_chunkSize).Select(Row.Of).OrderBy(r => r, Row.ConflictKeyOrder).ToArray();
            var tenants = PostgresTenantLocks.InLockOrder(rows.Select(r => r.Tenant));
            await PostgresRetry.RunAsync(attemptCt => AttemptAsync(rows, tenants, attemptCt), ct);
        }
    }

    /// <summary>One attempt of a chunk: one whole transaction, on a connection of its own, with nothing done outside it.</summary>
    private async Task AttemptAsync(Row[] rows, string[] tenants, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await PostgresTenantLocks.TakeSharedAsync(c, tx, tenants, ct);   // lock order, every lock before any row
        await using (var cmd = new NpgsqlCommand(UpsertSql, c, tx))
        {
            var t = cmd.Parameters.Add("t", NpgsqlDbType.Text);
            var e = cmd.Parameters.Add("e", NpgsqlDbType.Text);
            var p = cmd.Parameters.Add("p", NpgsqlDbType.Text);
            var v = cmd.Parameters.Add("v", NpgsqlDbType.Text);
            var d = cmd.Parameters.Add("d", NpgsqlDbType.Text);
            var r = cmd.Parameters.Add("r", NpgsqlDbType.Text);
            var w = cmd.Parameters.Add("w", NpgsqlDbType.Text);
            var cd = cmd.Parameters.Add("c", NpgsqlDbType.Bigint);
            var pub = cmd.Parameters.Add("pub", NpgsqlDbType.TimestampTz);
            var rev = cmd.Parameters.Add("rev", NpgsqlDbType.Bigint);
            var first = cmd.Parameters.Add("first", NpgsqlDbType.TimestampTz);
            var last = cmd.Parameters.Add("last", NpgsqlDbType.TimestampTz);
            var n = cmd.Parameters.Add("n", NpgsqlDbType.Bigint);
            foreach (var row in rows)   // conflict-key order
            {
                var rec = row.Record;
                t.Value = rec.TenantId;
                e.Value = row.Ecosystem;
                p.Value = rec.PackageId;
                v.Value = rec.Version;
                d.Value = row.Decision;
                r.Value = row.Reason;
                w.Value = (object?)rec.WaiverId ?? DBNull.Value;
                cd.Value = rec.CooldownDays is { } days ? (object)(long)days : DBNull.Value;
                pub.Value = row.PublishedAt is { } published ? (object)published : DBNull.Value;
                rev.Value = rec.PolicyRevision;
                first.Value = row.FirstSeenAt;
                last.Value = row.LastSeenAt;
                n.Value = rec.SeenCount;
                await cmd.ExecuteNonQueryAsync(ct);   // a purged tenant's record is dropped by its trigger
            }
        }
        await tx.CommitAsync(ct);
    }

    public async Task<int> PruneAsync(DateTimeOffset lastSeenBefore, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM decision_records WHERE last_seen_at < @cutoff", c);
        cmd.Parameters.AddWithValue("cutoff", PostgresTime.ToDatabase(lastSeenBefore));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// A record as it is written: its key's enum values by name, and its times in UTC cut to the microsecond (a time PostgreSQL cannot hold
    /// is refused here, before any attempt).
    /// </summary>
    private sealed record Row(DecisionRecord Record, string Ecosystem, string Decision, string Reason, DateTimeOffset? PublishedAt,
        DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt)
    {
        public string Tenant => Record.TenantId;

        public static Row Of(DecisionRecord rec)
        {
            ArgumentNullException.ThrowIfNull(rec);
            return new(rec, rec.Ecosystem.ToString(), rec.Decision.ToString(), rec.Reason.ToString(), PostgresTime.ToDatabase(rec.PublishedAt),
                PostgresTime.ToDatabase(rec.FirstSeenAt), PostgresTime.ToDatabase(rec.LastSeenAt));
        }

        /// <summary>
        /// The conflict key's order: tenant (by <see cref="PostgresTenantLocks.Comparer"/>, so the rows' trigger locks follow the pre-lock's
        /// order too), then ecosystem, package, version, decision and reason as stored, each ordinal.
        /// </summary>
        public static readonly IComparer<Row> ConflictKeyOrder = Comparer<Row>.Create((a, b) =>
        {
            var c = PostgresTenantLocks.Comparer.Compare(a.Tenant, b.Tenant);
            if (c == 0) c = string.CompareOrdinal(a.Ecosystem, b.Ecosystem);
            if (c == 0) c = string.CompareOrdinal(a.Record.PackageId, b.Record.PackageId);
            if (c == 0) c = string.CompareOrdinal(a.Record.Version, b.Record.Version);
            if (c == 0) c = string.CompareOrdinal(a.Decision, b.Decision);
            if (c == 0) c = string.CompareOrdinal(a.Reason, b.Reason);
            return c;
        });
    }
}
