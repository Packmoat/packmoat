using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure;

/// <summary>
/// The cookie protection keys on SQLite (migration 23, cookie_keys; #232 B1). Each row's XML is encrypted with the master key under its
/// own context, "cookie-key:" and the row's id, so a blob copied onto another row does not decrypt. A row that does not decrypt (another
/// master key, a tampered or copied blob) makes <see cref="GetAllAsync"/> throw: a key is never skipped.
/// </summary>
public sealed class SqliteCookieKeyStore : ICookieKeyStore
{
    private readonly SqliteDatabase _db;
    private readonly IClock _clock;
    private readonly AesGcmSecretProtector _protector;

    public SqliteCookieKeyStore(SqliteDatabase db, IClock clock, AesGcmSecretProtector protector)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    /// <summary>The encryption context of the row <paramref name="id"/>.</summary>
    public static string Context(string id) => "cookie-key:" + id;

    public async Task<IReadOnlyList<CookieKey>> GetAllAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, friendly_name, xml_encrypted FROM cookie_keys ORDER BY created, id";
        var keys = new List<CookieKey>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetString(0);
            // SQLite's BLOB column takes any value: anything but bytes is as unreadable as a wrong key, and named so.
            if (r.GetValue(2) is not byte[] blob) throw new CryptographicException($"The cookie key {id} in the database is not stored as encrypted bytes.");
            string xml;
            try { xml = _protector.Unprotect(blob, Context(id)); }
            catch (CryptographicException e)
            {
                // Never skipped: a key ring missing a key would quietly end sessions, or hide a tampered table.
                throw new CryptographicException($"The cookie key {id} in the database cannot be read with this master key.", e);
            }
            keys.Add(new CookieKey(r.GetString(1), xml));
        }
        return keys;
    }

    public async Task AddAsync(string friendlyName, string xml, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        await InsertAsync(cmd, new CookieKey(friendlyName, xml), Now(), ct);
    }

    public async Task<int> ImportIfEmptyAsync(IReadOnlyList<CookieKey> keys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) return 0;
        await using var c = await _db.OpenAsync(ct);
        // BEGIN IMMEDIATE: the count and the inserts happen under the write lock, so two servers importing at once leave one set.
        // (Postgres needs LOCK TABLE or an advisory lock for the same; the contract's race test covers it.)
        await using var tx = c.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM cookie_keys";
        if ((long)(await cmd.ExecuteScalarAsync(ct))! > 0) return 0;   // disposing the transaction rolls it back: nothing is written
        var now = Now();
        foreach (var key in keys)
        {
            cmd.Parameters.Clear();
            await InsertAsync(cmd, key, now, ct);
        }
        await tx.CommitAsync(ct);
        return keys.Count;
    }

    private string Now() => _clock.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private async Task InsertAsync(SqliteCommand cmd, CookieKey key, string created, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key.FriendlyName);
        ArgumentException.ThrowIfNullOrEmpty(key.Xml);
        var id = Guid.NewGuid().ToString("D");
        cmd.CommandText = "INSERT INTO cookie_keys(id, friendly_name, created, xml_encrypted) VALUES(@id, @name, @created, @xml)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", key.FriendlyName);
        cmd.Parameters.AddWithValue("@created", created);
        cmd.Parameters.AddWithValue("@xml", _protector.Protect(key.Xml, Context(id)));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
