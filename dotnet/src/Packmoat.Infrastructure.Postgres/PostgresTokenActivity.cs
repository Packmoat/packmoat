using Npgsql;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// Token use by day on PostgreSQL, as SQLite's: the <c>token_activity(token_id, day)</c> table holds a token id and a UTC
/// date and nothing about the caller. <c>day</c> is a date column, sent and read as a <see cref="DateOnly"/>, so no session time zone can
/// move it. "Today" is the app's clock, as on SQLite. Each call is one statement, so one transaction; none has a check-then-write rule, so
/// none takes a lock. PACKMOAT_DB_ENGINE=postgres selects it.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresTokenActivity : ITokenActivityLog, IActiveDeveloperQuery, ITokenActivityRetention
{
    private readonly PostgresDatabase _db;
    private readonly IClock _clock;

    public PostgresTokenActivity(PostgresDatabase db, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>SQLite's INSERT OR IGNORE: a token's second note on a day changes nothing. An unknown token is still refused (its foreign key).</summary>
    public async ValueTask RecordAsync(string tokenId, DateOnly day, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("INSERT INTO token_activity(token_id, day) VALUES(@id,@d) ON CONFLICT(token_id, day) DO NOTHING", c);
        cmd.Parameters.AddWithValue("id", tokenId);
        cmd.Parameters.AddWithValue("d", day);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ActiveDevelopers> GetAsync(string tenantId, int days, CancellationToken ct)
    {
        if (days < 1) throw new ArgumentOutOfRangeException(nameof(days));
        var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
        var from = today.AddDays(-(days - 1));   // N days including today

        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT t.id, t.label, MAX(a.day)
            FROM token_activity a JOIN tokens t ON t.id = a.token_id
            WHERE t.tenant_id=@t AND t.kind='developer' AND t.revoked_at IS NULL AND a.day >= @from AND a.day <= @to
            GROUP BY t.id, t.label
            ORDER BY MAX(a.day) DESC, t.label, t.id
            """, c);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", today);
        var list = new List<ActiveToken>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ActiveToken(r.GetString(0), r.GetString(1), r.GetFieldValue<DateOnly>(2)));
        return new ActiveDevelopers(tenantId, days, list.Count, list);
    }

    public async Task<int> PruneAsync(DateOnly keepFrom, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM token_activity WHERE day < @d", c);
        cmd.Parameters.AddWithValue("d", keepFrom);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
