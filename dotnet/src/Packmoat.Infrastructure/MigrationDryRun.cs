using Microsoft.Data.Sqlite;

namespace Packmoat.Infrastructure;

/// <summary>
/// The pre-deploy dry run of the migrations (#196, <c>packmoat-admin db check-migrations FILE</c>). It never opens the live database:
/// it refuses a file that resolves to it, verifies the file, copies it into a private temporary directory, runs this build's migration
/// runner (the core, then every module's track) on the copy, and compares the copy with a fresh database this build makes. The source
/// file is only read. Both temporary databases are deleted. The report holds names and counts only, never a row's contents.
/// </summary>
public static class MigrationDryRun
{
    /// <summary>One track: its version before and after (null: no table), and what the run did.</summary>
    public sealed record TrackLine(string Name, int? Before, int? After, string Verdict);

    /// <summary>
    /// One table's row count before and after (null: no such table). A table the migrations created and left empty (none before, 0
    /// after, #232 M1) changed no row, so it counts as equal; one they created with rows in it, or dropped, does not.
    /// </summary>
    public sealed record RowLine(string Table, long? Before, long? After)
    {
        public bool Equal => Before == After || NewAndEmpty;
        /// <summary>The migrations created this table and put no row in it.</summary>
        public bool NewAndEmpty => Before is null && After == 0;
    }

    /// <param name="SchemaDifferences">Objects that differ from a fresh build in structure or in SQL beyond whitespace, by type and name.</param>
    /// <param name="WhitespaceOnly">Objects whose stored SQL differs from a fresh build's in whitespace only (reported, not a difference).</param>
    /// <param name="Refusal">The runner's refusal, or null when it ran.</param>
    public sealed record Report(SqliteBackup.Verification Source, IReadOnlyList<TrackLine> Tracks, IReadOnlyList<RowLine> Rows,
        IReadOnlyList<string> SchemaDifferences, IReadOnlyList<string> WhitespaceOnly, string? Refusal)
    {
        /// <summary>The runner ran, no table's row count changed and the schema equals a fresh build's.</summary>
        public bool Ok => Source.Ok && Refusal is null && Rows.All(r => r.Equal) && SchemaDifferences.Count == 0;
    }

    /// <summary>Runs the dry run on a copy of <paramref name="file"/>. Throws <see cref="ArgumentException"/> when the file is the live database.</summary>
    public static async Task<Report> RunAsync(string file, string liveDatabase, IReadOnlyList<MigrationTrack> modules, AesGcmSecretProtector? key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modules);
        if (SamePath(file, liveDatabase))
            throw new ArgumentException($"{file} is the live database (PACKMOAT_DB). Refusing: the dry run never opens it. Take a backup and check that.");
        var source = await SqliteBackup.VerifyAsync(file, ct, key);
        if (!source.Ok) return new Report(source, [], [], [], [], "the file does not verify: " + source.Detail);

