using System.Globalization;
using System.Net;
using Npgsql;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>Whether a startup finding stops the start or is only logged.</summary>
public enum PostgresFindingKind
{
    /// <summary>The server refuses to start.</summary>
    Refusal,

    /// <summary>The server starts, and logs it as a warning.</summary>
    Warning,
}

/// <summary>One thing the start found wrong with the database. Its message never holds the connection string or the password.</summary>
public sealed record PostgresFinding(PostgresFindingKind Kind, string Message)
{
    internal static PostgresFinding Refuse(string message) => new(PostgresFindingKind.Refusal, message);
    internal static PostgresFinding Warn(string message) => new(PostgresFindingKind.Warning, message);
}

/// <summary>The start's checks refused the database (<see cref="PostgresStartupChecks"/>). Nothing was written.</summary>
public sealed class PostgresStartupException(string message) : InvalidOperationException(message);

/// <summary>
/// What the start's checks may be told.
/// </summary>
/// <param name="SingleRole">PACKMOAT_DB_SINGLE_ROLE=on: the server's role may own the tables and create in the schema (one role for the
/// migrations and the server), which is then a warning rather than a refusal. Never a superuser.</param>
public sealed record PostgresStartupOptions(bool SingleRole = false)
{
    /// <summary>The setting that lets the server's role own the tables (off by default, strict).</summary>
    public const string SingleRoleVariable = "PACKMOAT_DB_SINGLE_ROLE";

    /// <summary>
    /// The shortest idle_session_timeout the server accepts (other than 0, off). A module's per-tenant session advisory lock holds
    /// its connection idle while its holder works on other connections and calls out over the network (seconds a call): a server that
    /// ends an idle session sooner drops the lock silently. Five minutes is far past one tenant's work under such a lock.
    /// </summary>
    public TimeSpan ShortestIdleSessionTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The checks run before a <c>packmoat-admin</c> command, not a server's start: a refusal then says the command will not
    /// run, rather than that the server will not start. The checks themselves are the same.
    /// </summary>
    public bool ForCommand { get; init; }

    /// <summary>PACKMOAT_DB_SINGLE_ROLE's value: on, or off (also unset or empty). Anything else throws, naming the setting and never the value.</summary>
    public static bool ParseSingleRole(string? value) => value switch
    {
        null or "" or "off" => false,
        "on" => true,
        _ => throw new SettingsException($"{SingleRoleVariable} must be on or off."),
    };
}

/// <summary>
/// What the start reads from the database for its checks (<see cref="PostgresStartupChecks.GatherAsync"/>): facts only, so each check
/// is a pure function over them. When <see cref="ServerVersion"/> is older than <see cref="PostgresDatabase.SupportedMajorVersion"/>
/// nothing else was read, and the rest are their defaults.
/// </summary>
public sealed record PostgresStartupFacts
{
    public required Version ServerVersion { get; init; }
    public string TransactionIsolation { get; init; } = "";
    public string DefaultTransactionIsolation { get; init; } = "";
    public string SessionReplicationRole { get; init; } = "";

    /// <summary>Each trigger in the schema, "table.trigger", with its pg_trigger.tgenabled.</summary>
    public IReadOnlyDictionary<string, string> Triggers { get; init; } = new Dictionary<string, string>();

    /// <summary>idle_session_timeout; zero when off.</summary>
    public TimeSpan IdleSessionTimeout { get; init; }

    public IReadOnlyList<TrackVersion> Tracks { get; init; } = [];

    /// <summary>The schema the server's statements use (current_schema()), or null when its search_path names none that exists.</summary>
    public string? Schema { get; init; }

    /// <summary>
    /// Whether the login (session_user) is a superuser or a member of one: it could SET ROLE to it, whatever role the session runs as
    /// now (<c>-c role=…</c> or ALTER ROLE … SET role changes current_user only). A superuser counts itself.
    /// </summary>
    public bool Superuser { get; init; }

    /// <summary>
    /// What in <see cref="Schema"/> the login can act as the owner of (it owns it, or is a member of its owner, so it can SET ROLE to it),
    /// each as "table name", "function name" or "schema name": the schema itself, its tables, its functions, and every function the
    /// schema's triggers call wherever it lives. An owner can drop or replace a guard (DROP FUNCTION … CASCADE drops its trigger).
    /// </summary>
    public IReadOnlyList<string> Owned { get; init; } = [];

    /// <summary>
    /// Whether the login, or a role it is a member of, may SET session_replication_role or ALTER SYSTEM it (GRANT SET ON PARAMETER, since
    /// PostgreSQL 15): replica turns every guard trigger off for that session. True when it cannot be read.
    /// </summary>
    public bool CanSetReplicationRole { get; init; }

    /// <summary>Whether the login, or a role it is a member of, can CREATE in <see cref="Schema"/>. True when there is no schema to ask about.</summary>
    public bool CreateOnSchema { get; init; }

    /// <summary>
    /// The built-in roles that reach past the schema's privileges (<see cref="PostgresRoleChecks.BuiltInRoles"/>: a server program, the
    /// server's files, every table, other sessions) that the login is a member of.
    /// </summary>
    public IReadOnlyList<string> BuiltInRoles { get; init; } = [];

    /// <summary>
    /// Each table privilege the login holds, through any role it is a member of or PUBLIC, beyond reading and writing rows, as
    /// "PRIVILEGE on table": TRUNCATE, REFERENCES, TRIGGER or MAINTAIN on any table, and INSERT, UPDATE or DELETE on a version table.
    /// </summary>
    public IReadOnlyList<string> TablePrivileges { get; init; } = [];

    /// <summary>
    /// Each SECURITY DEFINER function, in any schema but pg_catalog and information_schema, that the login can run (through any role it is
    /// a member of or PUBLIC), whoever owns it, as "schema.name(arguments)": it does with its owner's privileges
    /// whatever its body says, calls included. A superuser's extension function is left out (<see cref="PostgresRoleChecks"/>).
    /// </summary>
    public IReadOnlyList<string> DefinerFunctions { get; init; } = [];

