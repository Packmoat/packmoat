using System.Security.Cryptography;
using Npgsql;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The cookie protection keys on PostgreSQL, as SQLite's: each row's XML is encrypted with the master key under its own
/// context, "cookie-key:" and the row's id, so a blob copied onto another row does not decrypt, and a row that does not decrypt makes
/// <see cref="GetAllAsync"/> throw: a key is never skipped. The one-time import is one transaction that takes the
/// <see cref="PostgresLock.CookieKeys"/> lock first, where SQLite's takes BEGIN IMMEDIATE, so of several servers importing at once one
/// counts an empty table and imports, and the rest wait for it, then count its keys and import nothing. No catch of a database error and
/// no SAVEPOINT. Times are timestamptz, in UTC to the microsecond (<see cref="PostgresTime"/>). PACKMOAT_DB_ENGINE=postgres selects it.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresCookieKeyStore : ICookieKeyStore
{
    private readonly PostgresDatabase _db;
    private readonly IClock _clock;
    private readonly AesGcmSecretProtector _protector;

    public PostgresCookieKeyStore(PostgresDatabase db, IClock clock, AesGcmSecretProtector protector)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    /// <summary>
    /// The encryption context of the row <paramref name="id"/>: the same as SQLite's (this adapter may not name a SQLite type, so it is
    /// written again; a test holds the two equal), so a key moved between the engines decrypts.
    /// </summary>
    public static string Context(string id) => "cookie-key:" + id;

    public async Task<IReadOnlyList<CookieKey>> GetAllAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        return await ReadAsync(c, null, ct);
    }

    /// <summary>Every row, decrypted, oldest first; one that does not decrypt throws.</summary>
    private async Task<List<CookieKey>> ReadAsync(NpgsqlConnection c, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var rows = new List<(string Id, string Name, byte[] Blob)>();
        await using (var cmd = new NpgsqlCommand("SELECT id, friendly_name, xml_encrypted FROM cookie_keys ORDER BY created, id", c, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) rows.Add((r.GetString(0), r.GetString(1), r.GetFieldValue<byte[]>(2)));
        }
        var keys = new List<CookieKey>(rows.Count);
        foreach (var (id, name, blob) in rows)
        {
            string xml;
            try { xml = _protector.Unprotect(blob, Context(id)); }
            catch (CryptographicException e)
            {
                // Never skipped: a key ring missing a key would quietly end sessions, or hide a tampered table.
                throw new CryptographicException($"The cookie key {id} in the database cannot be read with this master key.", e);
            }
            keys.Add(new CookieKey(name, xml));
        }
        return keys;
    }

    public async Task AddAsync(string friendlyName, string xml, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await InsertAsync(c, null, new CookieKey(friendlyName, xml), Now(), ct);
    }

    public async Task<int> ImportIfEmptyAsync(IReadOnlyList<CookieKey> keys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) return 0;
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        // The import's lock, held to the transaction's end: a second importer waits here, then (read committed) counts this one's keys.
        await PostgresLocks.TakeAsync(c, tx, PostgresLock.CookieKeys, ct);
        await using (var count = new NpgsqlCommand("SELECT COUNT(*) FROM cookie_keys", c, tx))
        {
            if ((long)(await count.ExecuteScalarAsync(ct))! > 0) return 0;   // disposing the transaction rolls it back: nothing is written
        }
        var now = Now();
        foreach (var key in keys) await InsertAsync(c, tx, key, now, ct);
        await tx.CommitAsync(ct);
        return keys.Count;
    }

    public async Task<bool> AddUnlessAnyAsync(CookieKey key, Func<CookieKey, bool> usable, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(usable);
        await using var c = await _db.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        // The import's lock, held to the transaction's end: a second server waits here, then (read committed) reads this one's key.
        await PostgresLocks.TakeAsync(c, tx, PostgresLock.CookieKeys, ct);
        if ((await ReadAsync(c, tx, ct)).Any(usable)) return false;   // disposing the transaction rolls it back: nothing is written
        await InsertAsync(c, tx, key, Now(), ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private DateTimeOffset Now() => PostgresTime.ToDatabase(_clock.UtcNow);

    private async Task InsertAsync(NpgsqlConnection c, NpgsqlTransaction? tx, CookieKey key, DateTimeOffset created, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key.FriendlyName);
        ArgumentException.ThrowIfNullOrEmpty(key.Xml);
        var id = Guid.NewGuid().ToString("D");
        await using var cmd = new NpgsqlCommand("INSERT INTO cookie_keys(id, friendly_name, created, xml_encrypted) VALUES(@id, @name, @created, @xml)", c, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("name", key.FriendlyName);
        cmd.Parameters.AddWithValue("created", created);
        cmd.Parameters.AddWithValue("xml", _protector.Protect(key.Xml, Context(id)));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