        var dir = Directory.CreateTempSubdirectory(TempPrefix);   // mode 700: the copy holds customer data
        SqliteDatabase? copy = null, fresh = null;
        try
        {
            var copyPath = Path.Combine(dir.FullName, "copy.db");
            await CopyAsync(file, copyPath, ct);
            copy = new SqliteDatabase(copyPath);
            var declared = modules.Prepend(SqliteDatabase.Core).ToList();
            var (versionsBefore, rowsBefore) = await ReadAsync(copy, declared, ct);

            string? refusal = null;
            IReadOnlyList<TrackOutcome> outcomes = [];
            try { outcomes = await copy.MigrateAsync(modules, ct); }
            catch (Exception ex) when (ex is SchemaTooNewException or SchemaTrackException or SchemaBaselineException or SqliteException or ArgumentException)
            {
                refusal = ex.Message;
            }
            var (versionsAfter, rowsAfter) = await ReadAsync(copy, declared, ct);

            int Order(string name) { var i = declared.FindIndex(t => t.Name == name); return i < 0 ? int.MaxValue : i; }
            var tracks = versionsBefore.Keys.Union(versionsAfter.Keys).OrderBy(Order).ThenBy(n => n, StringComparer.Ordinal)
                .Select(name =>
                {
                    versionsBefore.TryGetValue(name, out var b);
                    versionsAfter.TryGetValue(name, out var a);
                    var outcome = outcomes.FirstOrDefault(o => o.Name == name);
                    var verdict = declared.All(t => t.Name != name) ? "not declared by this build"
                        : refusal is not null ? "not run (refused)"
                        : outcome is { Adopted: true } ? $"adopted: versions 1 to {a} recorded as applied, nothing run"
                        : b == a ? "unchanged"
                        : b is null ? $"created and migrated to v{a}"
                        : $"migrated v{b} to v{a}";
                    return new TrackLine(name, b, a, verdict);
                }).ToList();
            var rows = rowsBefore.Keys.Union(rowsAfter.Keys).Order(StringComparer.Ordinal)
                .Select(t => new RowLine(t, rowsBefore.TryGetValue(t, out var b) ? b : null, rowsAfter.TryGetValue(t, out var a) ? a : null)).ToList();
            if (refusal is not null) return new Report(source, tracks, rows, [], [], refusal);

            fresh = new SqliteDatabase(Path.Combine(dir.FullName, "fresh.db"));
            await fresh.MigrateAsync(modules, ct);
            var (differences, whitespace) = await CompareAsync(copy, fresh, ct);
            return new Report(source, tracks, rows, differences, whitespace, null);
        }
        finally
        {
            copy?.ReleaseConnections();
            fresh?.ReleaseConnections();
            dir.Delete(recursive: true);   // a failure here is an error the operator sees, not a copy left behind quietly
        }
    }

    /// <summary>The name prefix of each run's own temporary directory (tests check by content, not by this name, that no new temporary
    /// directory is left holding the copy, the fresh database or the source's rows).</summary>
    internal const string TempPrefix = "packmoat-check-";

    /// <summary>Copies through SQLite's backup API from a read-only connection: the source is never written, and its write-ahead log is included.</summary>
    private static async Task CopyAsync(string file, string destination, CancellationToken ct)
    {
        await using var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await from.OpenAsync(ct);
        await using var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        await to.OpenAsync(ct);
        from.BackupDatabase(to);
    }

    /// <summary>Each track's version (declared ones and any other track table) and each other table's row count.</summary>
    private static async Task<(Dictionary<string, int?> Versions, Dictionary<string, long> Rows)> ReadAsync(SqliteDatabase db, IReadOnlyList<MigrationTrack> declared, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var versions = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var t in declared) versions[t.Name] = await SqliteDatabase.VersionAsync(c, t.Table, ct);
        foreach (var table in await SqliteDatabase.TrackTablesAsync(c, ct))
        {
            var name = table[..^"_schema_migrations".Length];
            if (!versions.ContainsKey(name)) versions[name] = await SqliteDatabase.VersionAsync(c, table, ct);
        }
        var trackTables = versions.Keys.Select(MigrationTrack.TableFor).ToHashSet(StringComparer.Ordinal);
        var tables = new List<string>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) tables.Add(r.GetString(0));
        }
        var rows = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables.Where(t => !trackTables.Contains(t)))
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
            rows[table] = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        return (versions, rows);
    }

    /// <summary>The copy against a fresh build, object by object (type and name only in the result).</summary>
    private static async Task<(List<string> Differences, List<string> WhitespaceOnly)> CompareAsync(SqliteDatabase copy, SqliteDatabase fresh, CancellationToken ct)
    {
        SqliteSchemaSnapshot a, b;
        await using (var c = await copy.OpenAsync(ct)) a = await SqliteSchemaSnapshot.ReadAsync(c, ct);
        await using (var c = await fresh.OpenAsync(ct)) b = await SqliteSchemaSnapshot.ReadAsync(c, ct);
        var mine = a.Objects.ToDictionary(o => o.Key, StringComparer.Ordinal);
        var theirs = b.Objects.ToDictionary(o => o.Key, StringComparer.Ordinal);
        var differences = new List<string>();
        var whitespace = new List<string>();
        foreach (var key in mine.Keys.Union(theirs.Keys).Order(StringComparer.Ordinal))
        {
            if (!theirs.TryGetValue(key, out var f)) { differences.Add($"{key}: only in the checked database"); continue; }
            if (!mine.TryGetValue(key, out var m)) { differences.Add($"{key}: only in a fresh build"); continue; }
            if (!string.Equals(m.Sql, f.Sql, StringComparison.Ordinal) || !m.Structure.SequenceEqual(f.Structure, StringComparer.Ordinal))
                differences.Add($"{key}: differs from a fresh build");
            else if (!string.Equals(m.RawSql, f.RawSql, StringComparison.Ordinal))
                whitespace.Add(key);
        }
        return (differences, whitespace);
    }

    /// <summary>True when both paths name the same file once every symbolic link on the way is followed.</summary>
    internal static bool SamePath(string a, string b) =>
        string.Equals(RealPath(a), RealPath(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>The full path with every symbolic link (of the file and of each directory above it) followed.</summary>
    internal static string RealPath(string path, int depth = 0)
    {
        if (depth > 40) throw new IOException($"Too many symbolic links resolving {path}.");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is { } target)
                current = RealPath(Path.IsPathRooted(target) ? target : Path.Combine(Path.GetDirectoryName(current)!, target), depth + 1);
        }
        return current;
    }
}
