using System.Data;
using System.Globalization;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure;

/// <summary>
/// The per-version decision record in the <c>decision_records</c> table (migration 17). Times are stored as round-trip UTC text, which
/// sorts as time. Enum values are stored by name, as in audit_events.
/// </summary>
public sealed class SqliteDecisionRecordLog : IDecisionRecordLog
{
    private const string UpsertSql = """
        INSERT INTO decision_records(tenant_id, ecosystem, package_id, version, decision, reason, waiver_id, cooldown_days, published_at,
                                     policy_revision, first_seen_at, last_seen_at, seen_count)
        VALUES(@t, @e, @p, @v, @d, @r, @w, @c, @pub, @rev, @first, @last, @n)
        ON CONFLICT(tenant_id, ecosystem, package_id, version, decision, reason) DO UPDATE SET
            first_seen_at = MIN(decision_records.first_seen_at, excluded.first_seen_at),
            seen_count = decision_records.seen_count + excluded.seen_count,
            last_seen_at = MAX(decision_records.last_seen_at, excluded.last_seen_at),
            waiver_id = CASE WHEN excluded.last_seen_at >= decision_records.last_seen_at THEN excluded.waiver_id ELSE decision_records.waiver_id END,
            cooldown_days = CASE WHEN excluded.last_seen_at >= decision_records.last_seen_at THEN excluded.cooldown_days ELSE decision_records.cooldown_days END,
            published_at = CASE WHEN excluded.last_seen_at >= decision_records.last_seen_at THEN excluded.published_at ELSE decision_records.published_at END,
            policy_revision = CASE WHEN excluded.last_seen_at >= decision_records.last_seen_at THEN excluded.policy_revision ELSE decision_records.policy_revision END
        """;

    /// <summary>Rows per transaction: about a few milliseconds of write lock each.</summary>
    public const int ChunkSize = 1000;

    private readonly SqliteDatabase _db;

    public SqliteDecisionRecordLog(SqliteDatabase db) => _db = db;

    /// <summary>
    /// Writes in chunks of <see cref="ChunkSize"/> rows, one transaction each, so the write lock is never held long. A failed chunk is
    /// rolled back and the error thrown: the chunks before it are kept, the rest are not written.
    /// </summary>
    public async Task UpsertAsync(IReadOnlyList<DecisionRecord> records, CancellationToken ct)
    {
        if (records.Count == 0) return;
        await using var c = await _db.OpenAsync(ct);
        for (var start = 0; start < records.Count; start += ChunkSize)
            await UpsertChunkAsync(c, records.Skip(start).Take(ChunkSize), ct);
    }

    private static async Task UpsertChunkAsync(Microsoft.Data.Sqlite.SqliteConnection c, IEnumerable<DecisionRecord> records, CancellationToken ct)
    {
        await using var tx = c.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = UpsertSql;
        var t = cmd.Parameters.Add("@t", Microsoft.Data.Sqlite.SqliteType.Text);
        var e = cmd.Parameters.Add("@e", Microsoft.Data.Sqlite.SqliteType.Text);
        var p = cmd.Parameters.Add("@p", Microsoft.Data.Sqlite.SqliteType.Text);
        var v = cmd.Parameters.Add("@v", Microsoft.Data.Sqlite.SqliteType.Text);
        var d = cmd.Parameters.Add("@d", Microsoft.Data.Sqlite.SqliteType.Text);
        var r = cmd.Parameters.Add("@r", Microsoft.Data.Sqlite.SqliteType.Text);
        var w = cmd.Parameters.Add("@w", Microsoft.Data.Sqlite.SqliteType.Text);
        var cd = cmd.Parameters.Add("@c", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pub = cmd.Parameters.Add("@pub", Microsoft.Data.Sqlite.SqliteType.Text);
        var rev = cmd.Parameters.Add("@rev", Microsoft.Data.Sqlite.SqliteType.Integer);
        var first = cmd.Parameters.Add("@first", Microsoft.Data.Sqlite.SqliteType.Text);
        var last = cmd.Parameters.Add("@last", Microsoft.Data.Sqlite.SqliteType.Text);
        var n = cmd.Parameters.Add("@n", Microsoft.Data.Sqlite.SqliteType.Integer);
        foreach (var rec in records)
        {
            t.Value = rec.TenantId;
            e.Value = rec.Ecosystem.ToString();
            p.Value = rec.PackageId;
            v.Value = rec.Version;
            d.Value = rec.Decision.ToString();
            r.Value = rec.Reason.ToString();
            w.Value = (object?)rec.WaiverId ?? DBNull.Value;
            cd.Value = (object?)rec.CooldownDays ?? DBNull.Value;
            pub.Value = rec.PublishedAt is { } published ? Format(published) : DBNull.Value;
            rev.Value = rec.PolicyRevision;
            first.Value = Format(rec.FirstSeenAt);
            last.Value = Format(rec.LastSeenAt);
            n.Value = rec.SeenCount;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<int> PruneAsync(DateTimeOffset lastSeenBefore, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM decision_records WHERE last_seen_at < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", Format(lastSeenBefore));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // Reads are in SqliteDecisionRecordQuery (the IDecisionRecordQuery port), which shares these two.
    internal static string Format(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    internal static DateTimeOffset Parse(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
