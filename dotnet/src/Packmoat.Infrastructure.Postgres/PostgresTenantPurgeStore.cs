using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Packmoat.Application;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The tenant purge on PostgreSQL, as SQLite's purge store does it, with its SQL written again for PostgreSQL. The core's
/// tables are <see cref="CorePurgeTables.Tables"/> (SQLite's list without sqlite_sequence, which PostgreSQL does not have); a module's
/// are its <see cref="IPurgeContributor"/>'s. The purge is one transaction, read committed, never retried, with no catch inside it and no
/// SAVEPOINT. Where SQLite's BEGIN IMMEDIATE takes the write lock before the first read, this takes, before anything else:
/// <list type="number">
/// <item><c>lock_tenant_id(id, true)</c>, the tenant id's lock exclusively: every late audit event or decision record for
/// the tenant (their triggers take it shared, and the batches take it before any row, <see cref="PostgresTenantLocks"/>) waits until the
/// purge has committed, and is then dropped, as the purge record is there;</item>
/// <item>the tenant's row FOR UPDATE: every other writer of the tenant waits (the tenant store's and the token cap's FOR NO KEY UPDATE, and
/// the foreign-key check of any child row's insert), and then finds no tenant. None is missed: SQLite's single writer, the same;</item>
/// <item>the tenant's tokens FOR UPDATE: token_activity's foreign key points at tokens, not tenants, so the tenant's row does not
/// hold back a token's activity note. With its token held, a note's foreign-key check (FOR KEY SHARE on the token) waits until the purge
/// commits and is then refused, as the token is gone; a note already written but not committed holds its token, so the purge waits for it
/// and then deletes it. No note lands between the purge's DELETE token_activity and its DELETE tokens, which would fail the purge with a
/// foreign-key violation;</item>
/// <item>the re-check, then the two guards and the deletes, as SQLite's.</item>
/// </list>
/// A deadlock (40P01) the server breaks by ending the purge is not retried: it is the one database error caught
/// here, outside the transaction, once the server has rolled it back, and it becomes a refusal: "Nothing was deleted; run the purge
/// again." Times are timestamptz, in UTC to the microsecond (<see cref="PostgresTime"/>). PACKMOAT_DB_ENGINE=postgres selects it.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresTenantPurgeStore : IPurgeSummaryStore
{
    /// <summary>The delete for each of the core's deleted tables before the contributors', in order (SQLite's). <c>@t</c> is the tenant id.</summary>
    private static readonly (string Table, string Sql)[] ChildDeletes =
    [
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
    ];

    /// <summary>What the purge never reaches, the core's part (the contributors' follow), as SQLite's.</summary>
    private static readonly string[] CoreNotReached = ["the access log (90 days)", "backups"];

    /// <summary>The refusal a deadlock becomes: nothing was written, and the same purge may simply be run again.</summary>
    internal static string DeadlockRefusal(string tenantId) =>
        $"Tenant {tenantId} was being written by something else at the same moment, and the database stopped the purge to let it finish. Nothing was deleted; run the purge again.";

    private readonly PostgresDatabase _db;
    private readonly IPurgeRecheck? _recheck;
    private readonly IReadOnlyList<IPurgeContributor> _contributors;

    /// <param name="recheck">Who is due, re-checked inside the purge's transaction; null: nobody is listed and every purge is refused.</param>
    /// <param name="contributors">The modules' tables in the purge (required: empty for none).</param>
    public PostgresTenantPurgeStore(PostgresDatabase db, IPurgeRecheck? recheck, IReadOnlyList<IPurgeContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(contributors);
        _db = db;
        _recheck = recheck;
        _contributors = contributors;
    }

    public IPurgeEligibility? Eligibility => _recheck;

    /// <summary>Every table's classification: the core's, then each contributor's.</summary>
    public IReadOnlyList<PurgeTable> Classification => CorePurgeTables.Tables.Concat(_contributors.SelectMany(c => c.Tables)).ToList();

    /// <summary>The tables a purge keeps, for the operator's summary: the operator's evidence first, the contributors', the purge record last.</summary>
    public IReadOnlyList<string> Kept =>
        CorePurgeTables.Tables.Where(t => t.Fate == PurgeTableFate.Kept && t.Table != "tenant_purges").Select(t => t.Table)
            .Concat(_contributors.SelectMany(c => c.Tables).Where(t => t.Fate == PurgeTableFate.Kept).Select(t => t.Table))
            .Append("tenant_purges").ToList();

    /// <summary>What the purge never reaches, for the operator's summary: the core's, then each contributor's.</summary>
    public IReadOnlyList<string> NotReached => CoreNotReached.Concat(_contributors.SelectMany(c => c.NotReached)).ToList();

    public async Task<TenantPurgeRecord?> FindPurgeAsync(string tenantId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT purged_at, reason, actor FROM tenant_purges WHERE tenant_id=@t", c);
        cmd.Parameters.AddWithValue("t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new TenantPurgeRecord(tenantId, PostgresTime.FromDatabase(r.GetFieldValue<DateTime>(0)), r.GetString(1), r.GetString(2));
    }

    public async Task<IReadOnlyList<PurgedTable>> PurgeAsync(PurgeCandidate candidate, TenantPurgeRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(record);
        if (record.TenantId != candidate.TenantId) throw new ArgumentException("The purge record names another tenant.", nameof(record));
        var recheck = _recheck ?? throw new PurgeRefusedException(TenantPurgeCommands.NoEligibility);
        try
        {
            return await PurgeOnceAsync(recheck, candidate, record, ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.DeadlockDetected)
        {
            // Outside the transaction: PurgeOnceAsync's connection and transaction are disposed, and the server rolled it back.
            throw new PurgeRefusedException(DeadlockRefusal(candidate.TenantId));
        }
    }

    /// <summary>The purge's one transaction (see <see cref="PostgresTenantPurgeStore"/>). Any failure rolls it back and is thrown.</summary>
    private async Task<IReadOnlyList<PurgedTable>> PurgeOnceAsync(IPurgeRecheck recheck, PurgeCandidate candidate, TenantPurgeRecord record, CancellationToken ct)
    {
        var tenantId = candidate.TenantId;
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);

        // 1. The tenant id's lock, exclusively, before anything else: no late audit event or decision record for it lands from here on.
        await PostgresTenantLocks.TakeExclusiveAsync(c, tx, tenantId, ct);

        // 2. The tenant's row, FOR UPDATE: every other writer of the tenant waits, then finds it gone. No row: nothing to purge.
        await using (var lockTenant = new NpgsqlCommand("SELECT 1 FROM tenants WHERE id=@t FOR UPDATE", c, tx))
        {
            lockTenant.Parameters.AddWithValue("t", tenantId);
            if (await lockTenant.ExecuteScalarAsync(ct) is null) throw new PurgeRefusedException($"Tenant {tenantId} no longer exists. Nothing was deleted.");
        }

        // 2b. The tenant's tokens, FOR UPDATE: no token activity note is written for them from here on (its foreign key waits).
        await ExecAsync(c, tx, "SELECT 1 FROM tokens WHERE tenant_id=@t FOR UPDATE", tenantId, ct);

        // 3. The re-check: what was decided on must still hold, read where no other writer of the tenant can change it. Anything changed
        // refuses; disposing the transaction rolls back.
        if (!await recheck.StillDueAsync(tx, candidate, record.PurgedAt, ct))
            throw new PurgeRefusedException($"Tenant {tenantId} changed while it was being checked{(recheck.Rechecked is { Length: > 0 } what ? $" ({what})" : "")}. Nothing was deleted; run the purge again.");

        // Guard 1: every table is classified, once. A table nobody named could hold the tenant's data and outlive the purge.
        var classification = Classification;
        await RequireClassifiedAsync(c, tx, classification, ct);

        // Token activity is found through the tenant's tokens: their ids, before they go, for the count afterwards.
        var tokenIds = new List<string>();
        await using (var tokens = new NpgsqlCommand("SELECT id FROM tokens WHERE tenant_id=@t", c, tx))
        {
            tokens.Parameters.AddWithValue("t", tenantId);
            await using var r = await tokens.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) tokenIds.Add(r.GetString(0));
        }

        var counts = new List<PurgedTable>();
        foreach (var (table, sql) in ChildDeletes)
            counts.Add(new PurgedTable(table, await ExecAsync(c, tx, sql, tenantId, ct)));
        foreach (var contributor in _contributors)
            counts.AddRange(await contributor.DeleteAsync(tx, tenantId, ct));
        // The tenant's feed-check lease (job_leases holds no tenant data but this row's name, AzureSetupState.LeaseJob): not counted.
        await ExecAsync(c, tx, CorePurgeTables.FeedCheckLeaseDelete, tenantId, ct);
        counts.Add(new PurgedTable("tenants", await ExecAsync(c, tx, "DELETE FROM tenants WHERE id=@t", tenantId, ct)));

        // Recorded only once the tenant row is gone (a trigger insists), with 'purging' set: the one moment its audit rows may be deleted.
        await using (var insert = new NpgsqlCommand("INSERT INTO tenant_purges(tenant_id, purged_at, reason, actor, purging) VALUES(@t,@at,@r,@a,1)", c, tx))
        {
            insert.Parameters.AddWithValue("t", tenantId);
            insert.Parameters.AddWithValue("at", PostgresTime.ToDatabase(record.PurgedAt));
            insert.Parameters.AddWithValue("r", record.Reason);
            insert.Parameters.AddWithValue("a", record.Actor);
            await insert.ExecuteNonQueryAsync(ct);
        }
        counts.Add(new PurgedTable("audit_events", await ExecAsync(c, tx, "DELETE FROM audit_events WHERE tenant_id=@t", tenantId, ct)));
        if (await ExecAsync(c, tx, "UPDATE tenant_purges SET purging=0 WHERE tenant_id=@t AND purging=1", tenantId, ct) != 1)
            throw new PurgeRefusedException("The purge record could not be closed. Nothing was deleted.");

        // Guard 2: every table declared deleted holds no row for the tenant now.
        await RequireDeletedAsync(c, tx, classification, tenantId, tokenIds, ct);

        await tx.CommitAsync(ct);
        return counts;
    }

    /// <summary>
    /// Refuses unless every table in the schema is classified exactly once (by the core or one contributor), every classified table
    /// exists, every name is a plain identifier, and every deleted table names its tenant column (token_activity, the core's, is counted
    /// through its tokens instead), as SQLite's. The schema's tables are its ordinary and partitioned tables (pg_class relkind r and p) in
    /// the connection's current schema, the one every table of the core's and the modules' migrations is made in.
    /// </summary>
    private static async Task RequireClassifiedAsync(NpgsqlConnection c, NpgsqlTransaction tx, IReadOnlyList<PurgeTable> classification, CancellationToken ct)
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
        var tables = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT relname::text FROM pg_catalog.pg_class WHERE relnamespace = (SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = pg_catalog.current_schema()) " +
            "AND relkind IN ('r', 'p') ORDER BY relname", c, tx))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) tables.Add(r.GetString(0));
        }
        var classified = classification.Select(t => t.Table).ToHashSet(StringComparer.Ordinal);
        if (tables.FirstOrDefault(t => !classified.Contains(t)) is { } unclassified)
            throw new PurgeRefusedException($"Table {unclassified} is not classified for the purge (deleted, kept or no tenant data). Nothing was deleted.");
        if (classification.FirstOrDefault(t => !tables.Contains(t.Table, StringComparer.Ordinal)) is { } missing)
            throw new PurgeRefusedException($"Table {missing.Table} is classified for the purge but does not exist. Nothing was deleted.");
    }

    /// <summary>Refuses unless every deleted table counts 0 rows for the tenant.</summary>
    private static async Task RequireDeletedAsync(NpgsqlConnection c, NpgsqlTransaction tx, IReadOnlyList<PurgeTable> classification, string tenantId,
        List<string> tokenIds, CancellationToken ct)
    {
        foreach (var t in classification.Where(t => t.Fate == PurgeTableFate.Deleted))
        {
            await using var cmd = new NpgsqlCommand { Connection = c, Transaction = tx };
            if (t.TenantColumn is { } column)
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM {t.Table} WHERE {column}=@t";
                cmd.Parameters.AddWithValue("t", tenantId);
            }
            else
            {
                if (tokenIds.Count == 0) continue;
                cmd.CommandText = "SELECT COUNT(*) FROM token_activity WHERE token_id = ANY(@k)";
                cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = tokenIds.ToArray() });
            }
            if ((long)(await cmd.ExecuteScalarAsync(ct))! is var left and > 0)
                throw new PurgeRefusedException($"Table {t.Table} still holds {left} row(s) for tenant {tenantId} after the purge. Nothing was deleted.");
        }
    }

    /// <summary>The meta row holding the last UTC day a purge nudge was sent (yyyy-MM-dd as UTF-8), as SQLite's.</summary>
    internal const string NudgeDayKey = "purge_nudge_day";

    public async Task<bool> TryClaimNudgeDayAsync(DateOnly day, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        // One statement: inserted, or changed from another day, is a claim (1 row); the same day already stored changes nothing (0 rows).
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO meta(key, value) VALUES(@k, @d)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value WHERE meta.value IS DISTINCT FROM excluded.value
            """, c);
        cmd.Parameters.AddWithValue("k", NudgeDayKey);
        cmd.Parameters.AddWithValue("d", System.Text.Encoding.UTF8.GetBytes(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Runs <paramref name="sql"/> with <c>@t</c> as <paramref name="tenantId"/> on <paramref name="tx"/>; the rows it changed.</summary>
    private static async Task<int> ExecAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, string tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        cmd.Parameters.AddWithValue("t", tenantId);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
