using Microsoft.Data.Sqlite;

namespace Packmoat.Infrastructure;

/// <summary>Consistent online backups (the SQLite backup API, safe while the server runs) and restore with verification.</summary>
public static class SqliteBackup
{
    public enum KeyStatus { NotChecked, Matches, Mismatch, NotRecorded }

    /// <param name="SchemaVersion">The core's schema version.</param>
    /// <param name="Tracks">Each module's migration track in the file (#196), by name, with its version; null or empty for none.</param>
    public sealed record Verification(bool Ok, string Detail, int SchemaVersion, long Tenants, long Tokens, long AuditEvents, KeyStatus Key = KeyStatus.NotChecked,
        IReadOnlyList<(string Track, int Version)>? Tracks = null);

    internal static bool KeyMatches(AesGcmSecretProtector protector, byte[] stored)
    {
        try { return protector.Unprotect(stored, "key_check") == SqliteTenantStore.KeyCheckPlaintext; }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }

    /// <summary>Copies the live database to <paramref name="destination"/>, then verifies the copy. The copy is only for the owner.</summary>
    public static async Task<Verification> BackupAsync(SqliteDatabase source, string destination, bool overwrite, CancellationToken ct = default, AesGcmSecretProtector? protector = null)
    {
        var full = Path.GetFullPath(destination);
        if (File.Exists(full) && !overwrite) throw new IOException($"{full} already exists. Choose another name or pass --force.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (File.Exists(full)) File.Delete(full);

        await using (var from = await source.OpenAsync(ct))
        await using (var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = full, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            await to.OpenAsync(ct);
            from.BackupDatabase(to);
            // A single self-contained file: no -wal or -shm companions to leave behind or forget.
            await using var mode = to.CreateCommand();
            mode.CommandText = "PRAGMA journal_mode=DELETE";
            await mode.ExecuteNonQueryAsync(ct);
        }
        // The destination connection above has Pooling=false, so disposing it closed the file: no process-wide pool clear is needed here
        // (and clearing every pool would pull connections out from under other threads).
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var check = await VerifyAsync(full, ct, protector);
        if (!check.Ok) { File.Delete(full); throw new IOException("The backup failed verification and was deleted: " + check.Detail); }
        return check;
    }

    /// <summary>Opens a file read-only and checks integrity and basic shape. Does not need the master key.</summary>
    public static async Task<Verification> VerifyAsync(string path, CancellationToken ct = default, AesGcmSecretProtector? protector = null)
    {
        if (!File.Exists(path)) return new Verification(false, "file not found", 0, 0, 0, 0);
        try
        {
            await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await c.OpenAsync(ct);
            var integrity = (string)(await Scalar(c, "PRAGMA integrity_check", ct))!;
            if (integrity != "ok") return new Verification(false, "integrity_check: " + integrity, 0, 0, 0, 0);
            var version = Convert.ToInt32(await Scalar(c, "SELECT COALESCE(MAX(version),0) FROM schema_migrations", ct));
            if (version == 0) return new Verification(false, "no schema migrations recorded: not a Packmoat database", 0, 0, 0, 0);
            var tracks = new List<(string, int)>();
            foreach (var table in await SqliteDatabase.TrackTablesAsync(c, ct))
                tracks.Add((table[..^"_schema_migrations".Length], (await SqliteDatabase.VersionAsync(c, table, ct)) ?? 0));
            var tenants = Convert.ToInt64(await Scalar(c, "SELECT COUNT(*) FROM tenants", ct));
            var tokens = Convert.ToInt64(await Scalar(c, "SELECT COUNT(*) FROM tokens", ct));
            var audit = Convert.ToInt64(await Scalar(c, "SELECT COUNT(*) FROM audit_events", ct));

            var key = KeyStatus.NotChecked;
            if (protector is not null)
            {
                var hasMeta = Convert.ToInt64(await Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='meta'", ct)) > 0;
                var stored = hasMeta ? await Scalar(c, "SELECT value FROM meta WHERE key='key_check'", ct) as byte[] : null;
                key = stored is null ? KeyStatus.NotRecorded : KeyMatches(protector, stored) ? KeyStatus.Matches : KeyStatus.Mismatch;
                if (key == KeyStatus.Mismatch)
                    return new Verification(false, "the master key in use does not match this database: its customer identities would not decrypt", version, tenants, tokens, audit, key, tracks);
            }
            return new Verification(true, "ok", version, tenants, tokens, audit, key, tracks);
        }
        catch (SqliteException ex) { return new Verification(false, ex.Message, 0, 0, 0, 0); }
    }

    /// <summary>
    /// Replaces the database at <paramref name="targetPath"/> with a verified backup. The server must be stopped.
    /// An existing database is kept next to it with a timestamp, never deleted.
    /// </summary>
    public static async Task<Verification> RestoreAsync(string backupPath, string targetPath, bool replaceExisting, DateTimeOffset now, CancellationToken ct = default, AesGcmSecretProtector? protector = null)
    {
        var check = await VerifyAsync(backupPath, ct, protector);
        if (!check.Ok) throw new IOException("Refusing to restore: the backup does not verify: " + check.Detail);

        var target = Path.GetFullPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            if (!replaceExisting) throw new IOException($"{target} already exists. Stop the server and pass --force to replace it (the old file is kept).");
            await CheckpointAsync(target, ct);   // fold committed-but-uncheckpointed writes into the file we are about to keep

            // Keep the old database complete: its -wal and -shm companions move with it, under the matching name.
            var kept = $"{target}.before-restore-{now:yyyyMMddHHmmss}";
            File.Move(target, kept);
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(target + suffix)) File.Move(target + suffix, kept + suffix);
        }
        foreach (var sidecar in new[] { target + "-wal", target + "-shm" }) if (File.Exists(sidecar)) File.Delete(sidecar); // only strays of a database that did not exist

        File.Copy(backupPath, target);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return await VerifyAsync(target, ct);
    }

    /// <summary>Merges the write-ahead log into the main file. Fails clearly if another process still has the database open.</summary>
    private static async Task CheckpointAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            await c.OpenAsync(ct);
            await using var busy = c.CreateCommand();
            busy.CommandText = "PRAGMA busy_timeout=2000";
            await busy.ExecuteNonQueryAsync(ct);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct) && r.GetInt32(0) != 0) throw new IOException("The database is in use by another process. Stop the server before restoring.");
        }
        catch (SqliteException ex)
        {
            throw new IOException("Could not checkpoint the existing database (is the server still running?): " + ex.Message);
        }
    }

    private static async Task<object?> Scalar(SqliteConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(ct);
    }
}
