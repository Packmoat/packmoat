using Npgsql;
using Packmoat.Infrastructure.Postgres;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// <c>db check-migrations</c> on PostgreSQL (<see cref="PostgresMigrationCheck"/>): this build's migrations run on the database
/// itself in one transaction, the result is compared with a fresh build in a scratch schema, and everything is rolled back. Whatever it
/// finds, the database after is the database before (<see cref="PostgresScratchDatabase.SnapshotAsync"/>, version rows and applied_at
/// included), and no scratch schema is left.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresMigrationCheckTests
{
    private static Dictionary<DatabaseEngine, string[][]> Pg(params string[][] migrations) => new() { [DatabaseEngine.Postgres] = migrations };

    /// <summary>A test core at version 1, and the same core at version 3 (a later build).</summary>
    private static readonly MigrationTrack CoreV1 = new(MigrationTrack.CoreName, Pg(
        ["CREATE TABLE a(id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, x integer)"]));

    private static readonly MigrationTrack CoreV3 = new(MigrationTrack.CoreName, Pg(
        ["CREATE TABLE a(id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, x integer)"],
        ["CREATE TABLE b(x integer NOT NULL, CONSTRAINT b_x CHECK (x > 0))", "CREATE INDEX b_by_x ON b(x)"],
        ["ALTER TABLE a ADD COLUMN y text",
         "CREATE FUNCTION a_refuse() RETURNS trigger LANGUAGE plpgsql SET search_path FROM CURRENT AS $$ BEGIN RAISE EXCEPTION 'no'; END $$",
         "CREATE TRIGGER a_no_delete BEFORE DELETE ON a FOR EACH ROW EXECUTE FUNCTION a_refuse()"]));

    /// <summary>Every schema but PostgreSQL's own: none named packmoat_check_ may outlive a check.</summary>
    private static Task<List<string>> SchemasAsync(PostgresScratchDatabase t) =>
        t.TextsAsync("SELECT nspname::text FROM pg_namespace WHERE nspname NOT LIKE 'pg\\_%' AND nspname <> 'information_schema' ORDER BY 1");

    [PostgresFact]   // mutation: the check commits instead of rolling back
    public async Task A_database_a_version_behind_is_migrated_compared_and_rolled_back()
    {
        await using var t = await PostgresScratchDatabase.CreateAsync();
        await using var db = t.Database();
        await db.MigrateAsync(CoreV1, []);
        await t.ExecuteAsync("INSERT INTO a(x) VALUES (1), (2)");
        var before = await t.SnapshotAsync();
        var schemas = await SchemasAsync(t);

        var report = await PostgresMigrationCheck.RunAsync(db, CoreV3, []);

        Assert.True(report.Ok, string.Join("; ", report.SchemaDifferences) + report.Refusal);
        Assert.Equal([new MigrationDryRun.TrackLine("core", 1, 3, "migrated v1 to v3")], report.Tracks);
        Assert.Equal([new MigrationDryRun.RowLine("a", 2, 2), new MigrationDryRun.RowLine("b", null, 0)], report.Rows);
        Assert.Empty(report.SchemaDifferences);
        Assert.Equal(before, await t.SnapshotAsync());
        Assert.Equal(schemas, await SchemasAsync(t));
    }

    /// <summary>
    /// The core's own schema (and a module's, as production's start declares them) checked on a migrated copy: equal to a fresh build in
    /// every object, its functions' search_path and its indexes' schema included once the schema's name is taken out.
    /// </summary>
    [PostgresFact]   // mutation: the schema's name left in the definitions (every function and index then differs)
    public async Task The_core_schema_checks_equal_to_a_fresh_build()
    {
        await using var t = await PostgresScratchDatabase.FromTemplateAsync(TestTracks.Modules);
        await using var db = t.Database();
        var before = await t.SnapshotAsync();

        var report = await PostgresMigrationCheck.RunAsync(db, TestTracks.Modules);

        Assert.True(report.Ok, string.Join("; ", report.SchemaDifferences) + report.Refusal);
        Assert.All(report.Tracks, l => Assert.Equal("unchanged", l.Verdict));
        Assert.True(report.Rows.Count >= 20, "too few tables to mean anything");
        Assert.Equal(before, await t.SnapshotAsync());
    }

    /// <summary>What a hand-made change leaves (an index added, a trigger turned off, a column's default changed) is reported, by kind and name.</summary>
    [PostgresFact]   // mutation: the comparison made vacuous
    public async Task A_hand_made_change_is_a_difference()
    {
        await using var t = await PostgresScratchDatabase.CreateAsync();
        await using var db = t.Database();
        await db.MigrateAsync(CoreV3, []);
        await t.ExecuteAsync("CREATE INDEX extra ON a(x)", "ALTER TABLE a DISABLE TRIGGER a_no_delete", "ALTER TABLE b ALTER COLUMN x SET DEFAULT 7");
        var before = await t.SnapshotAsync();

        var report = await PostgresMigrationCheck.RunAsync(db, CoreV3, []);

        Assert.False(report.Ok);
        Assert.Null(report.Refusal);
        Assert.Equal(["index extra: only in this database", "table b: differs from a fresh build", "trigger a.a_no_delete: differs from a fresh build"],
            report.SchemaDifferences);
        Assert.Equal(before, await t.SnapshotAsync());
    }

    /// <summary>
    /// A SECURITY DEFINER function in another schema is a difference too (a fresh build makes none outside its schema); a plain
    /// function there, and a superuser's SECURITY DEFINER function an extension owns, are not; one a role other than a superuser owns is,
    /// member of an extension or not (ALTER EXTENSION … ADD FUNCTION would otherwise hide it).
    /// </summary>
    [PostgresFact]   // mutations: the other schemas' look removed, limited to the migration's schema, or every extension member exempt
    public async Task A_SECURITY_DEFINER_function_in_another_schema_is_a_difference()
    {
        var plain = $"pm_plain_{Guid.NewGuid():N}";
        await PostgresTestRoles.AdminAsync($"CREATE ROLE {plain} NOLOGIN");
        try
        {
            await using var t = await PostgresScratchDatabase.CreateAsync();
            await using var db = t.Database();
            await db.MigrateAsync(CoreV3, []);
            await t.ExecuteAsync("CREATE SCHEMA rv_tools",
                "CREATE FUNCTION rv_tools.purge_all(integer) RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
                "CREATE FUNCTION rv_tools.plain() RETURNS integer LANGUAGE sql AS 'SELECT 1'",
                "CREATE FUNCTION rv_tools.extension_s() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
                "ALTER EXTENSION plpgsql ADD FUNCTION rv_tools.extension_s()",
                "CREATE FUNCTION rv_tools.added() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
                $"ALTER FUNCTION rv_tools.added() OWNER TO {plain}",
                "ALTER EXTENSION plpgsql ADD FUNCTION rv_tools.added()");

            var report = await PostgresMigrationCheck.RunAsync(db, CoreV3, []);

            Assert.False(report.Ok);
            Assert.Equal([
                "function rv_tools.added(): SECURITY DEFINER, outside the schema, only in this database",
                "function rv_tools.purge_all(integer): SECURITY DEFINER, outside the schema, only in this database",
            ], report.SchemaDifferences);
        }
        finally
        {
            await PostgresTestRoles.AdminAsync($"DROP ROLE IF EXISTS {plain}");
        }
    }

    /// <summary>A migration statement the data refuses (NOT NULL added over rows) is the report's refusal, and nothing is written.</summary>
    [PostgresFact]
    public async Task A_statement_the_data_refuses_is_reported_and_nothing_is_written()
    {
        await using var t = await PostgresScratchDatabase.CreateAsync();
        await using var db = t.Database();
        await db.MigrateAsync(CoreV1, []);
        await t.ExecuteAsync("INSERT INTO a(x) VALUES (1)");
        var before = await t.SnapshotAsync();
        var refusing = new MigrationTrack(MigrationTrack.CoreName, Pg(CoreV1.StatementsFor(DatabaseEngine.Postgres)[0], ["ALTER TABLE a ADD COLUMN z integer NOT NULL"]));

        var report = await PostgresMigrationCheck.RunAsync(db, refusing, []);

        Assert.False(report.Ok);
        Assert.Contains("SQLSTATE 23502", report.Refusal, StringComparison.Ordinal);
        Assert.Equal([new MigrationDryRun.TrackLine("core", 1, 1, "not run (refused)")], report.Tracks);
        Assert.Equal(before, await t.SnapshotAsync());
    }

    [PostgresFact]
    public async Task A_database_newer_than_the_build_is_refused()
    {
        await using var t = await PostgresScratchDatabase.CreateAsync();
        await using var db = t.Database();
        await db.MigrateAsync(CoreV3, []);
        var before = await t.SnapshotAsync();

        var report = await PostgresMigrationCheck.RunAsync(db, CoreV1, []);

        Assert.False(report.Ok);
        Assert.NotNull(report.Refusal);
        Assert.Equal(before, await t.SnapshotAsync());
    }

    /// <summary>The check waits at most 5 seconds for a lock, not the migration's 60: a migration lock held elsewhere is its refusal.</summary>
    [PostgresFact]   // mutation: the check given the migration's lock_timeout
    public async Task The_check_waits_five_seconds_for_the_migration_lock_and_refuses()
    {
        await using var t = await PostgresScratchDatabase.CreateAsync();
        await using var db = t.Database();
        await db.MigrateAsync(CoreV1, []);
        await using var holder = await t.OpenAsync();
        await using var holding = await holder.BeginTransactionAsync();
        await using (var take = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({PostgresDatabase.MigrationLockKey})", holder, holding)) await take.ExecuteNonQueryAsync();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var report = await PostgresMigrationCheck.RunAsync(db, CoreV3, []);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"waited {clock.Elapsed}");
        Assert.Contains("waited 5 second(s)", report.Refusal, StringComparison.Ordinal);
        Assert.False(report.Ok);
    }

    /// <summary>
    /// An owner role with no CREATE on the database cannot make the scratch schema: the check says so, REFUSED and not
    /// checked, and writes nothing.
    /// </summary>
    [PostgresFact]   // mutation: CREATE SCHEMA outside the reported step (an unhandled exception)
    public async Task An_owner_without_CREATE_on_the_database_gets_a_refused_check_not_a_crash()
    {
        var roles = await PostgresTestRoles.CreateAsync();
        try
        {
            await using var t = await PostgresScratchDatabase.CreateAsync();   // the test server's login's database: the owner role may not CREATE in it
            await t.ExecuteAsync($"GRANT CREATE ON SCHEMA public TO {roles.Owner}");
            await using var owner = roles.Database(t, roles.Owner);
            await owner.MigrateAsync(CoreV1, []);
            var before = await t.SnapshotAsync();
            var schemas = await SchemasAsync(t);

            var report = await PostgresMigrationCheck.RunAsync(owner, CoreV3, []);

            Assert.False(report.Ok);
            Assert.Null(report.Refusal);
            Assert.Contains("could not build a fresh schema", report.CompareFailure, StringComparison.Ordinal);
            Assert.Contains("SQLSTATE 42501", report.CompareFailure, StringComparison.Ordinal);
            Assert.Equal([new MigrationDryRun.TrackLine("core", 1, 3, "migrated v1 to v3")], report.Tracks);
            Assert.Equal(before, await t.SnapshotAsync());
            Assert.Equal(schemas, await SchemasAsync(t));
        }
        finally
        {
            await roles.DisposeAsync();
        }
    }

    /// <summary>
    /// A check stopped part way through its fresh build (here cancelled while a statement sleeps in the scratch schema only) leaves no
    /// scratch schema behind: it was made in the check's own transaction, never committed.
    /// </summary>
    [PostgresFact]   // mutation: the scratch schema made on a connection of its own and dropped after the report
    public async Task A_check_stopped_during_its_fresh_build_leaves_no_scratch_schema()
    {
        await using var t = await PostgresScratchDatabase.CreateAsync();
        await using var db = t.Database();
        var sleepsInScratch = new MigrationTrack(MigrationTrack.CoreName, Pg(
            ["CREATE TABLE a(id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, x integer)"],
            [@"SELECT pg_sleep(CASE WHEN current_schema() LIKE 'packmoat\_check\_%' THEN 30 ELSE 0 END)"]));
        await db.MigrateAsync(CoreV1, []);
        var schemas = await SchemasAsync(t);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PostgresMigrationCheck.RunAsync(db, sleepsInScratch, [], stop.Token));

        Assert.Equal(schemas, await SchemasAsync(t));
        Assert.Equal(new[] { 1 }, await t.VersionsAsync("schema_migrations"));
    }

    /// <summary>
    /// The command line's words (<see cref="PostgresDbCommands"/>), run as production's owner would: migrate with --grant-to, again (nothing
    /// to do), then the check, on a database the owner role owns.
    /// </summary>
    [PostgresFact]
    public async Task The_db_commands_migrate_grant_and_check_as_the_owner()
    {
        var roles = await PostgresTestRoles.CreateAsync();
        try
        {
            await using var t = await PostgresScratchDatabase.CreateAsync(owner: roles.Owner);
            await using var owner = roles.Database(t, roles.Owner);

            var first = await RunAsync(owner, "db", "migrate", "--grant-to", roles.App);
            Assert.Equal(0, first.Code);
            Assert.Contains($"track core: none -> v{CoreSchema.Track.LatestVersion}", first.Out, StringComparison.Ordinal);
            Assert.Matches($@"granted to {roles.App} on schema public: USAGE on the schema; SELECT, INSERT, UPDATE, DELETE on \d+ table\(s\); SELECT on 1 version table\(s\); USAGE on \d+ sequence\(s\)\.", first.Out);
            Assert.DoesNotContain(roles.Password, first.Out, StringComparison.Ordinal);

            var again = await RunAsync(owner, "db", "migrate", "--grant-to", roles.App);
            Assert.Equal(0, again.Code);
            Assert.Contains($"track core: unchanged at v{CoreSchema.Track.LatestVersion}", again.Out, StringComparison.Ordinal);

            var check = await RunAsync(owner, "db", "check-migrations");
            Assert.True(check.Code == 0, check.Out + check.Err);
            Assert.Contains("rolled back: nothing was written", check.Out, StringComparison.Ordinal);
            Assert.EndsWith("result: OK", check.Out.TrimEnd(), StringComparison.Ordinal);

            var missing = await RunAsync(owner, "db", "migrate", "--grant-to", "pm_nobody");
            Assert.Equal(1, missing.Code);
            Assert.StartsWith("REFUSED: There is no role named pm_nobody", missing.Err, StringComparison.Ordinal);

            // This install has an app role, so --single-role (which would leave its new tables ungranted) is refused.
            var single = await RunAsync(owner, "db", "migrate", "--single-role");
            Assert.Equal(1, single.Code);
            Assert.StartsWith($"REFUSED: --single-role, but {roles.App} also holds privileges on the schema's tables", single.Err, StringComparison.Ordinal);
            Assert.Contains("nothing was written", single.Err, StringComparison.Ordinal);
        }
        finally
        {
            await roles.DisposeAsync();
        }
    }

    /// <summary>
    /// <c>--single-role</c> on a one-role install (no role but the owner holds anything on the tables) migrates, twice, warning each time;
    /// once another role holds a privilege on a table (PUBLIC too), it is refused and writes nothing.
    /// </summary>
    [PostgresFact]   // mutation: the single-role refusal removed
    public async Task Single_role_migrates_a_one_role_install_and_is_refused_once_another_role_holds_a_table()
    {
        await using var t = await PostgresScratchDatabase.CreateAsync();
        await using var db = t.Database();

        foreach (var _ in new[] { 1, 2 })
        {
            var run = await RunAsync(db, "db", "migrate", "--single-role");
            Assert.True(run.Code == 0, run.Err);
            Assert.Contains("warning: no app role was granted to", run.Out, StringComparison.Ordinal);
        }
        await t.ExecuteAsync("GRANT SELECT ON tenants TO PUBLIC");
        var before = await t.SnapshotAsync();

        var refused = await RunAsync(db, "db", "migrate", "--single-role");

        Assert.Equal(1, refused.Code);
        Assert.StartsWith("REFUSED: --single-role, but PUBLIC also holds privileges", refused.Err, StringComparison.Ordinal);
        Assert.Equal(before, await t.SnapshotAsync());
    }

    internal static async Task<(int Code, string Out, string Err)> RunAsync(PostgresDatabase db, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await PostgresDbCommands.RunAsync(args, db, [], output, error);
        return (code, output.ToString(), error.ToString());
    }
}

