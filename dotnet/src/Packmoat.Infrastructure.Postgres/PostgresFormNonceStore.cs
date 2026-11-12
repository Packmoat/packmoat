using Npgsql;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The form nonces on PostgreSQL (core migration 25), as SQLite's. An issue is one transaction under
/// <see cref="PostgresLock.FormNonces"/>: the key's oldest nonces in the scope beyond the cap are deleted, then the new one is inserted, so
/// two copies issuing for one key at once cannot leave it over its cap. A consume is one statement, <c>DELETE … RETURNING</c>: of two
/// copies consuming one nonce, the second waits for the first's row lock and then finds the row gone, so only one gets it back. Times
/// come from the clock given, as timestamptz in UTC to the microsecond (<see cref="PostgresTime"/>).
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresFormNonceStore : IFormNonceStore
{
    private readonly PostgresDatabase _db;
    private readonly IClock _clock;

    public PostgresFormNonceStore(PostgresDatabase db, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task IssueAsync(string scope, string nonce, string key, TimeSpan life, int maxPerKey, CancellationToken ct = default)
    {
        FormNonceRules.RequireIssue(scope, nonce, key, life, maxPerKey);
        var now = PostgresTime.ToDatabase(_clock.UtcNow);
        var subject = FormNonceRules.Hash(key);
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await PostgresLocks.TakeAsync(c, tx, PostgresLock.FormNonces, ct);   // before the trim: a second issue waits, then sees this one's row
        await using (var trim = new NpgsqlCommand(
            """
            DELETE FROM form_nonces WHERE scope = @scope AND subject = @subject AND nonce_hash NOT IN (
                SELECT nonce_hash FROM form_nonces WHERE scope = @scope AND subject = @subject ORDER BY expires_at DESC, nonce_hash DESC LIMIT @keep)
            """, c, tx))
        {
            trim.Parameters.AddWithValue("scope", scope);
            trim.Parameters.AddWithValue("subject", subject);
            trim.Parameters.AddWithValue("keep", (long)maxPerKey - 1);
            await trim.ExecuteNonQueryAsync(ct);
        }
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO form_nonces(scope, nonce_hash, subject, expires_at) VALUES(@scope, @hash, @subject, @expires)", c, tx))
        {
            insert.Parameters.AddWithValue("scope", scope);
            insert.Parameters.AddWithValue("hash", FormNonceRules.Hash(nonce));
            insert.Parameters.AddWithValue("subject", subject);
            insert.Parameters.AddWithValue("expires", PostgresTime.ToDatabase(now + life));
            await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<bool> ConsumeAsync(string scope, string nonce, string key, CancellationToken ct = default)
    {
        FormNonceRules.RequireConsume(scope, nonce, key);
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM form_nonces WHERE scope = @scope AND nonce_hash = @hash RETURNING subject, expires_at", c);
        cmd.Parameters.AddWithValue("scope", scope);
        cmd.Parameters.AddWithValue("hash", FormNonceRules.Hash(nonce));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return false;
        var expires = PostgresTime.FromDatabase(r.GetFieldValue<DateTime>(1));
        return FormNonceRules.Accepts(r.GetString(0), expires, key, PostgresTime.ToDatabase(_clock.UtcNow));
    }

    public async Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM form_nonces WHERE expires_at <= @now", c);
        cmd.Parameters.AddWithValue("now", PostgresTime.ToDatabase(now));
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
