using Npgsql;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The shared rate counter on PostgreSQL (core migration 24), as SQLite's: a hit is one statement, an insert that becomes an
/// update starting a new window or adding one to the running window's hits (capped one above the limit), returning the window's end and
/// hits. Two copies counting one key at once cannot both take the last place: the key serialises them, and the second's ON CONFLICT
/// re-reads the first's committed row before it adds to it. Times come from the clock given, as timestamptz in UTC to the microsecond
/// (<see cref="PostgresTime"/>).
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresRateCounter : IRateCounter
{
    private readonly PostgresDatabase _db;
    private readonly IClock _clock;

    public PostgresRateCounter(PostgresDatabase db, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<RateDecision> HitAsync(string scope, string key, int max, TimeSpan window, CancellationToken ct = default)
    {
        RateCounters.Require(scope, key, max, window);
        var now = PostgresTime.ToDatabase(_clock.UtcNow);
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO rate_counters(scope, subject, started_at, ends_at, hits) VALUES(@scope, @subject, @now, @end, 1)
            ON CONFLICT(scope, subject) DO UPDATE SET
                started_at = CASE WHEN rate_counters.ends_at <= @now OR rate_counters.started_at > @end THEN excluded.started_at ELSE rate_counters.started_at END,
                ends_at = CASE WHEN rate_counters.ends_at <= @now OR rate_counters.started_at > @end THEN excluded.ends_at ELSE rate_counters.ends_at END,
                hits = CASE WHEN rate_counters.ends_at <= @now OR rate_counters.started_at > @end THEN 1 ELSE LEAST(rate_counters.hits + 1, @cap) END
            RETURNING ends_at, hits
            """, c);
        cmd.Parameters.AddWithValue("scope", scope);
        cmd.Parameters.AddWithValue("subject", RateCounters.Subject(key));
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("end", PostgresTime.ToDatabase(now + window));
        cmd.Parameters.AddWithValue("cap", (long)max + 1);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("The rate counter's write returned no row.");
        var end = PostgresTime.FromDatabase(r.GetFieldValue<DateTime>(0));
        return RateCounters.Decide(r.GetInt64(1), max, end, now);
    }

    public async Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM rate_counters WHERE ends_at <= @now", c);
        cmd.Parameters.AddWithValue("now", PostgresTime.ToDatabase(now));
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