/// <summary>The db commands' arguments: a malformed command is refused before anything connects, so these need no server.</summary>
public class PostgresDbCommandArgumentTests
{
    [Theory]
    [InlineData("db", "migrate")]
    [InlineData("db", "migrate", "--grant-to")]
    [InlineData("db", "migrate", "--grant-to", "")]
    [InlineData("db", "migrate", "--grant-to", "--single-role")]
    [InlineData("db", "migrate", "--grant-to", "app", "--single-role")]
    [InlineData("db", "migrate", "--single-role", "--grant-to", "app")]
    [InlineData("db", "migrate", "app")]
    [InlineData("db", "check-migrations", "backup.db")]
    [InlineData("db", "verify")]
    [InlineData("db")]
    public async Task A_malformed_db_command_is_refused_before_anything_connects(params string[] args)
    {
        // A server that is never reached: had the command connected, it would have thrown.
        await using var db = PostgresDatabase.Create(PostgresConnectionString.Parse("Host=db.invalid;Database=packmoat;Username=packmoat_owner"));
        var (code, output, error) = await PostgresMigrationCheckTests.RunAsync(db, args);
        Assert.Equal(2, code);
        Assert.Empty(output);
        Assert.Contains("Nothing was run.", error, StringComparison.Ordinal);
        Assert.Contains(PostgresDbCommands.Usage, error, StringComparison.Ordinal);
    }
}
