using System.Text.RegularExpressions;
using Npgsql;
using Packmoat.Infrastructure.Postgres;
using static Packmoat.Infrastructure.Postgres.PostgresFindingKind;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// The start's checks on PostgreSQL, each a pure function over the facts, without a server. The same refusals against a
/// real misconfigured server are in <see cref="PostgresStartupServerTests"/>.
/// </summary>
public class PostgresStartupChecksTests
{
    /// <summary>Facts every check passes on: PostgreSQL 17, read committed, every trigger firing, an app role, a direct local connection.</summary>
    internal static PostgresStartupFacts Good() => new()
    {
        ServerVersion = new Version(17, 11),
        TransactionIsolation = "read committed",
        DefaultTransactionIsolation = "read committed",
        SessionReplicationRole = "origin",
        Triggers = PostgresStartupChecks.PinnedTriggers.ToDictionary(t => t, _ => "O"),
        IdleSessionTimeout = TimeSpan.Zero,
        Tracks = [new TrackVersion("core", 23, 23, 23)],
        Schema = "public",
        Superuser = false,
        Owned = [],
        CreateOnSchema = false,
        PoolerProbeHeld = PostgresStartupChecks.PoolerProbeRounds,
        Host = "127.0.0.1",
        SslMode = SslMode.Prefer,
        Encrypted = false,
        ClockSkew = TimeSpan.FromMilliseconds(300),
        MaxConnections = 100,
        ReservedConnections = 3,
        MaxPoolSize = 20,
    };

    private static readonly PostgresStartupOptions Strict = new();

    private static PostgresFinding Only(PostgresStartupFacts facts, PostgresStartupOptions? options = null) =>
        Assert.Single(PostgresStartupChecks.Evaluate(facts, options ?? Strict));

    [Fact]   // the negative control: every other test changes one fact of these
    public void Good_facts_pass_every_check() => Assert.Empty(PostgresStartupChecks.Evaluate(Good(), Strict));

    // ------------------------------------------------------------------------------------------------ version

    [Theory]   // mutations: the version check inverted or removed; 16 accepted
    [InlineData(16, 9)]
    [InlineData(12, 0)]
    public void An_older_server_is_refused_and_nothing_else_is_judged(int major, int minor)
    {
        var f = Only(Good() with { ServerVersion = new Version(major, minor), Superuser = true, TransactionIsolation = "serializable" });
        Assert.Equal(Refusal, f.Kind);
        Assert.Equal($"The PostgreSQL server is version {major}.{minor}; Packmoat needs PostgreSQL 17 or newer.", f.Message);
    }

    [Fact]
    public void Seventeen_and_newer_pass() =>
        Assert.Empty(PostgresStartupChecks.Evaluate(Good() with { ServerVersion = new Version(18, 1) }, Strict));

    // ------------------------------------------------------------------------------------------------ read committed

    [Theory]   // mutations: either setting not read; the check inverted
    [InlineData("serializable", "read committed", "transaction_isolation is serializable.")]
    [InlineData("read committed", "repeatable read", "default_transaction_isolation is repeatable read.")]
    [InlineData("serializable", "serializable", "transaction_isolation is serializable, and default_transaction_isolation is serializable.")]
    public void An_isolation_other_than_read_committed_is_refused(string isolation, string @default, string start)
    {
        var f = Only(Good() with { TransactionIsolation = isolation, DefaultTransactionIsolation = @default });
        Assert.Equal(Refusal, f.Kind);
        Assert.StartsWith(start, f.Message);
        Assert.Contains("read committed", f.Message);
    }

    // ------------------------------------------------------------------------------------------------ session_replication_role

    [Theory]   // mutation: the check removed (replica turns every 'O' trigger off)
    [InlineData("replica")]
    [InlineData("local")]
    public void A_replication_role_other_than_origin_is_refused(string role)
    {
        var f = Only(Good() with { SessionReplicationRole = role });
        Assert.Equal(Refusal, f.Kind);
        Assert.StartsWith($"session_replication_role is {role}, not origin", f.Message);
    }

    // ------------------------------------------------------------------------------------------------ triggers

