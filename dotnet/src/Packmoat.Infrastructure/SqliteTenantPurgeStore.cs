using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Packmoat.Application;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Infrastructure;

/// <summary>
/// The tenant purge's SQLite side (migration 15). The core's tables are classified once, in <see cref="CoreTables"/>: deleted for the
/// tenant, kept, or holding no tenant data; a module's tables are classified by its <see cref="IPurgeContributor"/>. Inside the purge's
/// one transaction, two guards refuse and roll back: a table in the database that nobody classified (or two classified), and a
/// deleted table that still holds a row for the tenant afterwards. Who is due is the <see cref="IPurgeRecheck"/>'s to say, and it
/// re-checks under the write lock; with none, every purge is refused.
/// </summary>
public sealed class SqliteTenantPurgeStore : IPurgeSummaryStore
{
    /// <summary>
    /// The core's tables and what a purge does to them (<see cref="CorePurgeTables"/>, shared by every engine), with
    /// SQLite's own sqlite_sequence where it has always been: the same list, in the same order, as before the move.
    /// </summary>
    public static IReadOnlyList<PurgeTable> CoreTables { get; } =
        CorePurgeTables.WithEngineTables(new PurgeTable("sqlite_sequence", PurgeTableFate.NoTenantData, "SQLite's AUTOINCREMENT counters"));

    /// <summary>The delete for each of the core's deleted tables before the contributors', in order. <c>@t</c> is the tenant id.</summary>
    private static readonly (string Table, string Sql)[] ChildDeletes =
    {
        ("token_activity", "DELETE FROM token_activity WHERE token_id IN (SELECT id FROM tokens WHERE tenant_id=@t)"),
        ("decision_records", "DELETE FROM decision_records WHERE tenant_id=@t"),
        ("tokens", "DELETE FROM tokens WHERE tenant_id=@t"),
        ("shared_app_verifications", "DELETE FROM shared_app_verifications WHERE tenant_id=@t"),
        ("feed_checks", "DELETE FROM feed_checks WHERE tenant_id=@t"),
        ("feed_setup_scratch", "DELETE FROM feed_setup_scratch WHERE tenant_id=@t"),
        ("upstream_identities", "DELETE FROM upstream_identities WHERE tenant_id=@t"),
        ("sign_in_directories", "DELETE FROM sign_in_directories WHERE tenant_id=@t"),
        ("members", "DELETE FROM members WHERE tenant_id=@t"),
        ("auto_join_settings", "DELETE FROM auto_join_settings WHERE tenant_id=@t"),
        ("internal_prefixes", "DELETE FROM internal_prefixes WHERE tenant_id=@t"),
        ("package_overrides", "DELETE FROM package_overrides WHERE tenant_id=@t"),
        ("waivers", "DELETE FROM waivers WHERE tenant_id=@t"),
    };

    /// <summary>What the purge never reaches, the core's part (the contributors' follow).</summary>
    private static readonly string[] CoreNotReached = ["the access log (90 days)", "backups"];

    private readonly SqliteDatabase _db;
    private readonly IPurgeRecheck? _recheck;
    private readonly IReadOnlyList<IPurgeContributor> _contributors;

    /// <param name="recheck">Who is due, re-checked under the write lock; null: nobody is listed and every purge is refused.</param>
    /// <param name="contributors">The modules' tables in the purge (required: empty for none).</param>
    public SqliteTenantPurgeStore(SqliteDatabase db, IPurgeRecheck? recheck, IReadOnlyList<IPurgeContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(contributors);
        _db = db;
        _recheck = recheck;
        _contributors = contributors;
    }

    public IPurgeEligibility? Eligibility => _recheck;

    /// <summary>Every table's classification: the core's, then each contributor's.</summary>
    public IReadOnlyList<PurgeTable> Classification => CoreTables.Concat(_contributors.SelectMany(c => c.Tables)).ToList();

    /// <summary>The tables a purge keeps, for the operator's summary: the operator's evidence first, the contributors', the purge record last.</summary>
    public IReadOnlyList<string> Kept =>
        CoreTables.Where(t => t.Fate == PurgeTableFate.Kept && t.Table != "tenant_purges").Select(t => t.Table)
            .Concat(_contributors.SelectMany(c => c.Tables).Where(t => t.Fate == PurgeTableFate.Kept).Select(t => t.Table))
            .Append("tenant_purges").ToList();

