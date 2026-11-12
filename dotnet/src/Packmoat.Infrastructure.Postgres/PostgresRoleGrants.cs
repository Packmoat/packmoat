using Npgsql;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// The app role's grants. The owner role migrates and owns the schema; the server and the
/// day-to-day command line sign in as an app role that can read and write the rows and nothing more. An owner can disable the triggers
/// that enforce the retention floors and the purge guards, so the app role must never be one, nor able to become one.
/// <para>What the app role gets, on the migration's schema, re-applied on every migration (so a table a later migration adds gets it too):</para>
/// <list type="bullet">
/// <item>USAGE on the schema;</item>
/// <item>SELECT, INSERT, UPDATE and DELETE on every table, but the migration version tables (<c>schema_migrations</c> and
/// <c>*_schema_migrations</c>), which get SELECT only: the readiness check and the start's checks read them, and only the owner records a
/// version;</item>
/// <item>USAGE on every sequence (the identity columns' own), for its inserts.</item>
/// </list>
/// Each table and sequence first has ALL taken back from the app role, so a grant made by hand (TRUNCATE, say) does not outlive the next
/// migration. CREATE on the schema is taken back from PUBLIC (PostgreSQL 15 and newer no longer give it, but a database made from an older
/// template, or upgraded in place, still can). No TRUNCATE, no REFERENCES, no TRIGGER, no MAINTAIN, no DDL, no ownership, no CREATE, no
/// membership and no parameter privilege is ever granted.
/// <para>Then the app role is checked exactly as the start checks a login: the same reading
/// (<see cref="PostgresRoleChecks"/>: the role and every role it is a MEMBER of, PUBLIC included) and the same verdicts
/// (<see cref="PostgresStartupChecks.RoleFindings"/>): no superuser, no ownership of the schema, a table, a sequence, a function or a
/// trigger's function, no CREATE on the schema, no SET or ALTER SYSTEM on session_replication_role, no membership of a built-in role that
/// reaches past the schema (a server program or file, every table, other sessions), no TRUNCATE, REFERENCES, TRIGGER or MAINTAIN on any
/// table, and no INSERT, UPDATE or DELETE on a version table. Any refusal refuses the whole migration: nothing is written.</para>
/// </summary>
public static class PostgresRoleGrants
{
    /// <summary>The privileges the app role gets on every table but a version table.</summary>
    public const string TablePrivileges = "SELECT, INSERT, UPDATE, DELETE";

    /// <summary>The privilege the app role gets on a version table.</summary>
    public const string VersionTablePrivileges = "SELECT";

    /// <summary>The privilege the app role gets on every sequence.</summary>
    public const string SequencePrivileges = "USAGE";

    /// <summary>
    /// Grants <paramref name="appRole"/> its privileges on <paramref name="schema"/>'s tables and sequences, in <paramref name="tx"/>, then
    /// checks it (see the class remarks). Throws <see cref="PostgresRoleException"/> naming each problem; the caller's transaction then
    /// rolls back with everything in it.
    /// </summary>
    public static async Task<PostgresGrants> ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string schema, string appRole, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(appRole);
        await RequireGrantableAsync(connection, tx, appRole, cancellationToken);

        var s = Quote(schema);
        var role = Quote(appRole);
        await ExecuteAsync(connection, tx, $"REVOKE CREATE ON SCHEMA {s} FROM PUBLIC", cancellationToken);
        await ExecuteAsync(connection, tx, $"REVOKE ALL ON SCHEMA {s} FROM {role}", cancellationToken);
        await ExecuteAsync(connection, tx, $"GRANT USAGE ON SCHEMA {s} TO {role}", cancellationToken);
        int tables = 0, versionTables = 0, sequences = 0;
        foreach (var (name, kind) in await RelationsAsync(connection, tx, schema, cancellationToken))
        {
            var target = $"{s}.{Quote(name)}";
            if (kind == 'S')
            {
                await ExecuteAsync(connection, tx, $"REVOKE ALL ON SEQUENCE {target} FROM {role}", cancellationToken);
                await ExecuteAsync(connection, tx, $"GRANT {SequencePrivileges} ON SEQUENCE {target} TO {role}", cancellationToken);
                sequences++;
                continue;
            }
            var version = IsVersionTable(name);
            await ExecuteAsync(connection, tx, $"REVOKE ALL ON TABLE {target} FROM {role}", cancellationToken);
            await ExecuteAsync(connection, tx, $"GRANT {(version ? VersionTablePrivileges : TablePrivileges)} ON TABLE {target} TO {role}", cancellationToken);
            if (version) versionTables++;
            else tables++;
        }

