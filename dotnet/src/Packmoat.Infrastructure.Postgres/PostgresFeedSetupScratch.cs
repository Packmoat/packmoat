using Npgsql;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The feed setup scratch on PostgreSQL (core migration 26), as SQLite's: each call is one statement on the row's whole key (tenant,
/// person, kind); a put is an insert that becomes an update of that row, so two copies writing one key leave the later write. Times come
/// from the clock given, as timestamptz in UTC to the microsecond (<see cref="PostgresTime"/>).
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresFeedSetupScratch : IFeedSetupScratch
{
    private readonly PostgresDatabase _db;
    private readonly IClock _clock;

    public PostgresFeedSetupScratch(PostgresDatabase db, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task PutAsync(string tenantId, Guid personId, string kind, string payload, TimeSpan life, CancellationToken ct = default)
    {
        FeedSetupScratchRules.RequirePut(tenantId, personId, kind, payload, life);
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO feed_setup_scratch(tenant_id, person_oid, kind, payload, expires_at) VALUES(@t, @p, @k, @payload, @expires)
            ON CONFLICT(tenant_id, person_oid, kind) DO UPDATE SET payload = excluded.payload, expires_at = excluded.expires_at
            """, c);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("p", FeedSetupScratchRules.Person(personId));
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("payload", payload);
        cmd.Parameters.AddWithValue("expires", PostgresTime.ToDatabase(_clock.UtcNow + life));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetAsync(string tenantId, Guid personId, string kind, CancellationToken ct = default)
    {
        FeedSetupScratchRules.RequireKey(tenantId, personId, kind);
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT payload FROM feed_setup_scratch WHERE tenant_id = @t AND person_oid = @p AND kind = @k AND expires_at > @now", c);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("p", FeedSetupScratchRules.Person(personId));
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("now", PostgresTime.ToDatabase(_clock.UtcNow));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task DeleteAsync(string tenantId, Guid personId, string kind, CancellationToken ct = default)
    {
        FeedSetupScratchRules.RequireKey(tenantId, personId, kind);
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM feed_setup_scratch WHERE tenant_id = @t AND person_oid = @p AND kind = @k", c);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("p", FeedSetupScratchRules.Person(personId));
        cmd.Parameters.AddWithValue("k", kind);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM feed_setup_scratch WHERE expires_at <= @now", c);
        cmd.Parameters.AddWithValue("now", PostgresTime.ToDatabase(now));
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
