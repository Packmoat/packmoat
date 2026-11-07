using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure;

/// <summary>
/// Token use by day, in the <c>token_activity(token_id, day)</c> table. A row holds a token id and a UTC date and nothing about the
/// caller: no address, no user agent, no label. Days are kept for <see cref="AuditPolicy.TokenActivityRetentionDays"/> (#136).
/// </summary>
public sealed class SqliteTokenActivity : ITokenActivityLog, IActiveDeveloperQuery, ITokenActivityRetention
{
    private readonly SqliteDatabase _db;
    private readonly IClock _clock;

    public SqliteTokenActivity(SqliteDatabase db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async ValueTask RecordAsync(string tokenId, DateOnly day, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO token_activity(token_id, day) VALUES(@id,@d)";
        cmd.Parameters.AddWithValue("@id", tokenId);
        cmd.Parameters.AddWithValue("@d", Format(day));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ActiveDevelopers> GetAsync(string tenantId, int days, CancellationToken ct)
    {
        if (days < 1) throw new ArgumentOutOfRangeException(nameof(days));
        var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
        var from = today.AddDays(-(days - 1));   // N days including today

        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT t.id, t.label, MAX(a.day)
            FROM token_activity a JOIN tokens t ON t.id = a.token_id
            WHERE t.tenant_id=@t AND t.kind='developer' AND t.revoked_at IS NULL AND a.day >= @from AND a.day <= @to
            GROUP BY t.id, t.label
            ORDER BY MAX(a.day) DESC, t.label, t.id
            """;
        cmd.Parameters.AddWithValue("@t", tenantId);
        cmd.Parameters.AddWithValue("@from", Format(from));
        cmd.Parameters.AddWithValue("@to", Format(today));
        var list = new List<ActiveToken>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ActiveToken(r.GetString(0), r.GetString(1), DateOnly.ParseExact(r.GetString(2), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
        return new ActiveDevelopers(tenantId, days, list.Count, list);
    }

    public async Task<int> PruneAsync(DateOnly keepFrom, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM token_activity WHERE day < @d";
        cmd.Parameters.AddWithValue("@d", Format(keepFrom));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Format(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