    /// <summary>
    /// Each superuser-owned SECURITY DEFINER function, an extension's or not, in any schema, granted EXECUTE to the login or to a role it is
    /// a member of other than through PUBLIC alone, as "schema.name(arguments)": it runs as a superuser
    /// (<see cref="PostgresRoleChecks"/>).
    /// </summary>
    public IReadOnlyList<string> GrantedSuperuserFunctions { get; init; } = [];

    /// <summary>The role's facts as <see cref="PostgresRoleChecks"/> read them: the same reading the owner's migration checks its app role by.</summary>
    public PostgresRoleFacts Role => new(Superuser, Owned, CreateOnSchema, CanSetReplicationRole, BuiltInRoles, TablePrivileges, DefinerFunctions,
        GrantedSuperuserFunctions);

    /// <summary>These facts with <paramref name="role"/>'s.</summary>
    internal PostgresStartupFacts WithRole(PostgresRoleFacts role) => this with
    {
        Superuser = role.Superuser, Owned = role.Owned, CreateOnSchema = role.CreateOnSchema, CanSetReplicationRole = role.CanSetReplicationRole,
        BuiltInRoles = role.BuiltInRoles, TablePrivileges = role.TablePrivileges, DefinerFunctions = role.DefinerFunctions,
        GrantedSuperuserFunctions = role.GrantedSuperuserFunctions,
    };

    /// <summary>In how many of the pooler probe's rounds the lock its first statement took was seen held by its own session.</summary>
    public int PoolerProbeHeld { get; init; }

    /// <summary>
    /// Whether the pooler probe's second connection, opened while the first was still held, ran on the same server session as the first
    /// (the same pg_backend_pid): two connections sharing one session, as an idle pooler in transaction mode hands out.
    /// </summary>
    public bool PoolerSameSession { get; init; }

    /// <summary>
    /// Whether the pooler probe's second connection saw the lock the first took as held by its own session, or the first could not take
    /// it at all (failing closed): a lock one connection holds would be another's.
    /// </summary>
    public bool PoolerLockShared { get; init; }

    /// <summary>
    /// Whether the server refused the pooler probe's second connection as one too many (53300, max_connections or the role's CONNECTION
    /// LIMIT): the probe, and the server's own work, need two connections at once.
    /// </summary>
    public bool PoolerSecondConnectionRefused { get; init; }

    /// <summary>The connection string's Host (a list is comma-separated), for the TLS check.</summary>
    public string Host { get; init; } = "";

    internal SslMode SslMode { get; init; } = SslMode.Prefer;

    /// <summary>Whether the connection the checks ran on is encrypted (pg_stat_ssl).</summary>
    public bool Encrypted { get; init; }

    /// <summary>How far the database's clock is ahead of the server's (<see cref="PostgresDatabase.ClockSkewAsync"/>).</summary>
    public TimeSpan ClockSkew { get; init; }

    public int MaxConnections { get; init; }

    /// <summary>superuser_reserved_connections plus reserved_connections: the slots a role that is no superuser cannot use.</summary>
    public int ReservedConnections { get; init; }

    public int MaxPoolSize { get; init; }

    /// <summary>Whether this process capped its own pool below PACKMOAT_DB's Maximum Pool Size (<see cref="PostgresConnectionString.PoolCapped"/>).</summary>
    public bool PoolCapped { get; init; }
}

/// <summary>
/// The checks a server runs on a PostgreSQL database before it serves: each a pure function over
/// <see cref="PostgresStartupFacts"/>, and one gathering of the facts (<see cref="GatherAsync"/>). The server never migrates on
/// PostgreSQL, so a schema that is not current is refused too. No message ever holds the connection string or the password: they name a
/// setting, a table, a trigger or a number, and the database only as <see cref="PostgresConnectionString.Describe"/> gives it.
/// <para>The server runs them at start (PackmoatApp.Build), once the app is built and before the schema port, the master-key
/// check or anything served; a refusal stops the start.</para>
/// <para>Unstable: may change in any release before 1.0.</para>
/// </summary>
public static class PostgresStartupChecks
{
    /// <summary>
    /// Every trigger the core's schema pins (<see cref="PostgresCoreMigrations"/>): SQLite's 11 rules as row triggers, and the three
    /// TRUNCATE guards, as "table.trigger". Each must be there and fire: tgenabled 'O' (as normal) or 'A' (always). 'D' is disabled, and
    /// 'R' fires only in replica mode.
    /// </summary>
    public static readonly IReadOnlyList<string> PinnedTriggers =
    [
        "admin_actions.admin_actions_no_delete",
        "admin_actions.admin_actions_no_truncate",
        "admin_actions.admin_actions_no_update",
        "audit_events.audit_no_delete",
        "audit_events.audit_no_truncate",
        "audit_events.audit_no_update",
        "audit_events.audit_purged_tenant",
        "decision_records.decision_records_purged_tenant",
        "tenant_purges.tenant_purges_no_delete",
        "tenant_purges.tenant_purges_no_truncate",
        "tenant_purges.tenant_purges_no_update",
        "tenant_purges.tenant_purges_only_when_gone",
        "tenants.tenants_no_purged_id",
        "tenants.tenants_no_rename_to_purged_id",
    ];

    /// <summary>The pooler probe's lock key: "pmpp" in ASCII, with the session's own pid as the second key, so no two sessions contend.</summary>
    internal const int PoolerProbeKey = 0x706D7070;

    /// <summary>How many times the pooler probe takes its lock and looks for it.</summary>
    public const int PoolerProbeRounds = 3;

    /// <summary>The most the database's clock may differ from the server's before the start warns.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromSeconds(2);

    /// <summary>The most the database's clock may differ from the server's before the start is refused (<see cref="ClockSkew"/>).</summary>
    public static readonly TimeSpan RefusedClockSkew = TimeSpan.FromSeconds(30);

    // ------------------------------------------------------------------------------------------------------------- the checks