    [Theory]   // mutations: 'D' accepted; 'R' accepted; only 'O' accepted ('A' refused)
    [InlineData("D", "disabled")]
    [InlineData("R", "replica only")]
    public void A_trigger_that_does_not_fire_is_refused(string tgenabled, string state)
    {
        var triggers = PostgresStartupChecks.PinnedTriggers.ToDictionary(t => t, _ => "O");
        triggers["audit_events.audit_no_update"] = tgenabled;
        var f = Only(Good() with { Triggers = triggers });
        Assert.Equal(Refusal, f.Kind);
        Assert.Contains($"not firing: audit_events.audit_no_update ({state})", f.Message);
        Assert.DoesNotContain("missing", f.Message);
    }

    [Fact]
    public void A_trigger_set_to_fire_always_passes()
    {
        var triggers = PostgresStartupChecks.PinnedTriggers.ToDictionary(t => t, _ => "A");
        Assert.Empty(PostgresStartupChecks.Evaluate(Good() with { Triggers = triggers }, Strict));
    }

    [Fact]   // mutations: a missing trigger not looked for; a TRUNCATE guard left out of the list
    public void A_missing_trigger_or_truncate_guard_is_refused_by_name()
    {
        var triggers = PostgresStartupChecks.PinnedTriggers.ToDictionary(t => t, _ => "O");
        triggers.Remove("tenant_purges.tenant_purges_no_truncate");
        triggers.Remove("tenants.tenants_no_purged_id");
        triggers["tenants.something_else"] = "O";   // another trigger stands in for none
        var f = Only(Good() with { Triggers = triggers });
        Assert.Equal(Refusal, f.Kind);
        Assert.Contains("missing: tenant_purges.tenant_purges_no_truncate, tenants.tenants_no_purged_id", f.Message);
    }

    [Fact]   // One pinned trigger gone and one other in its place keeps the count, and is still refused by name
    public void A_trigger_replaced_by_another_of_another_name_is_refused()
    {
        var triggers = PostgresStartupChecks.PinnedTriggers.ToDictionary(t => t, _ => "O");
        triggers.Remove("audit_events.audit_no_delete");
        triggers["audit_events.audit_no_delete_v2"] = "O";
        Assert.Equal(PostgresStartupChecks.PinnedTriggers.Count, triggers.Count);
        var f = Only(Good() with { Triggers = triggers });
        Assert.Equal(Refusal, f.Kind);
        Assert.Contains("missing: audit_events.audit_no_delete)", f.Message);
    }

    private static readonly Regex CreateTrigger = new(@"CREATE\s+TRIGGER\s+(\w+)\s+BEFORE\s+(?:INSERT|UPDATE|DELETE|TRUNCATE)(?:\s+OF\s+\w+)?\s+ON\s+(\w+)",
        RegexOptions.IgnoreCase);

