using Microsoft.Data.Sqlite;
using Packmoat.Application;

namespace Packmoat.Infrastructure;

/// <summary>The master-key check on SQLite: the marker is the meta row key_check, encrypted with the master key.</summary>
public sealed class SqliteMasterKeyCheck : IMasterKeyCheck
{
    private const string KeyCheckName = MasterKeyMarker.Key;

    private readonly SqliteDatabase _db;
    private readonly AesGcmSecretProtector _protector;

    public SqliteMasterKeyCheck(SqliteDatabase db, AesGcmSecretProtector protector)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    /// <summary>
    /// Makes sure the master key in use is the one this database was made with. The first run records an encrypted marker;
    /// every later run must be able to read it. A wrong key stops the server instead of failing later, one customer at a time.
    /// <para>
    /// A database with a marker is only read, so a later start never takes the write lock. With none, the marker is written only if there
    /// is still none (<c>INSERT OR IGNORE</c>), then whatever marker the database holds is read back and must decrypt (#295): so of two
    /// servers starting at once on a new database with different keys, exactly one marker is kept and the other server stops, whichever
    /// wrote first. The connection is in autocommit (WAL, busy_timeout), so each statement reads the newest committed state.
    /// </para>
    /// </summary>
    public async Task EnsureAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct);
        if (await ReadMarkerAsync(c, ct) is not { } stored)
        {
            await using (var write = c.CreateCommand())
            {
                write.CommandText = "INSERT OR IGNORE INTO meta(key, value) VALUES(@k, @v)";
                write.Parameters.AddWithValue("@k", KeyCheckName);
                write.Parameters.AddWithValue("@v", MasterKeyMarker.Create(_protector));
                await write.ExecuteNonQueryAsync(ct);
            }
            stored = await ReadMarkerAsync(c, ct);
        }
        if (stored is null || !SqliteBackup.KeyMatches(_protector, stored))
            throw new InvalidOperationException(MasterKeyMarker.WrongKeyMessage);
    }

    /// <summary>
    /// True when the master key in use reads this database's key marker (#233: the readiness check). Read only: unlike
    /// <see cref="EnsureAsync"/> it never records a marker, and a database with none is false (the start always records one).
    /// </summary>
    public async Task<bool> MatchesAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenReadOnlyAsync(ct);
        await using var read = c.CreateCommand();
        read.CommandText = "SELECT value FROM meta WHERE key=@k";
        read.Parameters.AddWithValue("@k", KeyCheckName);
        return await read.ExecuteScalarAsync(ct) is byte[] stored && SqliteBackup.KeyMatches(_protector, stored);
    }

    private static async Task<byte[]?> ReadMarkerAsync(SqliteConnection c, CancellationToken ct)
    {
        await using var read = c.CreateCommand();
        read.CommandText = "SELECT value FROM meta WHERE key=@k";
        read.Parameters.AddWithValue("@k", KeyCheckName);
        return await read.ExecuteScalarAsync(ct) as byte[];
    }
}
