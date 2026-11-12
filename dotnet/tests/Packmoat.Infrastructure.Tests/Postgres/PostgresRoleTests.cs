using Npgsql;
using Packmoat.Domain;
using Packmoat.Infrastructure.Postgres;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// Separate roles: the owner role migrates with <c>db migrate --grant-to</c> an app role
/// (<see cref="PostgresDatabase.MigrateAndGrantAsync(IReadOnlyList{MigrationTrack}, string, CancellationToken)"/>), and the app role can
/// read and write the rows and do nothing else: not turn a guard off, not empty a table, not change the schema, not become the owner.
/// Each test makes its own owner and app roles (no superuser) and a database the owner owns, and drops them all in a finally.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresRoleTests
{
    private static Dictionary<DatabaseEngine, string[][]> Pg(params string[][] migrations) => new() { [DatabaseEngine.Postgres] = migrations };

    /// <summary>A module a later build adds: a table with an identity column, so a sequence too.</summary>
    private static readonly MigrationTrack Later = new("later", Pg(
        ["CREATE TABLE later_items(id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text NOT NULL)"]));

    /// <summary>Fresh roles and a database the owner owns (empty); <paramref name="body"/> runs; everything is dropped after, whatever happens.</summary>
    private static async Task WithRolesAsync(Func<PostgresTestRoles, PostgresScratchDatabase, Task> body)
    {
        var roles = await PostgresTestRoles.CreateAsync();
        try
        {
            var t = await PostgresScratchDatabase.CreateAsync(owner: roles.Owner);
            try
            {
                await body(roles, t);
            }
            finally
            {
                await t.DisposeAsync();
            }
        }
        finally
        {
            await roles.DisposeAsync();
        }
    }

    /// <summary>Migrates <paramref name="t"/> as its owner with --grant-to the app role: the core's track, then <paramref name="modules"/>'.</summary>
    private static async Task<PostgresMigration> MigrateAsync(PostgresTestRoles roles, PostgresScratchDatabase t, params MigrationTrack[] modules)
    {
        await using var owner = roles.Database(t, roles.Owner);
        return await owner.MigrateAndGrantAsync(modules, roles.App);
    }

    /// <summary>Opens a connection to <paramref name="t"/> signed in as <paramref name="role"/>, unpooled.</summary>
    private static async Task<NpgsqlConnection> OpenAsync(PostgresTestRoles roles, PostgresScratchDatabase t, string role)
    {
        var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(roles.ConnectionString(t, role)) { Pooling = false }.ConnectionString);
        await c.OpenAsync();
        return c;
    }

    private static async Task<List<string>> TextsAsync(NpgsqlConnection c, string sql, string? app = null)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        if (app is not null) cmd.Parameters.AddWithValue("app", app);
        await using var r = await cmd.ExecuteReaderAsync();
        var texts = new List<string>();
        while (await r.ReadAsync()) texts.Add(r.GetString(0));
        return texts;
    }

    /// <summary>
    /// What the app role can really do to each table and sequence of the schema (has_table_privilege and has_sequence_privilege: its own
    /// grants, PUBLIC's and every role's it inherits), as "name: privileges", and to the schema itself.
    /// </summary>
    private static async Task<List<string>> EffectiveAsync(PostgresTestRoles roles, PostgresScratchDatabase t)
    {
        await using var c = await t.OpenAsync();
        var lines = await TextsAsync(c, """
            SELECT c.relname::text || ': ' || COALESCE((SELECT string_agg(x.p, ', ' ORDER BY x.p) FROM unnest(
                CASE WHEN c.relkind = 'S' THEN ARRAY['SELECT', 'UPDATE', 'USAGE'] ELSE ARRAY['DELETE', 'INSERT', 'MAINTAIN', 'REFERENCES', 'SELECT', 'TRIGGER', 'TRUNCATE', 'UPDATE'] END) x(p)
              WHERE CASE WHEN c.relkind = 'S' THEN has_sequence_privilege(@app, c.oid, x.p) ELSE has_table_privilege(@app, c.oid, x.p) END), '(none)')
            FROM pg_class c WHERE c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = current_schema()) AND c.relkind IN ('r', 'p', 'S')
            ORDER BY c.relname COLLATE "C"
            """, roles.App);
        lines.AddRange(await TextsAsync(c, """
            SELECT 'schema: ' || CASE WHEN has_schema_privilege(@app, current_schema(), 'USAGE') THEN 'USAGE' ELSE '' END
                || CASE WHEN has_schema_privilege(@app, current_schema(), 'CREATE') THEN ' CREATE' ELSE '' END
            """, roles.App));
        lines.AddRange(await TextsAsync(c, "SELECT 'member of: ' || r.rolname::text FROM pg_roles r WHERE r.rolname <> @app AND pg_has_role(@app, r.oid, 'MEMBER')", roles.App));
        return lines;
    }

    /// <summary>The app role's privileges on a migrated database: the four on each data table, SELECT on the version tables, USAGE on the sequences.</summary>
    private static async Task AssertExactGrantsAsync(PostgresTestRoles roles, PostgresScratchDatabase t)
    {
        var effective = await EffectiveAsync(roles, t);
        var tables = effective.Where(l => !l.StartsWith("schema:", StringComparison.Ordinal) && !l.StartsWith("member of:", StringComparison.Ordinal)).ToList();
        Assert.True(tables.Count >= 20, "too few tables to mean anything: " + string.Join("; ", tables));
        foreach (var line in tables)
        {
            var name = line[..line.IndexOf(':')];
            var expected = name.EndsWith("_seq", StringComparison.Ordinal) ? "USAGE"
                : PostgresRoleGrants.IsVersionTable(name) ? "SELECT" : "DELETE, INSERT, SELECT, UPDATE";
            Assert.Equal($"{name}: {expected}", line);
        }
        Assert.Contains("schema: USAGE", effective);
        Assert.DoesNotContain(effective, l => l.StartsWith("member of:", StringComparison.Ordinal));
    }

    [PostgresFact]   // mutations: GRANT ALL; TRUNCATE granted; the version tables given the four
    public Task The_owners_migration_grants_the_app_role_its_rows_and_nothing_more() => WithRolesAsync(async (roles, t) =>
    {
        var done = await MigrateAsync(roles, t);

        Assert.All(done.Tracks, o => Assert.Null(o.Before));
        var grants = Assert.IsType<PostgresGrants>(done.Grants);
        Assert.Equal(roles.App, grants.Role);
        Assert.Equal("public", grants.Schema);
        Assert.True(grants.Tables >= 20 && grants.VersionTables == 1 && grants.Sequences >= 3, grants.ToString());
        await AssertExactGrantsAsync(roles, t);
        await using var c = await t.OpenAsync();
        Assert.Equal(["false"], await TextsAsync(c, "SELECT has_schema_privilege('public', current_schema(), 'CREATE')::text"));
    });

    /// <summary>
    /// Every way an owner could turn a guard off, empty a table or change the schema, tried as the app role: each is refused for want of a
    /// privilege (42501, insufficient_privilege), not by a trigger, and afterwards every guard still fires and every row is there.
    /// </summary>
    [PostgresFact]   // mutations: GRANT ALL; TRUNCATE granted; the app role granted membership in the owner
    public Task The_app_role_cannot_turn_a_guard_off_empty_a_table_or_change_the_schema() => WithRolesAsync(async (roles, t) =>
    {
        await MigrateAsync(roles, t);
        await using (var owner = await OpenAsync(roles, t, roles.Owner))
        await using (var seed = new NpgsqlCommand(
            "INSERT INTO tenants(id, name, upstream_base, allowed_path_prefix, feed_index_url, cooldown_days, created_at) " +
            "VALUES('acme', 'Acme', 'https://x/', '', 'https://x/i.json', 14, now())", owner))
            await seed.ExecuteNonQueryAsync();

        string[] refused =
        [
            "ALTER TABLE audit_events DISABLE TRIGGER audit_no_delete",
            "ALTER TABLE audit_events DISABLE TRIGGER ALL",
            "ALTER TABLE tenants DISABLE TRIGGER USER",
            "ALTER TABLE tenant_purges ENABLE REPLICA TRIGGER tenant_purges_no_update",
            "TRUNCATE audit_events",
            "TRUNCATE tenants CASCADE",
            "DROP TABLE tenant_purges",
            "DROP TRIGGER audit_no_delete ON audit_events",
            "DROP FUNCTION refuse()",
            "ALTER FUNCTION audit_no_delete() RENAME TO audit_no_delete_off",
            "ALTER FUNCTION refuse() SECURITY DEFINER",
            "CREATE OR REPLACE FUNCTION refuse() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN OLD; END $$",
            "CREATE TABLE app_made(x integer)",
            "CREATE FUNCTION app_made() RETURNS integer LANGUAGE sql AS 'SELECT 1'",
            "CREATE INDEX app_made ON tenants(name)",
            "ALTER TABLE tenants ADD COLUMN app_made text",
            "SET session_replication_role = replica",
            "ALTER SYSTEM SET session_replication_role = replica",
            $"SET ROLE {roles.Owner}",
            $"GRANT {roles.Owner} TO {roles.App}",
        ];
        await using (var app = await OpenAsync(roles, t, roles.App))
        {
            foreach (var sql in refused)
            {
                await using var cmd = new NpgsqlCommand(sql, app);
                var e = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
                Assert.True(e.SqlState == PostgresErrorCodes.InsufficientPrivilege, $"{sql}: {e.SqlState} {e.MessageText}, not insufficient_privilege");
            }
            Assert.Equal(["origin"], await TextsAsync(app, "SELECT current_setting('session_replication_role')"));
        }

        await using var c = await t.OpenAsync();
        Assert.Equal(["1"], await TextsAsync(c, "SELECT COUNT(*)::text FROM tenants"));
        Assert.Equal(["tenant_purges"], await TextsAsync(c, "SELECT relname::text FROM pg_class WHERE relname = 'tenant_purges'"));
        Assert.Equal(PostgresStartupChecks.PinnedTriggers, await TextsAsync(c,
            "SELECT r.relname::text || '.' || g.tgname::text FROM pg_trigger g JOIN pg_class r ON r.oid = g.tgrelid WHERE NOT g.tgisinternal AND g.tgenabled = 'O' " +
            "ORDER BY r.relname::text || '.' || g.tgname::text COLLATE \"C\""));
        await AssertExactGrantsAsync(roles, t);
    });

    /// <summary>The start's checks pass for the app role exactly as <c>db migrate --grant-to</c> leaves it, and for the owner refuse.</summary>
    [PostgresFact]   // mutations: GRANT ALL; the app role granted membership in the owner; CREATE through PUBLIC left
    public Task The_start_s_checks_pass_for_an_app_role_set_up_by_grant_to() => WithRolesAsync(async (roles, t) =>
    {
        await MigrateAsync(roles, t);

        await using var app = roles.Database(t, roles.App);
        var facts = await PostgresStartupChecks.GatherAsync(app, [], new SystemClock());
        Assert.False(facts.Superuser);
        Assert.Empty(facts.Owned);
        Assert.False(facts.CreateOnSchema);
        Assert.False(facts.CanSetReplicationRole);
        Assert.All(facts.Tracks, v => Assert.True(v.IsCurrent));
        Assert.DoesNotContain(PostgresStartupChecks.Evaluate(facts, new PostgresStartupOptions()),
            f => !f.Message.StartsWith("The database's clock", StringComparison.Ordinal));

        // The negative control: the owner, signed in itself, owns the schema and the tables, and is refused.
        await using var owner = roles.Database(t, roles.Owner);
        var ownerFacts = await PostgresStartupChecks.GatherAsync(owner, [], new SystemClock());
        Assert.NotEmpty(ownerFacts.Owned);
        Assert.Contains(PostgresStartupChecks.Evaluate(ownerFacts, new PostgresStartupOptions()), f => f.Kind == PostgresFindingKind.Refusal);
    });

    /// <summary>A table (and its identity sequence) that a later build's migration adds gets the app role's grants in that same migration.</summary>
    [PostgresFact]   // mutation: the grants not re-applied after a later migration
    public Task A_table_a_later_migration_adds_gets_the_grants() => WithRolesAsync(async (roles, t) =>
    {
        await MigrateAsync(roles, t);
        var done = await MigrateAsync(roles, t, Later);

        Assert.Contains(new TrackOutcome("later", null, 1, false), done.Tracks);
        Assert.Contains(done.Tracks, o => o.Name == MigrationTrack.CoreName && o.Before == o.After);
        var effective = await EffectiveAsync(roles, t);
        Assert.Contains("later_items: DELETE, INSERT, SELECT, UPDATE", effective);
        Assert.Contains("later_items_id_seq: USAGE", effective);
        Assert.Contains("later_schema_migrations: SELECT", effective);
        await AssertExactGrantsAsync(roles, t);

        await using var app = await OpenAsync(roles, t, roles.App);
        await using (var insert = new NpgsqlCommand("INSERT INTO later_items(name) VALUES('a') RETURNING id", app))
            Assert.Equal(1L, await insert.ExecuteScalarAsync());
        await using var truncate = new NpgsqlCommand("TRUNCATE later_items", app);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => truncate.ExecuteNonQueryAsync())).SqlState);
    });

    /// <summary>
    /// The grants are applied on every migration, when no version runs too: one taken away by hand comes back, and one added by hand (TRUNCATE)
    /// is taken away.
    /// </summary>
    [PostgresFact]   // mutation: the grants applied only when a version ran
    public Task Every_migration_applies_the_grants_again_when_nothing_ran() => WithRolesAsync(async (roles, t) =>
    {
        await MigrateAsync(roles, t);
        await using (var owner = await OpenAsync(roles, t, roles.Owner))
            foreach (var sql in new[] { $"REVOKE INSERT ON tenants FROM {roles.App}", $"GRANT TRUNCATE ON audit_events TO {roles.App}" })
            {
                await using var cmd = new NpgsqlCommand(sql, owner);
                await cmd.ExecuteNonQueryAsync();
            }
        Assert.Contains("tenants: DELETE, SELECT, UPDATE", await EffectiveAsync(roles, t));

        var done = await MigrateAsync(roles, t);

        Assert.All(done.Tracks, o => Assert.Equal(o.Before, o.After));
        await AssertExactGrantsAsync(roles, t);
    });

    /// <summary>
    /// A database from an older template (or upgraded in place) can still give PUBLIC, so every role, CREATE on the schema. The owner's
    /// migration takes it back, so the app role cannot make an object there.
    /// </summary>
    [PostgresFact]   // mutation: CREATE left to PUBLIC on the schema
    public Task CREATE_given_to_PUBLIC_on_the_schema_is_taken_back() => WithRolesAsync(async (roles, t) =>
    {
        await using (var owner = await OpenAsync(roles, t, roles.Owner))
        await using (var grant = new NpgsqlCommand("GRANT CREATE ON SCHEMA public TO PUBLIC", owner))
            await grant.ExecuteNonQueryAsync();
        await using (var c = await t.OpenAsync())
            Assert.Equal(["true"], await TextsAsync(c, "SELECT has_schema_privilege(@app, 'public', 'CREATE')::text", roles.App));

        await MigrateAsync(roles, t);

        await using (var c = await t.OpenAsync())
            Assert.Equal(["false", "false"], await TextsAsync(c,
                "SELECT has_schema_privilege(@app, 'public', 'CREATE')::text UNION ALL SELECT has_schema_privilege('public', 'public', 'CREATE')::text", roles.App));
        await using var app = await OpenAsync(roles, t, roles.App);
        await using var create = new NpgsqlCommand("CREATE TABLE app_made(x integer)", app);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => create.ExecuteNonQueryAsync())).SqlState);
    });

    /// <summary>
    /// Where the owner role cannot take CREATE back (the schema is another role's, which gave it to PUBLIC), the migration is refused,
    /// naming it, and writes nothing: the server would refuse to start on such a role anyway.
    /// </summary>
    [PostgresFact]   // mutation: the app role's check after the grants removed
    public async Task CREATE_on_the_schema_the_owner_cannot_take_back_refuses_the_migration_and_writes_nothing()
    {
        var roles = await PostgresTestRoles.CreateAsync();
        try
        {
            // The database (and so its public schema) is the test server's login's; it lets the owner role make tables, and gives PUBLIC CREATE.
            await using var t = await PostgresScratchDatabase.CreateAsync();
            await t.ExecuteAsync($"GRANT CREATE ON SCHEMA public TO {roles.Owner}", "GRANT CREATE ON SCHEMA public TO PUBLIC");
            var before = await t.SnapshotAsync();

            var e = await Assert.ThrowsAsync<PostgresRoleException>(() => MigrateAsync(roles, t));

            Assert.Contains("can CREATE in the schema", e.Message, StringComparison.Ordinal);
            Assert.Contains("nothing was written", e.Message, StringComparison.Ordinal);
            Assert.Equal(before, await t.SnapshotAsync());
        }
        finally
        {
            await roles.DisposeAsync();
        }
    }

    /// <summary>
    /// An app role that is a member of the owner could SET ROLE to it and do all an owner does: refused, naming it, and nothing written.
    /// So are a role that is not there, the owner itself, and a superuser.
    /// </summary>
    [PostgresFact]   // mutation: the app role granted membership in the owner; the check after the grants removed
    public Task An_app_role_that_could_act_as_the_owner_or_a_superuser_is_refused_and_nothing_is_written() => WithRolesAsync(async (roles, t) =>
    {
        var superuser = $"pm_super_{Guid.NewGuid():N}";
        await PostgresTestRoles.AdminAsync($"GRANT {roles.Owner} TO {roles.App}");
        await PostgresTestRoles.AdminAsync($"CREATE ROLE {superuser} SUPERUSER NOLOGIN");
        try
        {
            var before = await t.SnapshotAsync();
            await using var owner = roles.Database(t, roles.Owner);
            foreach (var (role, says) in new[]
            {
                (roles.App, "The server's role owns"),
                ($"pm_missing_{Guid.NewGuid():N}", "There is no role named"),
                (roles.Owner, "is the role running this migration"),
                (superuser, "signs in as a superuser"),
            })
            {
                var e = await Assert.ThrowsAsync<PostgresRoleException>(() => owner.MigrateAndGrantAsync([], role));
                Assert.Contains(says, e.Message, StringComparison.Ordinal);
                Assert.Contains("nothing was written", e.Message, StringComparison.Ordinal);
                Assert.Equal(before, await t.SnapshotAsync());
            }
        }
        finally
        {
            await PostgresTestRoles.AdminAsync($"DROP ROLE IF EXISTS {superuser}");
        }
    });

    /// <summary>
    /// The start's checks and the owner's migration judge a role the same way: after a clean --grant-to, each
    /// misconfiguration below (made by the test server's login, by hand: {app} is the app role, {group} a role it is made a member of
    /// where the case says) is refused by BOTH, or by neither, each refusal worded the same. The app role is checked by the start as it
    /// signs in, and by a second migration's check after it grants.
    /// </summary>
    [PostgresTheory]   // mutations: R3b (TRUNCATE through PUBLIC or a group missed), R13 (ALTER SYSTEM missed), R15 (another schema's trigger function), F1 (built-in roles, version-table writes)
    [InlineData("nothing", false, new string[0])]
    [InlineData("pg_execute_server_program", true, new[] { "GRANT pg_execute_server_program TO {app}" })]
    [InlineData("pg_write_server_files", true, new[] { "GRANT pg_write_server_files TO {app}" })]
    [InlineData("pg_write_all_data", true, new[] { "GRANT pg_write_all_data TO {app}" })]
    [InlineData("pg_read_server_files", true, new[] { "GRANT pg_read_server_files TO {app}" })]
    [InlineData("pg_signal_backend", true, new[] { "GRANT pg_signal_backend TO {app}" })]
    [InlineData("a built-in role through a group", true, new[] { "GRANT pg_write_all_data TO {group}", "GRANT {group} TO {app}" })]
    [InlineData("TRUNCATE and TRIGGER to PUBLIC", true, new[] { "GRANT TRUNCATE, TRIGGER ON tenants TO PUBLIC" })]
    [InlineData("TRUNCATE through a group", true, new[] { "GRANT TRUNCATE ON audit_events TO {group}", "GRANT {group} TO {app}" })]
    [InlineData("REFERENCES through a group it does not inherit", true, new[] { "GRANT REFERENCES ON tenants TO {group}", "GRANT {group} TO {app} WITH INHERIT FALSE" })]
    [InlineData("MAINTAIN to PUBLIC", true, new[] { "GRANT MAINTAIN ON members TO PUBLIC" })]
    [InlineData("INSERT on a version table through a group", true, new[] { "GRANT INSERT ON schema_migrations TO {group}", "GRANT {group} TO {app}" })]
    [InlineData("DELETE on a version table to PUBLIC", true, new[] { "GRANT DELETE ON schema_migrations TO PUBLIC" })]
    [InlineData("ALTER SYSTEM on session_replication_role through a group", true, new[] { "GRANT ALTER SYSTEM ON PARAMETER session_replication_role TO {group}", "GRANT {group} TO {app}" })]
    [InlineData("a trigger's function in another schema owned by the app role", true, new[]
    {
        "CREATE SCHEMA other",
        "CREATE FUNCTION other.pass() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$",
        "ALTER FUNCTION other.pass() OWNER TO {app}",
        "CREATE TRIGGER zz_other BEFORE INSERT ON tenants FOR EACH ROW EXECUTE FUNCTION other.pass()",
    })]
    // SECURITY DEFINER functions that run as the owner or a superuser, in other schemas.
    [InlineData("an owner's SECURITY DEFINER function in another schema, EXECUTE to PUBLIC by default", true, new[]
    {
        "CREATE SCHEMA rv_tools", "CREATE FUNCTION rv_tools.purge_all() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
        "ALTER FUNCTION rv_tools.purge_all() OWNER TO {owner}",
    })]
    [InlineData("the same with EXECUTE revoked from PUBLIC and the app role", false, new[]
    {
        "CREATE SCHEMA rv_tools", "CREATE FUNCTION rv_tools.purge_all() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
        "ALTER FUNCTION rv_tools.purge_all() OWNER TO {owner}", "REVOKE EXECUTE ON FUNCTION rv_tools.purge_all() FROM PUBLIC, {app}",
    })]
    [InlineData("the same revoked from PUBLIC but granted to a group of the app role", true, new[]
    {
        "CREATE SCHEMA rv_tools", "CREATE FUNCTION rv_tools.purge_all() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
        "ALTER FUNCTION rv_tools.purge_all() OWNER TO {owner}", "REVOKE EXECUTE ON FUNCTION rv_tools.purge_all() FROM PUBLIC",
        "GRANT EXECUTE ON FUNCTION rv_tools.purge_all() TO {group}", "GRANT {group} TO {app}",
    })]
    // A plain role's definer function, executable by PUBLIC, calls the owner's, granted to that plain role
    // alone. No check of owners sees it; every definer function the app role can run is refused.
    [InlineData("a chain: a plain role's SECURITY DEFINER function calling the owner's, granted to that role alone", true, new[]
    {
        "CREATE SCHEMA rv_tools", "GRANT USAGE ON SCHEMA rv_tools TO PUBLIC",
        "CREATE FUNCTION rv_tools.inner_purge() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
        "ALTER FUNCTION rv_tools.inner_purge() OWNER TO {owner}", "REVOKE EXECUTE ON FUNCTION rv_tools.inner_purge() FROM PUBLIC",
        "GRANT EXECUTE ON FUNCTION rv_tools.inner_purge() TO {group}",
        "CREATE FUNCTION rv_tools.outer_call() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT rv_tools.inner_purge()'",
        "ALTER FUNCTION rv_tools.outer_call() OWNER TO {group}",
    })]
    [InlineData("a plain role's SECURITY DEFINER function in another schema", true, new[]
    {
        "CREATE SCHEMA rv_tools", "CREATE FUNCTION rv_tools.anything() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
        "ALTER FUNCTION rv_tools.anything() OWNER TO {group}",
    })]
    [InlineData("a plain role's SECURITY DEFINER function added to an extension", true, new[]
    {
        "CREATE SCHEMA ext_tools", "CREATE FUNCTION ext_tools.helper() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
        "ALTER FUNCTION ext_tools.helper() OWNER TO {group}", "ALTER EXTENSION plpgsql ADD FUNCTION ext_tools.helper()",
    })]
    [InlineData("an owner's function in another schema that is not SECURITY DEFINER", false, new[]
    {
        "CREATE SCHEMA rv_tools", "CREATE FUNCTION rv_tools.plain() RETURNS integer LANGUAGE sql AS 'SELECT 1'", "ALTER FUNCTION rv_tools.plain() OWNER TO {owner}",
    })]
    [InlineData("a superuser's SECURITY DEFINER function in another schema", true, new[]
    {
        "CREATE SCHEMA su_tools", "CREATE FUNCTION su_tools.anything() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
    })]
    [InlineData("a superuser's SECURITY DEFINER function that an extension owns", false, new[]
    {
        "CREATE SCHEMA ext_tools", "CREATE FUNCTION ext_tools.helper() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
        "ALTER EXTENSION plpgsql ADD FUNCTION ext_tools.helper()",
    })]
    [InlineData("an owner's SECURITY DEFINER function added to an extension", true, new[]
    {
        "CREATE SCHEMA ext_tools", "CREATE FUNCTION ext_tools.helper() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
        "ALTER FUNCTION ext_tools.helper() OWNER TO {owner}", "ALTER EXTENSION plpgsql ADD FUNCTION ext_tools.helper()",
    })]
    // A superuser's extension function is left out only while the app role reaches it through PUBLIC alone.
    [InlineData("a superuser's extension SECURITY DEFINER function granted to the app role", true, new[]
    {
        "CREATE SCHEMA ext_tools", "CREATE FUNCTION ext_tools.helper() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
        "ALTER EXTENSION plpgsql ADD FUNCTION ext_tools.helper()", "GRANT EXECUTE ON FUNCTION ext_tools.helper() TO {app}",
    })]
    [InlineData("the same granted to the app role WITH GRANT OPTION", true, new[]
    {
        "CREATE SCHEMA ext_tools", "CREATE FUNCTION ext_tools.helper() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
        "ALTER EXTENSION plpgsql ADD FUNCTION ext_tools.helper()", "GRANT EXECUTE ON FUNCTION ext_tools.helper() TO {app} WITH GRANT OPTION",
    })]
    [InlineData("the same revoked from PUBLIC and granted to the app role", true, new[]
    {
        "CREATE SCHEMA ext_tools", "CREATE FUNCTION ext_tools.helper() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
        "ALTER EXTENSION plpgsql ADD FUNCTION ext_tools.helper()", "REVOKE EXECUTE ON FUNCTION ext_tools.helper() FROM PUBLIC",
        "GRANT EXECUTE ON FUNCTION ext_tools.helper() TO {app}",
    })]
    [InlineData("the same granted to a group of the app role", true, new[]
    {
        "CREATE SCHEMA ext_tools", "CREATE FUNCTION ext_tools.helper() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
        "ALTER EXTENSION plpgsql ADD FUNCTION ext_tools.helper()", "GRANT EXECUTE ON FUNCTION ext_tools.helper() TO {group}", "GRANT {group} TO {app}",
    })]
    [InlineData("the same granted to a group the app role does not inherit", true, new[]
    {
        "CREATE SCHEMA ext_tools", "CREATE FUNCTION ext_tools.helper() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
        "ALTER EXTENSION plpgsql ADD FUNCTION ext_tools.helper()", "GRANT EXECUTE ON FUNCTION ext_tools.helper() TO {group}",
        "GRANT {group} TO {app} WITH INHERIT FALSE",
    })]
    [InlineData("the same granted to a role the app role is not a member of", false, new[]
    {
        "CREATE SCHEMA ext_tools", "CREATE FUNCTION ext_tools.helper() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
        "ALTER EXTENSION plpgsql ADD FUNCTION ext_tools.helper()", "GRANT EXECUTE ON FUNCTION ext_tools.helper() TO {group}",
    })]
    [InlineData("the same revoked from PUBLIC and granted to PUBLIC again", false, new[]
    {
        "CREATE SCHEMA ext_tools", "CREATE FUNCTION ext_tools.helper() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
        "ALTER EXTENSION plpgsql ADD FUNCTION ext_tools.helper()", "REVOKE EXECUTE ON FUNCTION ext_tools.helper() FROM PUBLIC",
        "GRANT EXECUTE ON FUNCTION ext_tools.helper() TO PUBLIC",
    })]
    [InlineData("the same granted to the app role and revoked again", false, new[]
    {
        "CREATE SCHEMA ext_tools", "CREATE FUNCTION ext_tools.helper() RETURNS integer LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'",
        "ALTER EXTENSION plpgsql ADD FUNCTION ext_tools.helper()", "GRANT EXECUTE ON FUNCTION ext_tools.helper() TO {app}",
        "REVOKE EXECUTE ON FUNCTION ext_tools.helper() FROM {app}",
    })]
    public async Task The_start_and_the_migration_refuse_the_same_app_role(string name, bool refused, string[] misconfigure)
    {
        var group = $"pm_group_{Guid.NewGuid():N}";
        await PostgresTestRoles.AdminAsync($"CREATE ROLE {group} NOLOGIN");
        try
        {
            await WithRolesAsync(async (roles, t) =>
            {
                await MigrateAsync(roles, t);
                await t.ExecuteAsync(misconfigure.Select(s => s.Replace("{app}", roles.App, StringComparison.Ordinal).Replace("{group}", group, StringComparison.Ordinal)
                    .Replace("{owner}", roles.Owner, StringComparison.Ordinal)).ToArray());

                await using var app = roles.Database(t, roles.App);
                var start = PostgresStartupChecks.Evaluate(await PostgresStartupChecks.GatherAsync(app, [], new SystemClock()), new PostgresStartupOptions())
                    .Where(f => f.Kind == PostgresFindingKind.Refusal).Select(f => f.Message).ToList();
                var migration = await Record.ExceptionAsync(() => MigrateAsync(roles, t));

                Assert.True(refused == start.Count > 0, $"{name}: the start {(start.Count > 0 ? "refused: " + string.Join(" | ", start) : "refused nothing")}");
                Assert.True(refused == migration is PostgresRoleException, $"{name}: the migration {(migration is null ? "passed" : "threw " + migration.Message)}");
                // Worded the same: every refusal the start gives is one of the migration's.
                foreach (var message in start) Assert.Contains("  - " + message, migration!.Message, StringComparison.Ordinal);
            });
        }
        finally
        {
            await PostgresTestRoles.AdminAsync($"REVOKE ALL ON PARAMETER session_replication_role FROM {group}");
            await PostgresTestRoles.AdminAsync($"DROP ROLE IF EXISTS {group}");
        }
    }

    /// <summary>
    /// The owner role makes a SECURITY DEFINER function in a schema of its own, which PUBLIC may
    /// run by default. The app role runs it, and it runs as the owner (current_user inside it is the owner). Both checks refuse that app
    /// role, naming the function. Once EXECUTE is revoked from PUBLIC and the app role, the app role cannot run it (42501), and both pass.
    /// </summary>
    [PostgresFact]   // mutations: the check ignoring PUBLIC; the check counting only the current schema
    public Task An_owners_SECURITY_DEFINER_function_in_another_schema_is_refused_until_EXECUTE_is_revoked() => WithRolesAsync(async (roles, t) =>
    {
        await MigrateAsync(roles, t);
        await using (var owner = await OpenAsync(roles, t, roles.Owner))
        await using (var make = new NpgsqlCommand(
            "CREATE SCHEMA rv_tools; GRANT USAGE ON SCHEMA rv_tools TO PUBLIC; " +
            "CREATE FUNCTION rv_tools.whoami() RETURNS text LANGUAGE sql SECURITY DEFINER AS 'SELECT current_user::text'", owner))
            await make.ExecuteNonQueryAsync();

        await using (var app = await OpenAsync(roles, t, roles.App))
            Assert.Equal([roles.Owner], await TextsAsync(app, "SELECT rv_tools.whoami()"));   // the app role acts as the owner through it

        async Task<(List<string> Start, Exception? Migration)> CheckAsync()
        {
            await using var app = roles.Database(t, roles.App);
            var start = PostgresStartupChecks.Evaluate(await PostgresStartupChecks.GatherAsync(app, [], new SystemClock()), new PostgresStartupOptions())
                .Where(f => f.Kind == PostgresFindingKind.Refusal).Select(f => f.Message).ToList();
            return (start, await Record.ExceptionAsync(() => MigrateAsync(roles, t)));
        }

        var (start, migration) = await CheckAsync();
        Assert.Contains("rv_tools.whoami()", Assert.Single(start), StringComparison.Ordinal);
        Assert.Contains("SECURITY DEFINER", start[0], StringComparison.Ordinal);
        Assert.Contains("  - " + start[0], Assert.IsType<PostgresRoleException>(migration).Message, StringComparison.Ordinal);

        await using (var owner = await OpenAsync(roles, t, roles.Owner))
        await using (var revoke = new NpgsqlCommand($"REVOKE EXECUTE ON FUNCTION rv_tools.whoami() FROM PUBLIC, {roles.App}", owner))
            await revoke.ExecuteNonQueryAsync();
        await using (var app = await OpenAsync(roles, t, roles.App))
        {
            var denied = await Assert.ThrowsAsync<PostgresException>(() => TextsAsync(app, "SELECT rv_tools.whoami()"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }
        (start, migration) = await CheckAsync();
        Assert.Empty(start);
        Assert.Null(migration);
    });

    /// <summary>
    /// A provider's administration function, emulated (the test server has no such extension): a superuser makes a SECURITY
    /// DEFINER function that sets session_replication_role, in a schema named provider_extras, and adds it to an extension
    /// (plpgsql), so it is a superuser-owned extension member that PUBLIC may run. Through PUBLIC alone both checks leave it out, as they
    /// leave out every managed service's own. Granted to the app role, the app role turns the guard triggers off with it, and both checks
    /// refuse that app role, PACKMOAT_DB_SINGLE_ROLE=on or not; once the grant is revoked, both pass again.
    /// </summary>
    [PostgresFact]   // mutation: the extension exception still applied when EXECUTE is granted to the role, or through a group
    public Task A_superusers_extension_function_granted_to_the_app_role_is_refused_until_revoked() => WithRolesAsync(async (roles, t) =>
    {
        await MigrateAsync(roles, t);
        await t.ExecuteAsync(
            "CREATE SCHEMA provider_extras", "GRANT USAGE ON SCHEMA provider_extras TO PUBLIC",
            "CREATE FUNCTION provider_extras.session_replication_role(arg_parameter text) RETURNS text LANGUAGE sql SECURITY DEFINER " +
            "AS 'SELECT pg_catalog.set_config(''session_replication_role'', arg_parameter, false)'",
            "ALTER EXTENSION plpgsql ADD FUNCTION provider_extras.session_replication_role(text)");
        const string Function = "provider_extras.session_replication_role(arg_parameter text)";

        async Task<(List<PostgresFinding> Start, Exception? Migration)> CheckAsync(bool singleRole = false)
        {
            await using var app = roles.Database(t, roles.App);
            var start = PostgresStartupChecks.Evaluate(await PostgresStartupChecks.GatherAsync(app, [], new SystemClock()), new PostgresStartupOptions(singleRole))
                .Where(f => !f.Message.StartsWith("The database's clock", StringComparison.Ordinal)).ToList();
            return (start, await Record.ExceptionAsync(() => MigrateAsync(roles, t)));
        }

        // Through PUBLIC alone (the default): left out by both.
        var (start, migration) = await CheckAsync();
        Assert.Empty(start);
        Assert.Null(migration);

        await t.ExecuteAsync($"GRANT EXECUTE ON FUNCTION provider_extras.session_replication_role(text) TO {roles.App}");
        await using (var app = await OpenAsync(roles, t, roles.App))
        {
            await TextsAsync(app, "SELECT provider_extras.session_replication_role('replica')");
            Assert.Equal(["replica"], await TextsAsync(app, "SHOW session_replication_role"));   // the guard triggers are off for this session
        }

        (start, migration) = await CheckAsync();
        var refusal = Assert.Single(start);
        Assert.Equal(PostgresFindingKind.Refusal, refusal.Kind);
        Assert.StartsWith($"The server's role is granted {Function}: a superuser's SECURITY DEFINER function", refusal.Message);
        Assert.Contains("  - " + refusal.Message, Assert.IsType<PostgresRoleException>(migration).Message, StringComparison.Ordinal);
        (start, _) = await CheckAsync(singleRole: true);
        Assert.Contains(start, f => f.Kind == PostgresFindingKind.Refusal && f.Message == refusal.Message);

        await t.ExecuteAsync($"REVOKE EXECUTE ON FUNCTION provider_extras.session_replication_role(text) FROM {roles.App}");
        (start, migration) = await CheckAsync();
        Assert.Empty(start);
        Assert.Null(migration);
    });

    /// <summary>
    /// A superuser's SECURITY DEFINER function in pg_catalog (where a role that is no superuser can
    /// install a trusted extension), an extension's or not, and one in another schema that is no extension's, each granted to the app role,
    /// is refused by the start and the migration, PACKMOAT_DB_SINGLE_ROLE=on or not; the app role really turns the guard triggers off with it.
    /// </summary>
    [PostgresTheory]   // mutations: pg_catalog left out of the granted list; the list kept to extensions' functions; single-role making it a warning
    [InlineData("pg_catalog", true)]
    [InlineData("pg_catalog", false)]
    [InlineData("rv_tools", false)]
    public Task A_superusers_function_granted_to_the_app_role_is_refused_in_any_schema(string schema, bool extension) => WithRolesAsync(async (roles, t) =>
    {
        await MigrateAsync(roles, t);
        var name = $"{schema}.rv_srr_{Guid.NewGuid():N}";
        var setup = new List<string>();
        if (schema != "pg_catalog") setup.AddRange([$"CREATE SCHEMA {schema}", $"GRANT USAGE ON SCHEMA {schema} TO PUBLIC"]);
        setup.Add($"CREATE FUNCTION {name}(arg text) RETURNS text LANGUAGE sql SECURITY DEFINER AS 'SELECT pg_catalog.set_config(''session_replication_role'', arg, false)'");
        if (extension) setup.Add($"ALTER EXTENSION plpgsql ADD FUNCTION {name}(text)");
        setup.Add($"REVOKE EXECUTE ON FUNCTION {name}(text) FROM PUBLIC");
        setup.Add($"GRANT EXECUTE ON FUNCTION {name}(text) TO {roles.App}");
        await t.ExecuteAsync([.. setup]);

        await using (var app = await OpenAsync(roles, t, roles.App))
        {
            await TextsAsync(app, $"SELECT {name}('replica')");
            Assert.Equal(["replica"], await TextsAsync(app, "SHOW session_replication_role"));
        }
        foreach (var singleRole in new[] { false, true })
        {
            await using var app = roles.Database(t, roles.App);
            var start = PostgresStartupChecks.Evaluate(await PostgresStartupChecks.GatherAsync(app, [], new SystemClock()), new PostgresStartupOptions(singleRole));
            var refusal = Assert.Single(start, f => f.Message.Contains(name + "(arg text)", StringComparison.Ordinal));
            Assert.Equal(PostgresFindingKind.Refusal, refusal.Kind);
            Assert.StartsWith($"The server's role is granted {name}(arg text): a superuser's SECURITY DEFINER function", refusal.Message);
            if (!singleRole) Assert.Contains("  - " + refusal.Message, Assert.IsType<PostgresRoleException>(await Record.ExceptionAsync(() => MigrateAsync(roles, t))).Message, StringComparison.Ordinal);
        }
    });

    /// <summary>A role name with a double quote in it is granted to as itself: the name is quoted, the quote doubled.</summary>
    [PostgresFact]   // mutation: Quote does not double a quote
    public async Task A_role_name_with_a_quote_in_it_is_granted_to_as_itself()
    {
        var quoted = $"pm_app_\"q\"_{Guid.NewGuid():N}";
        var identifier = PostgresRoleGrants.Quote(quoted);
        await PostgresTestRoles.AdminAsync($"CREATE ROLE {identifier} LOGIN NOSUPERUSER");
        try
        {
            await WithRolesAsync(async (roles, t) =>
            {
                await using var owner = roles.Database(t, roles.Owner);
                var done = await owner.MigrateAndGrantAsync([], quoted);
                Assert.Equal(quoted, done.Grants!.Role);
                await using var c = await t.OpenAsync();
                await using var cmd = new NpgsqlCommand("SELECT has_table_privilege(@role, 'tenants', 'INSERT') AND NOT has_table_privilege(@role, 'tenants', 'TRUNCATE')", c);
                cmd.Parameters.AddWithValue("role", quoted);
                Assert.Equal(true, await cmd.ExecuteScalarAsync());
            });
        }
        finally
        {
            await PostgresTestRoles.AdminAsync($"DROP ROLE IF EXISTS {identifier}");
        }
    }

    /// <summary>CREATE on the schema given to the app role by hand is taken back by the next migration.</summary>
    [PostgresFact]   // mutation: REVOKE ALL ON SCHEMA … FROM the app role removed
    public Task CREATE_given_to_the_app_role_by_hand_is_taken_back() => WithRolesAsync(async (roles, t) =>
    {
        await MigrateAsync(roles, t);
        await using (var owner = await OpenAsync(roles, t, roles.Owner))
        await using (var grant = new NpgsqlCommand($"GRANT CREATE ON SCHEMA public TO {roles.App}", owner))
            await grant.ExecuteNonQueryAsync();

        await MigrateAsync(roles, t);

        await using var c = await t.OpenAsync();
        Assert.Equal(["false"], await TextsAsync(c, "SELECT has_schema_privilege(@app, 'public', 'CREATE')::text", roles.App));
        Assert.Contains("schema: USAGE", await EffectiveAsync(roles, t));
    });

    /// <summary>A role name is quoted, never run: one that is SQL is just a role that is not there.</summary>
    [PostgresFact]
    public Task A_role_name_that_is_SQL_is_a_name_not_a_statement() => WithRolesAsync(async (roles, t) =>
    {
        await using var owner = roles.Database(t, roles.Owner);
        var e = await Assert.ThrowsAsync<PostgresRoleException>(() => owner.MigrateAndGrantAsync([], $"{roles.App}; DROP TABLE schema_migrations; --"));
        Assert.StartsWith("There is no role named", e.Message, StringComparison.Ordinal);
        Assert.Empty(await t.SnapshotAsync());
    });

    /// <summary>
    /// On PostgreSQL the server never migrates: the schema port its start calls (the one <see cref="PostgresStoreRegistration.AddPostgresStores"/>
    /// returns) writes nothing on an empty database and refuses it, naming the command to run; on a migrated one it changes nothing.
    /// </summary>
    [PostgresFact]   // mutation: the server migrating on Postgres (the registration's migrator the database itself)
    public Task The_server_never_migrates_on_postgres() => WithRolesAsync(async (roles, t) =>
    {
        await using var app = roles.Database(t, roles.App);
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var registered = services.AddPostgresStores(new PostgresStores(app, AesGcmSecretProtector.FromBase64(AesGcmSecretProtector.GenerateKeyBase64())));

        var e = await Assert.ThrowsAsync<SchemaNotMigratedException>(() => registered.Migrator.MigrateAsync([]));
        Assert.Contains("the server never migrates", e.Message, StringComparison.Ordinal);
        Assert.Contains("db migrate --grant-to", e.Message, StringComparison.Ordinal);
        Assert.Empty(await t.SnapshotAsync());

        // Even as the owner, which could: still nothing.
        await using var ownerDb = roles.Database(t, roles.Owner);
        await Assert.ThrowsAsync<SchemaNotMigratedException>(() => new PostgresStores(ownerDb, AesGcmSecretProtector.FromBase64(AesGcmSecretProtector.GenerateKeyBase64())).Migrator.MigrateAsync([]));
        Assert.Empty(await t.SnapshotAsync());

        await MigrateAsync(roles, t);
        var migrated = await t.SnapshotAsync();
        var outcomes = await registered.Migrator.MigrateAsync([]);
        Assert.All(outcomes, o => Assert.Equal(o.Before, o.After));
        Assert.Equal(migrated, await t.SnapshotAsync());

        // A schema newer than this build (a later build migrated it) is refused too: the server does not run on it.
        await t.ExecuteAsync($"INSERT INTO schema_migrations(version, applied_at) VALUES({CoreSchema.Track.LatestVersion + 1}, now())");
        var newer = await Assert.ThrowsAsync<SchemaNotMigratedException>(() => registered.Migrator.MigrateAsync([]));
        Assert.Contains($"track core is at v{CoreSchema.Track.LatestVersion + 1}, this build needs v{CoreSchema.Track.LatestVersion}", newer.Message, StringComparison.Ordinal);
    });
}
