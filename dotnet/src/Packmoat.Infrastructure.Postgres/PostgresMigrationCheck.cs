using Npgsql;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The pre-deploy check of the migrations on PostgreSQL (<c>packmoat-admin db check-migrations</c>), the twin of SQLite's
/// <see cref="MigrationDryRun"/>. PostgreSQL's DDL is transactional, so it needs no copy: in ONE transaction on the database itself it
/// reads each track's version and each table's row count, runs this build's migrations (the core, then every module's track), reads them
/// again, builds the same tracks from nothing in a scratch schema of its own beside the database's, compares the two schemas object by
/// object, and then ROLLS BACK, whatever happened: the migration, the scratch schema and everything else in it are undone, and nothing is
/// ever committed. The report holds names and counts only, never a row's contents.
/// <para>Run it as the owner role: it runs the migrations' DDL, and makes the scratch schema (CREATE on the database). It holds the
/// migration lock from the start, and, when versions are pending, every lock their DDL takes, ACCESS EXCLUSIVE for an ALTER TABLE, which
/// stops that table's readers and writers, from the statement that takes it until the rollback: through the migration, the row counts
/// and both schemas' snapshots and the fresh build. With versions pending it is a maintenance-window step, as the migration itself is.
/// With none pending it takes no table's lock and runs alongside the server. Its lock_timeout is short (<see cref="LockTimeout"/>, 5
/// seconds, not the migration's 60): a lock it cannot have in time is its refusal.</para>
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public static class PostgresMigrationCheck
{
    /// <param name="Tracks">Each track: its version before and after, and what the run did.</param>
    /// <param name="Rows">Each table's row count before and after (version tables left out).</param>
    /// <param name="SchemaDifferences">Objects that differ from a fresh build's, by kind and name.</param>
    /// <param name="Refusal">The runner's refusal, or null when it ran.</param>
    public sealed record Report(IReadOnlyList<MigrationDryRun.TrackLine> Tracks, IReadOnlyList<MigrationDryRun.RowLine> Rows,
        IReadOnlyList<string> SchemaDifferences, string? Refusal)
    {
        /// <summary>Why the migrated schema could not be compared with a fresh build, or null when it was.</summary>
        public string? CompareFailure { get; init; }

        /// <summary>The runner ran, no table's row count changed and the schema equals a fresh build's.</summary>
        public bool Ok => Refusal is null && CompareFailure is null && Rows.All(r => r.Equal) && SchemaDifferences.Count == 0;
    }

    /// <summary>Checks this build's migrations (the core's and <paramref name="modules"/>') on <paramref name="db"/>, and rolls back.</summary>
    public static Task<Report> RunAsync(PostgresDatabase db, IReadOnlyList<MigrationTrack> modules, CancellationToken cancellationToken = default) =>
        RunAsync(db, CoreSchema.Track, modules, cancellationToken);

    /// <summary>As <see cref="RunAsync(PostgresDatabase, IReadOnlyList{MigrationTrack}, CancellationToken)"/>, with <paramref name="core"/> for the core's track (tests).</summary>
    internal static async Task<Report> RunAsync(PostgresDatabase db, MigrationTrack core, IReadOnlyList<MigrationTrack> modules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(modules);
        await using var connection = await db.OpenAsync(cancellationToken);
        // Never committed: rolled back below once the report is made, or by its disposal if anything throws first. The migration, the
        // scratch schema and every lock go with it.
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        var report = await CheckInAsync(db, connection, tx, core, modules, cancellationToken);
        await tx.RollbackAsync(cancellationToken);
        return report;
    }

    private static async Task<Report> CheckInAsync(PostgresDatabase db, NpgsqlConnection connection, NpgsqlTransaction tx, MigrationTrack core,
        IReadOnlyList<MigrationTrack> modules, CancellationToken cancellationToken)
    {
        var declared = modules.Prepend(core).ToList();
        var schema = await PostgresDatabase.SchemaAsync(connection, tx, cancellationToken);
        var (versionsBefore, rowsBefore) = await ReadAsync(connection, tx, schema, declared, cancellationToken);

        // A refusal is reported, not thrown: a savepoint lets the reads after it run on what was there before.
        await tx.SaveAsync(BeforeMigration, cancellationToken);
        var refusal = await RefusalOfAsync(() => db.MigrateInAsync(connection, tx, core, modules, cancellationToken, LockTimeout));
        if (refusal is not null) await tx.RollbackAsync(BeforeMigration, cancellationToken);
        var (versionsAfter, rowsAfter) = await ReadAsync(connection, tx, schema, declared, cancellationToken);
        var tracks = Tracks(declared, versionsBefore, versionsAfter, refusal);
        var rows = rowsBefore.Keys.Union(rowsAfter.Keys).Order(StringComparer.Ordinal)
            .Select(t => new MigrationDryRun.RowLine(t, rowsBefore.TryGetValue(t, out var b) ? b : null, rowsAfter.TryGetValue(t, out var a) ? a : null)).ToList();
        if (refusal is not null) return new Report(tracks, rows, [], refusal);

        // The fresh build, in the same transaction, so its scratch schema goes with the rollback. A failure here (no CREATE on the
        // database, a statement that needs what only this database has) is reported, not thrown.
        var migrated = await PostgresSchemaSnapshot.ReadAsync(connection, tx, schema, cancellationToken);
        var fresh = $"packmoat_check_{Guid.NewGuid():N}";
        var built = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var compare = await RefusalOfAsync(async () =>
        {
            await ExecuteAsync(connection, tx, $"CREATE SCHEMA {PostgresDatabase.QuotedSchema(fresh)}", cancellationToken);
            await ExecuteAsync(connection, tx, $"SET LOCAL search_path = {PostgresDatabase.QuotedSchema(fresh)}, pg_catalog, pg_temp", cancellationToken);
            await db.MigrateInAsync(connection, tx, core, modules, cancellationToken, LockTimeout);
            built = await PostgresSchemaSnapshot.ReadAsync(connection, tx, fresh, cancellationToken);
        });
        if (compare is not null)
            return new Report(tracks, rows, [], null) { CompareFailure = "could not build a fresh schema to compare with (the owner role needs CREATE on the database): " + compare };
        var differences = PostgresSchemaSnapshot.Compare(migrated, built);
        differences.AddRange(await DefinersElsewhereAsync(connection, tx, schema, fresh, cancellationToken));
        return new Report(tracks, rows, differences, null);
    }

    /// <summary>
    /// The SECURITY DEFINER functions in every other schema: a fresh build makes no function outside its own schema, so each is
    /// extra, and it runs as its owner for whoever may call it, whatever the schema's own objects say. A plain function runs as its caller
    /// and can do nothing the caller cannot, so other schemas' plain functions (another application's in a shared database) are not
    /// reported. As in <see cref="PostgresRoleChecks"/>, pg_catalog, information_schema and a superuser's extension functions are left out.
    /// </summary>
    private static async Task<List<string>> DefinersElsewhereAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string schema, string fresh, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT f.name FROM (
              SELECT 'function ' || n.nspname::text || '.' || p.proname::text || '(' || pg_catalog.pg_get_function_identity_arguments(p.oid) || ')' AS name
              FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace JOIN pg_catalog.pg_roles w ON w.oid = p.proowner
              WHERE p.prosecdef AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname <> @schema AND n.nspname <> @fresh
                AND NOT (w.rolsuper AND EXISTS (SELECT 1 FROM pg_catalog.pg_depend d
                                                WHERE d.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass AND d.objid = p.oid AND d.deptype = 'e'))) f
            ORDER BY f.name COLLATE "C"
            """, connection, tx);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("fresh", fresh);
        var found = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) found.Add(r.GetString(0) + ": SECURITY DEFINER, outside the schema, only in this database");
        return found;
    }

    /// <summary>
    /// The check's lock_timeout: short, as the check is a look and not the deploy itself. A migration lock or a table
    /// another session holds for longer makes the check a refusal, naming the wait, rather than a 60-second queue of everyone behind it.
    /// </summary>
    internal static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs <paramref name="step"/>; a refusal it meets (one of the migrator's own, a lock not had in time, or any statement PostgreSQL
    /// refuses) comes back as its text, and anything else is thrown. The caller rolls back what the step left aborted.
    /// </summary>
    private static async Task<string?> RefusalOfAsync(Func<Task> step)
    {
        try
        {
            await step();
            return null;
        }
        catch (Exception e) when (e is SchemaTooNewException or SchemaTrackException or SchemaBaselineException or TimeoutException or PostgresException)
        {
            return e is PostgresException p ? $"a statement failed: SQLSTATE {p.SqlState}: {p.MessageText}" : e.Message;
        }
    }

    private const string BeforeMigration = "before_migration";

    private static List<MigrationDryRun.TrackLine> Tracks(List<MigrationTrack> declared, Dictionary<string, int?> before, Dictionary<string, int?> after, string? refusal)
    {
        int Order(string name) { var i = declared.FindIndex(t => t.Name == name); return i < 0 ? int.MaxValue : i; }
        return before.Keys.Union(after.Keys).OrderBy(Order).ThenBy(n => n, StringComparer.Ordinal).Select(name =>
        {
            before.TryGetValue(name, out var b);
            after.TryGetValue(name, out var a);
            var verdict = declared.All(t => t.Name != name) ? "not declared by this build"
                : refusal is not null ? "not run (refused)"
                : b == a ? "unchanged"
                : b is null ? $"created and migrated to v{a}"
                : $"migrated v{b} to v{a}";
            return new MigrationDryRun.TrackLine(name, b, a, verdict);
        }).ToList();
    }

    /// <summary>Each track's version (the declared ones and any other version table) and each other table's row count, in <paramref name="schema"/>.</summary>
    private static async Task<(Dictionary<string, int?> Versions, Dictionary<string, long> Rows)> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        string schema, IReadOnlyList<MigrationTrack> declared, CancellationToken ct)
    {
        var versions = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var t in declared) versions[t.Name] = await PostgresDatabase.VersionAsync(connection, tx, schema, t.Table, ct);
        foreach (var table in await PostgresDatabase.TrackTablesAsync(connection, tx, schema, ct))
        {
            var name = table[..^"_schema_migrations".Length];
            if (!versions.ContainsKey(name)) versions[name] = await PostgresDatabase.VersionAsync(connection, tx, schema, table, ct);
        }
        var tables = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT relname::text FROM pg_catalog.pg_class WHERE relnamespace = (SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = @schema) " +
            "AND relkind IN ('r', 'p') ORDER BY relname COLLATE \"C\"", connection, tx))
        {
            cmd.Parameters.AddWithValue("schema", schema);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) tables.Add(r.GetString(0));
        }
        var rows = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables.Where(t => !PostgresRoleGrants.IsVersionTable(t)))
        {
            await using var count = new NpgsqlCommand($"SELECT COUNT(*) FROM {PostgresDatabase.QuotedSchema(schema)}.{PostgresRoleGrants.Quote(table)}", connection, tx);
            rows[table] = (long)(await count.ExecuteScalarAsync(ct))!;
        }
        return (versions, rows);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
