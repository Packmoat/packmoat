using System.Globalization;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure;

/// <summary>
/// The feed setup scratch on SQLite (migration 26, feed_setup_scratch). Each call is one statement on the row's whole key (tenant, person,
/// kind): a put is an insert that becomes an update of that row, so two copies writing one key leave the later write. A read returns the
/// row only while it has not expired. Times are UTC "O" text, which sorts as it reads.
/// </summary>
public sealed class SqliteFeedSetupScratch : IFeedSetupScratch
{
    private readonly SqliteDatabase _db;
    private readonly IClock _clock;

    public SqliteFeedSetupScratch(SqliteDatabase db, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task PutAsync(string tenantId, Guid personId, string kind, string payload, TimeSpan life, CancellationToken ct = default)
    {
        FeedSetupScratchRules.RequirePut(tenantId, personId, kind, payload, life);
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO feed_setup_scratch(tenant_id, person_oid, kind, payload, expires_at) VALUES(@t, @p, @k, @payload, @expires)
            ON CONFLICT(tenant_id, person_oid, kind) DO UPDATE SET payload = excluded.payload, expires_at = excluded.expires_at
            """;
        cmd.Parameters.AddWithValue("@t", tenantId);
        cmd.Parameters.AddWithValue("@p", FeedSetupScratchRules.Person(personId));
        cmd.Parameters.AddWithValue("@k", kind);
        cmd.Parameters.AddWithValue("@payload", payload);
        cmd.Parameters.AddWithValue("@expires", Format(_clock.UtcNow + life));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetAsync(string tenantId, Guid personId, string kind, CancellationToken ct = default)
    {
        FeedSetupScratchRules.RequireKey(tenantId, personId, kind);
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload FROM feed_setup_scratch WHERE tenant_id = @t AND person_oid = @p AND kind = @k AND expires_at > @now";
        cmd.Parameters.AddWithValue("@t", tenantId);
        cmd.Parameters.AddWithValue("@p", FeedSetupScratchRules.Person(personId));
        cmd.Parameters.AddWithValue("@k", kind);
        cmd.Parameters.AddWithValue("@now", Format(_clock.UtcNow));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task DeleteAsync(string tenantId, Guid personId, string kind, CancellationToken ct = default)
    {
        FeedSetupScratchRules.RequireKey(tenantId, personId, kind);
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM feed_setup_scratch WHERE tenant_id = @t AND person_oid = @p AND kind = @k";
        cmd.Parameters.AddWithValue("@t", tenantId);
        cmd.Parameters.AddWithValue("@p", FeedSetupScratchRules.Person(personId));
        cmd.Parameters.AddWithValue("@k", kind);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM feed_setup_scratch WHERE expires_at <= @now";
        cmd.Parameters.AddWithValue("@now", Format(now));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Format(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
