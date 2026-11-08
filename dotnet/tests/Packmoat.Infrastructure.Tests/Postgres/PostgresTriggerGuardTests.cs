using Npgsql;
using Packmoat.Infrastructure.Postgres;
using static Packmoat.Infrastructure.Tests.SchemaRulesContract;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// What the schema's rules do on PostgreSQL only (#288 P2, PR 6): the TRUNCATE guards (tests named <c>&lt;guard&gt;__…</c>, one per
/// <see cref="SchemaShape.PostgresOnlyTriggers"/>), the per-tenant advisory lock that keeps two read-committed transactions from each
/// passing a rule the other's uncommitted row breaks, and the retention floor under a session time zone with daylight saving. The races
/// are made, not hoped for: the second transaction is let go only once pg_locks shows it waiting on the first's lock.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public sealed class PostgresTriggerGuardTests
{
    private static PostgresSqlEngine Engine => PostgresSqlEngine.Instance;

    private static string Now => Engine.TimeAgoSql(TimeSpan.Zero);

    private static async Task ExecAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------------------------------------------------------------- TRUNCATE

    private static async Task RefusesTruncateAsync(string table, string insert, string message)
    {
        await using var t = await Engine.CreateAsync();
        await Engine.ExecAsync(t, insert);

        foreach (var sql in new[] { $"TRUNCATE {table}", $"TRUNCATE TABLE {table} RESTART IDENTITY CASCADE" })
        {
            var e = await Assert.ThrowsAsync<PostgresException>(() => Engine.ExecAsync(t, sql));
            Assert.Equal("P0001", e.SqlState);
            Assert.Equal(message, e.MessageText);
        }
        Assert.Equal(1L, await Engine.ScalarAsync(t, $"SELECT COUNT(*) FROM {table}"));
    }

    [PostgresFact]   // mutation: the guard dropped (a TRUNCATE skips the row triggers)
    public Task audit_no_truncate__refuses_truncate() =>
        RefusesTruncateAsync("audit_events", Audit(AuditRow(Now, "kept")), AuditAppendOnly);

    [PostgresFact]   // mutation: the guard dropped
    public Task admin_actions_no_truncate__refuses_truncate() =>
        RefusesTruncateAsync("admin_actions", AdminAction(Now, "kept"), AdminAppendOnly);

    [PostgresFact]   // mutation: the guard dropped
    public Task tenant_purges_no_truncate__refuses_truncate() =>
        RefusesTruncateAsync("tenant_purges", Purge("gone", 0), PurgesKept);

    // ---------------------------------------------------------------- the tenant id lock

    /// <summary>
    /// Waits (up to 30 s) until a session waits on a tenant id's advisory lock in this database, or <paramref name="other"/> has finished
    /// (it did not wait: the caller's assertion then fails). Polls pg_locks; never sleeps in place of a lock.
    /// </summary>
    private static async Task WaitForTenantLockAsync(NpgsqlConnection c, NpgsqlTransaction tx, Task other)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!other.IsCompleted)
        {
            await using var waiting = new NpgsqlCommand(
                $"""
                SELECT COUNT(*) FROM pg_catalog.pg_locks WHERE locktype = 'advisory' AND NOT granted AND classid = {PostgresCoreMigrations.TenantIdLockKey}
                  AND objsubid = 2 AND database = (SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database())
                """, c, tx);
            if ((long)(await waiting.ExecuteScalarAsync())! >= 1) return;
            Assert.True(DateTime.UtcNow < deadline, "the second transaction neither waited on the tenant id's lock nor finished");
            await Task.Delay(10);
        }
    }

    // mutation: the shared lock taken out of audit_purged_tenant or decision_records_purged_tenant (the late row is then written while the
    // purge record is uncommitted, and outlives the purge)
    [PostgresTheory]
    [InlineData("audit_events")]
    [InlineData("decision_records")]
    public async Task A_late_row_while_the_purge_runs_waits_for_it_and_is_then_dropped(string table)
    {
        await using var t = await Engine.CreateAsync();
        var db = ((PostgresTestDb)t).Db;
        var late = table == "audit_events"
            ? Audit(AuditRow(Now, "late", "gone"), AuditRow(Now, "live", "acme"))
            : Decisions(DecisionRow("gone", "late"), DecisionRow("acme", "live"));

        await using var purge = await db.OpenAsync();
        await using var tx = await purge.BeginTransactionAsync();
        await ExecAsync(purge, tx, Purge("gone", 1));
        await ExecAsync(purge, tx, $"DELETE FROM {table} WHERE tenant_id='gone'");

        var write = Task.Run(() => Engine.ExecAsync(t, late));
        await WaitForTenantLockAsync(purge, tx, write);
        Assert.False(write.IsCompleted, "the late write did not wait for the running purge");
        await ExecAsync(purge, tx, "UPDATE tenant_purges SET purging=0 WHERE tenant_id='gone'");
        await tx.CommitAsync();
        await write;

        Assert.Equal(["acme"], await Engine.ColumnAsync(t, $"SELECT tenant_id FROM {table}"));
    }

    // mutation: the shared lock taken out of tenants_no_purged_id or tenants_no_rename_to_purged_id (the tenant is then made while the
    // purge record is uncommitted: both commit, and a purged id is in use)
    [PostgresTheory]
    [InlineData("INSERT")]
    [InlineData("UPDATE OF id")]
    public async Task A_tenant_made_or_renamed_while_its_id_is_being_recorded_purged_waits_and_is_refused(string how)
    {
        await using var t = await Engine.CreateAsync();
        var db = ((PostgresTestDb)t).Db;
        if (how == "UPDATE OF id") await Engine.ExecAsync(t, Tenant("acme"));
        var create = how == "INSERT" ? Tenant("gone") : "UPDATE tenants SET id='gone' WHERE id='acme'";

        await using var purge = await db.OpenAsync();
        await using var tx = await purge.BeginTransactionAsync();
        await ExecAsync(purge, tx, Purge("gone", 1));

        var write = Task.Run(() => Engine.ExecAsync(t, create));
        await WaitForTenantLockAsync(purge, tx, write);
        Assert.False(write.IsCompleted, "the tenant write did not wait for the purge record");
        await ExecAsync(purge, tx, "UPDATE tenant_purges SET purging=0 WHERE tenant_id='gone'");
        await tx.CommitAsync();

        var e = await Assert.ThrowsAsync<PostgresException>(() => write);
        Assert.Equal(PurgedId, e.MessageText);
        Assert.Equal(0L, await Engine.ScalarAsync(t, "SELECT COUNT(*) FROM tenants WHERE id='gone'"));
    }

    // mutation: tenant_purges_only_when_gone's lock taken shared, or not at all (the record is then written while the tenant is being
    // made: both commit)
    [PostgresFact]
    public async Task A_purge_record_written_while_its_tenant_is_being_made_waits_and_is_refused()
    {
        await using var t = await Engine.CreateAsync();
        var db = ((PostgresTestDb)t).Db;

        await using var make = await db.OpenAsync();
        await using var tx = await make.BeginTransactionAsync();
        await ExecAsync(make, tx, Tenant("gone"));

        var record = Task.Run(() => Engine.ExecAsync(t, Purge("gone", 1)));
        await WaitForTenantLockAsync(make, tx, record);
        Assert.False(record.IsCompleted, "the purge record did not wait for the tenant being made");
        await tx.CommitAsync();

        var e = await Assert.ThrowsAsync<PostgresException>(() => record);
        Assert.Equal(PurgedOnce, e.MessageText);
        Assert.Equal(0L, await Engine.ScalarAsync(t, "SELECT COUNT(*) FROM tenant_purges"));
    }

    // ---------------------------------------------------------------- the functions' search_path (#298 review M1)

    // mutation: the functions' path captured from the session ("$user", public: pg_temp is then searched first for tables)
    [PostgresFact]
    public async Task Every_rule_function_searches_its_schema_then_pg_catalog_then_pg_temp()
    {
        await using var t = await Engine.CreateAsync();
        var schema = (await Engine.ColumnAsync(t, "SELECT quote_ident(current_schema())")).Single();
        var paths = await Engine.ColumnAsync(t,
            """
            SELECT p.proname::text || ' ' || COALESCE(array_to_string(p.proconfig, ' | '), 'none') FROM pg_catalog.pg_proc p
            WHERE p.pronamespace = (SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = current_schema()) ORDER BY p.proname COLLATE "C"
            """);

        Assert.Equal(10, paths.Count);
        Assert.All(paths, p => Assert.EndsWith($" search_path={schema}, pg_catalog, pg_temp", p, StringComparison.Ordinal));
        Assert.All(paths, p => Assert.DoesNotContain("$user", p, StringComparison.Ordinal));
    }

    /// <summary>Runs <paramref name="sql"/> on one connection that first makes <paramref name="temporary"/>, a temporary table.</summary>
    private static async Task WithTemporaryTableAsync(IEngineDb t, string temporary, params string[] sql)
    {
        await using var c = await ((PostgresTestDb)t).Db.OpenAsync();
        await ExecAsync(c, null, temporary);
        foreach (var s in sql) await ExecAsync(c, null, s);
    }

    // mutation: the old FROM CURRENT capture (the temporary table then says the tenant's purge is running, and the young row goes)
    [PostgresFact]
    public async Task A_temporary_tenant_purges_cannot_let_a_young_audit_row_be_deleted()
    {
        await using var t = await Engine.CreateAsync();
        await Engine.ExecAsync(t, Audit(AuditRow(Engine.TimeAgoSql(TimeSpan.FromMinutes(1)), "young", "acme")));

        var e = await Assert.ThrowsAsync<PostgresException>(() => WithTemporaryTableAsync(t,
            "CREATE TEMP TABLE tenant_purges(tenant_id text, purging bigint)",
            "INSERT INTO tenant_purges VALUES('acme', 1)",
            "DELETE FROM audit_events WHERE tenant_id='acme'"));

        Assert.Equal(AuditFloor, e.MessageText);
        Assert.Equal(["young"], await Engine.ColumnAsync(t, "SELECT request_id FROM audit_events"));
    }

    // mutation: the old FROM CURRENT capture (the empty temporary table then hides the purge record)
    [PostgresTheory]
    [InlineData("INSERT")]
    [InlineData("UPDATE OF id")]
    public async Task A_temporary_tenant_purges_cannot_let_a_purged_id_be_used_again(string how)
    {
        await using var t = await Engine.CreateAsync();
        await Engine.ExecAsync(t, Purge("gone", 0));
        if (how == "UPDATE OF id") await Engine.ExecAsync(t, Tenant("acme"));

        var e = await Assert.ThrowsAsync<PostgresException>(() => WithTemporaryTableAsync(t, "CREATE TEMP TABLE tenant_purges(tenant_id text)",
            how == "INSERT" ? Tenant("gone") : "UPDATE tenants SET id='gone' WHERE id='acme'"));

        Assert.Equal(PurgedId, e.MessageText);
        Assert.Equal(0L, await Engine.ScalarAsync(t, "SELECT COUNT(*) FROM tenants WHERE id='gone'"));
    }

    // mutation: the old FROM CURRENT capture (an empty temporary tenants then hides the live tenant)
    [PostgresFact]
    public async Task A_temporary_tenants_cannot_let_a_live_tenant_be_recorded_purged()
    {
        await using var t = await Engine.CreateAsync();
        await Engine.ExecAsync(t, Tenant("acme"));

        var e = await Assert.ThrowsAsync<PostgresException>(() => WithTemporaryTableAsync(t, "CREATE TEMP TABLE tenants(id text)", Purge("acme", 1)));

        Assert.Equal(PurgedOnce, e.MessageText);
        Assert.Equal(0L, await Engine.ScalarAsync(t, "SELECT COUNT(*) FROM tenant_purges"));
    }

    // ---------------------------------------------------------------- the floor and the session's time zone

    /// <summary>
    /// A POSIX time zone (UTC, and one hour ahead in summer) whose summer time began 45 days before <paramref name="utcNow"/> and ends 90
    /// days after it, so exactly one change lies in the last 90 days, whatever the date. Julian days 1 to 365, 29 February never counted.
    /// </summary>
    internal static string DaylightSavingZone(DateTime utcNow)
    {
        static int Julian(DateTime d) => DateTime.IsLeapYear(d.Year) && d.Month > 2 ? d.DayOfYear - 1 : d.DayOfYear;
        return $"XST0XDT,J{Julian(utcNow.AddDays(-45))},J{Julian(utcNow.AddDays(90))}";
    }

    // mutation: interval '90 days' for '2160 hours' (a day is then the session's local day, 23 or 25 hours across the change)
    [PostgresFact]
    public async Task The_audit_floor_is_2160_hours_under_a_session_time_zone_with_daylight_saving()
    {
        await using var t = await Engine.CreateAsync();
        await using var c = await ((PostgresTestDb)t).Db.OpenAsync();
        await ExecAsync(c, null, $"SET TIME ZONE '{DaylightSavingZone(DateTime.UtcNow)}'");
        await using (var differs = new NpgsqlCommand("SELECT statement_timestamp() - interval '90 days' <> statement_timestamp() - interval '2160 hours'", c))
            Assert.True((bool)(await differs.ExecuteScalarAsync())!, "the session's time zone has no daylight-saving change in the last 90 days");

        await ExecAsync(c, null, Audit(
            AuditRow("(statement_timestamp() - interval '2159 hours 30 minutes')", "half an hour young"),
            AuditRow("(statement_timestamp() - interval '2160 hours 30 minutes')", "half an hour old")));

        var e = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(c, null, "DELETE FROM audit_events WHERE request_id='half an hour young'"));
        Assert.Equal(AuditFloor, e.MessageText);
        await ExecAsync(c, null, "DELETE FROM audit_events WHERE request_id='half an hour old'");

        Assert.Equal(["half an hour young"], await Engine.ColumnAsync(t, "SELECT request_id FROM audit_events"));
    }
}
