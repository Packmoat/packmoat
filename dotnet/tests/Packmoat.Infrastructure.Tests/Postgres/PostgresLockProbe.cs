using Npgsql;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// What the race tests read of other sessions' locks: pg_locks and pg_stat_activity, from a session of the test's own database,
/// polled and never slept for. Moved here from the audit batch's race test when the decision-record chunks' needed it too.
/// </summary>
public static class PostgresLockProbe
{
    /// <summary>Another session of this test's database: not the probing one, and not one of another test's database.</summary>
    public const string Others = "l.pid <> pg_catalog.pg_backend_pid() AND l.pid IN (SELECT pid FROM pg_catalog.pg_stat_activity WHERE datname = pg_catalog.current_database())";

    /// <summary>The advisory lock lock_tenant_id takes for <paramref name="id"/>: the two-key form, the schema's key and hashtext(id).</summary>
    public static string TenantLock(string id) =>
        $"l.locktype = 'advisory' AND l.objsubid = 2 AND l.classid::bigint = {PostgresCoreMigrations.TenantIdLockKey} " +
        $"AND l.objid::bigint = (pg_catalog.hashtext('{id}')::bigint & 4294967295)";

    /// <summary>
    /// Runs <paramref name="sql"/>, a count, on <paramref name="c"/> (in <paramref name="tx"/>, or none). The session's snapshot of
    /// pg_stat_activity is dropped first (pg_stat_clear_snapshot): inside a transaction PostgreSQL reads that view once and keeps it to the
    /// transaction's end, so a poll from a holder's transaction (<see cref="Others"/> reads it) would never see a session that connected
    /// after its first read.
    /// </summary>
    public static async Task<long> CountAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string sql)
    {
        await using (var clear = new NpgsqlCommand("SELECT pg_catalog.pg_stat_clear_snapshot()", c, tx)) await clear.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Polls <paramref name="sql"/> until it reaches <paramref name="count"/>, or one of <paramref name="running"/> ends; up to 30 s.</summary>
    public static async Task WaitUntilAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, long count, params Task[] running)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!running.Any(r => r.IsCompleted) && await CountAsync(c, tx, sql) < count)
        {
            Assert.True(DateTime.UtcNow < deadline, "the writers neither waited for the lock held nor finished");
            await Task.Delay(10);
        }
    }
}
