using Npgsql;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// What a role could do to the schema beyond reading and writing its rows, read the same way for
/// both checks that ask: the start's (<see cref="PostgresStartupChecks"/>, for the login, session_user) and the owner's migration's
/// after it grants (<see cref="PostgresRoleGrants"/>, for the app role). One query, one reading, and the same verdicts
/// (<see cref="PostgresStartupChecks.RoleFindings"/>), so the server never starts as a role a migration would refuse to grant to, nor the
/// other way round.
/// <para>Every fact is the role's and every role it is a MEMBER of (it could SET ROLE to it, whether or not it inherits), PUBLIC included
/// wherever PostgreSQL counts it (has_*_privilege of the role itself). Each fails closed: what cannot be read counts as there.</para>
/// </summary>
internal static class PostgresRoleChecks
{
    /// <summary>
    /// The built-in roles that reach past the schema's own privileges: run a program or write a file as the server
    /// (and so change the database as its owner), write any table, or end another session. A role that is a member of any is refused,
    /// whatever PACKMOAT_DB_SINGLE_ROLE says.
    /// </summary>
    public static readonly string[] BuiltInRoles =
        ["pg_execute_server_program", "pg_read_server_files", "pg_signal_backend", "pg_write_all_data", "pg_write_server_files"];

    /// <summary>The table privileges never to be held on any table (PostgreSQL 17's list but the four the app role is granted).</summary>
    public static readonly string[] RefusedTablePrivileges = ["TRUNCATE", "REFERENCES", "TRIGGER", "MAINTAIN"];

    /// <summary>The privileges never to be held on a version table: only the owner records a version.</summary>
    public static readonly string[] RefusedVersionTablePrivileges = ["INSERT", "UPDATE", "DELETE"];

