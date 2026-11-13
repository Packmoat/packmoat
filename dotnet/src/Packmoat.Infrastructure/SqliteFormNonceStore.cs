using System.Globalization;
using Microsoft.Data.Sqlite;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure;

/// <summary>
/// The form nonces on SQLite (migration 25, form_nonces). An issue is one BEGIN IMMEDIATE transaction: the key's oldest nonces in the
/// scope beyond the cap are deleted, then the new one is inserted, so no other writer sees the key over its cap. A consume is one
/// statement, <c>DELETE … RETURNING</c>: the database serialises the writes, so of two copies consuming one nonce only the first gets
/// its row back. Times are UTC "O" text, which sorts as it reads.
/// </summary>
public sealed class SqliteFormNonceStore : IFormNonceStore
{
    private readonly SqliteDatabase _db;
    private readonly IClock _clock;

    public SqliteFormNonceStore(SqliteDatabase db, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task IssueAsync(string scope, string nonce, string key, TimeSpan life, int maxPerKey, CancellationToken ct = default)
    {
        FormNonceRules.RequireIssue(scope, nonce, key, life, maxPerKey);
        var now = _clock.UtcNow.ToUniversalTime();
        var subject = FormNonceRules.Hash(key);
        await using var c = await _db.OpenAsync(ct);
        await using var tx = c.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);   // BEGIN IMMEDIATE: explicit, so a later read ahead of the trim cannot make this a deferred read-then-write (no test can tell: the trim is the first statement)
        await using (var trim = c.CreateCommand())
        {
            trim.Transaction = tx;
            // Keep the key's newest maxPerKey - 1 (by expiry), so with the new one it holds maxPerKey.
            trim.CommandText = """
                DELETE FROM form_nonces WHERE scope = @scope AND subject = @subject AND nonce_hash NOT IN (
                    SELECT nonce_hash FROM form_nonces WHERE scope = @scope AND subject = @subject ORDER BY expires_at DESC, nonce_hash DESC LIMIT @keep)
                """;
            trim.Parameters.AddWithValue("@scope", scope);
            trim.Parameters.AddWithValue("@subject", subject);
            trim.Parameters.AddWithValue("@keep", (long)maxPerKey - 1);
            await trim.ExecuteNonQueryAsync(ct);
        }
        await using (var insert = c.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO form_nonces(scope, nonce_hash, subject, expires_at) VALUES(@scope, @hash, @subject, @expires)";
            insert.Parameters.AddWithValue("@scope", scope);
            insert.Parameters.AddWithValue("@hash", FormNonceRules.Hash(nonce));
            insert.Parameters.AddWithValue("@subject", subject);
            insert.Parameters.AddWithValue("@expires", Format(now + life));
            await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<bool> ConsumeAsync(string scope, string nonce, string key, CancellationToken ct = default)
    {
        FormNonceRules.RequireConsume(scope, nonce, key);
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM form_nonces WHERE scope = @scope AND nonce_hash = @hash RETURNING subject, expires_at";
        cmd.Parameters.AddWithValue("@scope", scope);
        cmd.Parameters.AddWithValue("@hash", FormNonceRules.Hash(nonce));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return false;
        var expires = DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return FormNonceRules.Accepts(r.GetString(0), expires, key, _clock.UtcNow);
    }

    public async Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM form_nonces WHERE expires_at <= @now";
        cmd.Parameters.AddWithValue("@now", Format(now));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Format(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
