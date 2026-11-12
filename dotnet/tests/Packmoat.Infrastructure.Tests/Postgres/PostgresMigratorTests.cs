using System.Diagnostics;
using Npgsql;
using Packmoat.Infrastructure.Postgres;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// The PostgreSQL migrator on test tracks (the core track's PostgreSQL statements are the core's own). Each test has its own database
/// (<see cref="PostgresScratchDatabase"/>), made before it and dropped after it. The rules the SQLite migrator shares (a track declared
/// twice, a module named core, an undeclared track table, a track newer than this build) come from <see cref="TrackRules"/>, so their
/// messages are SQLite's word for word.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public sealed class PostgresMigratorTests : IAsyncLifetime
{
    private static Dictionary<DatabaseEngine, string[][]> Pg(params string[][] migrations) => new() { [DatabaseEngine.Postgres] = migrations };

    /// <summary>A test core: three versions, the second empty (an empty one still records its version).</summary>
    private static readonly MigrationTrack Core = new(MigrationTrack.CoreName, Pg(
        ["CREATE TABLE a(x integer)"],
        [],
        ["CREATE TABLE b(x integer)", "ALTER TABLE a ADD COLUMN y text"]));

    /// <summary>A test module: two versions.</summary>
    private static readonly MigrationTrack Probe = new("probe", Pg(
        ["CREATE TABLE probe_items(id integer PRIMARY KEY)"],
        ["ALTER TABLE probe_items ADD COLUMN name text"]));

    private PostgresScratchDatabase scratch = null!;

    public async Task InitializeAsync() => scratch = await PostgresScratchDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (scratch is not null) await scratch.DisposeAsync();
    }

    [PostgresFact]
    public async Task A_fresh_run_records_every_version_of_every_track()
    {
        await using var db = scratch.Database();
        var outcomes = await db.MigrateAsync(Core, [Probe]);

        Assert.Equal([new TrackOutcome("core", null, 3, false), new TrackOutcome("probe", null, 2, false)], outcomes);
        var snapshot = await scratch.SnapshotAsync();
        Assert.Equal(new[] { 1, 2, 3 }, await scratch.VersionsAsync("schema_migrations"));
        Assert.Equal(new[] { 1, 2 }, await scratch.VersionsAsync("probe_schema_migrations"));
        Assert.Contains("a.y text", snapshot);
        Assert.Contains("b.x integer", snapshot);
        Assert.Contains("probe_items.name text", snapshot);
        Assert.Contains("schema_migrations.version integer", snapshot);
        Assert.Contains("schema_migrations.applied_at timestamp with time zone", snapshot);
    }

    [PostgresFact]
    public async Task A_second_run_is_a_no_op()
    {
        await using var db = scratch.Database();
        await db.MigrateAsync(Core, [Probe]);
        var first = await scratch.SnapshotAsync();

        var outcomes = await db.MigrateAsync(Core, [Probe]);

        Assert.Equal([new TrackOutcome("core", 3, 3, false), new TrackOutcome("probe", 2, 2, false)], outcomes);
        Assert.Equal(first, await scratch.SnapshotAsync());   // applied_at included: nothing was recorded again
    }

    [PostgresFact]
    public async Task A_later_build_runs_only_the_new_versions()
    {
        await using var db = scratch.Database();
        await db.MigrateAsync(new MigrationTrack(MigrationTrack.CoreName, Pg(["CREATE TABLE a(x integer)"])), [Probe]);

        var outcomes = await db.MigrateAsync(Core, [Probe]);

        Assert.Equal([new TrackOutcome("core", 1, 3, false), new TrackOutcome("probe", 2, 2, false)], outcomes);
        Assert.Equal(new[] { 1, 2, 3 }, await scratch.VersionsAsync("schema_migrations"));
    }

    // mutation: commit per track (the core's table a, and its version table, would then survive)
    [PostgresFact]
    public async Task A_failure_in_the_second_track_leaves_nothing_of_the_first()
    {
        await using var db = scratch.Database();
        var broken = new MigrationTrack("probe", Pg(["CREATE TABLE probe_items(id integer)", "SELECT x FROM no_such_table"]));

        var e = await Assert.ThrowsAsync<PostgresException>(() => db.MigrateAsync(Core, [broken]));

        Assert.Equal(PostgresErrorCodes.UndefinedTable, e.SqlState);
        Assert.Empty(await scratch.SnapshotAsync());
    }

    // mutation: no advisory lock (both create the version tables at once, and one fails; locally 5 of 5 rounds)
    [PostgresFact]
    public async Task Two_migrators_at_once_both_succeed_and_record_every_version_once()
    {
        // The first migration sleeps inside the transaction, so the second call always starts while the first still runs.
        var slowCore = new MigrationTrack(MigrationTrack.CoreName, Pg(["CREATE TABLE a(x integer)", "SELECT pg_sleep(0.5)"], [], ["CREATE TABLE b(x integer)"]));
        for (var round = 1; round <= 5; round++)
        {
            await using var scratchRound = await PostgresScratchDatabase.CreateAsync();
            await using var one = scratchRound.Database();
            await using var two = scratchRound.Database();
            using var barrier = new Barrier(2);
            var clock = Stopwatch.StartNew();

            async Task<(TimeSpan Start, TimeSpan End, IReadOnlyList<TrackOutcome> Outcomes)> Run(PostgresDatabase db)
            {
                await Task.Yield();
                barrier.SignalAndWait(TimeSpan.FromSeconds(30));
                var start = clock.Elapsed;
                var outcomes = await db.MigrateAsync(slowCore, [Probe]);
                return (start, clock.Elapsed, outcomes);
            }

            var runs = await Task.WhenAll(Task.Run(() => Run(one)), Task.Run(() => Run(two)));

            Assert.True(runs.Max(r => r.Start) < runs.Min(r => r.End), $"round {round}: the two calls did not overlap");
            Assert.All(runs, r => Assert.Equal(3, r.Outcomes[0].After));
            Assert.All(runs, r => Assert.Equal(2, r.Outcomes[1].After));
            Assert.Single(runs, r => r.Outcomes[0].Before is null);   // one ran the migrations, the other found them done
            Assert.Equal(new[] { 1, 2, 3 }, await scratchRound.VersionsAsync("schema_migrations"));
            Assert.Equal(new[] { 1, 2 }, await scratchRound.VersionsAsync("probe_schema_migrations"));
        }
    }

    // mutation: the version rows written after the commit (on the same connection, in a transaction of their own)
    [PostgresFact]
    public async Task The_version_rows_are_written_in_the_transaction_that_made_the_tables()
    {
        await using var db = scratch.Database();
        await db.MigrateAsync(Core, [Probe]);

        // xmin is the transaction that wrote a row: the version rows, and the catalogue rows of the tables the migrations made.
        var writers = await scratch.TextsAsync(
            """
            SELECT xmin::text FROM schema_migrations
            UNION SELECT xmin::text FROM probe_schema_migrations
            UNION SELECT xmin::text FROM pg_class
              WHERE relname IN ('a', 'b', 'probe_items') AND relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = current_schema())
            """);
        Assert.Single(writers);
    }

    // mutation (review): current_schema()::regnamespace again ("Tenant" is then read as tenant, which does not exist: 3F000)
    [PostgresFact]
    public async Task A_schema_with_capitals_in_its_name_is_the_one_used()
    {
        await scratch.ExecuteAsync("CREATE SCHEMA \"Tenant\"");
        await scratch.SetSearchPathAsync("\"Tenant\"");
        await using var db = scratch.Database();

        Assert.Equal([new TrackOutcome("core", null, 3, false), new TrackOutcome("probe", null, 2, false)], await db.MigrateAsync(Core, [Probe]));
        Assert.Equal([new TrackOutcome("core", 3, 3, false), new TrackOutcome("probe", 2, 2, false)], await db.MigrateAsync(Core, [Probe]));
        Assert.All(await db.TrackVersionsAsync(Core, [Probe]), v => Assert.True(v.IsCurrent));
        Assert.Equal(new[] { 1, 2, 3 }, await scratch.VersionsAsync("\"Tenant\".schema_migrations"));
    }

    // mutation (review): current_schema()::regnamespace again (tenant's version 99 is then read as this track's: too new)
    [PostgresFact]
    public async Task A_lower_case_twin_of_the_schema_is_not_read()
    {
        await scratch.ExecuteAsync(
            "CREATE SCHEMA \"Tenant\"",
            "CREATE SCHEMA tenant",
            "CREATE TABLE tenant.schema_migrations(version integer PRIMARY KEY, applied_at timestamptz NOT NULL)",
            "INSERT INTO tenant.schema_migrations VALUES (99, now())",
            "CREATE TABLE tenant.zzz_schema_migrations(version integer PRIMARY KEY, applied_at timestamptz NOT NULL)");
        await scratch.SetSearchPathAsync("\"Tenant\", tenant");
        await using var db = scratch.Database();

        Assert.Equal([new TrackOutcome("core", null, 3, false), new TrackOutcome("probe", null, 2, false)], await db.MigrateAsync(Core, [Probe]));
        Assert.Equal(new[] { 1, 2, 3 }, await scratch.VersionsAsync("\"Tenant\".schema_migrations"));
        Assert.Equal(new[] { 99 }, await scratch.VersionsAsync("tenant.schema_migrations"));
    }

    // mutation (review): the schema filter replaced by TRUE (other's tables then count: too new, undeclared, or a baseline present)
    [PostgresFact]
    public async Task Tables_in_a_later_schema_on_the_search_path_do_not_count()
    {
        await scratch.ExecuteAsync(
            "CREATE SCHEMA other",
            "CREATE TABLE other.schema_migrations(version integer PRIMARY KEY, applied_at timestamptz NOT NULL)",
            "INSERT INTO other.schema_migrations VALUES (99, now())",
            "CREATE TABLE other.zzz_schema_migrations(version integer PRIMARY KEY, applied_at timestamptz NOT NULL)",
            "CREATE TABLE other.probe_items(id integer, name text)");
        await scratch.SetSearchPathAsync("public, other");
        await using var db = scratch.Database();

        Assert.Equal([new TrackVersion("core", 3, null, 0), new TrackVersion("probe", 2, null, 0)], await db.TrackVersionsAsync(Core, [Baselined]));
        Assert.Equal([new TrackOutcome("core", null, 3, false), new TrackOutcome("probe", null, 2, false)], await db.MigrateAsync(Core, [Baselined]));
        Assert.Equal(new[] { 1, 2, 3 }, await scratch.VersionsAsync("public.schema_migrations"));
        Assert.Equal(new[] { 99 }, await scratch.VersionsAsync("other.schema_migrations"));
        Assert.Contains("probe_items.name text", await scratch.SnapshotAsync());   // made in public, beside other's
    }

    // mutation: no advisory lock (nothing waits, and the run succeeds); SET LOCAL lock_timeout dropped (it waits forever)
    [PostgresFact]
    public async Task A_held_migration_lock_times_out_and_nothing_is_written()
    {
        await using var holder = await scratch.OpenAsync();
        await using (var take = new NpgsqlCommand($"SELECT pg_advisory_lock({PostgresDatabase.MigrationLockKey})", holder)) await take.ExecuteNonQueryAsync();
        await using var db = scratch.Database(lockTimeout: TimeSpan.FromSeconds(1));

        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var e = await Assert.ThrowsAsync<TimeoutException>(() => db.MigrateAsync(Core, [Probe], limit.Token));

        Assert.Equal($"Another process holds the migration lock on {db.Describe()}: waited 1 second(s). Refusing to run. Nothing was written.", e.Message);
        Assert.Empty(await scratch.SnapshotAsync());
    }

    [PostgresFact]
    public async Task A_track_newer_than_this_build_is_refused_and_nothing_is_written()
    {
        await scratch.ExecuteAsync(
            "CREATE TABLE probe_schema_migrations(version integer PRIMARY KEY, applied_at timestamptz NOT NULL)",
            "INSERT INTO probe_schema_migrations VALUES (1, now()), (2, now()), (3, now())");
        var before = await scratch.SnapshotAsync();
        await using var db = scratch.Database();

        var e = await Assert.ThrowsAsync<SchemaTooNewException>(() => db.MigrateAsync(Core, [Probe]));

        Assert.Equal(new SchemaTooNewException("probe", 3, 2).Message, e.Message);
        Assert.Equal(before, await scratch.SnapshotAsync());
    }

    [PostgresFact]
    public async Task A_core_newer_than_this_build_is_refused_and_nothing_is_written()
    {
        await scratch.ExecuteAsync(
            "CREATE TABLE schema_migrations(version integer PRIMARY KEY, applied_at timestamptz NOT NULL)",
            "INSERT INTO schema_migrations VALUES (4, now())");
        var before = await scratch.SnapshotAsync();
        await using var db = scratch.Database();

        var e = await Assert.ThrowsAsync<SchemaTooNewException>(() => db.MigrateAsync(Core, [Probe]));

        Assert.Equal(new SchemaTooNewException(4, 3).Message, e.Message);
        Assert.Equal(before, await scratch.SnapshotAsync());
    }

    [PostgresFact]
    public async Task A_track_declared_twice_or_a_module_named_core_is_refused_and_nothing_is_written()
    {
        await using var db = scratch.Database();

        var twice = await Assert.ThrowsAsync<SchemaTrackException>(() => db.MigrateAsync(Core, [Probe, Probe]));
        Assert.Equal("The migration track \"probe\" is declared twice. Refusing to run.", twice.Message);
        var named = await Assert.ThrowsAsync<SchemaTrackException>(() => db.MigrateAsync(Core, [Core]));
        Assert.Equal("A module's migration track is named \"core\", which is the core's. Refusing to run.", named.Message);

        Assert.Empty(await scratch.SnapshotAsync());
    }

    [PostgresFact]
    public async Task An_undeclared_track_table_is_refused_and_nothing_is_written()
    {
        await scratch.ExecuteAsync("CREATE TABLE other_schema_migrations(version integer PRIMARY KEY, applied_at timestamptz NOT NULL)");
        var before = await scratch.SnapshotAsync();
        await using var db = scratch.Database();

        var e = await Assert.ThrowsAsync<SchemaTrackException>(() => db.MigrateAsync(Core, [Probe]));

        Assert.Equal("The database has the migration track table(s) other_schema_migrations, which this build does not declare " +
            "(a module it does not have made them). Refusing to run: it could misread or damage that module's tables. Run a build with that module.", e.Message);
        Assert.Equal(before, await scratch.SnapshotAsync());
    }

    [PostgresFact]
    public async Task A_table_merely_named_like_a_track_table_is_not_one()
    {
        // LIKE's _ is a wildcard: unescaped, "xschemaxmigrations" would count as a track table.
        await scratch.ExecuteAsync("CREATE TABLE otherxschemaxmigrations(x integer)");
        await using var db = scratch.Database();

        await db.MigrateAsync(Core, [Probe]);
    }

    // mutation: the Postgres statements asked for only when a track is reached (the core would then have run)
    [PostgresFact]
    public async Task A_track_with_no_postgres_statements_is_refused_and_nothing_is_written()
    {
        await using var db = scratch.Database();
        var sqliteOnly = new MigrationTrack("probe", [["CREATE TABLE probe_items(id INTEGER PRIMARY KEY)"]]);

        var e = await Assert.ThrowsAsync<SchemaTrackException>(() => db.MigrateAsync(Core, [sqliteOnly]));

        Assert.Equal("The migration track \"probe\" has no postgres statements. Refusing to run. Nothing was written.", e.Message);
        Assert.Empty(await scratch.SnapshotAsync());
    }

    private static readonly MigrationTrack Baselined = new("probe", Pg(
        ["CREATE TABLE probe_items(id integer PRIMARY KEY)"],
        ["ALTER TABLE probe_items ADD COLUMN name text"]),
        new TrackBaseline(2, 1, 3, ["probe_items"], [("probe_items", "name")]));

    // mutation: adopt the baseline (record its versions when all of it is present)
    [PostgresTheory]
    [InlineData("CREATE TABLE probe_items(id integer)", "Present: table probe_items. Missing: column probe_items.name.")]
    [InlineData("CREATE TABLE probe_items(id integer, name text)", "Present: table probe_items, column probe_items.name. Missing: (none).")]
    public async Task A_baseline_track_whose_structure_is_already_there_is_refused_and_nothing_is_written(string structure, string named)
    {
        await using (var setup = scratch.Database()) await setup.MigrateAsync(Core, []);
        await scratch.ExecuteAsync(structure);
        var before = await scratch.SnapshotAsync();
        await using var db = scratch.Database();

        var e = await Assert.ThrowsAsync<SchemaBaselineException>(() => db.MigrateAsync(Core, [Baselined]));

        Assert.Equal("The probe track has no version table, but this database already has some of its structure. " + named +
            " Refusing to run: on PostgreSQL a track's baseline is never adopted, since no older build ran its statements in the core's track. Nothing was written.", e.Message);
        Assert.Equal(before, await scratch.SnapshotAsync());
    }

    [PostgresFact]
    public async Task A_baseline_track_with_none_of_its_structure_runs_from_version_1()
    {
        await using var db = scratch.Database();

        var outcomes = await db.MigrateAsync(Core, [Baselined]);

        Assert.Equal(new TrackOutcome("probe", null, 2, false), outcomes[1]);
        Assert.Contains("probe_items.name text", await scratch.SnapshotAsync());
    }

    // Review F5. mutation: the part-way rule applied to every track (the later build then refuses the module at version 2 of 3)
    [PostgresFact]
    public async Task A_module_recorded_at_its_empty_versions_runs_on_when_a_later_build_adds_statements()
    {
        await using var db = scratch.Database();
        var shippedEmpty = new MigrationTrack("probe", Pg([], []));
        Assert.Equal(new TrackOutcome("probe", null, 2, false), (await db.MigrateAsync(Core, [shippedEmpty]))[1]);

        var later = new MigrationTrack("probe", Pg([], [], ["CREATE TABLE probe_items(id integer PRIMARY KEY)"]));
        Assert.Equal(new TrackOutcome("probe", 2, 3, false), (await db.MigrateAsync(Core, [later]))[1]);
        Assert.Contains("probe_items.id integer", await scratch.SnapshotAsync());
    }

    // Review F5: the core's rule stands for a test core whose first version is empty too
    [PostgresFact]
    public async Task A_core_recorded_before_its_first_version_with_statements_is_refused()
    {
        await scratch.ExecuteAsync(
            "CREATE TABLE schema_migrations(version integer PRIMARY KEY, applied_at timestamptz NOT NULL)",
            "INSERT INTO schema_migrations VALUES (1, now())");
        var before = await scratch.SnapshotAsync();
        await using var db = scratch.Database();
        var core = new MigrationTrack(MigrationTrack.CoreName, Pg([], ["CREATE TABLE a(x integer)"]));

        var e = await Assert.ThrowsAsync<SchemaBaselineException>(() => db.MigrateAsync(core, []));

        Assert.StartsWith("The core track is recorded at version 1, but on PostgreSQL it starts at version 2", e.Message);
        Assert.Equal(before, await scratch.SnapshotAsync());
    }

    // mutation: readiness migrates (TrackVersionsAsync runs MigrateAsync, or writes the version tables)
    [PostgresFact]
    public async Task Track_versions_on_an_empty_database_creates_nothing()
    {
        await using var db = scratch.Database();

        var versions = await db.TrackVersionsAsync(Core, [Probe]);

        Assert.Equal([new TrackVersion("core", 3, null, 0), new TrackVersion("probe", 2, null, 0)], versions);
        Assert.Empty(await scratch.SnapshotAsync());
    }

    [PostgresFact]
    public async Task Track_versions_after_a_run_are_current_and_a_later_build_is_behind()
    {
        await using var db = scratch.Database();
        await db.MigrateAsync(Core, [Probe]);

        Assert.All(await db.TrackVersionsAsync(Core, [Probe]), v => Assert.True(v.IsCurrent));
        var later = new MigrationTrack("probe", Pg(["CREATE TABLE probe_items(id integer PRIMARY KEY)"], ["ALTER TABLE probe_items ADD COLUMN name text"], []));
        Assert.Equal(new TrackVersion("probe", 3, 2, 2), (await db.TrackVersionsAsync(Core, [later]))[1]);
    }

    // mutation: TrackVersionsAsync takes the migration lock (it then waits for the holder, and the limit cancels it)
    [PostgresFact]
    public async Task Track_versions_take_no_lock_and_read_while_a_migrator_holds_it()
    {
        await using (var setup = scratch.Database()) await setup.MigrateAsync(Core, [Probe]);
        await using var holder = await scratch.OpenAsync();
        await using (var take = new NpgsqlCommand($"SELECT pg_advisory_lock({PostgresDatabase.MigrationLockKey})", holder)) await take.ExecuteNonQueryAsync();
        await using var db = scratch.Database();

        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.All(await db.TrackVersionsAsync(Core, [Probe], limit.Token), v => Assert.True(v.IsCurrent));
    }
}

