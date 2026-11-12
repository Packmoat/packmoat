using Npgsql;
using Packmoat.Domain;
using Packmoat.Infrastructure.Postgres;
using static Packmoat.Infrastructure.Postgres.PostgresFindingKind;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// The start's checks against a real server, each refusal on a real misconfiguration that its test sets up on a scratch
/// database of its own (dropped at its end) or a role of its own (dropped in a finally): a setting set on the database, a trigger
/// disabled or dropped, a version row deleted, a table given to the role. Two cannot be set up on the test server and are judged on the
/// facts it really gave: an older server (the facts it gives are PostgreSQL 17's) and a remote host (the server is local). A pooler in
/// transaction mode is stood for by running the probe's look on another session. The pure checks are in <see cref="PostgresStartupChecksTests"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresStartupServerTests
{
    /// <summary>A login role that is no superuser, with only the data grants an app role has; dropped (after its database) when disposed.</summary>
    private sealed class AppRole : IAsyncDisposable
    {
        public string Name { get; } = $"pm_app_{Guid.NewGuid():N}";
        public string Password { get; } = PostgresConnectionStringTests.Sentinel();

        public static async Task<AppRole> CreateAsync()
        {
            var role = new AppRole();
            await AdminAsync($"CREATE ROLE {role.Name} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '{role.Password}'");
            return role;
        }

        /// <summary>
        /// The data grants on <paramref name="t"/>'s tables, as <c>db migrate --grant-to</c> makes them (<see cref="PostgresRoleGrants"/>): no TRUNCATE, no DDL, no ownership, SELECT only on the version tables.
        /// </summary>
        public async Task GrantAsync(PostgresScratchDatabase t)
        {
            await using var c = await t.OpenAsync();
            await using var tx = await c.BeginTransactionAsync();
            await PostgresRoleGrants.ApplyAsync(c, tx, "public", Name);
            await tx.CommitAsync();
        }

        /// <summary>This role's connection string to <paramref name="t"/>, with a pool the test server's max_connections can take.</summary>
        public string ConnectionString(PostgresScratchDatabase t, int maxPoolSize = 10) =>
            new NpgsqlConnectionStringBuilder(PostgresTestServer.ConnectionString)
            { Database = t.Name, Username = Name, Password = Password, MaxPoolSize = maxPoolSize }.ConnectionString;

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await AdminAsync($"DROP ROLE IF EXISTS {Name}");
        }
    }

    private static async Task AdminAsync(string sql)
    {
        await using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(PostgresTestServer.ConnectionString) { Pooling = false }.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A migrated scratch database and an app role granted on it: the setup every check passes on.</summary>
    private static async Task<(AppRole Role, PostgresScratchDatabase Db)> GoodAsync()
    {
        var role = await AppRole.CreateAsync();
        try
        {
            var t = await PostgresScratchDatabase.FromTemplateAsync();
            await role.GrantAsync(t);
            return (role, t);
        }
        catch
        {
            await role.DisposeAsync();
            throw;
        }
    }

    private static async Task<PostgresStartupFacts> GatherAsync(string connectionString, IClock? clock = null)
    {
        await using var db = PostgresDatabase.Create(PostgresConnectionString.Parse(connectionString));
        return await PostgresStartupChecks.GatherAsync(db, TestTracks.Modules, clock ?? new SystemClock());
    }

    /// <summary>The findings but the clock's: the test server's clock is the container's, which may drift from this machine's.</summary>
    private static List<PostgresFinding> Findings(PostgresStartupFacts facts, PostgresStartupOptions? options = null) =>
        PostgresStartupChecks.Evaluate(facts, options ?? new PostgresStartupOptions())
            .Where(f => !f.Message.StartsWith("The database's clock", StringComparison.Ordinal)).ToList();

    /// <summary>
    /// Runs <paramref name="body"/> on a good setup with <paramref name="misconfigure"/> applied first (as the owner, on the scratch
    /// database). The scratch database is dropped and the role dropped at the end, whatever happens.
    /// </summary>
    private static async Task WithGoodAsync(Func<PostgresScratchDatabase, AppRole, Task> misconfigure, Func<AppRole, PostgresScratchDatabase, Task> body)
    {
        var (role, t) = await GoodAsync();
        try
        {
            await misconfigure(t, role);
            await body(role, t);
        }
        finally
        {
            await t.DisposeAsync();
            await role.DisposeAsync();
        }
    }

    private static Task Nothing(PostgresScratchDatabase t, AppRole r) => Task.CompletedTask;

    [PostgresFact]   // the negative control for every refusal below
    public Task An_app_role_on_a_migrated_database_passes_every_check() => WithGoodAsync(Nothing, async (role, t) =>
    {
        var facts = await GatherAsync(role.ConnectionString(t));
        Assert.Empty(Findings(facts));
        // The facts are really read, not defaults that happen to pass.
        Assert.Equal(PostgresDatabase.SupportedMajorVersion, facts.ServerVersion.Major);
        Assert.Equal("read committed", facts.DefaultTransactionIsolation);
        Assert.Equal("origin", facts.SessionReplicationRole);
        Assert.Equal(PostgresStartupChecks.PinnedTriggers, facts.Triggers.Keys.Order(StringComparer.Ordinal));
        Assert.All(facts.Triggers.Values, e => Assert.Equal("O", e));
        Assert.Equal("public", facts.Schema);
        Assert.False(facts.Superuser);
        Assert.Empty(facts.Owned);
        Assert.False(facts.CreateOnSchema);
        Assert.False(facts.CanSetReplicationRole);
        Assert.Equal(PostgresStartupChecks.PoolerProbeRounds, facts.PoolerProbeHeld);
        Assert.False(facts.PoolerSameSession);   // The probe's second connection is a session of its own
        Assert.False(facts.PoolerLockShared);
        Assert.False(facts.PoolerSecondConnectionRefused);
        Assert.Empty(facts.GrantedSuperuserFunctions);
        Assert.True(facts.MaxConnections > 0);
        Assert.Equal(10, facts.MaxPoolSize);
        Assert.All(facts.Tracks, v => Assert.True(v.IsCurrent));
    });

    [PostgresFact]   // the test server's own login is a superuser (it owns everything and can create anywhere)
    public async Task A_superuser_is_refused()
    {
        await using var t = await PostgresScratchDatabase.FromTemplateAsync();
        var facts = await GatherAsync(new NpgsqlConnectionStringBuilder(PostgresTestServer.ConnectionString) { Database = t.Name, MaxPoolSize = 10 }.ConnectionString);
        Assert.True(facts.Superuser);
        var findings = Findings(facts, new PostgresStartupOptions(SingleRole: true));
        Assert.Contains(findings, f => f.Kind == Refusal && f.Message.StartsWith("PACKMOAT_DB signs in as a superuser", StringComparison.Ordinal));
    }

    [PostgresFact]
    public Task Default_transaction_isolation_serializable_is_refused() => WithGoodAsync(
        (t, _) => t.ExecuteAsync($"ALTER DATABASE {t.Name} SET default_transaction_isolation = 'serializable'"),
        async (role, t) =>
        {
            var f = Assert.Single(Findings(await GatherAsync(role.ConnectionString(t))));
            Assert.Equal(Refusal, f.Kind);
            Assert.StartsWith("transaction_isolation is serializable, and default_transaction_isolation is serializable.", f.Message);
        });

    [PostgresFact]
    public Task Session_replication_role_replica_is_refused() => WithGoodAsync(
        (t, _) => t.ExecuteAsync($"ALTER DATABASE {t.Name} SET session_replication_role = 'replica'"),
        async (role, t) =>
        {
            var f = Assert.Single(Findings(await GatherAsync(role.ConnectionString(t))));
            Assert.Equal(Refusal, f.Kind);
            Assert.StartsWith("session_replication_role is replica, not origin", f.Message);
        });

    [PostgresFact]   // mutation: tgenabled 'D' accepted
    public Task A_disabled_trigger_is_refused() => WithGoodAsync(
        (t, _) => t.ExecuteAsync("ALTER TABLE audit_events DISABLE TRIGGER audit_no_update"),
        async (role, t) =>
        {
            var facts = await GatherAsync(role.ConnectionString(t));
            Assert.Equal("D", facts.Triggers["audit_events.audit_no_update"]);
            var f = Assert.Single(Findings(facts));
            Assert.Equal(Refusal, f.Kind);
            Assert.Contains("not firing: audit_events.audit_no_update (disabled)", f.Message);
        });

    [PostgresFact]
    public Task A_trigger_that_fires_only_in_replica_mode_is_refused() => WithGoodAsync(
        (t, _) => t.ExecuteAsync("ALTER TABLE tenant_purges ENABLE REPLICA TRIGGER tenant_purges_no_delete"),
        async (role, t) =>
        {
            var f = Assert.Single(Findings(await GatherAsync(role.ConnectionString(t))));
            Assert.Contains("not firing: tenant_purges.tenant_purges_no_delete (replica only)", f.Message);
        });

    [PostgresFact]
    public Task A_dropped_truncate_guard_is_refused() => WithGoodAsync(
        (t, _) => t.ExecuteAsync("DROP TRIGGER audit_no_truncate ON audit_events"),
        async (role, t) =>
        {
            var f = Assert.Single(Findings(await GatherAsync(role.ConnectionString(t))));
            Assert.Equal(Refusal, f.Kind);
            Assert.Contains("missing: audit_events.audit_no_truncate", f.Message);
        });

    [PostgresFact]
    public Task A_short_idle_session_timeout_is_refused() => WithGoodAsync(
        (t, _) => t.ExecuteAsync($"ALTER DATABASE {t.Name} SET idle_session_timeout = '30s'"),
        async (role, t) =>
        {
            var facts = await GatherAsync(role.ConnectionString(t));
            Assert.Equal(TimeSpan.FromSeconds(30), facts.IdleSessionTimeout);
            var f = Assert.Single(Findings(facts));
            Assert.StartsWith("idle_session_timeout is 30 s: shorter than 300 s", f.Message);
        });

    [PostgresFact]   // the server never migrates on PostgreSQL: a database behind is refused, not brought up to date
    public Task A_schema_behind_this_build_is_refused_and_left_as_it_is() => WithGoodAsync(
        (t, _) => t.ExecuteAsync("DELETE FROM schema_migrations WHERE version = 26"),   // a database at 25 under a build that needs 26
        async (role, t) =>
        {
            var f = Assert.Single(Findings(await GatherAsync(role.ConnectionString(t))));
            Assert.Equal(Refusal, f.Kind);
            Assert.Contains("core: at version 25 with 25 of 26 recorded, this build needs 26", f.Message);
            Assert.Equal(25, (await t.VersionsAsync("schema_migrations")).Max());
        });

    [PostgresFact]   // mutation: ownership not checked; the escape not honoured
    public Task A_role_that_owns_a_table_is_refused_unless_single_role_is_on() => WithGoodAsync(
        (t, role) => t.ExecuteAsync($"ALTER TABLE tenants OWNER TO {role.Name}"),
        async (role, t) =>
        {
            var facts = await GatherAsync(role.ConnectionString(t));
            Assert.Equal(["table tenants"], facts.Owned);
            var strict = Assert.Single(Findings(facts));
            Assert.Equal(Refusal, strict.Kind);
            Assert.StartsWith("The server's role owns table tenants:", strict.Message);
            Assert.Equal(Warning, Assert.Single(Findings(facts, new PostgresStartupOptions(SingleRole: true))).Kind);
        });

    [PostgresFact]   // mutation: CREATE on the schema not checked
    public Task A_role_that_can_create_in_the_schema_is_refused() => WithGoodAsync(
        (t, role) => t.ExecuteAsync($"GRANT CREATE ON SCHEMA public TO {role.Name}"),
        async (role, t) =>
        {
            var facts = await GatherAsync(role.ConnectionString(t));
            Assert.True(facts.CreateOnSchema);
            var f = Assert.Single(Findings(facts));
            Assert.Equal(Refusal, f.Kind);
            Assert.StartsWith("The server's role can CREATE in the schema:", f.Message);
        });

    [PostgresFact]   // a member of a table's owner can act as the owner (SET ROLE), so it counts as one. mutation: pg_has_role replaced by relowner = current_user
    public async Task A_role_that_is_a_member_of_a_table_s_owner_is_refused()
    {
        var owner = $"pm_owner_{Guid.NewGuid():N}";
        await AdminAsync($"CREATE ROLE {owner} NOLOGIN");
        try
        {
            await WithGoodAsync(
                (t, role) => t.ExecuteAsync($"ALTER TABLE audit_events OWNER TO {owner}", $"GRANT {owner} TO {role.Name}"),
                async (role, t) =>
                {
                    var facts = await GatherAsync(role.ConnectionString(t));
                    Assert.Equal(["sequence audit_events_id_seq", "table audit_events"], facts.Owned);   // its identity sequence goes with it (sequences are counted)
                    Assert.Equal(Refusal, Assert.Single(Findings(facts)).Kind);
                });
        }
        finally
        {
            await AdminAsync($"DROP ROLE IF EXISTS {owner}");
        }
    }

    /// <summary>The superuser refusal, whatever the escape says.</summary>
    private static void AssertSuperuserRefused(PostgresStartupFacts facts)
    {
        Assert.True(facts.Superuser);
        foreach (var singleRole in new[] { false, true })
            Assert.Contains(Findings(facts, new PostgresStartupOptions(singleRole)),
                f => f.Kind == Refusal && f.Message.StartsWith("PACKMOAT_DB signs in as a superuser", StringComparison.Ordinal));
    }

    // A superuser login that runs as the app role (-c role=…, as ALTER ROLE … SET role would): current_user is the
    // app role, but RESET ROLE gives the superuser back. mutation: the superuser fact read for current_user
    [PostgresFact]
    public Task A_superuser_login_running_as_the_app_role_is_refused() => WithGoodAsync(Nothing, async (role, t) =>
    {
        var builder = new NpgsqlConnectionStringBuilder(PostgresTestServer.ConnectionString) { Database = t.Name, MaxPoolSize = 10 };
        builder.Options = $"{builder.Options} -c role={role.Name}".TrimStart();
        AssertSuperuserRefused(await GatherAsync(builder.ConnectionString));
    });

    // An app role that is a member of a superuser role can SET ROLE to it. mutation: membership not counted
    [PostgresFact]
    public async Task An_app_role_that_is_a_member_of_a_superuser_role_is_refused()
    {
        var super = $"pm_super_{Guid.NewGuid():N}";
        await AdminAsync($"CREATE ROLE {super} SUPERUSER NOLOGIN");
        try
        {
            await WithGoodAsync(
                (_, role) => AdminAsync($"GRANT {super} TO {role.Name}"),
                async (role, t) => AssertSuperuserRefused(await GatherAsync(role.ConnectionString(t))));
        }
        finally
        {
            await AdminAsync($"DROP ROLE IF EXISTS {super}");
        }
    }

    /// <summary>The refusal for a login that may set session_replication_role, with and without the escape.</summary>
    private static void AssertReplicationRoleRefused(PostgresStartupFacts facts)
    {
        Assert.True(facts.CanSetReplicationRole);
        foreach (var singleRole in new[] { false, true })
        {
            var f = Assert.Single(Findings(facts, new PostgresStartupOptions(singleRole)), x => x.Kind == Refusal);
            Assert.StartsWith("The server's login, or a role it is a member of, may set session_replication_role", f.Message);
        }
    }

    // GRANT SET ON PARAMETER session_replication_role lets the app role turn every guard off for its session.
    // mutation: the check removed
    [PostgresFact]
    public Task An_app_role_granted_set_on_session_replication_role_is_refused() => WithGoodAsync(
        (_, role) => AdminAsync($"GRANT SET ON PARAMETER session_replication_role TO {role.Name}"),
        async (role, t) =>
        {
            try { AssertReplicationRoleRefused(await GatherAsync(role.ConnectionString(t))); }
            finally { await AdminAsync($"REVOKE SET ON PARAMETER session_replication_role FROM {role.Name}"); }
        });

    // the same through a role the app role is a member of without inheriting (ALTER SYSTEM this time): it cannot use the grant as itself,
    // but SET ROLE gives it. An inherited grant is seen even for the login alone (has_parameter_privilege follows INHERIT); this one is
    // not. mutations: only the login's own privileges looked at; ALTER SYSTEM missed
    [PostgresFact]
    public async Task An_app_role_in_a_role_granted_session_replication_role_is_refused()
    {
        var holder = $"pm_srr_{Guid.NewGuid():N}";
        await AdminAsync($"CREATE ROLE {holder} NOLOGIN");
        try
        {
            await WithGoodAsync(
                async (_, role) =>
                {
                    await AdminAsync($"GRANT ALTER SYSTEM ON PARAMETER session_replication_role TO {holder}");
                    await AdminAsync($"GRANT {holder} TO {role.Name} WITH INHERIT FALSE, SET TRUE");
                },
                async (role, t) => AssertReplicationRoleRefused(await GatherAsync(role.ConnectionString(t))));
        }
        finally
        {
            await AdminAsync($"REVOKE ALTER SYSTEM ON PARAMETER session_replication_role FROM {holder}");
            await AdminAsync($"DROP ROLE IF EXISTS {holder}");
        }
    }

    // The owner of a guard's function can DROP FUNCTION … CASCADE, and the trigger goes with it. mutation: only
    // tables looked at; the triggers' functions left out
    [PostgresFact]
    public Task A_role_that_owns_a_guard_s_function_is_refused() => WithGoodAsync(
        (t, role) => t.ExecuteAsync($"ALTER FUNCTION audit_no_delete() OWNER TO {role.Name}"),
        async (role, t) =>
        {
            var facts = await GatherAsync(role.ConnectionString(t));
            Assert.Equal(["function audit_no_delete"], facts.Owned);
            var f = Assert.Single(Findings(facts));
            Assert.Equal(Refusal, f.Kind);
            Assert.StartsWith("The server's role owns function audit_no_delete:", f.Message);
        });

    // a function the guards call that is no trigger's own: its owner can replace it (CREATE OR REPLACE) and so turn the guards off.
    // mutation: the schema's own functions left out (only the triggers' functions looked at)
    [PostgresFact]
    public Task A_role_that_owns_a_function_the_guards_call_is_refused() => WithGoodAsync(
        (t, role) => t.ExecuteAsync($"ALTER FUNCTION lock_tenant_id(text, boolean) OWNER TO {role.Name}"),
        async (role, t) =>
        {
            var facts = await GatherAsync(role.ConnectionString(t));
            Assert.Equal(["function lock_tenant_id"], facts.Owned);
            Assert.Equal(Refusal, Assert.Single(Findings(facts)).Kind);
        });

    // a trigger on a Packmoat table whose function lives in another schema: still the login's to drop. mutation: the triggers'
    // functions left out (only the schema's own functions looked at)
    [PostgresFact]
    public Task A_role_that_owns_a_trigger_s_function_in_another_schema_is_refused() => WithGoodAsync(
        (t, role) => t.ExecuteAsync(
            "CREATE SCHEMA pm_elsewhere",
            "CREATE FUNCTION pm_elsewhere.stand_in() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$",
            $"ALTER FUNCTION pm_elsewhere.stand_in() OWNER TO {role.Name}",
            "CREATE TRIGGER tenants_stand_in BEFORE INSERT ON tenants FOR EACH ROW EXECUTE FUNCTION pm_elsewhere.stand_in()"),
        async (role, t) =>
        {
            var facts = await GatherAsync(role.ConnectionString(t));
            Assert.Equal(["function stand_in"], facts.Owned);
            Assert.Equal(Refusal, Assert.Single(Findings(facts)).Kind);
        });

    [PostgresFact]   // the schema's owner can drop anything in it
    public Task A_role_that_owns_the_schema_is_refused() => WithGoodAsync(
        (t, role) => t.ExecuteAsync($"ALTER SCHEMA public OWNER TO {role.Name}"),
        async (role, t) =>
        {
            var facts = await GatherAsync(role.ConnectionString(t));
            Assert.Contains("schema public", facts.Owned);
            Assert.Contains(Findings(facts), f => f.Kind == Refusal && f.Message.StartsWith("The server's role owns", StringComparison.Ordinal));
        });

    // A gather that cannot read fails the start, never passes it, and never shows the password
    [PostgresFact]
    public Task A_gather_that_cannot_read_fails_closed_without_the_password() => WithGoodAsync(Nothing, async (role, t) =>
    {
        var wrongPassword = new NpgsqlConnectionStringBuilder(role.ConnectionString(t)) { Password = PostgresConnectionStringTests.Sentinel(), Pooling = false };
        var unreachable = new NpgsqlConnectionStringBuilder(role.ConnectionString(t)) { Host = "127.0.0.1", Port = 1, Timeout = 3, Pooling = false };
        var missingDb = new NpgsqlConnectionStringBuilder(role.ConnectionString(t)) { Database = $"pm_missing_{Guid.NewGuid():N}", Pooling = false };
        foreach (var builder in new[] { wrongPassword, unreachable, missingDb })
        {
            var whole = builder.ConnectionString;
            await using var db = PostgresDatabase.Create(PostgresConnectionString.Parse(whole));
            var e = await Assert.ThrowsAnyAsync<Exception>(() =>
                PostgresStartupChecks.RequireAsync(db, TestTracks.Modules, new SystemClock(), new PostgresStartupOptions(), _ => { }));
            Assert.IsNotType<PostgresStartupException>(e);   // not judged at all: nothing was read
            PostgresConnectionStringTests.AssertRedacted(e, builder.Password!, whole);
        }
    });

    // Another session holding the probe key with any other second key (1, say) does not stop the probe: its second key is
    // the probing session's own pid. mutation: a fixed second key
    [PostgresFact]
    public Task The_pooler_probe_is_not_stopped_by_another_session_s_lock() => WithGoodAsync(Nothing, async (role, t) =>
    {
        await using var other = new NpgsqlConnection(role.ConnectionString(t));
        await using var probe = new NpgsqlConnection(role.ConnectionString(t));
        await other.OpenAsync();
        await probe.OpenAsync();
        await using (var hold = new NpgsqlCommand($"SELECT pg_advisory_lock({PostgresStartupChecks.PoolerProbeKey}, 1)", other))
            await hold.ExecuteNonQueryAsync();
        Assert.Equal(PostgresStartupChecks.PoolerProbeRounds, await PostgresStartupChecks.PoolerProbeAsync(probe, probe, CancellationToken.None));
    });

    // A pool of one is judged (the gather holds one connection at a time) and refused, not left waiting on itself
    [PostgresFact]
    public Task A_pool_of_one_is_refused_not_left_waiting() => WithGoodAsync(Nothing, async (role, t) =>
    {
        var facts = await GatherAsync(role.ConnectionString(t, maxPoolSize: 1));
        var f = Assert.Single(Findings(facts));
        Assert.Equal(Refusal, f.Kind);
        Assert.StartsWith("PACKMOAT_DB's Maximum Pool Size is 1: Packmoat needs at least 2", f.Message);
    });

    [PostgresFact]   // mutation: the probe's look run on the session that took the lock whatever the connection (a pooler not detected)
    public Task The_pooler_probe_holds_on_one_session_and_fails_across_two() => WithGoodAsync(Nothing, async (role, t) =>
    {
        await using var a = new NpgsqlConnection(role.ConnectionString(t));
        await using var b = new NpgsqlConnection(role.ConnectionString(t));
        await a.OpenAsync();
        await b.OpenAsync();
        Assert.Equal(PostgresStartupChecks.PoolerProbeRounds, await PostgresStartupChecks.PoolerProbeAsync(a, a, CancellationToken.None));
        // A pooler in transaction mode runs the look on another session: the lock is not seen there.
        var held = await PostgresStartupChecks.PoolerProbeAsync(a, b, CancellationToken.None);
        Assert.Equal(0, held);
        Assert.Equal(Refusal, PostgresStartupChecks.Pooler(held, sameSession: false, lockShared: false)!.Kind);
        // Every probe let its lock go.
        await using var left = new NpgsqlCommand($"SELECT COUNT(*) FROM pg_locks WHERE locktype = 'advisory' AND classid::bigint = {PostgresStartupChecks.PoolerProbeKey}", b);
        Assert.Equal(0L, await left.ExecuteScalarAsync());
    });

    // An idle pooler in transaction mode gave two client connections one server session (the same pid both times), and the
    // rounds above all held. Stood for by running both "connections" of the two-connection probe on one physical connection.
    // mutations: the second connection the first (in the gather); the pid comparison dropped; the lock sighting dropped
    [PostgresFact]
    public Task The_two_connection_probe_refuses_one_shared_session_and_passes_two() => WithGoodAsync(Nothing, async (role, t) =>
    {
        await using var a = new NpgsqlConnection(role.ConnectionString(t));
        await using var b = new NpgsqlConnection(role.ConnectionString(t));
        await a.OpenAsync();
        await b.OpenAsync();

        Assert.Equal((false, false), await PostgresStartupChecks.PoolerPairProbeAsync(a, b, CancellationToken.None));
        Assert.Null(PostgresStartupChecks.Pooler(PostgresStartupChecks.PoolerProbeRounds, false, false));

        // One session behind both: every round of the old probe holds there, yet the pair is caught on both signals.
        Assert.Equal(PostgresStartupChecks.PoolerProbeRounds, await PostgresStartupChecks.PoolerProbeAsync(a, a, CancellationToken.None));
        var (sameSession, lockShared) = await PostgresStartupChecks.PoolerPairProbeAsync(a, a, CancellationToken.None);
        Assert.True(sameSession);
        Assert.True(lockShared);
        Assert.Equal(Refusal, PostgresStartupChecks.Pooler(PostgresStartupChecks.PoolerProbeRounds, sameSession, lockShared)!.Kind);

        // Every probe let its lock go.
        await using var left = new NpgsqlCommand($"SELECT COUNT(*) FROM pg_locks WHERE locktype = 'advisory' AND classid::bigint = {PostgresStartupChecks.PoolerProbeKey}", b);
        Assert.Equal(0L, await left.ExecuteScalarAsync());
    });

    // A role allowed one connection gets the probe's second refused (53300). The start refuses, saying why,
    // rather than failing with the server's error. mutation: the 53300 let through as an error
    [PostgresFact]
    public Task A_role_allowed_one_connection_is_refused_with_why() => WithGoodAsync(
        (t, role) => AdminAsync($"ALTER ROLE {role.Name} CONNECTION LIMIT 1"), async (role, t) =>
    {
        var facts = await GatherAsync(role.ConnectionString(t));
        Assert.True(facts.PoolerSecondConnectionRefused);
        var f = Assert.Single(Findings(facts));
        Assert.Equal(Refusal, f.Kind);
        Assert.StartsWith("The PostgreSQL server refused a second connection as too many", f.Message);
    });

    // Opt-in (dotnet/tools/pgbouncer-tests.sh): what happens on a real PgBouncer in transaction mode. Idle, it hands every
    // statement the same server session, so the rounds can all hold; the two-connection probe sees one session behind both and refuses.
    [PgBouncerFact]
    public async Task A_transaction_mode_PgBouncer_is_refused()
    {
        await using var t = await PostgresScratchDatabase.FromTemplateAsync();
        var facts = await GatherAsync(new NpgsqlConnectionStringBuilder(PgBouncerFactAttribute.Value) { Database = t.Name, MaxPoolSize = 10 }.ConnectionString);
        Assert.True(facts.PoolerSameSession || facts.PoolerLockShared,
            $"same session {facts.PoolerSameSession}, lock shared {facts.PoolerLockShared}, rounds held {facts.PoolerProbeHeld}");
        Assert.Contains(Findings(facts), f => f.Kind == Refusal && f.Message.Contains("PACKMOAT_DB goes through a pooler in transaction or statement mode", StringComparison.Ordinal));
    }

    [PostgresFact]   // the test server is local and plain: the facts are real, the host is then judged as a remote one
    public Task TLS_off_is_refused_for_a_host_that_is_not_local() => WithGoodAsync(Nothing, async (role, t) =>
    {
        var cs = new NpgsqlConnectionStringBuilder(role.ConnectionString(t)) { SslMode = SslMode.Disable }.ConnectionString;
        var facts = await GatherAsync(cs);
        Assert.False(facts.Encrypted);
        Assert.Equal(SslMode.Disable, facts.SslMode);
        Assert.Empty(Findings(facts));   // local: allowed
        var f = Assert.Single(Findings(facts with { Host = "db.example.com" }));
        Assert.StartsWith("The connection to the PostgreSQL server is not required to be encrypted", f.Message);
    });

    [PostgresFact]   // the server's version as the facts read it; an older one is refused (judged in PostgresStartupChecksTests)
    public Task The_server_version_is_read_from_the_server() => WithGoodAsync(Nothing, async (role, t) =>
    {
        var facts = await GatherAsync(role.ConnectionString(t));
        Assert.Null(PostgresStartupChecks.ServerVersion(facts.ServerVersion));
        Assert.Equal(Refusal, Assert.Single(PostgresStartupChecks.Evaluate(facts with { ServerVersion = new Version(16, 4) }, new())).Kind);
    });

    [PostgresFact]   // a clock 10 s off this machine's is a warning (the server reads its own); mutation: the skew not read from the server
    public Task A_clock_skew_is_a_warning() => WithGoodAsync(Nothing, async (role, t) =>
    {
        var facts = await GatherAsync(role.ConnectionString(t), new FakeClock { UtcNow = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10) });
        var f = Assert.Single(PostgresStartupChecks.Evaluate(facts, new()));
        Assert.Equal(Warning, f.Kind);
        Assert.Contains(" s behind this server's", f.Message);
    });

    [PostgresFact]   // an hour off is refused: copies would disagree on the shared leases. mutation: the refusal left a warning
    public Task A_clock_skew_over_thirty_seconds_is_refused() => WithGoodAsync(Nothing, async (role, t) =>
    {
        var facts = await GatherAsync(role.ConnectionString(t), new FakeClock { UtcNow = DateTimeOffset.UtcNow + TimeSpan.FromHours(1) });
        var f = Assert.Single(PostgresStartupChecks.Evaluate(facts, new()));
        Assert.Equal(Refusal, f.Kind);
        Assert.Contains(" s behind this server's. Over 30 s, copies of the server would disagree", f.Message);
    });

    [PostgresFact]
    public Task A_pool_larger_than_max_connections_is_a_warning() => WithGoodAsync(Nothing, async (role, t) =>
    {
        var facts = await GatherAsync(role.ConnectionString(t, maxPoolSize: 1000));
        var f = Assert.Single(Findings(facts));
        Assert.Equal(Warning, f.Kind);
        Assert.StartsWith("PACKMOAT_DB's Maximum Pool Size is 1000, but the server takes at most", f.Message);
    });

    [PostgresFact]   // mutation: the connection string put into the refusal; RequireAsync not passing the warnings on
    public Task RequireAsync_refuses_with_every_refusal_and_never_the_password() => WithGoodAsync(
        (t, _) => t.ExecuteAsync($"ALTER DATABASE {t.Name} SET default_transaction_isolation = 'repeatable read'",
            "ALTER TABLE audit_events DISABLE TRIGGER audit_no_delete"),
        async (role, t) =>
        {
            var whole = role.ConnectionString(t, maxPoolSize: 1000);
            var warnings = new List<string>();
            await using var db = PostgresDatabase.Create(PostgresConnectionString.Parse(whole));
            var e = await Assert.ThrowsAsync<PostgresStartupException>(() =>
                PostgresStartupChecks.RequireAsync(db, TestTracks.Modules, new SystemClock(), new PostgresStartupOptions(), warnings.Add));
            Assert.StartsWith($"The PostgreSQL database at {db.Describe()} is not safe to serve from. Refusing to start:\n  - ", e.Message);
            Assert.Contains("default_transaction_isolation is repeatable read", e.Message);
            Assert.Contains("audit_events.audit_no_delete (disabled)", e.Message);
            Assert.Contains(warnings, w => w.StartsWith("PACKMOAT_DB's Maximum Pool Size is 1000", StringComparison.Ordinal));
            PostgresConnectionStringTests.AssertRedacted(e, role.Password, whole);
            Assert.All(warnings, w => Assert.DoesNotContain(role.Password, w, StringComparison.Ordinal));
        });

    [PostgresFact]
    public Task RequireAsync_passes_a_good_database() => WithGoodAsync(Nothing, async (role, t) =>
    {
        await using var db = PostgresDatabase.Create(PostgresConnectionString.Parse(role.ConnectionString(t)));
        await PostgresStartupChecks.RequireAsync(db, TestTracks.Modules, new SystemClock(), new PostgresStartupOptions(), _ => { });
    });
}
