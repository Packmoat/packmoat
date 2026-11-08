using Microsoft.Data.Sqlite;

namespace Packmoat.Infrastructure;

/// <summary>The database was migrated by a newer build. This build refuses to run on it.</summary>
public sealed class SchemaTooNewException : InvalidOperationException
{
    public SchemaTooNewException(int found, int known)
        : base($"The database schema is version {found}, newer than this build knows ({known}). Refusing to run: an older build could misread or damage it. " +
               "Run the newer build, or restore a backup taken before the upgrade (packmoat-admin restore).") { }

    /// <summary>A module's track (#196) is newer than this build knows.</summary>
    public SchemaTooNewException(string track, int found, int known)
        : base($"The database's {track} schema is version {found}, newer than this build knows ({known}). Refusing to run: an older build could misread or damage it. " +
               "Run the newer build, or restore a backup taken before the upgrade (packmoat-admin restore).") { }
}

/// <summary>Opens connections with the right settings and applies schema migrations. One file on local disk (WAL mode).</summary>
public sealed class SqliteDatabase : IRelationalDatabase, ISchemaMigrator
{
    private readonly string _connectionString;
    private readonly string _readOnlyConnectionString;

    /// <summary>Always <see cref="DatabaseEngine.Sqlite"/>.</summary>
    public DatabaseEngine Engine => DatabaseEngine.Sqlite;

    /// <summary>
    /// The one approved way from the module seams' <see cref="IRelationalDatabase"/> back to SQLite's own database (#278 step 0): the same
    /// instance when it is SQLite's. Anything else (another engine, or an object that is not this class) is refused, naming its engine.
    /// </summary>
    public static SqliteDatabase From(IRelationalDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database as SqliteDatabase ?? throw new InvalidOperationException(
            $"This part of Packmoat runs on SQLite only, and was given a {DatabaseEngines.Name(database.Engine)} database ({DatabaseEngines.Variable}). Refusing to run.");
    }

