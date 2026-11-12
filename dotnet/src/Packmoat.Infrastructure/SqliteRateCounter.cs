using System.Globalization;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure;

/// <summary>
/// The shared rate counter on SQLite (migration 24, rate_counters). A hit is one statement: an insert that, when the key already
/// has a row, becomes an update that starts a new window (the old one ended, or started more than a window ahead of now: a clock set back)
/// or adds one to the running window's hits, capped one above the limit. The statement returns the window's end and hits, which decide the
/// hit (<see cref="RateCounters.Decide"/>). The database serialises the writes, so two copies counting at once cannot both take the last
/// place. Times are UTC "O" text, which sorts as it reads.
/// </summary>
public sealed class SqliteRateCounter : IRateCounter
{
    private readonly SqliteDatabase _db;
    private readonly IClock _clock;

    public SqliteRateCounter(SqliteDatabase db, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<RateDecision> HitAsync(string scope, string key, int max, TimeSpan window, CancellationToken ct = default)
    {
        RateCounters.Require(scope, key, max, window);
        var now = _clock.UtcNow.ToUniversalTime();
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rate_counters(scope, subject, started_at, ends_at, hits) VALUES(@scope, @subject, @now, @end, 1)
            ON CONFLICT(scope, subject) DO UPDATE SET
                started_at = CASE WHEN rate_counters.ends_at <= @now OR rate_counters.started_at > @end THEN excluded.started_at ELSE rate_counters.started_at END,
                ends_at = CASE WHEN rate_counters.ends_at <= @now OR rate_counters.started_at > @end THEN excluded.ends_at ELSE rate_counters.ends_at END,
                hits = CASE WHEN rate_counters.ends_at <= @now OR rate_counters.started_at > @end THEN 1 ELSE MIN(rate_counters.hits + 1, @cap) END
            RETURNING ends_at, hits
            """;
        cmd.Parameters.AddWithValue("@scope", scope);
        cmd.Parameters.AddWithValue("@subject", RateCounters.Subject(key));
        cmd.Parameters.AddWithValue("@now", Format(now));
        cmd.Parameters.AddWithValue("@end", Format(now + window));
        cmd.Parameters.AddWithValue("@cap", (long)max + 1);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("The rate counter's write returned no row.");
        var end = DateTimeOffset.Parse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return RateCounters.Decide(r.GetInt64(1), max, end, now);
    }

    public async Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM rate_counters WHERE ends_at <= @now";
        cmd.Parameters.AddWithValue("@now", Format(now));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Format(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
