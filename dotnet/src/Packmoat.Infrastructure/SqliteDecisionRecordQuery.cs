using Microsoft.Data.Sqlite;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure;

/// <summary>
/// Reads the per-version decision record (<c>decision_records</c>, migration 17) for the support view. Every read is one tenant and
/// bounded (it fetches one row past the limit only to say whether more matched). With no package it walks ix_decision_records_tenant_seen
/// newest first; with a package it seeks the primary key (tenant, ecosystem, package). Every value is a parameter, and the package is
/// compared exactly or by prefix with <c>substr</c>, never LIKE, so '%', '_' and '\' in an id or a search are ordinary characters.
/// </summary>
public sealed class SqliteDecisionRecordQuery : IDecisionRecordQuery
{
    private const string Columns = """
        SELECT tenant_id, ecosystem, package_id, version, decision, reason, waiver_id, cooldown_days, published_at, policy_revision,
               first_seen_at, last_seen_at, seen_count
        FROM decision_records
        """;

    private readonly SqliteDatabase _db;

    public SqliteDecisionRecordQuery(SqliteDatabase db) => _db = db;

    public async Task<DecisionRecordPage> QueryAsync(DecisionRecordQuery query, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, DecisionRecordQuery.MaxLimit);
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = Command(c, query, Columns);
        var list = new List<DecisionRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new DecisionRecord(r.GetString(0), Enum.Parse<Ecosystem>(r.GetString(1)), r.GetString(2), r.GetString(3),
                Enum.Parse<Decision>(r.GetString(4)), Enum.Parse<VerdictReason>(r.GetString(5)),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetInt32(7),
                r.IsDBNull(8) ? null : SqliteDecisionRecordLog.Parse(r.GetString(8)),
                r.GetInt64(9), SqliteDecisionRecordLog.Parse(r.GetString(10)), SqliteDecisionRecordLog.Parse(r.GetString(11)), r.GetInt64(12)));
        var more = list.Count > query.Limit;
        if (more) list.RemoveAt(list.Count - 1);
        return new DecisionRecordPage(list, more);
    }

    /// <summary>The query plan SQLite chooses for <paramref name="query"/> (EXPLAIN QUERY PLAN details, joined). For the index tests.</summary>
    public static async Task<string> PlanAsync(SqliteDatabase db, DecisionRecordQuery query, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await using var cmd = Command(c, query, "EXPLAIN QUERY PLAN " + Columns);
        var lines = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) lines.Add(r.GetString(3));
        return string.Join("; ", lines);
    }

    private static SqliteCommand Command(SqliteConnection c, DecisionRecordQuery query, string select)
    {
        var cmd = c.CreateCommand();
        // Without a package, the planner would rather seek the primary key on tenant_id and sort every row the tenant has. The
        // (tenant_id, last_seen_at) index walked backwards gives the newest first and stops at the limit, so it is named here.
        var where = query.PackageId is null ? "INDEXED BY ix_decision_records_tenant_seen WHERE tenant_id = @t" : "WHERE tenant_id = @t";
        cmd.Parameters.AddWithValue("@t", query.TenantId);
        if (query.PackageId is { } package)
        {
            cmd.Parameters.AddWithValue("@e", query.Ecosystem.ToString());
            cmd.Parameters.AddWithValue("@p", package);
            // A prefix: the range [@p, @p + U+10FFFF) lets the primary key seek and stop at the end of the prefix (UTF-8 compares
            // byte-wise, and no character sorts above U+10FFFF); substr keeps exactly the ids that begin with it (no pattern characters).
            where += query.PackagePrefix
                ? " AND ecosystem = @e AND package_id >= @p AND package_id < @p || char(1114111) AND substr(package_id, 1, length(@p)) = @p"
                : " AND ecosystem = @e AND package_id = @p";
            if (query.Version is { } version)
            {
                where += " AND version = @v";
                cmd.Parameters.AddWithValue("@v", version);
            }
        }
        else if (query.Version is not null)
            throw new ArgumentException("A version needs a package.", nameof(query));
        cmd.Parameters.AddWithValue("@n", query.Limit + 1);
        cmd.CommandText = $"{select} {where} ORDER BY last_seen_at DESC, package_id, version, decision, reason LIMIT @n";
        return cmd;
    }
}