/// <summary>
/// The migrator's settings, with no server: the lock timeout and the lock's key. (Through <see cref="ISchemaMigrator"/> it runs the core's
/// real track: PostgresCoreBaselineTests.)
/// </summary>
public class PostgresMigratorRefusalTests
{
    // mutation (review): the setter's check dropped (0 and under 1 ms then reach SET LOCAL as '0ms': wait forever)
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(-1.0)]
    public async Task A_lock_timeout_under_1_ms_is_refused(double milliseconds)
    {
        await using var db = PostgresDatabase.Create(PostgresConnectionString.Parse("Host=127.0.0.1;Port=1;Database=packmoat;Username=packmoat;Password=x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => db.LockTimeout = TimeSpan.FromMilliseconds(milliseconds));
        Assert.Equal(TimeSpan.FromSeconds(60), db.LockTimeout);
        db.LockTimeout = TimeSpan.FromMilliseconds(1);
        Assert.Equal(TimeSpan.FromMilliseconds(1), db.LockTimeout);
    }

    [Fact]
    public void The_migration_lock_key_is_packmoat_in_ASCII() =>
        Assert.Equal("packmoat", System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(PostgresDatabase.MigrationLockKey).Reverse().ToArray()));
}

/// <summary>
/// A database of its own on the test server: CREATE DATABASE pm_&lt;guid&gt; when made, DROP DATABASE … WITH (FORCE) when disposed.
/// Public so a module's test project, which references this one, can make and read one too.
/// </summary>
public sealed class PostgresScratchDatabase : IAsyncDisposable
{
    private readonly string name;
    private readonly string connectionString;

