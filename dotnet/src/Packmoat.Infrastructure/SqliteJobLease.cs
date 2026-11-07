using System.Globalization;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure;

/// <summary>
/// The job lease on SQLite (migration 23, job_leases). Taking it is one statement, an insert that becomes an update only when the stored
/// lease has run out or is the caller's own, so two copies racing for it cannot both win: the database serialises the writes, and the
/// loser's update matches no row. The same statement runs on Postgres. Times are UTC "O" text, which sorts as it reads.
/// </summary>
public sealed class SqliteJobLease : IJobLease
{
    private readonly SqliteDatabase _db;
    private readonly IClock _clock;

    public SqliteJobLease(SqliteDatabase db, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<bool> TryAcquireAsync(string job, string holder, TimeSpan ttl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(job);
        ArgumentException.ThrowIfNullOrEmpty(holder);
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl), "A lease must run for some time.");
        var now = _clock.UtcNow;
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO job_leases(job, holder, expires_at) VALUES(@job, @me, @until)
            ON CONFLICT(job) DO UPDATE SET holder = excluded.holder, expires_at = excluded.expires_at
            WHERE job_leases.expires_at < @now OR job_leases.holder = @me
            """;
        cmd.Parameters.AddWithValue("@job", job);
        cmd.Parameters.AddWithValue("@me", holder);
        cmd.Parameters.AddWithValue("@until", Format(now + ttl));
        cmd.Parameters.AddWithValue("@now", Format(now));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> ReleaseAsync(string job, string holder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(job);
        ArgumentException.ThrowIfNullOrEmpty(holder);
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE job_leases SET expires_at = @now WHERE job = @job AND holder = @me";
        cmd.Parameters.AddWithValue("@job", job);
        cmd.Parameters.AddWithValue("@me", holder);
        cmd.Parameters.AddWithValue("@now", Format(_clock.UtcNow));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static string Format(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