    /// <summary>Every finding for <paramref name="facts"/>. An old server is the only one: the rest need PostgreSQL 17 to be read.</summary>
    public static IReadOnlyList<PostgresFinding> Evaluate(PostgresStartupFacts facts, PostgresStartupOptions options)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(options);
        if (ServerVersion(facts.ServerVersion) is { } old) return [old];
        var found = new List<PostgresFinding>();
        void Add(PostgresFinding? f) { if (f is not null) found.Add(f); }
        Add(ReadCommitted(facts.TransactionIsolation, facts.DefaultTransactionIsolation));
        Add(ReplicationRole(facts.SessionReplicationRole));
        Add(SchemaExists(facts.Schema));
        Add(Triggers(facts.Triggers));
        Add(IdleSessionTimeout(facts.IdleSessionTimeout, options.ShortestIdleSessionTimeout));
        Add(SchemaCurrent(facts.Tracks));
        found.AddRange(RoleFindings(facts.Role, options.SingleRole));
        Add(Pooler(facts.PoolerProbeHeld, facts.PoolerSameSession, facts.PoolerLockShared));
        Add(SecondConnection(facts.PoolerSecondConnectionRefused));
        Add(Tls(facts.Host, facts.SslMode, facts.Encrypted));
        Add(ClockSkew(facts.ClockSkew));
        Add(PoolTooSmall(facts.MaxPoolSize));
        // The pool-size warning is about the server's pool: a process that capped its own (the command line's) skips it. The refusal of a
        // pool under 2 stays, since a cap never lowers a pool that small.
        if (!facts.PoolCapped) Add(PoolSize(facts.MaxPoolSize, facts.MaxConnections, facts.ReservedConnections));
        return found;
    }

    /// <summary>A server older than <see cref="PostgresDatabase.SupportedMajorVersion"/> is refused.</summary>
    public static PostgresFinding? ServerVersion(Version server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Major >= PostgresDatabase.SupportedMajorVersion ? null
            : PostgresFinding.Refuse($"The PostgreSQL server is version {server}; Packmoat needs PostgreSQL {PostgresDatabase.SupportedMajorVersion} or newer.");
    }

    /// <summary>Either isolation setting other than read committed is refused: the stores' locking is written for read committed.</summary>
    public static PostgresFinding? ReadCommitted(string transactionIsolation, string defaultTransactionIsolation)
    {
        var wrong = new List<string>();
        if (transactionIsolation != "read committed") wrong.Add($"transaction_isolation is {transactionIsolation}");
        if (defaultTransactionIsolation != "read committed") wrong.Add($"default_transaction_isolation is {defaultTransactionIsolation}");
        return wrong.Count == 0 ? null : PostgresFinding.Refuse(
            $"{string.Join(", and ", wrong)}. Packmoat runs only at read committed: its locking is written for it. Set it back for the " +
            "server's role and database (ALTER ROLE or ALTER DATABASE … RESET), or remove it from PACKMOAT_DB's Options.");
    }

    /// <summary>session_replication_role other than origin is refused: replica turns the schema's triggers off.</summary>
    public static PostgresFinding? ReplicationRole(string sessionReplicationRole) =>
        sessionReplicationRole == "origin" ? null : PostgresFinding.Refuse(
            $"session_replication_role is {sessionReplicationRole}, not origin: the schema's triggers, which keep the audit and purge " +
            "records, would not fire. Set it back to origin for the server's role and database.");

    /// <summary>A search_path that names no schema that exists is refused.</summary>
    public static PostgresFinding? SchemaExists(string? schema) =>
        schema is not null ? null : PostgresFinding.Refuse(
            "The server's search_path names no schema that exists, so it would find none of Packmoat's tables. Set search_path for the server's role.");

    /// <summary>
    /// Each of <see cref="PinnedTriggers"/> must be in <paramref name="triggers"/> and fire ('O' or 'A'). One finding names every one
    /// missing and every one that does not fire, with its state.
    /// </summary>
    public static PostgresFinding? Triggers(IReadOnlyDictionary<string, string> triggers)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        var missing = PinnedTriggers.Where(t => !triggers.ContainsKey(t)).ToList();
        var off = PinnedTriggers.Where(t => triggers.TryGetValue(t, out var e) && !Fires(e)).Select(t => $"{t} ({State(triggers[t])})").ToList();
        if (missing.Count == 0 && off.Count == 0) return null;
        var parts = new List<string>();
        if (missing.Count > 0) parts.Add($"missing: {string.Join(", ", missing)}");
        if (off.Count > 0) parts.Add($"not firing: {string.Join(", ", off)}");
        return PostgresFinding.Refuse(
            $"The schema's triggers, which keep the audit and purge records, are not all in place ({string.Join("; ", parts)}). " +
            "Enable them (ALTER TABLE … ENABLE TRIGGER), or run packmoat-admin db migrate on a new database.");
    }

    /// <summary>tgenabled 'O' (as normal) and 'A' (always) fire; 'D' (disabled) and 'R' (replica only) do not.</summary>
    internal static bool Fires(string tgenabled) => tgenabled is "O" or "A";

    private static string State(string tgenabled) => tgenabled switch
    {
        "D" => "disabled",
        "R" => "replica only",
        _ => "state " + tgenabled,
    };

    /// <summary>
    /// idle_session_timeout set (not 0) but shorter than <paramref name="shortest"/> is refused: a session lock's idle connection would
    /// be ended, and the lock let go, without its holder knowing.
    /// </summary>
    public static PostgresFinding? IdleSessionTimeout(TimeSpan timeout, TimeSpan shortest) =>
        timeout <= TimeSpan.Zero || timeout >= shortest ? null : PostgresFinding.Refuse(
            $"idle_session_timeout is {Seconds(timeout)}: shorter than {Seconds(shortest)}, it can end the idle session that holds a tenant's " +
            "session lock, and let the lock go unnoticed. Set it to 0 (off) or at least that, for the server's role and database.");

    /// <summary>
    /// Every track must be current (<see cref="TrackVersion.IsCurrent"/>): on PostgreSQL the server never migrates, so the owner runs
    /// packmoat-admin db migrate first.
    /// </summary>
    public static PostgresFinding? SchemaCurrent(IReadOnlyList<TrackVersion> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var behind = tracks.Where(t => !t.IsCurrent).ToList();
        if (behind.Count == 0) return null;
        var lines = behind.Select(t => t.Newest switch
        {
            null => $"{t.Track}: not migrated, this build needs version {t.Latest}",
            { } n when n > t.Latest => $"{t.Track}: at version {n}, newer than this build's {t.Latest}",
            { } n => $"{t.Track}: at version {n} with {t.Recorded} of {t.Latest} recorded, this build needs {t.Latest}",
        });
        return PostgresFinding.Refuse($"The database's schema is not this build's ({string.Join("; ", lines)}). On PostgreSQL the server " +
            "never migrates: run packmoat-admin db migrate as the owner role first, or run a build as new as the database.");
    }

    /// <summary>
    /// The server's login must not be (or be able to become) a superuser, own (or be able to act as the owner of) the schema, a table or
    /// a function in it or one its triggers call, or be able to create in it: an owner can turn off, drop or replace the triggers that
    /// keep the audit and purge records. With <paramref name="singleRole"/> (PACKMOAT_DB_SINGLE_ROLE=on) owning and creating are
    /// warnings; a superuser is refused whatever it says.
    /// </summary>
    public static IReadOnlyList<PostgresFinding> Role(bool superuser, IReadOnlyList<string> owned, bool createOnSchema, bool singleRole)
    {
        ArgumentNullException.ThrowIfNull(owned);
        var found = new List<PostgresFinding>();
        if (superuser)
            found.Add(PostgresFinding.Refuse("PACKMOAT_DB signs in as a superuser (or a member of one), which can turn off the triggers that keep the audit and purge " +
                $"records. Sign in as an app role that is no superuser (refused whatever {PostgresStartupOptions.SingleRoleVariable} says)."));
        var relaxed = new List<string>();
        if (owned.Count > 0) relaxed.Add($"owns {Objects(owned)}");
        if (createOnSchema) relaxed.Add("can CREATE in the schema");
        if (relaxed.Count > 0)
        {
            var what = $"The server's role {string.Join(", and ", relaxed)}: an owner can turn off the triggers that keep the audit and purge records.";
            found.Add(singleRole
                ? PostgresFinding.Warn($"{what} Allowed by {PostgresStartupOptions.SingleRoleVariable}=on.")
                : PostgresFinding.Refuse($"{what} Run the migrations as the owner role and the server as an app role that owns nothing " +
                    $"(packmoat-admin db migrate --grant-to), or set {PostgresStartupOptions.SingleRoleVariable}=on to accept one role."));
        }
        else if (singleRole && !superuser)
            found.Add(PostgresFinding.Warn($"{PostgresStartupOptions.SingleRoleVariable}=on, but the server's role owns nothing in the schema and cannot create " +
                "in it: it is not needed. Turn it off."));
        return found;
    }

    /// <summary>
    /// Every finding about a role: the start's for its login, and the owner's migration's for its app role after it grants
    /// (with <paramref name="singleRole"/> false), so both refuse the same things. <see cref="Role"/>, then
    /// <see cref="ReplicationRolePrivilege"/>, <see cref="BuiltInRoleMembership"/>, <see cref="TablePrivileges"/>, <see cref="DefinerFunctions"/>
    /// and <see cref="SuperuserFunctionGrants"/>.
    /// </summary>
    public static IReadOnlyList<PostgresFinding> RoleFindings(PostgresRoleFacts role, bool singleRole)
    {
        ArgumentNullException.ThrowIfNull(role);
        var found = new List<PostgresFinding>(Role(role.Superuser, role.Owned, role.CreateOnSchema, singleRole));
        if (ReplicationRolePrivilege(role.CanSetReplicationRole) is { } srr) found.Add(srr);
        if (BuiltInRoleMembership(role.BuiltInRoles) is { } builtIn) found.Add(builtIn);
        if (TablePrivileges(role.TablePrivileges, singleRole) is { } privileges) found.Add(privileges);
        // A superuser's function granted to the role is SuperuserFunctionGrants' alone (refused whatever single-role says), not named twice.
        var definerOnly = role.DefinerFunctions.Except(role.GrantedSuperuserFunctions, StringComparer.Ordinal).ToList();
        if (DefinerFunctions(definerOnly, singleRole) is { } definers) found.Add(definers);
        if (SuperuserFunctionGrants(role.GrantedSuperuserFunctions) is { } granted) found.Add(granted);
        return found;
    }

    /// <summary>
    /// A login granted EXECUTE on a superuser-owned SECURITY DEFINER function, an extension's or not and in any schema, directly or through
    /// a role it is a member of, and not only through PUBLIC, is refused whatever PACKMOAT_DB_SINGLE_ROLE says: the function runs as a superuser, and the exception <see cref="DefinerFunctions"/> makes for an extension's holds only for what
    /// everyone may run. For example, a provider's extension function that sets session_replication_role, once granted to the app role, would let it turn
    /// off the triggers that keep the audit and purge records.
    /// </summary>
    public static PostgresFinding? SuperuserFunctionGrants(IReadOnlyList<string> functions)
    {
        ArgumentNullException.ThrowIfNull(functions);
        if (functions.Count == 0) return null;
        var one = functions.Count == 1;
        return PostgresFinding.Refuse(
            $"The server's role is granted {(functions.Count <= 3 ? string.Join(", ", functions) : $"{functions.Count} functions ({string.Join(", ", functions.Take(3))}, …)")}: " +
            $"a superuser's SECURITY DEFINER function{(one ? "" : "s")}, granted to the role or a role it is a member of, not only to PUBLIC. " +
            $"{(one ? "It runs" : "Each runs")} as a superuser, and can turn off the triggers that keep the audit and purge records. " +
            $"Revoke EXECUTE on {(one ? "it" : "each")} from the server's role and its roles (refused whatever {PostgresStartupOptions.SingleRoleVariable} says).");
    }

    /// <summary>
    /// A login that can run any SECURITY DEFINER function, in any schema, through any role it is a member of or PUBLIC (EXECUTE is PUBLIC's
    /// by default), is refused, whoever owns it: the function runs as its owner, and through what it calls, as
    /// whatever owner granted those calls to it, so the login can do whatever that chain does (turn off the triggers that keep the audit
    /// and purge records, delete a kept row), however little the login itself may. A superuser-owned extension's own function is left out
    /// (<see cref="PostgresRoleChecks"/>) while the login reaches it through PUBLIC alone; granted to it otherwise, it is
    /// <see cref="SuperuserFunctionGrants"/>'s. With <paramref name="singleRole"/> a warning, as the login is the owner then.
    /// </summary>
    public static PostgresFinding? DefinerFunctions(IReadOnlyList<string> functions, bool singleRole)
    {
        ArgumentNullException.ThrowIfNull(functions);
        if (functions.Count == 0) return null;
        var one = functions.Count == 1;
        var what = $"The server's role can run {(functions.Count <= 3 ? string.Join(", ", functions) : $"{functions.Count} functions ({string.Join(", ", functions.Take(3))}, …)")}: " +
            $"SECURITY DEFINER function{(one ? "" : "s")} (run through PUBLIC, a role, or directly), so {(one ? "it runs" : "each runs")} as {(one ? "its" : "their")} owner, " +
            "and through what it calls as another owner, and can turn off the triggers that keep the audit and purge records.";
        return singleRole
            ? PostgresFinding.Warn($"{what} Allowed by {PostgresStartupOptions.SingleRoleVariable}=on.")
            : PostgresFinding.Refuse($"{what} Revoke EXECUTE on {(one ? "it" : "each")} from PUBLIC and the server's role, or drop {(one ? "it" : "them")}.");
    }

    /// <summary>
    /// A login that is a member of a built-in role that reaches past the schema's privileges (<see cref="PostgresRoleChecks.BuiltInRoles"/>)
    /// is refused, whatever PACKMOAT_DB_SINGLE_ROLE says: pg_execute_server_program and pg_write_server_files run a
    /// program or write a file as the database server, so can change anything as its owner (turn a guard trigger off); pg_write_all_data
    /// writes every table (the version tables too); pg_read_server_files reads the server's files; pg_signal_backend ends other sessions.
    /// </summary>
    public static PostgresFinding? BuiltInRoleMembership(IReadOnlyList<string> builtInRoles)
    {
        ArgumentNullException.ThrowIfNull(builtInRoles);
        return builtInRoles.Count == 0 ? null : PostgresFinding.Refuse(
            $"The server's role is a member of {(builtInRoles.Count == 1 ? "a built-in role that reaches" : "built-in roles that reach")} past the schema's privileges: {string.Join("; ", builtInRoles.Select(BuiltInRoleReach))}. " +
            $"Revoke {(builtInRoles.Count == 1 ? "it" : "them")} (refused whatever {PostgresStartupOptions.SingleRoleVariable} says).");
    }

    /// <summary>A built-in role and what it lets its members do (pg_signal_backend's own words, not a server program's).</summary>
    private static string BuiltInRoleReach(string role) => role switch
    {
        "pg_execute_server_program" => $"{role} (runs programs as the database server, so can change the database as its owner and {TurnOff})",
        "pg_write_server_files" => $"{role} (writes files as the database server, so can change the database as its owner and {TurnOff})",
        "pg_read_server_files" => $"{role} (reads the database server's files)",
        "pg_write_all_data" => $"{role} (writes every table, the schema's version tables included)",
        "pg_signal_backend" => $"{role} (can cancel or end other sessions, among them one that holds a tenant's session lock)",
        _ => role,
    };

    private const string TurnOff = "turn off the triggers that keep the audit and purge records";

    /// <summary>
    /// A login that holds TRUNCATE, REFERENCES, TRIGGER or MAINTAIN on a table, or INSERT, UPDATE or DELETE on a version table, through any
    /// role it is a member of or PUBLIC: TRUNCATE skips the row triggers; only the owner records a version. Refused;
    /// with <paramref name="singleRole"/> a warning, as an owner holds them all.
    /// </summary>
    public static PostgresFinding? TablePrivileges(IReadOnlyList<string> privileges, bool singleRole)
    {
        ArgumentNullException.ThrowIfNull(privileges);
        if (privileges.Count == 0) return null;
        var what = $"The server's role holds {(privileges.Count <= 3 ? string.Join(", ", privileges) : $"{privileges.Count} privileges ({string.Join(", ", privileges.Take(3))}, …)")} " +
            "(directly, through a role, or through PUBLIC): more than reading and writing rows.";
        return singleRole
            ? PostgresFinding.Warn($"{what} Allowed by {PostgresStartupOptions.SingleRoleVariable}=on.")
            : PostgresFinding.Refuse($"{what} Revoke them; packmoat-admin db migrate --grant-to grants an app role only SELECT, INSERT, UPDATE and DELETE.");
    }

    /// <summary>
    /// A login that may set session_replication_role (directly or through a role it is a member of) is refused, whatever
    /// PACKMOAT_DB_SINGLE_ROLE says: SET session_replication_role = replica turns off every trigger that keeps the audit and purge
    /// records, for that session, with no other trace.
    /// </summary>
    public static PostgresFinding? ReplicationRolePrivilege(bool canSet) =>
        !canSet ? null : PostgresFinding.Refuse(
            "The server's login, or a role it is a member of, may set session_replication_role (GRANT SET or ALTER SYSTEM ON PARAMETER), " +
            "which turns off the triggers that keep the audit and purge records. Revoke it " +
            $"(refused whatever {PostgresStartupOptions.SingleRoleVariable} says).");

    private static string Objects(IReadOnlyList<string> owned) =>
        owned.Count <= 3 ? string.Join(", ", owned) : $"{owned.Count} objects ({string.Join(", ", owned.Take(3))}, …)";

    /// <summary>
    /// The pooler probe's two signals. Every round must have seen its lock held by its own session: one that did not means the statements
    /// ran on different server sessions. And two connections held open at once must run on two server sessions, neither seeing the other's
    /// lock as its own: an idle pooler in transaction mode hands the same session to both, so every round passes there, but a lock
    /// one holds is the other's. Either is a pooler in transaction or statement mode, where a session lock does not hold.
    /// </summary>
    public static PostgresFinding? Pooler(int roundsHeld, bool sameSession, bool lockShared)
    {
        var found = new List<string>();
        if (roundsHeld < PoolerProbeRounds) found.Add($"a session lock did not stay with its session ({roundsHeld} of {PoolerProbeRounds} probes held)");
        if (sameSession) found.Add("two connections held open at once ran on the same server session");
        if (lockShared) found.Add("a second connection saw the first one's session lock as its own");
        if (found.Count == 0) return null;
        var seen = string.Join("; ", found);
        return PostgresFinding.Refuse(
            $"{char.ToUpperInvariant(seen[0])}{seen[1..]}: PACKMOAT_DB goes through a pooler in transaction or statement " +
            "mode, where one connection's session lock can pass to another. Connect directly, or through a pooler in session mode.");
    }

    /// <summary>
    /// The server refused the pooler probe's second connection as one too many (53300): the probe holds two connections
    /// at once, and so does the server's own work (a held lock keeps one while the work runs on another), so this is refused, saying so
    /// rather than failing with the server's own error.
    /// </summary>
    public static PostgresFinding? SecondConnection(bool refused) =>
        !refused ? null : PostgresFinding.Refuse(
            "The PostgreSQL server refused a second connection as too many (max_connections, or the role's CONNECTION LIMIT): the start's " +
            "pooler check holds two connections at once, and Packmoat needs at least two, since a held lock keeps one while the work runs " +
            "on another. Raise the role's CONNECTION LIMIT (to at least Maximum Pool Size plus one) or free connections on the server.");

    /// <summary>
    /// A host that is not this machine must be reached over TLS, and TLS must be required: SSL Mode Require, VerifyCA or VerifyFull, which
    /// Npgsql enforces on every connection the pool opens (Prefer and Allow can fall back to plain text, on any later connection too).
    /// The connection the checks ran on must also really be encrypted (pg_stat_ssl), a second check. GSSAPI encryption is not counted:
    /// pg_stat_ssl shows such a connection as unencrypted, so a server reached over GSS encryption alone is refused.
    /// </summary>
    internal static PostgresFinding? Tls(string host, SslMode mode, bool encrypted) =>
        IsLocal(host) || (mode is SslMode.Require or SslMode.VerifyCA or SslMode.VerifyFull && encrypted) ? null : PostgresFinding.Refuse(
            "The connection to the PostgreSQL server is not required to be encrypted, and the server is not on this machine. Set " +
            "SSL Mode=VerifyFull (or Require) in PACKMOAT_DB.");

    /// <summary>Whether every host in <paramref name="hosts"/> (a comma-separated list, each perhaps with a port) is this machine.</summary>
    internal static bool IsLocal(string hosts) => hosts.Split(',').All(h => IsLocalHost(h.Trim()));

    private static bool IsLocalHost(string host)
    {
        if (host.StartsWith('/') || host.StartsWith('@')) return true;   // a Unix socket's directory, or an abstract socket
        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']', StringComparison.Ordinal);
            host = close > 0 ? host[1..close] : host;
        }
        else if (host.Count(c => c == ':') == 1) host = host[..host.IndexOf(':', StringComparison.Ordinal)];
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
    }

    /// <summary>
    /// A clock skew over <see cref="MaxClockSkew"/> either way is a warning: retention cutoffs are on the server's clock, floors on the
    /// database's. Over <see cref="RefusedClockSkew"/> it is refused: on PostgreSQL more than one copy may run, and each copy judges the
    /// shared job leases, rate windows and form lifetimes by its own clock, so two copies each this far off the database's would disagree
    /// by up to twice as much. Always on PostgreSQL, with no setting: a clock this far off is a fault whether one copy runs or several.
    /// </summary>
    public static PostgresFinding? ClockSkew(TimeSpan skew)
    {
        if (skew.Duration() <= MaxClockSkew) return null;
        var off = $"The database's clock is {skew.Duration().TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s " +
            $"{(skew > TimeSpan.Zero ? "ahead of" : "behind")} this server's.";
        return skew.Duration() > RefusedClockSkew
            ? PostgresFinding.Refuse(off + $" Over {RefusedClockSkew.TotalSeconds:0} s, copies of the server would disagree on the shared job " +
                "leases, rate limits and form lifetimes, each judged by its own clock. Sync both clocks (NTP).")
            : PostgresFinding.Warn(off + " Retention runs on this server's clock and the schema's floors on " +
                "the database's, so a prune may be refused or rows kept longer. Sync both clocks (NTP).");
    }

    /// <summary>
    /// A Maximum Pool Size under 2 is refused: a session lock holds one connection while its holder works on others, so a pool of
    /// one waits on itself until the connection timeout.
    /// </summary>
    public static PostgresFinding? PoolTooSmall(int maxPoolSize) =>
        maxPoolSize >= 2 ? null : PostgresFinding.Refuse(
            $"PACKMOAT_DB's Maximum Pool Size is {maxPoolSize}: Packmoat needs at least 2, since a held lock keeps one connection while the work runs on another.");

    /// <summary>A Maximum Pool Size above what the server lets a role that is no superuser open is a warning.</summary>
    public static PostgresFinding? PoolSize(int maxPoolSize, int maxConnections, int reservedConnections)
    {
        var available = maxConnections - reservedConnections;
        return maxPoolSize <= available ? null : PostgresFinding.Warn(
            $"PACKMOAT_DB's Maximum Pool Size is {maxPoolSize}, but the server takes at most {available} connections from a role that is no " +
            $"superuser (max_connections {maxConnections}, less {reservedConnections} reserved), shared with every other client. Lower Maximum Pool Size or raise max_connections.");
    }

    private static string Seconds(TimeSpan t) => $"{t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s";

    // ------------------------------------------------------------------------------------------------------------- the facts

    /// <summary>
    /// Runs every check on <paramref name="db"/> and stops the start on any refusal (<see cref="PostgresStartupException"/>, listing each),
    /// after passing each warning to <paramref name="warn"/>. Reads only, but for the pooler probe's own lock, which it lets go.
    /// </summary>
    public static async Task RequireAsync(PostgresDatabase db, IReadOnlyList<MigrationTrack> modules, IClock clock, PostgresStartupOptions options,
        Action<string> warn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(warn);
        var findings = Evaluate(await GatherAsync(db, modules, clock, cancellationToken), options);
        foreach (var w in findings.Where(f => f.Kind == PostgresFindingKind.Warning)) warn(w.Message);
        var refused = findings.Where(f => f.Kind == PostgresFindingKind.Refusal).Select(f => "  - " + f.Message).ToList();
        if (refused.Count > 0) throw new PostgresStartupException(RefusalMessage(db.Describe(), refused, options.ForCommand));
    }

    /// <summary>
    /// The refusal's message: where the database is (<paramref name="describe"/>, never the password), then one line per refusal. A server
    /// refuses to start; a command (<paramref name="forCommand"/>) refuses to run.
    /// </summary>
    internal static string RefusalMessage(string describe, IReadOnlyList<string> refused, bool forCommand) => forCommand
        ? $"The PostgreSQL database at {describe} fails the checks the server makes before it starts. Refusing to run the command:\n{string.Join("\n", refused)}"
        : $"The PostgreSQL database at {describe} is not safe to serve from. Refusing to start:\n{string.Join("\n", refused)}";

    /// <summary>
    /// Reads every fact the checks need from <paramref name="db"/>, holding one pooled connection at a time (so a pool of one is judged, not
    /// left waiting on itself): the server's version, then (on 17 or newer) the settings and the role in one query, the schema's triggers,
    /// what the login can act as the owner of, and the pooler probe, on one connection, with the probe's second connection opened
    /// alongside it outside the pool; then the tracks' versions for the core and
    /// <paramref name="modules"/>, and the clock skew, each on its own. Anything that fails to read (a wrong password, a server not
    /// reached, a database that is not there) throws: the start fails closed.
    /// </summary>
    public static async Task<PostgresStartupFacts> GatherAsync(PostgresDatabase db, IReadOnlyList<MigrationTrack> modules, IClock clock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(clock);
        var cs = db.ConnectionString;
        PostgresStartupFacts facts;
        await using (var connection = await db.OpenAnyVersionAsync(cancellationToken))
        {
            var version = connection.PostgreSqlVersion;
            if (ServerVersion(version) is not null) return new PostgresStartupFacts { ServerVersion = version };
            facts = await OneConnectionFactsAsync(connection, version, cs, cancellationToken);
            // The pooler probe's second connection, opened while the first is held, outside the pool (so a pool of one is still
            // judged): through an idle pooler in transaction mode it gets the first's server session back. A server that refuses it as one
            // too many (53300) is a finding of its own, not an error.
            NpgsqlConnection? second;
            try
            {
                second = await db.OpenUnpooledAsync(cancellationToken);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.TooManyConnections)
            {
                second = null;
            }
            if (second is null) facts = facts with { PoolerSecondConnectionRefused = true };
            else
                await using (second)
                {
                    var (sameSession, lockShared) = await PoolerPairProbeAsync(connection, second, cancellationToken);
                    facts = facts with { PoolerSameSession = sameSession, PoolerLockShared = lockShared };
                }
        }
        return facts with
        {
            Tracks = await ((ISchemaMigrator)db).TrackVersionsAsync(modules, cancellationToken),
            ClockSkew = await db.ClockSkewAsync(clock, cancellationToken),
        };
    }

    /// <summary>The facts read on the one connection <see cref="GatherAsync"/> holds: everything but the tracks and the clock skew.</summary>
    private static async Task<PostgresStartupFacts> OneConnectionFactsAsync(NpgsqlConnection connection, Version version, PostgresConnectionString cs,
        CancellationToken cancellationToken)
    {
        PostgresStartupFacts facts;
        await using (var settings = new NpgsqlCommand(SettingsSql, connection))
        await using (var r = await settings.ExecuteReaderAsync(cancellationToken))
        {
            await r.ReadAsync(cancellationToken);
            facts = new PostgresStartupFacts
            {
                ServerVersion = version,
                TransactionIsolation = r.GetString(0),
                DefaultTransactionIsolation = r.GetString(1),
                SessionReplicationRole = r.GetString(2),
                IdleSessionTimeout = TimeSpan.FromMilliseconds(r.GetInt64(3)),
                MaxConnections = r.GetInt32(4),
                ReservedConnections = r.GetInt32(5),
                Schema = r.IsDBNull(6) ? null : r.GetString(6),
                Encrypted = r.GetBoolean(7),
                Host = cs.Host,
                SslMode = cs.SslMode,
                MaxPoolSize = cs.MaxPoolSize,
                PoolCapped = cs.PoolCapped,
            };
        }
        var triggers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, enabled) in await PairsAsync(connection, TriggersSql, cancellationToken)) triggers[name] = enabled;
        // The login's role facts, read exactly as the owner's migration reads the app role's after it grants.
        var role = await PostgresRoleChecks.ReadAsync(connection, null, role: null, cancellationToken);
        var held = await PoolerProbeAsync(connection, connection, cancellationToken);
        return facts.WithRole(role) with { Triggers = triggers, PoolerProbeHeld = held };
    }

    /// <summary>
    /// The settings, in one row. idle_session_timeout from pg_settings, in milliseconds. The role's facts are read apart
    /// (<see cref="PostgresRoleChecks"/>): the login's (session_user) and those of every role it is a member of, never current_user
    /// alone, which <c>-c role=…</c> changes.
    /// </summary>
    private const string SettingsSql = """
        SELECT pg_catalog.current_setting('transaction_isolation'),
               pg_catalog.current_setting('default_transaction_isolation'),
               pg_catalog.current_setting('session_replication_role'),
               (SELECT setting::bigint FROM pg_catalog.pg_settings WHERE name = 'idle_session_timeout'),
               pg_catalog.current_setting('max_connections')::int,
               pg_catalog.current_setting('superuser_reserved_connections')::int + pg_catalog.current_setting('reserved_connections')::int,
               pg_catalog.current_schema()::text,
               COALESCE((SELECT ssl FROM pg_catalog.pg_stat_ssl WHERE pid = pg_catalog.pg_backend_pid()), false)
        """;

    /// <summary>The schema's own triggers (not the ones PostgreSQL makes for constraints), "table.trigger" and tgenabled.</summary>
    private const string TriggersSql = """
        SELECT c.relname::text || '.' || t.tgname::text, t.tgenabled::text
        FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid
        WHERE c.relnamespace = (SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = pg_catalog.current_schema()) AND NOT t.tgisinternal
        """;

    private static async Task<List<(string, string)>> PairsAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var pairs = new List<(string, string)>();
        while (await r.ReadAsync(ct)) pairs.Add((r.GetString(0), r.GetString(1)));
        return pairs;
    }

    /// <summary>
    /// The pooler probe, <see cref="PoolerProbeRounds"/> times, each in separate statements outside any transaction: on
    /// <paramref name="take"/>, a session advisory lock keyed by the session's own pid; then on <paramref name="check"/>, whether pg_locks
    /// shows that lock held by the session the statement runs in; then the lock let go on <paramref name="take"/>. Through a pooler in
    /// transaction mode the statements can land on different sessions, and the lock is not seen. Returns the rounds that saw it.
    /// The server passes one connection as both; a test passes two to stand for such a pooler.
    /// </summary>
    internal static async Task<int> PoolerProbeAsync(NpgsqlConnection take, NpgsqlConnection check, CancellationToken ct)
    {
        var held = 0;
        for (var round = 0; round < PoolerProbeRounds; round++)
        {
            int pid;
            bool taken;
            await using (var lockCmd = new NpgsqlCommand(
                $"SELECT pg_catalog.pg_backend_pid(), pg_catalog.pg_try_advisory_lock({PoolerProbeKey}, pg_catalog.pg_backend_pid())", take))
            await using (var r = await lockCmd.ExecuteReaderAsync(ct))
            {
                await r.ReadAsync(ct);
                pid = r.GetInt32(0);
                taken = r.GetBoolean(1);
            }
            bool seen;
            await using (var look = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_locks WHERE locktype = 'advisory' AND classid::bigint = @key AND objid::bigint = @pid " +
                "AND objsubid = 2 AND granted AND pid = pg_catalog.pg_backend_pid())", check))
            {
                look.Parameters.AddWithValue("key", (long)PoolerProbeKey);
                look.Parameters.AddWithValue("pid", (long)pid);
                seen = (bool)(await look.ExecuteScalarAsync(ct))!;
            }
            if (taken)
                await using (var unlock = new NpgsqlCommand($"SELECT pg_catalog.pg_advisory_unlock({PoolerProbeKey}, @pid)", take))
                {
                    unlock.Parameters.AddWithValue("pid", pid);
                    await unlock.ExecuteScalarAsync(ct);
                }
            if (taken && seen) held++;
        }
        return held;
    }

    /// <summary>
    /// The pooler probe across two connections held open at once: on <paramref name="first"/>, a session advisory lock keyed by
    /// its session's pid; then on <paramref name="second"/>, its own session's pid and whether pg_locks shows that lock held by its own
    /// session; then the lock let go. Returns whether the two ran on the same server session (the same pid), and whether the second saw
    /// the first's lock as its own (or the first could not take it: failing closed). Two direct connections are two sessions and see
    /// neither. The server passes two physical connections; a test passes one as both to stand for a pooler that shares a session.
    /// </summary>
    internal static async Task<(bool SameSession, bool LockShared)> PoolerPairProbeAsync(NpgsqlConnection first, NpgsqlConnection second,
        CancellationToken ct)
    {
        int firstPid;
        bool taken;
        await using (var lockCmd = new NpgsqlCommand(
            $"SELECT pg_catalog.pg_backend_pid(), pg_catalog.pg_try_advisory_lock({PoolerProbeKey}, pg_catalog.pg_backend_pid())", first))
        await using (var r = await lockCmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            firstPid = r.GetInt32(0);
            taken = r.GetBoolean(1);
        }
        int secondPid;
        bool seen;
        await using (var look = new NpgsqlCommand(
            "SELECT pg_catalog.pg_backend_pid(), EXISTS (SELECT 1 FROM pg_catalog.pg_locks WHERE locktype = 'advisory' AND classid::bigint = @key " +
            "AND objid::bigint = @pid AND objsubid = 2 AND granted AND pid = pg_catalog.pg_backend_pid())", second))
        {
            look.Parameters.AddWithValue("key", (long)PoolerProbeKey);
            look.Parameters.AddWithValue("pid", (long)firstPid);
            await using var r = await look.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            secondPid = r.GetInt32(0);
            seen = r.GetBoolean(1);
        }
        if (taken)
            await using (var unlock = new NpgsqlCommand($"SELECT pg_catalog.pg_advisory_unlock({PoolerProbeKey}, @pid)", first))
            {
                unlock.Parameters.AddWithValue("pid", firstPid);
                await unlock.ExecuteScalarAsync(ct);
            }
        return PairSignals(firstPid, secondPid, taken, seen);
    }

    /// <summary>
    /// The two-connection probe's reading (<see cref="PoolerPairProbeAsync"/>): the same session when the pids are equal; the lock shared
    /// when the second saw it as its own, or when the first could not take it at all, failing closed: a lock the first
    /// session cannot take is one some session sharing it already holds.
    /// </summary>
    internal static (bool SameSession, bool LockShared) PairSignals(int firstPid, int secondPid, bool taken, bool seen) =>
        (firstPid == secondPid, !taken || seen);
}
