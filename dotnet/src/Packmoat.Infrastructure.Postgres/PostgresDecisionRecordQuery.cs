using Npgsql;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// Reads the per-version decision record (<c>decision_records</c>) for the support view on PostgreSQL, the twin of
/// SQLite's. Every read is one tenant and bounded (it fetches one row past the limit only to say whether more matched), newest last seen
/// first. Every value is a parameter, and the package is compared exactly or by prefix with <c>substr</c>, never LIKE, so '%', '_' and '\'
/// in an id or a search are ordinary characters. The text columns are COLLATE "C", so the order and the prefix's range compare bytes, as
/// SQLite's BINARY does. SQLite names its index for the newest-first walk; PostgreSQL's planner needs no hint.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresDecisionRecordQuery : IDecisionRecordQuery
{
    private const string Columns = """
        SELECT tenant_id, ecosystem, package_id, version, decision, reason, waiver_id, cooldown_days, published_at, policy_revision,
               first_seen_at, last_seen_at, seen_count
        FROM decision_records
        """;

    private readonly PostgresDatabase _db;

    public PostgresDecisionRecordQuery(PostgresDatabase db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<DecisionRecordPage> QueryAsync(DecisionRecordQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, DecisionRecordQuery.MaxLimit);
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = Command(c, query);
        var list = new List<DecisionRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new DecisionRecord(r.GetString(0), Enum.Parse<Ecosystem>(r.GetString(1)), r.GetString(2), r.GetString(3),
                Enum.Parse<Decision>(r.GetString(4)), Enum.Parse<VerdictReason>(r.GetString(5)),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : checked((int)r.GetInt64(7)),
                r.IsDBNull(8) ? null : PostgresTime.FromDatabase(r.GetFieldValue<DateTime>(8)),
                r.GetInt64(9), PostgresTime.FromDatabase(r.GetFieldValue<DateTime>(10)), PostgresTime.FromDatabase(r.GetFieldValue<DateTime>(11)),
                r.GetInt64(12)));
        var more = list.Count > query.Limit;
        if (more) list.RemoveAt(list.Count - 1);
        return new DecisionRecordPage(list, more);
    }

    private static NpgsqlCommand Command(NpgsqlConnection c, DecisionRecordQuery query)
    {
        var cmd = new NpgsqlCommand { Connection = c };
        var where = "WHERE tenant_id = @t";
        cmd.Parameters.AddWithValue("t", query.TenantId);
        if (query.PackageId is { } package)
        {
            cmd.Parameters.AddWithValue("e", query.Ecosystem.ToString());
            cmd.Parameters.AddWithValue("p", package);
            // A prefix: the range [@p, @p || U+10FFFF) lets the primary key seek and stop at the end of the prefix (COLLATE "C" compares
            // UTF-8 bytes, and no character sorts above U+10FFFF); substr keeps exactly the ids that begin with it (no pattern characters).
            where += query.PackagePrefix
                ? " AND ecosystem = @e AND package_id >= @p AND package_id < @p || chr(1114111) AND substr(package_id, 1, length(@p)) = @p"
                : " AND ecosystem = @e AND package_id = @p";
            if (query.Version is { } version)
            {
                where += " AND version = @v";
                cmd.Parameters.AddWithValue("v", version);
            }
        }
        else if (query.Version is not null)
            throw new ArgumentException("A version needs a package.", nameof(query));
        cmd.Parameters.AddWithValue("n", (long)query.Limit + 1);
        cmd.CommandText = $"{Columns} {where} ORDER BY last_seen_at DESC, package_id, version, decision, reason LIMIT @n";
        return cmd;
    }
}