    private PostgresScratchDatabase(string name, string connectionString)
    {
        this.name = name;
        this.connectionString = connectionString;
    }

    /// <summary>The database's name (pm_ and a GUID).</summary>
    public string Name => name;

    /// <summary>
    /// An empty database, or (with <paramref name="template"/>) a copy of that one: CREATE DATABASE … TEMPLATE. With <paramref name="owner"/>,
    /// that role owns it (and so its public schema, which PostgreSQL 15 and newer give to the database's owner).
    /// </summary>
    public static async Task<PostgresScratchDatabase> CreateAsync(string? template = null, string? owner = null)
    {
        var name = $"pm_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(Admin()))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}" + (template is null ? "" : $" TEMPLATE {template}") + (owner is null ? "" : $" OWNER {owner}"), admin);
            await create.ExecuteNonQueryAsync();
        }
        return new PostgresScratchDatabase(name, new NpgsqlConnectionStringBuilder(PostgresTestServer.ConnectionString) { Database = name }.ConnectionString);
    }

    private static readonly SemaphoreSlim TemplateGate = new(1, 1);

    /// <summary>The templates this run made, by the names of the module tracks each was migrated with ("" for the core's alone).</summary>
    private static readonly Dictionary<string, PostgresScratchDatabase> Templates = new(StringComparer.Ordinal);

    /// <summary>
    /// A database at the core's current schema, then <paramref name="modules"/>' (none: the core's alone): a copy of the template
    /// for exactly those tracks, which is made once per test run, on first use, by the migrator (<see cref="ISchemaMigrator"/>), and
    /// dropped when the Postgres collection ends (<see cref="PostgresTemplateFixture"/>). An engine that adds a module's track so gets a template of its own beside the core's. Copying is far quicker than migrating each database. A template is never
    /// connected to after it is made: PostgreSQL copies a database only while nothing is connected to it.
    /// </summary>
    public static async Task<PostgresScratchDatabase> FromTemplateAsync(IReadOnlyList<MigrationTrack>? modules = null) =>
        (await FromTemplateAsync(modules, asAppRole: false)).Scratch;

    /// <summary>
    /// As <see cref="FromTemplateAsync(IReadOnlyList{MigrationTrack}?)"/>; with <paramref name="asAppRole"/> the template is
    /// owned by this run's owner role (<see cref="RunRoles"/>) and migrated by it with <c>db migrate --grant-to</c> this run's app role, as
    /// production's owner would, and each copy is owned by the owner role too. Returns the copy and, with <paramref name="asAppRole"/>, the roles.
    /// </summary>
    public static async Task<(PostgresScratchDatabase Scratch, PostgresTestRoles? Roles)> FromTemplateAsync(IReadOnlyList<MigrationTrack>? modules, bool asAppRole)
    {
        modules ??= [];
        var key = (asAppRole ? "app-role:" : "") + string.Join(",", modules.Select(m => m.Name));
        await TemplateGate.WaitAsync();
        try
        {
            var roles = asAppRole ? RunRoles ??= await PostgresTestRoles.CreateAsync() : null;
            if (!Templates.TryGetValue(key, out var template))
            {
                var made = await CreateAsync(owner: roles?.Owner);
                try
                {
                    if (roles is null)
                    {
                        await using var db = made.Database();
                        await ((ISchemaMigrator)db).MigrateAsync(modules);
                    }
                    else
                    {
                        await using var db = roles.Database(made, roles.Owner);
                        await db.MigrateAndGrantAsync(modules, roles.App);
                    }
                }
                catch
                {
                    await made.DisposeAsync();
                    throw;
                }
                // The migrator's pooled connections are closed with its data source (disposed above), so nothing holds the template open.
                Templates[key] = template = made;
            }
            return (await CreateAsync(template.Name, roles?.Owner), roles);
        }
        finally
        {
            TemplateGate.Release();
        }
    }

    /// <summary>
    /// This run's owner and app roles, made with the first app-role template and dropped with the templates
    /// (<see cref="DropTemplateAsync"/>), after them: a role is dropped only once no database holds anything of it.
    /// </summary>
    private static PostgresTestRoles? RunRoles;

    /// <summary>
    /// Drops every template this run made, then this run's roles. The next <see cref="FromTemplateAsync(IReadOnlyList{MigrationTrack}?)"/>
    /// makes its template again.
    /// </summary>
    public static async Task DropTemplateAsync()
    {
        await TemplateGate.WaitAsync();
        try
        {
            var made = Templates.Values.ToList();
            Templates.Clear();
            try
            {
                foreach (var template in made) await template.DisposeAsync();
            }
            finally
            {
                var roles = RunRoles;
                RunRoles = null;
                if (roles is not null) await roles.DisposeAsync();
            }
        }
        finally
        {
            TemplateGate.Release();
        }
    }

    /// <summary>The test server's own database, unpooled, for CREATE and DROP.</summary>
    private static string Admin() => new NpgsqlConnectionStringBuilder(PostgresTestServer.ConnectionString) { Pooling = false }.ConnectionString;

    /// <summary>A migrator over this database; the caller disposes it.</summary>
    public PostgresDatabase Database(TimeSpan? lockTimeout = null)
    {
        var db = PostgresDatabase.Create(PostgresConnectionString.Parse(connectionString));
        if (lockTimeout is { } t) db.LockTimeout = t;
        return db;
    }

    /// <summary>A plain, unpooled connection to this database.</summary>
    public async Task<NpgsqlConnection> OpenAsync()
    {
        var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await c.OpenAsync();
        return c;
    }

    public async Task ExecuteAsync(params string[] statements)
    {
        await using var c = await OpenAsync();
        foreach (var sql in statements)
        {
            await using var cmd = new NpgsqlCommand(sql, c);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Every relation in the current schema as "table.column type" lines, then every version table's rows as "table: version applied_at":
    /// equal snapshots mean nothing was created, altered or recorded.
    /// </summary>
    public async Task<List<string>> SnapshotAsync()
    {
        await using var c = await OpenAsync();
        var lines = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT line FROM (
              SELECT c.relname::text || COALESCE('.' || a.attname::text || ' ' || format_type(a.atttypid, a.atttypmod), ' (' || c.relkind::text || ')') AS line
              FROM pg_class c LEFT JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
              WHERE c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = current_schema())) s
            ORDER BY line COLLATE "C"
            """, c))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) lines.Add(r.GetString(0));
        foreach (var table in lines.Where(l => l.EndsWith("schema_migrations.version integer", StringComparison.Ordinal)).Select(l => l[..l.IndexOf('.')]).ToList())
        {
            await using var rows = new NpgsqlCommand($"SELECT version::text || ' ' || applied_at::text FROM {table} ORDER BY version", c);
            await using var r = await rows.ExecuteReaderAsync();
            while (await r.ReadAsync()) lines.Add($"{table}: {r.GetString(0)}");
        }
        return lines;
    }

    /// <summary>Sets this database's search_path for every connection opened from now on (ALTER DATABASE … SET).</summary>
    public Task SetSearchPathAsync(string path) => ExecuteAsync($"ALTER DATABASE {name} SET search_path TO {path}");

    /// <summary>The first column of <paramref name="sql"/>'s rows, as text.</summary>
    public async Task<List<string>> TextsAsync(string sql)
    {
        await using var c = await OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync();
        var texts = new List<string>();
        while (await r.ReadAsync()) texts.Add(r.GetString(0));
        return texts;
    }

    public async Task<int[]> VersionsAsync(string table)
    {
        await using var c = await OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT version FROM {table} ORDER BY version", c);
        await using var r = await cmd.ExecuteReaderAsync();
        var versions = new List<int>();
        while (await r.ReadAsync()) versions.Add(r.GetInt32(0));
        return [.. versions];
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(Admin());
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// The Postgres collection's fixture: when the collection's last test has run, it drops the migrated template every scratch database
/// was copied from (<see cref="PostgresScratchDatabase.FromTemplateAsync"/>), so a run leaves no database behind on the server.
/// </summary>
public sealed class PostgresTemplateFixture : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => PostgresScratchDatabase.DropTemplateAsync();
}