    public SqliteDatabase(string path)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            // A directory we create is private to the owner. An existing one (for example a mounted volume) is left as the operator made it.
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
            else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (!File.Exists(full) && !OperatingSystem.IsWindows())
        {
            // Create the database file ourselves with mode 0600 before SQLite does; SQLite gives its -wal and -shm files the same mode.
            using var created = new FileStream(full, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        }
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
        _readOnlyConnectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = true }.ToString();
    }

    /// <summary>
    /// A connection that cannot write (#233: the readiness check). It never creates the file, sets no journal mode, and refuses any write
    /// (query_only), so a missing or moved database is an error here, not a new empty one.
    /// </summary>
    internal async Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_readOnlyConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var pragmas = connection.CreateCommand();
            pragmas.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=5000;";
            await pragmas.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Where each of the core's and <paramref name="modules"/>' tracks stands, read only (#233: the readiness check). Nothing is created or
    /// recorded: a track with no table has <see cref="TrackVersion.Newest"/> null.
    /// </summary>
    public async Task<IReadOnlyList<TrackVersion>> TrackVersionsAsync(IReadOnlyList<MigrationTrack> modules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modules);
        await using var connection = await OpenReadOnlyAsync(cancellationToken);
        var versions = new List<TrackVersion>();
        foreach (var track in modules.Prepend(Core))
        {
            var newest = await VersionAsync(connection, track.Table, cancellationToken);
            var recorded = 0;
            if (newest is not null)
            {
                await using var count = connection.CreateCommand();
                count.CommandText = $"SELECT COUNT(*) FROM \"{track.Table}\" WHERE version BETWEEN 1 AND @latest";
                count.Parameters.AddWithValue("@latest", track.LatestVersion);
                recorded = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            }
            versions.Add(new TrackVersion(track.Name, track.LatestVersion, newest, recorded));
        }
        return versions;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var pragmas = connection.CreateCommand();
        // FULL: an audit row that was acknowledged survives a power cut. Volume is small, so the cost is acceptable.
        pragmas.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;";
        await pragmas.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    /// <summary>The core's migration track (table schema_migrations): <see cref="CoreSchema.Track"/>, whose SQLite statements are
    /// <see cref="Migrations"/>.</summary>
    public static MigrationTrack Core => CoreSchema.Track;

    /// <summary>
    /// Applies the core's migrations, then each of <paramref name="modules"/>' tracks in order (#196). Before anything is written it
    /// refuses: a <c>*_schema_migrations</c> table no track here declares, a track declared twice (or a module's named "core"), a track
    /// whose table is newer than this build knows (<see cref="SchemaTooNewException"/>), and a database a module's
    /// <see cref="TrackBaseline"/> cannot take over (<see cref="SchemaBaselineException"/>). Production passes every module's track;
    /// an empty list is the core alone.
    /// </summary>
    public Task<IReadOnlyList<TrackOutcome>> MigrateAsync(IReadOnlyList<MigrationTrack> modules, CancellationToken cancellationToken = default) =>
        MigrateAsync(Core, modules, int.MaxValue, cancellationToken);

    /// <summary>The newest core schema version this build knows.</summary>
    public static int LatestVersion => Migrations.Length;

    /// <summary>
    /// Applies the core's migrations alone, up to and including <paramref name="upToVersion"/>. Tests use it to build an older database
    /// and then upgrade it.
    /// </summary>
    public Task MigrateAsync(int upToVersion, CancellationToken cancellationToken = default) => MigrateAsync(Core, [], upToVersion, cancellationToken);

    /// <summary>Tests: <paramref name="migrations"/> as the core's track, alone, up to <paramref name="upToVersion"/>.</summary>
    internal Task MigrateAsync(string[][] migrations, int upToVersion, CancellationToken cancellationToken = default) =>
        MigrateAsync(new MigrationTrack(MigrationTrack.CoreName, migrations), [], upToVersion, cancellationToken);

    /// <summary>
    /// The runner. Each migration is a list of statements, and each statement runs as its own command inside the migration's one
    /// transaction, so an error from any statement is thrown and rolls the whole migration back (Microsoft.Data.Sqlite does not report an
    /// error from a statement that follows an ADD COLUMN with a CHECK in the same command text, #162). Public for the tests of a
    /// module's track (#197): <paramref name="core"/> as the core's track, then <paramref name="modules"/>, with the core only up to
    /// <paramref name="coreUpToVersion"/>.
    /// </summary>
    public async Task<IReadOnlyList<TrackOutcome>> MigrateAsync(MigrationTrack core, IReadOnlyList<MigrationTrack> modules, int coreUpToVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modules);
        TrackRules.RequireDistinct(modules);
        // Every track has SQLite statements (#278 step 0, B7), checked before the database is opened: a refusal makes no track table.
        foreach (var track in modules.Prepend(core)) track.StatementsFor(DatabaseEngine.Sqlite);

        await using var connection = await OpenAsync(cancellationToken);

        // Fail closed, before anything is written. A track table nobody here declares belongs to a module this build does not have: its
        // tables could be misread or damaged. A track whose table is newer than this build knows: an older build must not run on it.
        TrackRules.RequireDeclared(core, modules, await TrackTablesAsync(connection, cancellationToken));
        var before = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var track in modules.Prepend(core))
        {
            var version = await VersionAsync(connection, track.Table, cancellationToken);
            TrackRules.RequireNotNewer(track, version);
            before[track.Name] = version;
        }
        var coreBefore = before[core.Name] ?? 0;
        foreach (var track in modules.Where(m => m.Baseline is not null && before[m.Name] is null))
            await JudgeBaselineAsync(connection, track, coreBefore, cancellationToken);   // throws on a refusal

        var outcomes = new List<TrackOutcome>();
        await RunTrackAsync(connection, core, coreUpToVersion, cancellationToken);
        outcomes.Add(new TrackOutcome(core.Name, before[core.Name], await VersionAsync(connection, core.Table, cancellationToken) ?? 0, false));
        foreach (var track in modules)
        {
            var adopted = track.Baseline is not null && before[track.Name] is null && await AdoptAsync(connection, track, coreBefore, cancellationToken);
            await RunTrackAsync(connection, track, int.MaxValue, cancellationToken);
            outcomes.Add(new TrackOutcome(track.Name, before[track.Name], await VersionAsync(connection, track.Table, cancellationToken) ?? 0, adopted));
        }
        return outcomes;
    }

    private enum BaselineFinding { None, All }

    /// <summary>Refuses a database the baseline cannot take over; otherwise says whether all or none of its structure is present.</summary>
    private static async Task<BaselineFinding> JudgeBaselineAsync(SqliteConnection connection, MigrationTrack track, int coreVersion, CancellationToken ct, SqliteTransaction? tx = null)
    {
        var b = track.Baseline!;
        if (coreVersion >= b.FirstCoreVersion && coreVersion < b.CoreVersion)
            throw new SchemaBaselineException($"The database's core schema is version {coreVersion}, from a build that ran only part of the {track.Name} track's statements " +
                $"(core versions {b.FirstCoreVersion} to {b.CoreVersion}). Refusing to run: upgrade it to core version {b.CoreVersion} with a build from before the " +
                $"{track.Name} track, or restore a backup at that version. Nothing was written.");
        var (present, missing) = await StructureAsync(connection, b, ct, tx);
        if (missing.Count == 0 && coreVersion == b.CoreVersion) return BaselineFinding.All;
        if (present.Count == 0) return BaselineFinding.None;
        throw new SchemaBaselineException($"The {track.Name} track cannot take over this database (core schema version {coreVersion}). Present: {Names(present)}. " +
            $"Missing: {Names(missing)}. Refusing to run: an older build left only part of it. Nothing was written.");
    }

    private static string Names(List<string> names) => names.Count == 0 ? "(none)" : string.Join(", ", names);

    /// <summary>
    /// A module track with no table yet. In one BEGIN IMMEDIATE transaction it judges the database's structure again (another process may
    /// have adopted it meanwhile) and, when everything is present, records versions 1 to the baseline's as applied without running them.
    /// When nothing is present it writes nothing and the track runs as usual. Returns true when it adopted. The core version judged is the
    /// one the database had before this run (<paramref name="coreBefore"/>), as the class says: the core has run by now, and a core
    /// migration after the baseline's version (core 22, #207) adds nothing the baseline looks at.
    /// </summary>
    private static async Task<bool> AdoptAsync(SqliteConnection connection, MigrationTrack track, int coreBefore, CancellationToken ct)
    {
        await using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);   // BEGIN IMMEDIATE
        if (await VersionAsync(connection, track.Table, ct, tx) is not null) return false;   // another process got there first
        if (await JudgeBaselineAsync(connection, track, coreBefore, ct, tx) == BaselineFinding.None) return false;
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"CREATE TABLE {track.Table}(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)";
        await cmd.ExecuteNonQueryAsync(ct);
        cmd.CommandText = $"INSERT INTO {track.Table}(version, applied_at) VALUES(@v, @t)";
        var v = cmd.Parameters.Add("@v", SqliteType.Integer);
        cmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToString("O"));
        for (var version = 1; version <= track.Baseline!.Version; version++)
        {
            v.Value = version;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return true;
    }

    private static async Task RunTrackAsync(SqliteConnection connection, MigrationTrack track, int upToVersion, CancellationToken cancellationToken)
    {
        await Execute(connection, $"CREATE TABLE IF NOT EXISTS {track.Table}(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)", cancellationToken);
        var migrations = track.StatementsFor(DatabaseEngine.Sqlite);
        for (var i = 0; i < Math.Min(upToVersion, migrations.Length); i++)
        {
            var version = i + 1;
            await using var check = connection.CreateCommand();
            check.CommandText = $"SELECT COUNT(*) FROM {track.Table} WHERE version=@v";
            check.Parameters.AddWithValue("@v", version);
            if ((long)(await check.ExecuteScalarAsync(cancellationToken))! > 0) continue;

            // BEGIN IMMEDIATE, then the check again inside it: another process may have run this migration while this one waited for the lock.
            await using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
            check.Transaction = tx;
            if ((long)(await check.ExecuteScalarAsync(cancellationToken))! > 0) continue;
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            foreach (var statement in migrations[i])
            {
                cmd.CommandText = statement;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            cmd.CommandText = $"INSERT INTO {track.Table}(version, applied_at) VALUES(@v, @t)";
            cmd.Parameters.AddWithValue("@v", version);
            cmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
    }

    /// <summary>Every table named like a module track's (<c>*_schema_migrations</c>; the core's schema_migrations is not one).</summary>
    internal static async Task<List<string>> TrackTablesAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name GLOB '*_schema_migrations' ORDER BY name";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var names = new List<string>();
        while (await r.ReadAsync(ct)) names.Add(r.GetString(0));
        return names;
    }

    /// <summary>A track's newest recorded version: null when it has no table, 0 when its table is empty.</summary>
    internal static async Task<int?> VersionAsync(SqliteConnection connection, string table, CancellationToken ct, SqliteTransaction? tx = null)
    {
        await using var exists = connection.CreateCommand();
        exists.Transaction = tx;
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n";
        exists.Parameters.AddWithValue("@n", table);
        if ((long)(await exists.ExecuteScalarAsync(ct))! == 0) return null;
        await using var newest = connection.CreateCommand();
        newest.Transaction = tx;
        newest.CommandText = $"SELECT COALESCE(MAX(version), 0) FROM \"{table}\"";
        return Convert.ToInt32(await newest.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Which of the baseline's tables and columns the database has, by structure (sqlite_master and table_info), never by SQL text.</summary>
    private static async Task<(List<string> Present, List<string> Missing)> StructureAsync(SqliteConnection connection, TrackBaseline b, CancellationToken ct, SqliteTransaction? tx)
    {
        var present = new List<string>();
        var missing = new List<string>();
        foreach (var table in b.Tables)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n";
            cmd.Parameters.AddWithValue("@n", table);
            ((long)(await cmd.ExecuteScalarAsync(ct))! > 0 ? present : missing).Add("table " + table);
        }
        foreach (var (table, column) in b.Columns)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info(@t) WHERE name=@c";
            cmd.Parameters.AddWithValue("@t", table);
            cmd.Parameters.AddWithValue("@c", column);
            ((long)(await cmd.ExecuteScalarAsync(ct))! > 0 ? present : missing).Add($"column {table}.{column}");
        }
        return (present, missing);
    }

    /// <summary>Closes this database's idle pooled connections, so its files can be deleted (the migration dry run's copies).</summary>
    public void ReleaseConnections()
    {
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
        using var readOnly = new SqliteConnection(_readOnlyConnectionString);
        SqliteConnection.ClearPool(readOnly);
    }

    private static async Task Execute(SqliteConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // One element per migration (version = index + 1), and one statement per inner element: MigrateAsync runs each as its own command
    // (#162). A trigger is one statement, BEGIN ... END included. Never put two statements in one element (a test checks).
    // Never edit an applied migration: each one's text is frozen by hash in MigrationHashTests (#168). Add a new migration instead.
    // The one deliberate exception (#196): the hosted service's statements in 9, 10, 12, 15 and 21 moved, word for word and in order, to
    // its own track's H1. The slots keep their numbers; an empty one still records its version. No core statement may name a module's
    // table (an architecture test checks).
    internal static readonly string[][] Migrations =
    [
        [
            """
            CREATE TABLE tenants(
                id TEXT PRIMARY KEY, name TEXT NOT NULL, upstream_base TEXT NOT NULL, allowed_path_prefix TEXT NOT NULL,
                feed_index_url TEXT NOT NULL, cooldown_days INTEGER NOT NULL CHECK(cooldown_days >= 0),
                policy_revision INTEGER NOT NULL DEFAULT 1, active INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL);
            """,
            """
            CREATE TABLE internal_prefixes(
                tenant_id TEXT NOT NULL REFERENCES tenants(id), prefix TEXT NOT NULL, PRIMARY KEY(tenant_id, prefix));
            """,
            """
            CREATE TABLE package_overrides(
                tenant_id TEXT NOT NULL REFERENCES tenants(id), package_id TEXT NOT NULL,
                cooldown_days INTEGER NOT NULL CHECK(cooldown_days >= 0), PRIMARY KEY(tenant_id, package_id));
            """,
            """
            CREATE TABLE waivers(
                id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL REFERENCES tenants(id), package_id TEXT NOT NULL, version TEXT NOT NULL,
                expires_at TEXT NOT NULL, reason TEXT NOT NULL, created_by TEXT NOT NULL, created_at TEXT NOT NULL, revoked_at TEXT);
            """,
            """
            CREATE TABLE tokens(
                id TEXT PRIMARY KEY, tenant_id TEXT NOT NULL REFERENCES tenants(id), token_hash TEXT NOT NULL UNIQUE,
                display_prefix TEXT NOT NULL, label TEXT NOT NULL, created_at TEXT NOT NULL, revoked_at TEXT, last_used_at TEXT);
            """,
            """
            CREATE TABLE upstream_identities(
                tenant_id TEXT PRIMARY KEY REFERENCES tenants(id), azure_tenant_id TEXT NOT NULL, client_id TEXT NOT NULL,
                cert_encrypted BLOB NOT NULL, updated_at TEXT NOT NULL);
            """,
            """
            CREATE TABLE audit_events(
                id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT NOT NULL, request_id TEXT NOT NULL, tenant_id TEXT NOT NULL,
                token_id TEXT NOT NULL, kind TEXT NOT NULL, package_id TEXT NOT NULL, version TEXT NOT NULL,
                decision TEXT NOT NULL, reason TEXT NOT NULL, waiver_id TEXT);
            """,
            "CREATE INDEX ix_audit_tenant_time ON audit_events(tenant_id, time);",
            "CREATE TRIGGER audit_no_update BEFORE UPDATE ON audit_events BEGIN SELECT RAISE(ABORT, 'audit log is append-only'); END;",
            "CREATE TRIGGER audit_no_delete BEFORE DELETE ON audit_events BEGIN SELECT RAISE(ABORT, 'audit log is append-only'); END;",
        ],
        // 2: a small encrypted marker, so a database can be checked against the master key without touching any customer secret.
        [
            "CREATE TABLE meta(key TEXT PRIMARY KEY, value BLOB NOT NULL);",
        ],
        // 3: what a token is for. Existing rows become 'developer' (the default), so nothing changes for tokens already issued.
        [
            "ALTER TABLE tokens ADD COLUMN kind TEXT NOT NULL DEFAULT 'developer' CHECK(kind IN ('developer','automation'));",
        ],
        // 4: which token was used on which UTC day. One row per token per day: a token id and a date, no address, no user agent, no label.
        [
            "CREATE TABLE token_activity(token_id TEXT NOT NULL REFERENCES tokens(id), day TEXT NOT NULL, PRIMARY KEY(token_id, day)) WITHOUT ROWID;",
        ],
        // 5: what the operator did from the admin site. Append-only like audit_events. No secret ever goes in detail. No foreign key on tenant_id: a row must survive whatever happens to the tenant.
        [
            """
            CREATE TABLE admin_actions(
                id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT NOT NULL, actor_tid TEXT NOT NULL, actor_oid TEXT NOT NULL, actor_name TEXT NOT NULL,
                action TEXT NOT NULL, tenant_id TEXT NOT NULL, detail TEXT NOT NULL);
            """,
            "CREATE INDEX ix_admin_actions_time ON admin_actions(time);",
            "CREATE TRIGGER admin_actions_no_update BEFORE UPDATE ON admin_actions BEGIN SELECT RAISE(ABORT, 'admin_actions is append-only'); END;",
            "CREATE TRIGGER admin_actions_no_delete BEFORE DELETE ON admin_actions BEGIN SELECT RAISE(ABORT, 'admin_actions is append-only'); END;",
        ],
        // 6: shared-app identity mode. The shared app's credential (one active, one waiting during a rotation) belongs to no tenant.
        // upstream_identities is rebuilt (SQLite cannot relax NOT NULL in place) with a kind: every existing row is copied as 'own',
        // column for column, so live tenants keep exactly the identity they had. Own rows must have a client id and key; shared rows must not.
        [
            """
            CREATE TABLE shared_app_credentials(
                slot TEXT PRIMARY KEY CHECK(slot IN ('active','next')), client_id TEXT NOT NULL, cert_encrypted BLOB NOT NULL, updated_at TEXT NOT NULL);
            """,
            """
            CREATE TABLE upstream_identities_v6(
                tenant_id TEXT PRIMARY KEY REFERENCES tenants(id),
                kind TEXT NOT NULL DEFAULT 'own' CHECK(kind IN ('own','shared')),
                azure_tenant_id TEXT NOT NULL, client_id TEXT, cert_encrypted BLOB, updated_at TEXT NOT NULL,
                CHECK((kind = 'own' AND client_id IS NOT NULL AND cert_encrypted IS NOT NULL)
                   OR (kind = 'shared' AND client_id IS NULL AND cert_encrypted IS NULL)));
            """,
            """
            INSERT INTO upstream_identities_v6(tenant_id, kind, azure_tenant_id, client_id, cert_encrypted, updated_at)
                SELECT tenant_id, 'own', azure_tenant_id, client_id, cert_encrypted, updated_at FROM upstream_identities;
            """,
            "DROP TABLE upstream_identities;",
            "ALTER TABLE upstream_identities_v6 RENAME TO upstream_identities;",
            "CREATE UNIQUE INDEX ux_shared_directory ON upstream_identities(lower(azure_tenant_id)) WHERE kind = 'shared';",
            """
            CREATE TABLE shared_app_verifications(
                tenant_id TEXT PRIMARY KEY REFERENCES tenants(id), azure_tenant_id TEXT NOT NULL, feed_index_url TEXT NOT NULL,
                client_id TEXT NOT NULL, verified_at TEXT NOT NULL, verified_by TEXT NOT NULL);
            """,
        ],
        // 7: customer sign-in. An operator-made link from a customer's Microsoft directory (lower-case canonical GUID) to one Packmoat tenant;
        // the primary key means one directory maps to at most one tenant. admin_actions learns which site (or the CLI) made each row.
        [
            """
            CREATE TABLE sign_in_directories(
                azure_tenant_id TEXT PRIMARY KEY CHECK(azure_tenant_id = lower(azure_tenant_id) AND length(azure_tenant_id) = 36),
                tenant_id TEXT NOT NULL REFERENCES tenants(id), bound_at TEXT NOT NULL, bound_by TEXT NOT NULL);
            """,
            "CREATE INDEX ix_sign_in_directories_tenant ON sign_in_directories(tenant_id);",
            "ALTER TABLE admin_actions ADD COLUMN site TEXT NOT NULL DEFAULT 'admin';",
        ],
        // 8: tokens owned by people, with expiry. members: who signed in on the customer site, as (directory, object id), both lower-case
        // canonical GUIDs. tokens learns its owner, expiry, how it was issued and who revoked it on the customer site. Existing rows are
        // unchanged in meaning: no owner, no expiry (NULL), issued via 'operator'.
        [
            """
            CREATE TABLE members(
                azure_tenant_id TEXT NOT NULL CHECK(azure_tenant_id = lower(azure_tenant_id) AND length(azure_tenant_id) = 36),
                object_id TEXT NOT NULL CHECK(object_id = lower(object_id) AND length(object_id) = 36),
                tenant_id TEXT NOT NULL REFERENCES tenants(id), display_name TEXT NOT NULL, first_seen_at TEXT NOT NULL, last_seen_at TEXT NOT NULL,
                PRIMARY KEY(azure_tenant_id, object_id));
            """,
            "CREATE INDEX ix_members_tenant ON members(tenant_id);",
            "ALTER TABLE tokens ADD COLUMN owner_tid TEXT NULL;",
            "ALTER TABLE tokens ADD COLUMN owner_oid TEXT NULL;",
            "ALTER TABLE tokens ADD COLUMN expires_at TEXT NULL;",
            "ALTER TABLE tokens ADD COLUMN issued_via TEXT NOT NULL DEFAULT 'operator' CHECK(issued_via IN ('operator','self'));",
            "ALTER TABLE tokens ADD COLUMN revoked_by TEXT NULL;",
            "CREATE INDEX ix_tokens_owner ON tokens(tenant_id, owner_tid, owner_oid);",
        ],
        // 9: tenants learn who made them: every existing row is 'operator' (the default), with no creator oid. (The hosted service's
        // tables, made here before #196, are its track's H1.)
        [
            "ALTER TABLE tenants ADD COLUMN origin TEXT NOT NULL DEFAULT 'operator' CHECK(origin IN ('operator','self'));",
            "ALTER TABLE tenants ADD COLUMN created_by_oid TEXT NULL;",
        ],
        // 10: empty since #196 (its statement is the hosted track's H1).
        [
        ],
        // 11: auto-join (#80). One row per tenant once an admin changes it; NO row means ON, so every existing tenant starts on and nothing
        // is inserted here. enabled is 0 or 1 only.
        [
            """
            CREATE TABLE auto_join_settings(
                tenant_id TEXT PRIMARY KEY REFERENCES tenants(id),
                enabled INTEGER NOT NULL CHECK(enabled IN (0,1)),
                changed_at TEXT NOT NULL, changed_by TEXT NOT NULL);
            """,
        ],
        // 12: empty since #196 (its statement is the hosted track's H1).
        [
        ],
        // 13: the Azure setup wizard (#63 H1). feed_checks: every run of the feed setup check, who ran it and exactly what it checked;
        // results and confirmed_admins are JSON (confirmed_admins holds display names: personal data, 30-day retention); never a token.
        // shared_app_verifications learns how each record was made: every existing row is 'operator' (the default, the CLI's onboard
        // check, which keeps writing it that way), with no feed check. Nothing that serves reads these columns yet.
        // feed_check_id must name a real feed_checks row (a foreign key; NULL for operator-made records). The key does not check that the
        // feed check is the same tenant's: part I (the wizard writing it) must check feed_checks.tenant_id = the verification's tenant_id.
        // After 30 days confirmed_admins and checked_by_name are blanked and every result detail is redacted (IFeedCheckStore.BlankPersonalDataAsync, #63 H2,
        // #107); checked_by_tid and checked_by_oid stay until the tenant purge. Rows are never deleted, as a verification may refer to one.
        [
            """
            CREATE TABLE feed_checks(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id TEXT NOT NULL REFERENCES tenants(id),
                checked_at TEXT NOT NULL,
                checked_by_tid TEXT NOT NULL, checked_by_oid TEXT NOT NULL, checked_by_name TEXT NOT NULL,
                azure_tenant_id TEXT NOT NULL, feed_index_url TEXT NOT NULL, client_id TEXT NOT NULL,
                outcome TEXT NOT NULL CHECK(outcome IN ('passed','failed','incomplete')),
                results TEXT NOT NULL,
                confirmed_admins TEXT NOT NULL DEFAULT '[]');
            """,
            "CREATE INDEX ix_feed_checks_tenant ON feed_checks(tenant_id, checked_at);",
            "ALTER TABLE shared_app_verifications ADD COLUMN verified_via TEXT NOT NULL DEFAULT 'operator' CHECK (verified_via IN ('operator','wizard'));",
            "ALTER TABLE shared_app_verifications ADD COLUMN feed_check_id INTEGER NULL REFERENCES feed_checks(id);",
        ],
        // 14: connecting a checked feed (#64 I1). feed_checks learns what each row is about: every existing row is an Admin's settings check
        // (the default); 'developer' rows are a developer's proof that they cannot read the feed directly (#64 I2). A wizard-made
        // verification names both rows it relied on: developer_check_id must name a real feed_checks row (NULL for operator-made records
        // and for every existing row). As with feed_check_id, the key does not check the tenant: ConnectSelfServiceFeedAsync does.
        [
            "ALTER TABLE feed_checks ADD COLUMN kind TEXT NOT NULL DEFAULT 'settings' CHECK(kind IN ('settings','developer'));",
            "CREATE INDEX ix_feed_checks_tenant_kind ON feed_checks(tenant_id, kind, checked_at);",
            "ALTER TABLE shared_app_verifications ADD COLUMN developer_check_id INTEGER NULL REFERENCES feed_checks(id);",
        ],
        // 15: tenant purge (#105, #81). Additive: no existing row changes. (Its first statement is the hosted track's H1 since #196.)
        // tenant_purges: one row per purged tenant id, kept forever (no personal fields): makes a second purge a no-op and stops the id
        // being used again (no insert, no rename to it). A row can be added only once the tenant row is gone and only once per id (so
        // INSERT OR REPLACE cannot swap it), never deleted, and changed only by clearing 'purging'.
        // audit_events stays append-only: the only delete allowed is of a purged tenant's rows while its purge is running ('purging' = 1,
        // set and cleared inside the purge's one transaction, so no other connection ever sees it set). A late audit row for a purged
        // tenant (a request already in flight) is silently dropped, so nothing of it is kept after the purge.
        [
            """
            CREATE TABLE tenant_purges(
                tenant_id TEXT PRIMARY KEY, purged_at TEXT NOT NULL, reason TEXT NOT NULL, actor TEXT NOT NULL,
                purging INTEGER NOT NULL DEFAULT 0 CHECK(purging IN (0,1)));
            """,
            """
            CREATE TRIGGER tenant_purges_only_when_gone BEFORE INSERT ON tenant_purges
                WHEN EXISTS (SELECT 1 FROM tenants WHERE id = NEW.tenant_id) OR EXISTS (SELECT 1 FROM tenant_purges WHERE tenant_id = NEW.tenant_id)
                BEGIN SELECT RAISE(ABORT, 'a tenant is recorded as purged only once, and only once its rows are gone'); END;
            """,
            "CREATE TRIGGER tenant_purges_no_delete BEFORE DELETE ON tenant_purges BEGIN SELECT RAISE(ABORT, 'tenant_purges is kept forever'); END;",
            """
            CREATE TRIGGER tenant_purges_no_update BEFORE UPDATE ON tenant_purges
                WHEN NOT (OLD.purging = 1 AND NEW.purging = 0 AND NEW.tenant_id IS OLD.tenant_id AND NEW.purged_at IS OLD.purged_at
                          AND NEW.reason IS OLD.reason AND NEW.actor IS OLD.actor)
                BEGIN SELECT RAISE(ABORT, 'tenant_purges is append-only'); END;
            """,
            """
            CREATE TRIGGER tenants_no_purged_id BEFORE INSERT ON tenants
                WHEN EXISTS (SELECT 1 FROM tenant_purges WHERE tenant_id = NEW.id)
                BEGIN SELECT RAISE(ABORT, 'this tenant id was purged and is never used again'); END;
            """,
            """
            CREATE TRIGGER tenants_no_rename_to_purged_id BEFORE UPDATE OF id ON tenants
                WHEN EXISTS (SELECT 1 FROM tenant_purges WHERE tenant_id = NEW.id)
                BEGIN SELECT RAISE(ABORT, 'this tenant id was purged and is never used again'); END;
            """,
            "DROP TRIGGER audit_no_delete;",
            """
            CREATE TRIGGER audit_no_delete BEFORE DELETE ON audit_events
                WHEN NOT EXISTS (SELECT 1 FROM tenant_purges p WHERE p.tenant_id = OLD.tenant_id AND p.purging = 1)
                BEGIN SELECT RAISE(ABORT, 'audit log is append-only'); END;
            """,
            """
            CREATE TRIGGER audit_purged_tenant BEFORE INSERT ON audit_events
                WHEN EXISTS (SELECT 1 FROM tenant_purges WHERE tenant_id = NEW.tenant_id)
                BEGIN SELECT RAISE(IGNORE); END;
            """,
        ],
        // 16: feed names on a check (#119 item 6). Additive and nullable: every existing row reads NULL (no names known, so the pages
        // show the feed URL as before). A wizard settings check writes the names the Admin typed (organisation, project or none, feed) as
        // JSON; nothing else reads or writes it. Never personal data or a token.
        [
            "ALTER TABLE feed_checks ADD COLUMN feed_names TEXT NULL;",
        ],
        // 17: the per-version decision record (#123). Additive: a new, empty table; no existing row changes. One row per tenant, ecosystem,
        // package, version, decision and reason, so it grows with distinct versions, not with requests. It holds no token, person, name
        // or address. Written in batches by RecordingPackageGate's background writer; rows not seen for DecisionRecordPolicy.RetentionDays
        // are pruned daily (ix_decision_records_seen serves that delete across all tenants); a tenant purge deletes them. No foreign
        // key to tenants: a late batch for a purged tenant (a request already in flight) is silently dropped by the trigger, as
        // audit_events does, rather than failing the whole batch.
        [
            """
            CREATE TABLE decision_records(
                tenant_id TEXT NOT NULL, ecosystem TEXT NOT NULL, package_id TEXT NOT NULL, version TEXT NOT NULL,
                decision TEXT NOT NULL, reason TEXT NOT NULL, waiver_id TEXT NULL, cooldown_days INTEGER NULL, published_at TEXT NULL,
                policy_revision INTEGER NOT NULL, first_seen_at TEXT NOT NULL, last_seen_at TEXT NOT NULL,
                seen_count INTEGER NOT NULL CHECK(seen_count >= 1),
                PRIMARY KEY(tenant_id, ecosystem, package_id, version, decision, reason)) WITHOUT ROWID;
            """,
            "CREATE INDEX ix_decision_records_tenant_seen ON decision_records(tenant_id, last_seen_at);",
            "CREATE INDEX ix_decision_records_seen ON decision_records(last_seen_at);",
            """
            CREATE TRIGGER decision_records_purged_tenant BEFORE INSERT ON decision_records
                WHEN EXISTS (SELECT 1 FROM tenant_purges WHERE tenant_id = NEW.tenant_id)
                BEGIN SELECT RAISE(IGNORE); END;
            """,
        ],
        // 18: admin_actions is kept for 12 months (#117, decision 2026-10-19). No row changes. The no-delete trigger is recreated
        // so it allows deleting a row only once it is more than 365 days old by the database's own clock: no code path (a prune with a
        // clock running ahead, a purge bug) can delete a younger one. A time that cannot be read is never old enough. UPDATE stays refused.
        [
            "DROP TRIGGER admin_actions_no_delete;",
            """
            CREATE TRIGGER admin_actions_no_delete BEFORE DELETE ON admin_actions
                WHEN julianday(OLD.time) IS NULL OR julianday(OLD.time) >= julianday('now', '-365 days')
                BEGIN SELECT RAISE(ABORT, 'admin_actions is append-only: a row is deleted only once it is more than 365 days old'); END;
            """,
        ],
        // 19: audit_events is kept for 90 days (#136; AuditPolicy.RetentionDays, which this floor must never exceed: a test checks).
        // No row changes. The no-delete trigger is recreated so it allows a delete while the tenant's purge is running (migration 15's
        // condition, unchanged) or once a row is more than 90 days old by the database's own clock. A time that cannot be read is never
        // old enough, and neither is one not written as a date and time (SQLite reads a bare number such as '12' as a Julian day, which
        // would make it deletable at any age). UPDATE stays refused. ix_audit_time serves the daily prune across all tenants. Applied in one transaction, as every
        // migration is: a failure part way leaves the migration 15 trigger and no index.
        [
            "DROP TRIGGER audit_no_delete;",
            """
            CREATE TRIGGER audit_no_delete BEFORE DELETE ON audit_events
                WHEN NOT EXISTS (SELECT 1 FROM tenant_purges p WHERE p.tenant_id = OLD.tenant_id AND p.purging = 1)
                     AND (OLD.time NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T*' OR julianday(OLD.time) IS NULL
                          OR julianday(OLD.time) >= julianday('now', '-90 days'))
                BEGIN SELECT RAISE(ABORT, 'audit log is append-only: a row is deleted only once it is more than 90 days old, or by its tenant''s purge'); END;
            """,
            "CREATE INDEX ix_audit_time ON audit_events(time);",
        ],
        // 20: who made a waiver, and who revoked it (#157 self-service waivers). Additive: every existing waiver was made by the operator
        // (the CLI or the admin site), so it reads 'operator'; revoked_by is NULL for every existing row, revoked or not (unknown). The
        // customer site writes 'customer' rows, capped per tenant in the insert's transaction (SqliteTenantStore.AddCustomerWaiverAsync).
        // revoked_by holds a person's display name and object id, or the operator's name: personal data, deleted with the tenant's waivers
        // on purge. Applied in one transaction, as every migration is. The column with a CHECK was added LAST because, when this migration was
        // one command, Microsoft.Data.Sqlite did not report an error from a statement after an ADD COLUMN with a CHECK; each statement now
        // runs as its own command (#162), so order no longer hides an error.
        [
            "ALTER TABLE waivers ADD COLUMN revoked_by TEXT NULL;",
            "ALTER TABLE waivers ADD COLUMN source TEXT NOT NULL DEFAULT 'operator' CHECK(source IN ('operator','customer'));",
        ],
        // 21: empty since #196 (its statement is the hosted track's H1).
        [
        ],
        // 22: who sets up a tenant's feed (#207). Additive: every existing row is 'operator' (the default), so nothing changes for any
        // tenant. 'customer' lets an operator-made tenant's own Admins connect their feed with the Azure setup wizard, as a self-made
        // tenant's do (FeedSetupRules.CustomerSetsUpFeed); any operator write of the upstream or identity sets it back to 'operator'.
        [
            "ALTER TABLE tenants ADD COLUMN feed_setup TEXT NOT NULL DEFAULT 'operator' CHECK(feed_setup IN ('operator','customer'));",
        ],
        // 23: state shared by every copy of the server (#232 M1). Additive: two new, empty tables; no existing row changes. Neither holds
        // tenant data. Every key column is NOT NULL as well (SQLite would otherwise allow a NULL key; Postgres never does).
        // job_leases: one row per scheduled job, "holder runs job until expires_at" (SqliteJobLease, #232 M2); holder is a random id per
        // process, expires_at UTC "O" text. cookie_keys: the cookie protection keys, for a server that keeps them in the database instead
        // of the admin-keys folder (#232 B1, SqliteCookieKeyStore, with PACKMOAT_COOKIE_KEYS=database only): id a GUID made by the store, friendly_name the name the key
        // ring gives (it repeats, for revocations, so it is not the key), created UTC "O" text, xml_encrypted the key's XML as
        // AesGcmSecretProtector bytes under the master key.
        [
            "CREATE TABLE job_leases(job TEXT NOT NULL PRIMARY KEY, holder TEXT NOT NULL, expires_at TEXT NOT NULL);",
            "CREATE TABLE cookie_keys(id TEXT NOT NULL PRIMARY KEY, friendly_name TEXT NOT NULL, created TEXT NOT NULL, xml_encrypted BLOB NOT NULL);",
        ],
    ];
}