    [Fact]   // mutation: a trigger dropped from PinnedTriggers, or one added to the schema and not pinned
    public void The_pinned_triggers_are_exactly_the_ones_the_PostgreSQL_schema_makes()
    {
        var made = PostgresCoreMigrations.Statements.SelectMany(v => v).SelectMany(sql => CreateTrigger.Matches(sql))
            .Select(m => $"{m.Groups[2].Value}.{m.Groups[1].Value}").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(14, made.Length);
        Assert.Equal(made, PostgresStartupChecks.PinnedTriggers);
        // SQLite's 11 rules, and the three TRUNCATE guards.
        Assert.Equal(SchemaTriggerTests.Current, PostgresStartupChecks.PinnedTriggers.Select(t => t[(t.IndexOf('.') + 1)..])
            .Where(t => !t.EndsWith("_no_truncate", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.Equal(3, PostgresStartupChecks.PinnedTriggers.Count(t => t.EndsWith("_no_truncate", StringComparison.Ordinal)));
    }

    // ------------------------------------------------------------------------------------------------ idle_session_timeout

    [Theory]   // mutations: the check removed; 0 (off) refused; the comparison turned round
    [InlineData(30, true)]
    [InlineData(299, true)]
    [InlineData(300, false)]
    [InlineData(3600, false)]
    [InlineData(0, false)]
    public void An_idle_session_timeout_shorter_than_the_longest_lock_hold_is_refused(int seconds, bool refused)
    {
        var findings = PostgresStartupChecks.Evaluate(Good() with { IdleSessionTimeout = TimeSpan.FromSeconds(seconds) }, Strict);
        if (!refused) { Assert.Empty(findings); return; }
        var f = Assert.Single(findings);
        Assert.Equal(Refusal, f.Kind);
        Assert.StartsWith($"idle_session_timeout is {seconds} s: shorter than 300 s", f.Message);
    }

    [Fact]
    public void The_shortest_idle_session_timeout_is_an_option() =>
        Assert.Empty(PostgresStartupChecks.Evaluate(Good() with { IdleSessionTimeout = TimeSpan.FromSeconds(30) },
            Strict with { ShortestIdleSessionTimeout = TimeSpan.FromSeconds(10) }));

    // ------------------------------------------------------------------------------------------------ schema version

    [Theory]   // mutations: the check removed; IsCurrent ignored for a module's track; a newer database accepted
    [InlineData(null, 0, "core: not migrated, this build needs version 23")]
    [InlineData(22, 22, "core: at version 22 with 22 of 23 recorded, this build needs 23")]
    [InlineData(23, 22, "core: at version 23 with 22 of 23 recorded, this build needs 23")]
    [InlineData(24, 24, "core: at version 24, newer than this build's 23")]
    public void A_schema_that_is_not_this_build_s_is_refused_and_never_migrated(int? newest, int recorded, string line)
    {
        var f = Only(Good() with { Tracks = [new TrackVersion("core", 23, newest, recorded)] });
        Assert.Equal(Refusal, f.Kind);
        Assert.Contains(line, f.Message);
        Assert.Contains("the server never migrates: run packmoat-admin db migrate as the owner role first", f.Message);
    }

    [Fact]
    public void A_module_track_behind_is_refused_too()
    {
        var f = Only(Good() with { Tracks = [new TrackVersion("core", 23, 23, 23), new TrackVersion("hosted", 9, 8, 8)] });
        Assert.Contains("hosted: at version 8", f.Message);
        Assert.DoesNotContain("core:", f.Message);
    }

    [Fact]
    public void No_schema_on_the_search_path_is_refused()
    {
        var f = Only(Good() with { Schema = null });
        Assert.Equal(Refusal, f.Kind);   // Never only a warning
        Assert.StartsWith("The server's search_path names no schema that exists", f.Message);
    }

    // ------------------------------------------------------------------------------------------------ the role

    [Theory]   // mutations: superuser accepted; the single-role escape let a superuser through
    [InlineData(false)]
    [InlineData(true)]
    public void A_superuser_is_refused_even_with_the_single_role_escape(bool singleRole)
    {
        var f = Only(Good() with { Superuser = true }, new PostgresStartupOptions(singleRole));
        Assert.Equal(Refusal, f.Kind);
        Assert.StartsWith("PACKMOAT_DB signs in as a superuser", f.Message);
    }

    [Theory]   // Refused whatever the escape says. mutations: the check removed; the escape let it through
    [InlineData(false)]
    [InlineData(true)]
    public void A_login_that_may_set_session_replication_role_is_refused(bool singleRole)
    {
        var findings = PostgresStartupChecks.Evaluate(Good() with { CanSetReplicationRole = true }, new PostgresStartupOptions(singleRole));
        var f = Assert.Single(findings, x => x.Kind == Refusal);
        Assert.StartsWith("The server's login, or a role it is a member of, may set session_replication_role", f.Message);
    }

    [Fact]   // mutations: ownership not checked; CREATE not checked
    public void A_role_that_owns_a_table_or_can_create_in_the_schema_is_refused()
    {
        var owner = Only(Good() with { Owned = ["table audit_events", "table tenants"] });
        Assert.Equal(Refusal, owner.Kind);
        Assert.StartsWith("The server's role owns table audit_events, table tenants:", owner.Message);
        Assert.Contains("PACKMOAT_DB_SINGLE_ROLE=on", owner.Message);

        var create = Only(Good() with { CreateOnSchema = true });
        Assert.Equal(Refusal, create.Kind);
        Assert.StartsWith("The server's role can CREATE in the schema:", create.Message);

        var many = Only(Good() with { Owned = ["schema public", "table b", "function c", "table d"], CreateOnSchema = true });
        Assert.StartsWith("The server's role owns 4 objects (schema public, table b, function c, …), and can CREATE in the schema:", many.Message);

        // A trigger's function alone is enough: its owner can DROP FUNCTION … CASCADE, and the guard goes with it.
        var function = Only(Good() with { Owned = ["function audit_no_delete"] });
        Assert.Equal(Refusal, function.Kind);
        Assert.StartsWith("The server's role owns function audit_no_delete:", function.Message);
    }

    [Fact]   // mutation: the escape ignored (still refused), or it silences the finding instead of warning
    public void The_single_role_escape_makes_owning_and_creating_a_warning()
    {
        var f = Only(Good() with { Owned = ["table audit_events"], CreateOnSchema = true }, new PostgresStartupOptions(SingleRole: true));
        Assert.Equal(Warning, f.Kind);
        Assert.EndsWith("Allowed by PACKMOAT_DB_SINGLE_ROLE=on.", f.Message);
    }

    [Fact]   // mutations: the finding removed from RoleFindings; the escape ignored or silencing it
    public void A_role_that_can_run_an_owners_SECURITY_DEFINER_function_is_refused_and_the_escape_makes_it_a_warning()
    {
        var one = Only(Good() with { DefinerFunctions = ["rv_tools.purge_all()"] });
        Assert.Equal(Refusal, one.Kind);
        Assert.StartsWith("The server's role can run rv_tools.purge_all(): SECURITY DEFINER function (run through PUBLIC, a role, or directly), so it runs as its owner", one.Message);
        Assert.EndsWith("Revoke EXECUTE on it from PUBLIC and the server's role, or drop it.", one.Message);

        var many = Only(Good() with { DefinerFunctions = ["a.f()", "b.g(integer)", "c.h()", "d.i()"] });
        Assert.StartsWith("The server's role can run 4 functions (a.f(), b.g(integer), c.h(), …): SECURITY DEFINER functions", many.Message);

        var escaped = PostgresStartupChecks.Evaluate(Good() with { Owned = ["table t"], DefinerFunctions = ["a.f()"] }, new PostgresStartupOptions(SingleRole: true));
        Assert.All(escaped, f => Assert.Equal(Warning, f.Kind));
        Assert.Contains(escaped, f => f.Message.StartsWith("The server's role can run a.f()", StringComparison.Ordinal) && f.Message.EndsWith("Allowed by PACKMOAT_DB_SINGLE_ROLE=on.", StringComparison.Ordinal));
    }

    [Fact]   // mutations: the finding removed from RoleFindings; the single-role escape applied to it
    public void A_superusers_function_granted_to_the_role_is_refused_whatever_single_role_says()
    {
        var one = Only(Good() with { GrantedSuperuserFunctions = ["provider_extras.session_replication_role(arg_parameter text)"] });
        Assert.Equal(Refusal, one.Kind);
        Assert.Equal("The server's role is granted provider_extras.session_replication_role(arg_parameter text): a superuser's SECURITY DEFINER " +
            "function, granted to the role or a role it is a member of, not only to PUBLIC. It runs as a superuser, and can turn off the triggers " +
            "that keep the audit and purge records. Revoke EXECUTE on it from the server's role and its roles (refused whatever " +
            "PACKMOAT_DB_SINGLE_ROLE says).", one.Message);

        var many = Only(Good() with { GrantedSuperuserFunctions = ["a.f()", "b.g(integer)", "c.h()", "d.i()"] });
        Assert.StartsWith("The server's role is granted 4 functions (a.f(), b.g(integer), c.h(), …): a superuser's SECURITY DEFINER functions", many.Message);

        var single = PostgresStartupChecks.Evaluate(Good() with { Owned = ["table t"], GrantedSuperuserFunctions = ["a.f()"] }, new PostgresStartupOptions(SingleRole: true));
        Assert.Contains(single, f => f.Kind == Refusal && f.Message.StartsWith("The server's role is granted a.f()", StringComparison.Ordinal));
    }

    // A superuser's granted function that is no extension's is in both lists; it is named once, by the refusal that holds
    // whatever single-role says. mutation: the de-duplication dropped (a second, definer finding: a warning under single-role)
    [Fact]
    public void A_granted_superusers_function_is_named_once_and_refused_under_single_role()
    {
        var facts = Good() with { DefinerFunctions = ["rv_tools.srr(arg text)", "rv_tools.other()"], GrantedSuperuserFunctions = ["rv_tools.srr(arg text)"] };
        var strict = PostgresStartupChecks.Evaluate(facts, Strict);
        Assert.Equal(2, strict.Count);
        Assert.Single(strict, f => f.Message.Contains("rv_tools.srr(arg text)", StringComparison.Ordinal));
        Assert.Single(strict, f => f.Message.StartsWith("The server's role can run rv_tools.other():", StringComparison.Ordinal));

        var single = PostgresStartupChecks.Evaluate(facts with { DefinerFunctions = ["rv_tools.srr(arg text)"], Owned = ["table t"] }, new PostgresStartupOptions(SingleRole: true));
        var srr = Assert.Single(single, f => f.Message.Contains("rv_tools.srr(arg text)", StringComparison.Ordinal));
        Assert.Equal(Refusal, srr.Kind);
    }

    [Fact]
    public void The_single_role_escape_on_an_app_role_warns_it_is_not_needed()
    {
        var f = Only(Good(), new PostgresStartupOptions(SingleRole: true));
        Assert.Equal(Warning, f.Kind);
        Assert.StartsWith("PACKMOAT_DB_SINGLE_ROLE=on, but the server's role owns nothing in the schema", f.Message);
    }

    [Theory]   // strict by default: only on turns it on
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("off", false)]
    [InlineData("on", true)]
    public void PACKMOAT_DB_SINGLE_ROLE_is_on_or_off(string? value, bool on) => Assert.Equal(on, PostgresStartupOptions.ParseSingleRole(value));

    [Theory]
    [InlineData("ON")]
    [InlineData("true")]
    [InlineData("1")]
    public void Any_other_PACKMOAT_DB_SINGLE_ROLE_stops_the_start_without_its_value(string value)
    {
        var e = Assert.Throws<SettingsException>(() => PostgresStartupOptions.ParseSingleRole(value));
        Assert.Equal("PACKMOAT_DB_SINGLE_ROLE must be on or off.", e.Message);
    }

    [Fact]
    public void The_default_options_are_strict() => Assert.False(new PostgresStartupOptions().SingleRole);

    // ------------------------------------------------------------------------------------------------ pooler

    [Theory]   // mutations: the probe's result ignored; two of three rounds accepted
    [InlineData(0)]
    [InlineData(2)]
    public void A_probe_round_that_lost_its_lock_is_refused(int held)
    {
        var f = Only(Good() with { PoolerProbeHeld = held });
        Assert.Equal(Refusal, f.Kind);
        Assert.StartsWith($"A session lock did not stay with its session ({held} of 3 probes held)", f.Message);
    }

    // An idle pooler in transaction mode hands both connections the same session, so every round holds; the two-connection signals
    // refuse it. mutations: the pid comparison dropped; the second connection's lock sighting ignored
    [Theory]
    [InlineData(true, false, "Two connections held open at once ran on the same server session: ")]
    [InlineData(false, true, "A second connection saw the first one's session lock as its own: ")]
    [InlineData(true, true, "Two connections held open at once ran on the same server session; a second connection saw the first one's session lock as its own: ")]
    public void Two_connections_on_one_session_are_refused_though_every_round_held(bool sameSession, bool lockShared, string start)
    {
        var f = Only(Good() with { PoolerSameSession = sameSession, PoolerLockShared = lockShared });
        Assert.Equal(Refusal, f.Kind);
        Assert.Equal(start + "PACKMOAT_DB goes through a pooler in transaction or statement mode, where one connection's session lock can pass to " +
            "another. Connect directly, or through a pooler in session mode.", f.Message);
    }

    [Fact]   // both signals in one finding
    public void A_lost_round_and_a_shared_session_are_one_refusal()
    {
        var f = Only(Good() with { PoolerProbeHeld = 1, PoolerSameSession = true });
        Assert.StartsWith("A session lock did not stay with its session (1 of 3 probes held); two connections held open at once ran on the same server session: ", f.Message);
    }

    // The two-connection probe's reading. A lock the first session could not take counts as shared (fail closed).
    // mutations: the fail-closed !taken dropped (m4); the pid comparison dropped
    [Theory]
    [InlineData(10, 11, true, false, false, false)]   // two sessions, the lock taken and not seen: a direct connection
    [InlineData(10, 10, true, true, true, true)]      // one session behind both
    [InlineData(10, 11, true, true, false, true)]     // seen from another session
    [InlineData(10, 11, false, false, false, true)]   // not taken: fails closed
    [InlineData(10, 10, false, false, true, true)]
    public void The_pair_probe_reads_one_session_and_fails_closed_on_a_lock_it_could_not_take(int firstPid, int secondPid, bool taken, bool seen,
        bool sameSession, bool lockShared) =>
        Assert.Equal((sameSession, lockShared), PostgresStartupChecks.PairSignals(firstPid, secondPid, taken, seen));

    [Fact]   // A server that refuses the probe's second connection as one too many. mutation: the finding not added
    public void A_second_connection_refused_as_too_many_is_refused_with_why()
    {
        var f = Only(Good() with { PoolerSecondConnectionRefused = true });
        Assert.Equal(Refusal, f.Kind);
        Assert.Equal("The PostgreSQL server refused a second connection as too many (max_connections, or the role's CONNECTION LIMIT): the start's " +
            "pooler check holds two connections at once, and Packmoat needs at least two, since a held lock keeps one while the work runs " +
            "on another. Raise the role's CONNECTION LIMIT (to at least Maximum Pool Size plus one) or free connections on the server.", f.Message);
    }

    // ------------------------------------------------------------------------------------------------ TLS

    [Theory]   // mutations: the check removed; Disable accepted when the connection says encrypted; a local host refused
    [InlineData("db.example.com", SslMode.Disable, false, true)]
    [InlineData("db.example.com", SslMode.Disable, true, true)]
    [InlineData("db.example.com", SslMode.Prefer, false, true)]     // Prefer fell back to plain text
    [InlineData("db.example.com", SslMode.Prefer, true, true)]      // encrypted now, but a later pooled connection may fall back
    [InlineData("db.example.com", SslMode.Allow, true, true)]
    [InlineData("10.0.0.5", SslMode.Require, false, true)]          // required, yet this connection is not encrypted (GSS alone, say)
    [InlineData("db.example.com", SslMode.VerifyCA, true, false)]
    // Only localhost itself and loopback addresses are local, never a name that merely contains one
    [InlineData("localhost.evil.com", SslMode.Disable, false, true)]
    [InlineData("evil-localhost", SslMode.Disable, false, true)]
    [InlineData("127.0.0.1.nip.io", SslMode.Disable, false, true)]
    [InlineData("127.evil.com", SslMode.Disable, false, true)]
    [InlineData("LOCALHOST", SslMode.Disable, false, false)]
    [InlineData("127.0.0.2", SslMode.Disable, false, false)]
    [InlineData("localhost,db.example.com", SslMode.Disable, false, true)]   // one remote host in the list is enough
    [InlineData("db.example.com", SslMode.Require, true, false)]
    [InlineData("db.example.com", SslMode.VerifyFull, true, false)]
    [InlineData("localhost", SslMode.Disable, false, false)]
    [InlineData("127.0.0.1", SslMode.Disable, false, false)]
    [InlineData("::1", SslMode.Disable, false, false)]
    [InlineData("[::1]:5433", SslMode.Disable, false, false)]
    [InlineData("127.0.0.1:5433,localhost", SslMode.Disable, false, false)]
    [InlineData("/var/run/postgresql", SslMode.Disable, false, false)]
    public void TLS_off_for_a_host_that_is_not_local_is_refused(string host, SslMode mode, bool encrypted, bool refused)
    {
        var findings = PostgresStartupChecks.Evaluate(Good() with { Host = host, SslMode = mode, Encrypted = encrypted }, Strict);
        if (!refused) { Assert.Empty(findings); return; }
        var f = Assert.Single(findings);
        Assert.Equal(Refusal, f.Kind);
        Assert.StartsWith("The connection to the PostgreSQL server is not required to be encrypted, and the server is not on this machine.", f.Message);
    }

    // ------------------------------------------------------------------------------------------------ warnings

    [Theory]   // mutations: the skew check removed; the sign ignored (behind not warned); a refusal instead of a warning
    [InlineData(2500, "2.5 s ahead of")]
    [InlineData(-3000, "3.0 s behind")]
    [InlineData(30000, "30.0 s ahead of")]
    [InlineData(-30000, "30.0 s behind")]
    public void A_clock_skew_over_two_seconds_is_a_warning(int milliseconds, string text)
    {
        var f = Only(Good() with { ClockSkew = TimeSpan.FromMilliseconds(milliseconds) });
        Assert.Equal(Warning, f.Kind);
        Assert.Contains(text, f.Message);
        Assert.EndsWith("so a prune may be refused or rows kept longer. Sync both clocks (NTP).", f.Message);
    }

    // On PostgreSQL more than one copy may run, each judging the shared leases and windows by its own clock. mutations: the bound
    // inverted (a small skew refused, a large one only warned); 30 s raised (3000 s); the sign ignored; a warning instead of a refusal;
    // the refusal only for the server (the command line shares the leases too)
    [Theory]
    [InlineData(30001, "30.0 s ahead of")]
    [InlineData(-30001, "30.0 s behind")]
    [InlineData(31000, "31.0 s ahead of")]
    [InlineData(-3600000, "3600.0 s behind")]
    public void A_clock_skew_over_thirty_seconds_is_refused(int milliseconds, string text)
    {
        Assert.Equal(TimeSpan.FromSeconds(30), PostgresStartupChecks.RefusedClockSkew);
        foreach (var options in new[] { Strict, new PostgresStartupOptions { ForCommand = true } })
        {
            var f = Only(Good() with { ClockSkew = TimeSpan.FromMilliseconds(milliseconds) }, options);
            Assert.Equal(Refusal, f.Kind);
            Assert.Equal($"The database's clock is {text} this server's. Over 30 s, copies of the server would disagree on the shared job leases, " +
                "rate limits and form lifetimes, each judged by its own clock. Sync both clocks (NTP).", f.Message);
        }
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(-2000)]
    public void Two_seconds_either_way_is_no_warning(int milliseconds) =>
        Assert.Empty(PostgresStartupChecks.Evaluate(Good() with { ClockSkew = TimeSpan.FromMilliseconds(milliseconds) }, Strict));

    [Theory]   // mutations: the pool check removed; the reserved slots not taken off; a refusal instead of a warning
    [InlineData(98, 100, 3, true)]
    [InlineData(97, 100, 3, false)]
    [InlineData(100, 100, 0, false)]
    public void A_pool_larger_than_the_server_s_connections_is_a_warning(int pool, int max, int reserved, bool warned)
    {
        var findings = PostgresStartupChecks.Evaluate(Good() with { MaxPoolSize = pool, MaxConnections = max, ReservedConnections = reserved }, Strict);
        if (!warned) { Assert.Empty(findings); return; }
        var f = Assert.Single(findings);
        Assert.Equal(Warning, f.Kind);
        Assert.StartsWith($"PACKMOAT_DB's Maximum Pool Size is {pool}, but the server takes at most {max - reserved} connections", f.Message);
    }

    [Theory]   // A pool of one waits on itself (a held lock keeps the only connection)
    [InlineData(1, true)]
    [InlineData(0, true)]
    [InlineData(2, false)]
    public void A_pool_of_fewer_than_two_is_refused(int pool, bool refused)
    {
        var findings = PostgresStartupChecks.Evaluate(Good() with { MaxPoolSize = pool }, Strict);
        if (!refused) { Assert.Empty(findings); return; }
        var f = Assert.Single(findings);
        Assert.Equal(Refusal, f.Kind);
        Assert.StartsWith($"PACKMOAT_DB's Maximum Pool Size is {pool}: Packmoat needs at least 2", f.Message);
    }

    // A process that capped its own pool (the command line's) skips the pool-size warning, which is about the server's pool,
    // and only that. mutations: the warning skipped even without the cap; the refusal of a pool under 2 skipped with it
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void A_capped_pool_skips_only_the_pool_size_warning(bool capped, bool warned)
    {
        var findings = PostgresStartupChecks.Evaluate(Good() with { MaxPoolSize = 98, MaxConnections = 100, ReservedConnections = 3, PoolCapped = capped }, Strict);
        Assert.Equal(warned, findings.Any(f => f.Kind == Warning && f.Message.StartsWith("PACKMOAT_DB's Maximum Pool Size is 98", StringComparison.Ordinal)));
        Assert.Equal(warned ? 1 : 0, findings.Count);
        var other = PostgresStartupChecks.Evaluate(Good() with { PoolCapped = capped, ClockSkew = TimeSpan.FromSeconds(5) }, Strict);
        Assert.Equal(Warning, Assert.Single(other).Kind);   // the other warnings stay
        var refused = Only(Good() with { MaxPoolSize = 1, PoolCapped = capped });
        Assert.Equal(Refusal, refused.Kind);
        Assert.StartsWith("PACKMOAT_DB's Maximum Pool Size is 1: Packmoat needs at least 2", refused.Message);
    }

    // The server's refusal is as before; a command's says the command will not run. mutation: the command's wording reverted
    // to the server's
    [Fact]
    public void A_refusal_says_the_server_will_not_start_or_the_command_will_not_run()
    {
        string[] refused = ["  - one", "  - two"];
        Assert.Equal("The PostgreSQL database at h:5432/d as u is not safe to serve from. Refusing to start:\n  - one\n  - two",
            PostgresStartupChecks.RefusalMessage("h:5432/d as u", refused, forCommand: false));
        var command = PostgresStartupChecks.RefusalMessage("h:5432/d as u", refused, forCommand: true);
        Assert.Equal("The PostgreSQL database at h:5432/d as u fails the checks the server makes before it starts. Refusing to run the command:\n  - one\n  - two", command);
        Assert.DoesNotContain("serve from", command, StringComparison.Ordinal);
        Assert.DoesNotContain("Refusing to start", command, StringComparison.Ordinal);
        Assert.False(new PostgresStartupOptions().ForCommand);   // the server's default
    }

    // Each built-in role's refusal says what that role can do; pg_signal_backend is not a server
    // program or file. mutation: one reason for every role
    [Theory]
    [InlineData("pg_signal_backend", "can cancel or end other sessions, among them one that holds a tenant's session lock")]
    [InlineData("pg_execute_server_program", "runs programs as the database server")]
    [InlineData("pg_write_server_files", "writes files as the database server")]
    [InlineData("pg_read_server_files", "reads the database server's files")]
    [InlineData("pg_write_all_data", "writes every table")]
    public void Each_built_in_role_s_refusal_says_what_it_can_do(string role, string reach)
    {
        var f = PostgresStartupChecks.BuiltInRoleMembership([role])!;
        Assert.Equal(Refusal, f.Kind);
        Assert.StartsWith($"The server's role is a member of a built-in role that reaches past the schema's privileges: {role} ({reach}", f.Message);
        Assert.EndsWith("Revoke it (refused whatever PACKMOAT_DB_SINGLE_ROLE says).", f.Message);
        if (role == "pg_signal_backend") Assert.DoesNotContain("server program", f.Message, StringComparison.Ordinal);
        var two = PostgresStartupChecks.BuiltInRoleMembership(["pg_signal_backend", "pg_write_all_data"])!;
        Assert.Contains("built-in roles that reach past", two.Message, StringComparison.Ordinal);
        Assert.Contains("pg_signal_backend (can cancel", two.Message, StringComparison.Ordinal);
        Assert.Contains("; pg_write_all_data (writes every table", two.Message, StringComparison.Ordinal);
        Assert.EndsWith("Revoke them (refused whatever PACKMOAT_DB_SINGLE_ROLE says).", two.Message);
    }

    // ------------------------------------------------------------------------------------------------ nothing leaks

    [Fact]   // mutation: the connection string (or its password) put into any message
    public void No_message_holds_the_connection_string_or_the_password()
    {
        var sentinel = PostgresConnectionStringTests.Sentinel();
        var whole = $"Host=db.example.com;Database=packmoat;Username=packmoat_app;Password={sentinel};SSL Mode=Disable";
        var cs = PostgresConnectionString.Parse(whole);
        var bad = new PostgresStartupFacts
        {
            ServerVersion = new Version(17, 0),
            TransactionIsolation = "serializable",
            DefaultTransactionIsolation = "serializable",
            SessionReplicationRole = "replica",
            Triggers = new Dictionary<string, string> { ["audit_events.audit_no_update"] = "D" },
            IdleSessionTimeout = TimeSpan.FromSeconds(1),
            Tracks = [new TrackVersion("core", 23, 1, 1)],
            Schema = null,
            Superuser = true,
            Owned = ["table audit_events"],
            CreateOnSchema = true,
            CanSetReplicationRole = true,
            DefinerFunctions = ["rv_tools.purge_all()"],
            GrantedSuperuserFunctions = ["provider_extras.session_replication_role(arg_parameter text)"],
            PoolerProbeHeld = 1,
            PoolerSameSession = true,
            PoolerLockShared = true,
            PoolerSecondConnectionRefused = true,
            Host = cs.Host,
            SslMode = SslMode.Disable,
            ClockSkew = TimeSpan.FromMinutes(-5),
            MaxConnections = 10,
            ReservedConnections = 3,
            MaxPoolSize = cs.MaxPoolSize,
        };
        foreach (var options in new[] { Strict, new PostgresStartupOptions(SingleRole: true) })
        {
            var findings = PostgresStartupChecks.Evaluate(bad, options);
            Assert.True(findings.Count >= 15, $"every check should find something: {findings.Count}");
            Assert.All(findings, f =>
            {
                Assert.DoesNotContain(sentinel, f.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(whole, f.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("Password", f.Message, StringComparison.OrdinalIgnoreCase);
            });
        }
    }
}