    /// <summary>What the purge never reaches, for the operator's summary: the core's, then each contributor's.</summary>
    public IReadOnlyList<string> NotReached => CoreNotReached.Concat(_contributors.SelectMany(c => c.NotReached)).ToList();

    public async Task<TenantPurgeRecord?> FindPurgeAsync(string tenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT purged_at, reason, actor FROM tenant_purges WHERE tenant_id=@t";
        cmd.Parameters.AddWithValue("@t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new TenantPurgeRecord(tenantId, Time(r, 0) ?? DateTimeOffset.MinValue, r.GetString(1), r.GetString(2));
    }

    public async Task<IReadOnlyList<PurgedTable>> PurgeAsync(PurgeCandidate candidate, TenantPurgeRecord record, CancellationToken ct)
    {
        if (record.TenantId != candidate.TenantId) throw new ArgumentException("The purge record names another tenant.", nameof(record));
        var recheck = _recheck ?? throw new PurgeRefusedException(TenantPurgeCommands.NoEligibility);
        var tenantId = candidate.TenantId;
        await using var c = await _db.OpenAsync(ct);
        await using var tx = c.BeginTransaction(IsolationLevel.Serializable, deferred: false);   // BEGIN IMMEDIATE: the write lock before the first read
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;

        // The re-check: what was decided on must still hold, read under the write lock. Anything changed (a webhook, a sync, a checkout,
        // the operator) refuses; disposing the transaction rolls back.
        cmd.CommandText = "SELECT 1 FROM tenants WHERE id=@t";
        cmd.Parameters.AddWithValue("@t", tenantId);
        if (await cmd.ExecuteScalarAsync(ct) is null) throw new PurgeRefusedException($"Tenant {tenantId} no longer exists. Nothing was deleted.");
        if (!await recheck.StillDueAsync(tx, candidate, record.PurgedAt, ct))
            throw new PurgeRefusedException($"Tenant {tenantId} changed while it was being checked{(recheck.Rechecked is { Length: > 0 } what ? $" ({what})" : "")}. Nothing was deleted; run the purge again.");

        // Guard 1: every table is classified, once. A table nobody named could hold the tenant's data and outlive the purge.
        var classification = Classification;
        await RequireClassifiedAsync(cmd, classification, ct);

        // Token activity is found through the tenant's tokens: their ids, before they go, for the count afterwards.
        var tokenIds = await ReadStringsAsync(cmd, "SELECT id FROM tokens WHERE tenant_id=@t", tenantId, ct);

        var counts = new List<PurgedTable>();
        foreach (var (table, sql) in ChildDeletes)
            counts.Add(new PurgedTable(table, await ExecAsync(cmd, sql, ct, ("@t", tenantId))));
        foreach (var contributor in _contributors)
            counts.AddRange(await contributor.DeleteAsync(tx, tenantId, ct));
        // The tenant's feed-check lease (job_leases holds no tenant data but this row's name, AzureSetupState.LeaseJob): not counted.
        await ExecAsync(cmd, CorePurgeTables.FeedCheckLeaseDelete, ct, ("@t", tenantId));
        counts.Add(new PurgedTable("tenants", await ExecAsync(cmd, "DELETE FROM tenants WHERE id=@t", ct, ("@t", tenantId))));

        // Recorded only once the tenant row is gone (a trigger insists), with 'purging' set: the one moment its audit rows may be deleted.
        await ExecAsync(cmd, "INSERT INTO tenant_purges(tenant_id, purged_at, reason, actor, purging) VALUES(@t,@at,@r,@a,1)", ct,
            ("@t", tenantId), ("@at", Format(record.PurgedAt)), ("@r", record.Reason), ("@a", record.Actor));
        counts.Add(new PurgedTable("audit_events", await ExecAsync(cmd, "DELETE FROM audit_events WHERE tenant_id=@t", ct, ("@t", tenantId))));
        if (await ExecAsync(cmd, "UPDATE tenant_purges SET purging=0 WHERE tenant_id=@t AND purging=1", ct, ("@t", tenantId)) != 1)
            throw new PurgeRefusedException("The purge record could not be closed. Nothing was deleted.");

        // Guard 2: every table declared deleted holds no row for the tenant now.
        await RequireDeletedAsync(cmd, classification, tenantId, tokenIds, ct);

        await tx.CommitAsync(ct);
        return counts;
    }

    /// <summary>
    /// Refuses unless every table in the database is classified exactly once (by the core or one contributor), every classified table
    /// exists, every name is a plain identifier, and every deleted table names its tenant column (token_activity, the core's, is counted
    /// through its tokens instead).
    /// </summary>
    private static async Task RequireClassifiedAsync(SqliteCommand cmd, IReadOnlyList<PurgeTable> classification, CancellationToken ct)
    {
        foreach (var t in classification)
        {
            if (!PurgeTable.IsIdentifier(t.Table) || (t.TenantColumn is not null && !PurgeTable.IsIdentifier(t.TenantColumn)))
                throw new PurgeRefusedException($"The purge classification names a table or column that is not a plain name ({t.Table}). Nothing was deleted.");
            if (t.Fate == PurgeTableFate.Deleted && t.TenantColumn is null && t.Table != "token_activity")
                throw new PurgeRefusedException($"Table {t.Table} is declared deleted with no tenant column. Nothing was deleted.");
        }
        if (classification.GroupBy(t => t.Table, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new PurgeRefusedException($"Table {twice.Key} is classified more than once for the purge. Nothing was deleted.");
        var tables = await ReadStringsAsync(cmd, "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name", null, ct);
        var classified = classification.Select(t => t.Table).ToHashSet(StringComparer.Ordinal);
        if (tables.FirstOrDefault(t => !classified.Contains(t)) is { } unclassified)
            throw new PurgeRefusedException($"Table {unclassified} is not classified for the purge (deleted, kept or no tenant data). Nothing was deleted.");
        if (classification.FirstOrDefault(t => !tables.Contains(t.Table)) is { } missing)
            throw new PurgeRefusedException($"Table {missing.Table} is classified for the purge but does not exist. Nothing was deleted.");
    }

    /// <summary>Refuses unless every deleted table counts 0 rows for the tenant.</summary>
    private static async Task RequireDeletedAsync(SqliteCommand cmd, IReadOnlyList<PurgeTable> classification, string tenantId, List<string> tokenIds, CancellationToken ct)
    {
        foreach (var t in classification.Where(t => t.Fate == PurgeTableFate.Deleted))
        {
            cmd.Parameters.Clear();
            if (t.TenantColumn is { } column)
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM {t.Table} WHERE {column}=@t";
                cmd.Parameters.AddWithValue("@t", tenantId);
            }
            else
            {
                if (tokenIds.Count == 0) continue;
                cmd.CommandText = $"SELECT COUNT(*) FROM token_activity WHERE token_id IN ({string.Join(",", tokenIds.Select((_, i) => "@k" + i))})";
                for (var i = 0; i < tokenIds.Count; i++) cmd.Parameters.AddWithValue("@k" + i, tokenIds[i]);
            }
            if ((long)(await cmd.ExecuteScalarAsync(ct))! is var left and > 0)
                throw new PurgeRefusedException($"Table {t.Table} still holds {left} row(s) for tenant {tenantId} after the purge. Nothing was deleted.");
        }
    }

    /// <summary>The meta row holding the last UTC day a purge nudge was sent (yyyy-MM-dd as UTF-8). No migration: meta exists since migration 2.</summary>
    internal const string NudgeDayKey = "purge_nudge_day";

    public async Task<bool> TryClaimNudgeDayAsync(DateOnly day, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        // One statement: inserted, or changed from another day, is a claim (1 row); the same day already stored changes nothing (0 rows).
        cmd.CommandText = """
            INSERT INTO meta(key, value) VALUES(@k, @d)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value WHERE meta.value IS NOT excluded.value
            """;
        cmd.Parameters.AddWithValue("@k", NudgeDayKey);
        cmd.Parameters.AddWithValue("@d", System.Text.Encoding.UTF8.GetBytes(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static async Task<List<string>> ReadStringsAsync(SqliteCommand cmd, string sql, string? tenantId, CancellationToken ct)
    {
        cmd.Parameters.Clear();
        cmd.CommandText = sql;
        if (tenantId is not null) cmd.Parameters.AddWithValue("@t", tenantId);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    private static async Task<int> ExecAsync(SqliteCommand cmd, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        cmd.Parameters.Clear();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Format(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset? Time(SqliteDataReader r, int i) =>
        !r.IsDBNull(i) && DateTimeOffset.TryParse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;
}