    /// <summary>
    /// The facts for <paramref name="role"/> (null: the login, session_user) on the session's current schema, on
    /// <paramref name="connection"/> (in <paramref name="tx"/>, if any). The owner's migration has set search_path to its schema, so
    /// current_schema() is the migration's schema there.
    /// </summary>
    public static async Task<PostgresRoleFacts> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, string? role, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(Sql, connection, tx);
        cmd.Parameters.Add(new NpgsqlParameter("role", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)role ?? DBNull.Value });
        cmd.Parameters.AddWithValue("builtin", BuiltInRoles);
        cmd.Parameters.AddWithValue("refused", RefusedTablePrivileges);
        cmd.Parameters.AddWithValue("version_refused", RefusedVersionTablePrivileges);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new PostgresRoleFacts(r.GetBoolean(0), r.GetFieldValue<string[]>(1), r.GetBoolean(2), r.GetBoolean(3),
            r.GetFieldValue<string[]>(4), r.GetFieldValue<string[]>(5), r.GetFieldValue<string[]>(6), r.GetFieldValue<string[]>(7));
    }

    /// <summary>
    /// One row: superuser, what it can act as the owner of ("schema s", "table t", "sequence q", "function f": the schema, its tables,
    /// sequences and functions, and every function its triggers call wherever it lives), CREATE on the schema, SET or ALTER SYSTEM on
    /// session_replication_role, the built-in roles above it is a member of, each refused privilege on a table as "PRIVILEGE on t"
    /// (a table it can act as the owner of is left out there: an owner holds them all, and its ownership is already the finding), and the
    /// SECURITY DEFINER functions it can run (<see cref="PostgresRoleFacts.DefinerFunctions"/>, as "schema.name(arguments)").
    /// <para>The SECURITY DEFINER functions, failing closed: EVERY one in any schema but pg_catalog and
    /// information_schema that the role can EXECUTE through any role it is a member of or PUBLIC (has_function_privilege, as for the
    /// tables), whoever owns it, and whether or not the role has USAGE on its schema (that can be granted at any time). Such a function
    /// runs as its owner, and its owner need not be the schema's: a plain role's definer function may call one the owner made and granted
    /// to that plain role alone, a chain no check of owners can follow. The one exception: an extension's own function owned by
    /// a superuser. Its body comes from the extension's script on the server (CREATE EXTENSION cannot take one from the session), so no
    /// role of the deployment wrote it, and a managed service's extensions put such functions in every database, where no operator can
    /// revoke them. One a role other than a superuser owns is never left out, member of an extension or not: ALTER EXTENSION … ADD
    /// FUNCTION would otherwise hide a role-made one.</para>
    /// <para>That exception holds only while the role reaches the function through PUBLIC alone:
    /// <see cref="PostgresRoleFacts.GrantedSuperuserFunctions"/> lists every superuser-owned SECURITY DEFINER function, an extension's or
    /// not, in ANY schema (pg_catalog too: a role that is no superuser can install a trusted extension there), whose ACL
    /// grants EXECUTE to the role or to any role it is a member of (directly, WITH GRANT OPTION, or through a group, inherited or not).
    /// The start and the migration refuse it whatever PACKMOAT_DB_SINGLE_ROLE says, as it runs as a superuser: a provider's extension function that sets
    /// session_replication_role, once granted to the app role, would let it turn the guard triggers off. A NULL proacl is the default, PUBLIC and the
    /// owner only, so aclexplode gives no row for it; nor does PUBLIC (grantee 0) ever match. A stock catalog function carries no
    /// grant to a deployment's roles, so none is listed. What the start cannot tell apart: an extension's function granted to PUBLIC
    /// again after a REVOKE looks like the default, so it is still left out (never grant one to PUBLIC).</para>
    /// </summary>
    private const string Sql = """
        WITH a AS (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = COALESCE(@role, session_user::text)),
             m AS (SELECT r.oid, r.rolname, r.rolsuper FROM pg_catalog.pg_roles r, a WHERE pg_catalog.pg_has_role(a.oid, r.oid, 'MEMBER')),
             s AS (SELECT oid, nspname, nspowner FROM pg_catalog.pg_namespace WHERE nspname = pg_catalog.current_schema())
        SELECT
          NOT EXISTS (SELECT 1 FROM a) OR EXISTS (SELECT 1 FROM m WHERE m.rolsuper),
          ARRAY(SELECT o.name FROM (
              SELECT 'schema ' || s.nspname::text AS name FROM s, a WHERE pg_catalog.pg_has_role(a.oid, s.nspowner, 'MEMBER')
              UNION
              SELECT CASE WHEN c.relkind = 'S' THEN 'sequence ' ELSE 'table ' END || c.relname::text
              FROM pg_catalog.pg_class c JOIN s ON c.relnamespace = s.oid, a
              WHERE c.relkind IN ('r', 'p', 'S', 'v', 'm', 'f') AND pg_catalog.pg_has_role(a.oid, c.relowner, 'MEMBER')
              UNION
              SELECT 'function ' || p.proname::text FROM pg_catalog.pg_proc p JOIN s ON p.pronamespace = s.oid, a
              WHERE pg_catalog.pg_has_role(a.oid, p.proowner, 'MEMBER')
              UNION
              SELECT 'function ' || p.proname::text
              FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid JOIN s ON c.relnamespace = s.oid
                JOIN pg_catalog.pg_proc p ON p.oid = t.tgfoid, a
              WHERE NOT t.tgisinternal AND pg_catalog.pg_has_role(a.oid, p.proowner, 'MEMBER')) o
            ORDER BY o.name COLLATE "C"),
          COALESCE((SELECT bool_or(pg_catalog.has_schema_privilege(m.oid, s.oid, 'CREATE')) FROM m, s), true),
          COALESCE((SELECT bool_or(pg_catalog.has_parameter_privilege(m.oid, 'session_replication_role', 'SET')
                                   OR pg_catalog.has_parameter_privilege(m.oid, 'session_replication_role', 'ALTER SYSTEM')) FROM m), true),
          ARRAY(SELECT m.rolname::text FROM m WHERE m.rolname = ANY(@builtin) ORDER BY m.rolname COLLATE "C"),
          ARRAY(SELECT x.p || ' on ' || c.relname::text
                FROM pg_catalog.pg_class c JOIN s ON c.relnamespace = s.oid, a,
                  pg_catalog.unnest(CASE WHEN c.relname = 'schema_migrations' OR c.relname LIKE '%\_schema\_migrations'
                                         THEN @refused::text[] || @version_refused::text[] ELSE @refused::text[] END) AS x(p)
                WHERE c.relkind IN ('r', 'p') AND NOT pg_catalog.pg_has_role(a.oid, c.relowner, 'MEMBER')
                  AND EXISTS (SELECT 1 FROM m WHERE pg_catalog.has_table_privilege(m.oid, c.oid, x.p))
                ORDER BY c.relname COLLATE "C", x.p COLLATE "C"),
          ARRAY(SELECT f.name FROM (
              SELECT n.nspname::text || '.' || p.proname::text || '(' || pg_catalog.pg_get_function_identity_arguments(p.oid) || ')' AS name
              FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace JOIN pg_catalog.pg_roles w ON w.oid = p.proowner
              WHERE p.prosecdef AND n.nspname NOT IN ('pg_catalog', 'information_schema')
                AND NOT (w.rolsuper AND EXISTS (SELECT 1 FROM pg_catalog.pg_depend d
                                                WHERE d.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass AND d.objid = p.oid AND d.deptype = 'e'))
                AND EXISTS (SELECT 1 FROM m WHERE pg_catalog.has_function_privilege(m.oid, p.oid, 'EXECUTE'))) f
            ORDER BY f.name COLLATE "C"),
          ARRAY(SELECT f.name FROM (
              SELECT n.nspname::text || '.' || p.proname::text || '(' || pg_catalog.pg_get_function_identity_arguments(p.oid) || ')' AS name
              FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace JOIN pg_catalog.pg_roles w ON w.oid = p.proowner
              WHERE p.prosecdef AND w.rolsuper
                AND EXISTS (SELECT 1 FROM pg_catalog.aclexplode(p.proacl) x JOIN m ON x.grantee = m.oid WHERE x.privilege_type = 'EXECUTE')) f
            ORDER BY f.name COLLATE "C")
        """;
}

/// <summary>
/// One role's facts (<see cref="PostgresRoleChecks"/>): whether it is (or could become) a superuser; what it can act as the owner of; CREATE
/// on the schema; whether it may set session_replication_role; the built-in roles beyond the schema it is a member of; every refused
/// table privilege it holds, as "PRIVILEGE on table"; every SECURITY DEFINER function it can run, in any schema, as
/// "schema.name(arguments)", but a superuser-owned extension's own; and every superuser-owned SECURITY DEFINER
/// function, in any schema, granted to it other than through PUBLIC alone, the same way.
/// </summary>
public sealed record PostgresRoleFacts(bool Superuser, IReadOnlyList<string> Owned, bool CreateOnSchema, bool CanSetReplicationRole,
    IReadOnlyList<string> BuiltInRoles, IReadOnlyList<string> TablePrivileges, IReadOnlyList<string> DefinerFunctions,
    IReadOnlyList<string> GrantedSuperuserFunctions);