        var problems = await ProblemsAsync(connection, tx, appRole, cancellationToken);
        if (problems.Count > 0)
            throw new PostgresRoleException($"The app role {appRole} would not be safe to serve as. Refusing to migrate; nothing was written:\n" +
                string.Join("\n", problems.Select(p => "  - " + p)));
        return new PostgresGrants(appRole, schema, tables, versionTables, sequences);
    }

    /// <summary>A migration version table: the core's (schema_migrations) or a module's (*_schema_migrations).</summary>
    internal static bool IsVersionTable(string table) =>
        table == CoreSchema.Track.Table || table.EndsWith("_schema_migrations", StringComparison.Ordinal);

    /// <summary>
    /// Refuses, before anything is granted, a role that is not there, or that is the role migrating (the login or the role it runs as):
    /// that role owns what it makes, so an app role equal to it would own the schema.
    /// </summary>
    private static async Task RequireGrantableAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string appRole, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = @role), @role = session_user::text OR @role = current_user::text", connection, tx);
        cmd.Parameters.AddWithValue("role", appRole);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        if (!r.GetBoolean(0))
            throw new PostgresRoleException($"There is no role named {appRole} to grant to. Create it first (CREATE ROLE … LOGIN, no SUPERUSER). Refusing to migrate; nothing was written.");
        if (r.GetBoolean(1))
            throw new PostgresRoleException($"{appRole} is the role running this migration, which owns what it makes. Grant to a separate app role. Refusing to migrate; nothing was written.");
    }

    /// <summary>The schema's tables (relkind r or p) and sequences (S), as (name, kind), in byte order.</summary>
    private static async Task<List<(string Name, char Kind)>> RelationsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string schema, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT c.relname::text, c.relkind FROM pg_catalog.pg_class c WHERE c.relnamespace = (SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = @schema) " +
            "AND c.relkind IN ('r', 'p', 'S') ORDER BY c.relname COLLATE \"C\"", connection, tx);
        cmd.Parameters.AddWithValue("schema", schema);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var relations = new List<(string, char)>();
        while (await r.ReadAsync(ct)) relations.Add((r.GetString(0), r.GetChar(1)));
        return relations;
    }

    /// <summary>
    /// What is wrong with <paramref name="appRole"/> on the session's current schema: the start's refusals for it
    /// (<see cref="PostgresStartupChecks.RoleFindings"/> over <see cref="PostgresRoleChecks"/>'s reading, with no single-role escape), as
    /// the start would word them. Empty: nothing.
    /// </summary>
    internal static async Task<List<string>> ProblemsAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, string appRole, CancellationToken ct) =>
        PostgresStartupChecks.RoleFindings(await PostgresRoleChecks.ReadAsync(connection, tx, appRole, ct), singleRole: false)
            .Where(f => f.Kind == PostgresFindingKind.Refusal).Select(f => f.Message).ToList();

    /// <summary>
    /// <c>db migrate --single-role</c> grants no app role, so it is refused when any role but a table's owner holds a privilege on one of
    /// <paramref name="schema"/>'s tables: that is a two-role install, whose app role would not get the tables this
    /// migration adds. Run with --grant-to that role instead.
    /// </summary>
    internal static async Task RequireSingleRoleAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string schema, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT g.name FROM (
              SELECT DISTINCT CASE WHEN e.grantee = 0 THEN 'PUBLIC' ELSE (SELECT rolname::text FROM pg_catalog.pg_roles WHERE oid = e.grantee) END AS name
              FROM pg_catalog.pg_class c, pg_catalog.aclexplode(c.relacl) e
              WHERE c.relnamespace = (SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = @schema) AND c.relkind IN ('r', 'p')
                AND e.grantee <> c.relowner) g
            ORDER BY g.name COLLATE "C"
            """, connection, tx);
        cmd.Parameters.AddWithValue("schema", schema);
        var grantees = new List<string>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) grantees.Add(r.GetString(0));
        if (grantees.Count > 0)
            throw new PostgresRoleException($"--single-role, but {string.Join(", ", grantees)} also hold{(grantees.Count == 1 ? "s" : "")} privileges on the schema's tables: " +
                "this install has an app role, which would not get the tables this migration adds. Run db migrate --grant-to <app role> instead. " +
                "Refusing to migrate; nothing was written.");
    }

    /// <summary><paramref name="name"/> as a quoted identifier: any name a role or table can have, with no way out of the quotes.</summary>
    internal static string Quote(string name) => $"\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// The grants one migration applied: to <paramref name="Role"/>, on <paramref name="Schema"/>, USAGE; on
/// <paramref name="Tables"/> tables <see cref="PostgresRoleGrants.TablePrivileges"/>; on <paramref name="VersionTables"/> version tables
/// SELECT; on <paramref name="Sequences"/> sequences USAGE.
/// </summary>
public sealed record PostgresGrants(string Role, string Schema, int Tables, int VersionTables, int Sequences);

/// <summary>The app role cannot be granted to safely. The migration it was part of wrote nothing.</summary>
public sealed class PostgresRoleException(string message) : Exception(message);
