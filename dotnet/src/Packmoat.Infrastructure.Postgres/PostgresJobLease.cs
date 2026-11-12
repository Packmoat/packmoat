using Npgsql;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The job lease on PostgreSQL, as SQLite's: taking it is one statement, an insert that becomes an update only when the
/// stored lease has run out or is the caller's own. Two copies racing for it cannot both win: the job's key serialises them, and the loser's
/// ON CONFLICT re-reads the winner's committed row, which its WHERE then refuses, so it changes no row. Times come from the clock given
/// (the app's), as timestamptz in UTC to the microsecond (<see cref="PostgresTime"/>). PACKMOAT_DB_ENGINE=postgres selects it.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresJobLease : IJobLease
{
    private readonly PostgresDatabase _db;
    private readonly IClock _clock;

    public PostgresJobLease(PostgresDatabase db, IClock clock)
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
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO job_leases(job, holder, expires_at) VALUES(@job, @me, @until)
            ON CONFLICT(job) DO UPDATE SET holder = excluded.holder, expires_at = excluded.expires_at
            WHERE job_leases.expires_at < @now OR job_leases.holder = @me
            """, c);
        cmd.Parameters.AddWithValue("job", job);
        cmd.Parameters.AddWithValue("me", holder);
        cmd.Parameters.AddWithValue("until", PostgresTime.ToDatabase(now + ttl));
        cmd.Parameters.AddWithValue("now", PostgresTime.ToDatabase(now));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> ReleaseAsync(string job, string holder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(job);
        ArgumentException.ThrowIfNullOrEmpty(holder);
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("UPDATE job_leases SET expires_at = @now WHERE job = @job AND holder = @me", c);
        cmd.Parameters.AddWithValue("job", job);
        cmd.Parameters.AddWithValue("me", holder);
        cmd.Parameters.AddWithValue("now", PostgresTime.ToDatabase(_clock.UtcNow));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
