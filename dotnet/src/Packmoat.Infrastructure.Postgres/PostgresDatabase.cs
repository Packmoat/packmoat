using Npgsql;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The core's database on PostgreSQL (#288): one <see cref="NpgsqlDataSource"/> (Npgsql's pool) over a checked
/// <see cref="PostgresConnectionString"/>. Nothing selects it yet: PACKMOAT_DB_ENGINE=postgres is refused, and no project in src/ references
/// this one. No logger factory and no parameter logging are ever configured, so neither a password nor a value reaches a log through Npgsql.
/// But a <c>PostgresException</c>'s message (a constraint's text, say) and Npgsql's Activity events can still carry a parameter's value, even
/// with Include Error Detail off: code whose queries carry a secret or a token must not log a raw exception message.
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public sealed class PostgresDatabase : IRelationalDatabase, ISchemaMigrator, IAsyncDisposable
{
    /// <summary>The one major version this build supports (#288). An older server is refused when a connection opens.</summary>
    public const int SupportedMajorVersion = 17;

    private readonly NpgsqlDataSource source;

    private PostgresDatabase(PostgresConnectionString connectionString, NpgsqlDataSource source)
    {
        ConnectionString = connectionString;
        this.source = source;
    }

    /// <inheritdoc/>
    public DatabaseEngine Engine => DatabaseEngine.Postgres;

    /// <summary>The checked connection string; its <see cref="PostgresConnectionString.ToString"/> never shows the password.</summary>
    public PostgresConnectionString ConnectionString { get; }

    /// <summary>Where this connects, never the password (<see cref="PostgresConnectionString.Describe"/>).</summary>
    public string Describe() => ConnectionString.Describe();

    /// <summary>A database over <paramref name="connectionString"/>. Connects nothing until <see cref="OpenAsync"/>.</summary>
    public static PostgresDatabase Create(PostgresConnectionString connectionString) =>
        new(connectionString, new NpgsqlDataSourceBuilder(connectionString.ForNpgsql).Build());

    /// <summary>An open connection from the pool, once the server is known to be <see cref="SupportedMajorVersion"/> or newer.</summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = await source.OpenConnectionAsync(cancellationToken);
        try
        {
            RequireSupported(connection.PostgreSqlVersion, Describe());
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Throws when <paramref name="server"/> is older than <see cref="SupportedMajorVersion"/>, naming <paramref name="where"/>.</summary>
    public static void RequireSupported(Version server, string where)
    {
        if (server.Major < SupportedMajorVersion)
            throw new NotSupportedException($"The PostgreSQL server at {where} is version {server}; Packmoat needs PostgreSQL {SupportedMajorVersion} or newer.");
    }

    /// <summary>
    /// The key of the migration lock (<c>pg_advisory_xact_lock</c>): "packmoat" in ASCII, as one bigint. Every migrator of every build
    /// takes the same key, so two never run at once on one database; the lock ends with the migration's transaction.
    /// </summary>
    internal const long MigrationLockKey = 0x7061636B6D6F6174;

    private TimeSpan lockTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a migration waits for the migration lock, or for any other lock it needs, before it gives up (SET LOCAL lock_timeout).
    /// At least 1 ms, rounded up to whole milliseconds: lock_timeout 0 means wait forever, so a smaller value is refused.
    /// </summary>
    internal TimeSpan LockTimeout
    {
        get => lockTimeout;
        set => lockTimeout = value >= TimeSpan.FromMilliseconds(1) ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "The migration lock timeout must be at least 1 ms: PostgreSQL reads 0 as wait forever.");
    }

    /// <summary>
    /// Applies the core's migrations (<see cref="CoreSchema.Track"/>), then each of <paramref name="modules"/>' tracks in order, in one
    /// transaction (see <see cref="MigrateAsync(MigrationTrack, IReadOnlyList{MigrationTrack}, CancellationToken)"/>). Nothing calls it yet
    /// outside the tests: PACKMOAT_DB_ENGINE=postgres is refused.
    /// </summary>
    public Task<IReadOnlyList<TrackOutcome>> MigrateAsync(IReadOnlyList<MigrationTrack> modules, CancellationToken cancellationToken = default) =>
        MigrateAsync(CoreSchema.Track, modules, cancellationToken);

    /// <summary>Where the core's (<see cref="CoreSchema.Track"/>) and <paramref name="modules"/>' tracks stand. Read only.</summary>
    public Task<IReadOnlyList<TrackVersion>> TrackVersionsAsync(IReadOnlyList<MigrationTrack> modules, CancellationToken cancellationToken = default) =>
        TrackVersionsAsync(CoreSchema.Track, modules, cancellationToken);

    /// <summary>
    /// Applies <paramref name="core"/>'s migrations, then each of <paramref name="modules"/>' tracks in order, all in ONE transaction: it
    /// sets lock_timeout (<see cref="LockTimeout"/>), takes the migration lock, checks, and runs, so another migrator waits for it and then
    /// finds the work done, and a failure anywhere (a later track's statement too) leaves nothing written. Before anything is written it
    /// refuses what SQLite refuses (<see cref="TrackRules"/>: a track declared twice, a module named core, an undeclared
    /// <c>*_schema_migrations</c> table, a track newer than this build), a track with no PostgreSQL statements, and a module whose
    /// <see cref="TrackBaseline"/> structure is already there with no version table: on PostgreSQL a baseline is never adopted (no older
    /// build ran a module's statements in the core's track), so such a track runs from version 1 only when none of it exists.
    /// </summary>
    internal async Task<IReadOnlyList<TrackOutcome>> MigrateAsync(MigrationTrack core, IReadOnlyList<MigrationTrack> modules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(modules);
        TrackRules.RequireDistinct(modules);
        var tracks = modules.Prepend(core).ToList();
        // Every track has PostgreSQL statements, checked before anything connects: a refusal writes nothing.
        var statements = tracks.Select(t => t.StatementsFor(DatabaseEngine.Postgres)).ToList();

        await using var connection = await OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        // SET LOCAL ends with the transaction. A plain SET would also be undone today, since Npgsql resets a pooled connection's session
        // (DISCARD ALL) when it goes back to the pool, but it would leak into the pool's next user with No Reset On Close, which
        // PostgresConnectionString.Parse refuses for that reason.
        await ExecuteAsync(connection, tx, $"SET LOCAL lock_timeout = '{(long)Math.Ceiling(LockTimeout.TotalMilliseconds)}ms'", cancellationToken);
        try
        {
            await ExecuteAsync(connection, tx, $"SELECT pg_advisory_xact_lock({MigrationLockKey})", cancellationToken);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            throw new TimeoutException($"Another process holds the migration lock on {Describe()}: waited {(long)LockTimeout.TotalSeconds} second(s). " +
                "Refusing to run. Nothing was written.", e);
        }

        // Under the lock, and read committed: every query from here sees what a migrator before this one committed. Every track table is
        // looked for, and named, in this one schema, so a temporary table or a later search_path entry cannot stand in for it.
        var schema = await SchemaAsync(connection, tx, cancellationToken);
        TrackRules.RequireDeclared(core, modules, await TrackTablesAsync(connection, tx, schema, cancellationToken));
        var before = new List<int?>();
        foreach (var track in tracks)
        {
            var version = await VersionAsync(connection, tx, schema, track.Table, cancellationToken);
            TrackRules.RequireNotNewer(track, version);
            RequireNotPartWayToBaseline(track, statements[before.Count], version);
            before.Add(version);
        }
        for (var i = 1; i < tracks.Count; i++)
            if (tracks[i].Baseline is { } baseline && before[i] is null) await RequireNoBaselineStructureAsync(connection, tx, schema, tracks[i], baseline, cancellationToken);

        // The migrations run with exactly this schema, then pg_catalog, then pg_temp on the path, for this transaction only: an unqualified
        // name in them (a table, a function, a trigger function) means this schema's, never a temporary table's, and never a "$user"
        // schema's. A function made with SET search_path FROM CURRENT keeps this path for good (#298 review M1: with the session's path,
        // pg_temp came first and a caller's temporary tenant_purges stood in for the real one).
        await ExecuteAsync(connection, tx, $"SET LOCAL search_path = {QuotedSchema(schema)}, pg_catalog, pg_temp", cancellationToken);

        var outcomes = new List<TrackOutcome>();
        for (var i = 0; i < tracks.Count; i++)
        {
            await RunTrackAsync(connection, tx, schema, tracks[i], statements[i], cancellationToken);
            outcomes.Add(new TrackOutcome(tracks[i].Name, before[i], tracks[i].LatestVersion, false));
        }
        await tx.CommitAsync(cancellationToken);
        return outcomes;
    }

    /// <summary>
    /// Where each of <paramref name="core"/>'s and <paramref name="modules"/>' tracks stands, in one READ ONLY transaction that takes no
    /// lock (the readiness check): nothing is created or recorded, and a migration in progress does not make it wait.
    /// </summary>
    internal async Task<IReadOnlyList<TrackVersion>> TrackVersionsAsync(MigrationTrack core, IReadOnlyList<MigrationTrack> modules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(modules);
        await using var connection = await OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, tx, "SET TRANSACTION READ ONLY", cancellationToken);
        var schema = await SchemaAsync(connection, tx, cancellationToken);
        var versions = new List<TrackVersion>();
        foreach (var track in modules.Prepend(core))
        {
            var newest = await VersionAsync(connection, tx, schema, track.Table, cancellationToken);
            var recorded = 0;
            if (newest is not null)
            {
                await using var count = new NpgsqlCommand($"SELECT COUNT(*) FROM {Qualified(schema, track.Table)} WHERE version BETWEEN 1 AND @latest", connection, tx);
                count.Parameters.AddWithValue("latest", track.LatestVersion);
                recorded = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            }
            versions.Add(new TrackVersion(track.Name, track.LatestVersion, newest, recorded));
        }
        await tx.RollbackAsync(cancellationToken);
        return versions;
    }

    /// <summary>The track's version table (made if missing), then each migration not yet recorded: its statements, then its version.</summary>
    private static async Task RunTrackAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string schema, MigrationTrack track, string[][] migrations, CancellationToken ct)
    {
        var table = Qualified(schema, track.Table);
        await ExecuteAsync(connection, tx, $"CREATE TABLE IF NOT EXISTS {table}(version integer PRIMARY KEY, applied_at timestamptz NOT NULL)", ct);
        var recorded = new HashSet<int>();
        await using (var read = new NpgsqlCommand($"SELECT version FROM {table}", connection, tx))
        await using (var r = await read.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) recorded.Add(r.GetInt32(0));
        for (var version = 1; version <= migrations.Length; version++)
        {
            if (recorded.Contains(version)) continue;
            foreach (var statement in migrations[version - 1]) await ExecuteAsync(connection, tx, statement, ct);
            await using var record = new NpgsqlCommand($"INSERT INTO {table}(version, applied_at) VALUES(@v, clock_timestamp())", connection, tx);
            record.Parameters.AddWithValue("v", version);
            await record.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// A module track with a baseline and no version table: refused, naming what is there and what is not, when any of the baseline's
    /// tables or columns exists. Never adopted (see <see cref="MigrateAsync(MigrationTrack, IReadOnlyList{MigrationTrack}, CancellationToken)"/>).
    /// </summary>
    private static async Task RequireNoBaselineStructureAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string schema, MigrationTrack track, TrackBaseline baseline, CancellationToken ct)
    {
        var present = new List<string>();
        var missing = new List<string>();
        foreach (var table in baseline.Tables)
            (await TableExistsAsync(connection, tx, schema, table, ct) ? present : missing).Add("table " + table);
        foreach (var (table, column) in baseline.Columns)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_class c ON c.oid = a.attrelid " +
                $"WHERE c.relnamespace = {InSchema} AND c.relname = @t AND a.attname = @c AND a.attnum > 0 AND NOT a.attisdropped", connection, tx);
            cmd.Parameters.AddWithValue("schema", schema);
            cmd.Parameters.AddWithValue("t", table);
            cmd.Parameters.AddWithValue("c", column);
            ((long)(await cmd.ExecuteScalarAsync(ct))! > 0 ? present : missing).Add($"column {table}.{column}");
        }
        if (present.Count == 0) return;
        throw new SchemaBaselineException($"The {track.Name} track has no version table, but this database already has some of its structure. " +
            $"Present: {Names(present)}. Missing: {Names(missing)}. Refusing to run: on PostgreSQL a track's baseline is never adopted, since no older build " +
            "ran its statements in the core's track. Nothing was written.");
    }

    /// <summary>
    /// Refuses the core's track recorded part way to its first version with statements on PostgreSQL (version 23,
    /// <see cref="PostgresCoreMigrations.BaselineVersion"/>): its earlier versions are empty here only because SQLite's history is, and
    /// version 23 makes every table at once, so a build recording them alone does not exist; running on would make the baseline over
    /// whatever is there. The core alone: a module's empty versions are versions it shipped (a build may record them, and a later build
    /// add statements after them), so a module recorded at one of them is a database a real build made.
    /// </summary>
    private static void RequireNotPartWayToBaseline(MigrationTrack track, string[][] migrations, int? version)
    {
        if (track.Name != MigrationTrack.CoreName) return;
        var first = Array.FindIndex(migrations, m => m.Length > 0) + 1;
        if (version is { } v && v >= 1 && v < first)
            throw new SchemaBaselineException($"The {track.Name} track is recorded at version {v}, but on PostgreSQL it starts at version {first}: " +
                $"versions 1 to {first - 1} are empty there, so no Packmoat build made this database. Refusing to run. Nothing was written.");
    }

    private static string Names(List<string> names) => names.Count == 0 ? "(none)" : string.Join(", ", names);

    /// <summary>Every table in <paramref name="schema"/> named like a module track's (<c>*_schema_migrations</c>), in byte order.</summary>
    private static async Task<List<string>> TrackTablesAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string schema, CancellationToken ct)
    {
        // LIKE's _ is a wildcard: escaped, so only a literal "_schema_migrations" ending counts (as SQLite's GLOB '*_schema_migrations').
        await using var cmd = new NpgsqlCommand(
            $"SELECT relname FROM pg_catalog.pg_class WHERE relnamespace = {InSchema} AND relkind IN ('r', 'p') " +
            @"AND relname LIKE '%\_schema\_migrations' ORDER BY relname COLLATE ""C""", connection, tx);
        cmd.Parameters.AddWithValue("schema", schema);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var names = new List<string>();
        while (await r.ReadAsync(ct)) names.Add(r.GetString(0));
        return names;
    }

    /// <summary>A track's newest recorded version: null when it has no table, 0 when its table is empty.</summary>
    private static async Task<int?> VersionAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string schema, string table, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, tx, schema, table, ct)) return null;
        await using var newest = new NpgsqlCommand($"SELECT COALESCE(MAX(version), 0) FROM {Qualified(schema, table)}", connection, tx);
        return Convert.ToInt32(await newest.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Whether <paramref name="schema"/> has a table named <paramref name="table"/> (by catalogue, not by search path).</summary>
    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string schema, string table, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM pg_catalog.pg_class WHERE relnamespace = {InSchema} AND relkind IN ('r', 'p') AND relname = @n", connection, tx);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("n", table);
        return (long)(await cmd.ExecuteScalarAsync(ct))! > 0;
    }

    /// <summary>
    /// The oid of the schema the @schema parameter names, matched by name exactly. Never <c>current_schema()::regnamespace</c>: that
    /// parses the name again unquoted, so "Tenant" would become tenant (another schema, or none).
    /// </summary>
    private const string InSchema = "(SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = @schema)";

    /// <summary>
    /// The schema the migration's unqualified statements make their tables in: current_schema(), the first schema on search_path that
    /// exists. Refused when there is none.
    /// </summary>
    private static async Task<string> SchemaAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT current_schema()", connection, tx);
        return await cmd.ExecuteScalarAsync(ct) as string ?? throw new InvalidOperationException(
            "The database's search_path names no schema that exists, so there is nowhere to make the migration tables. Refusing to run. Nothing was written.");
    }

    /// <summary><paramref name="table"/> (a track's table: lower-case letters, digits and _) in <paramref name="schema"/>, the schema quoted.</summary>
    private static string Qualified(string schema, string table) => $"{QuotedSchema(schema)}.{table}";

    /// <summary><paramref name="schema"/> as a quoted identifier.</summary>
    private static string QuotedSchema(string schema) => $"\"{schema.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
