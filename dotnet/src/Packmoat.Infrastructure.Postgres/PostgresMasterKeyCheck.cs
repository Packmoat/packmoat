using Npgsql;
using Packmoat.Application;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The master-key check on PostgreSQL: the marker (<see cref="MasterKeyMarker"/>) is the meta row key_check, as on SQLite.
/// PACKMOAT_DB_ENGINE=postgres selects it.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresMasterKeyCheck : IMasterKeyCheck
{
    private readonly PostgresDatabase db;
    private readonly AesGcmSecretProtector protector;

    public PostgresMasterKeyCheck(PostgresDatabase db, AesGcmSecretProtector protector)
    {
        this.db = db ?? throw new ArgumentNullException(nameof(db));
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    /// <summary>
    /// Makes sure the master key in use is the one this database was made with. The marker is written only if there is none
    /// (<c>ON CONFLICT DO NOTHING</c>), then whatever marker the database holds is read back and must decrypt: so of two servers starting
    /// at once on a new database with different keys, exactly one marker is kept and the other server stops, whichever wrote first.
    /// </summary>
    public async Task EnsureAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using (var write = new NpgsqlCommand("INSERT INTO meta(key, value) VALUES(@k, @v) ON CONFLICT (key) DO NOTHING", connection))
        {
            write.Parameters.AddWithValue("k", MasterKeyMarker.Key);
            write.Parameters.AddWithValue("v", MasterKeyMarker.Create(protector));
            await write.ExecuteNonQueryAsync(ct);
        }
        if (await ReadAsync(connection, ct) is not { } stored || !MasterKeyMarker.Matches(protector, stored))
            throw new InvalidOperationException(MasterKeyMarker.WrongKeyMessage);
    }

    /// <summary>
    /// True when the master key in use reads this database's marker (the readiness check). Read only, in a READ ONLY transaction: it never
    /// records a marker, and a database with none is false (the start always records one).
    /// </summary>
    public async Task<bool> MatchesAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, tx)) await readOnly.ExecuteNonQueryAsync(ct);
        var stored = await ReadAsync(connection, ct, tx);
        await tx.RollbackAsync(ct);
        return stored is not null && MasterKeyMarker.Matches(protector, stored);
    }

    private static async Task<byte[]?> ReadAsync(NpgsqlConnection connection, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        await using var read = new NpgsqlCommand("SELECT value FROM meta WHERE key = @k", connection, tx);
        read.Parameters.AddWithValue("k", MasterKeyMarker.Key);
        return await read.ExecuteScalarAsync(ct) as byte[];
    }
}
