using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Packmoat.Infrastructure.Tests.Postgres;

/// <summary>
/// A database's core schema in one shape for both engines, read from each engine's own catalogue: SQLite's sqlite_master and
/// pragma_* tables, PostgreSQL's information_schema, pg_index and pg_constraint. One line per fact, so two schemas compare as two lists:
/// <list type="bullet">
/// <item>"table t";</item>
/// <item>"t#NN column type NULL|NOT NULL [pkN] [default=…] [identity …]", in column order, the type as PostgreSQL names it (SQLite's
/// mapped by <see cref="SqliteTypes"/>);</item>
/// <item>"t unique(a, b)": a UNIQUE constraint (not the primary key), sorted;</item>
/// <item>"t fk(a) -> u(b) on update … on delete …", sorted, and "t fks deferrable=N initially-deferred=M";</item>
/// <item>"t collations=[…]": every column collation other than byte order. SQLite: each COLLATE in the table's text but BINARY,
/// its default. PostgreSQL: the collation of every column that has one (text) and is not "C", so a text column left on the database's
/// default collation reads "default" and differs from SQLite;</item>
/// <item>"t checks=N": how many CHECK constraints (column and table), and "t check-literals=[…]": the string literals (exactly) and
/// numbers inside them, sorted;</item>
/// <item>"index name on t(…) [unique] [where …]": every index made by CREATE INDEX, expressions and predicates normalised;</item>
/// <item>"trigger name on t before|after|instead of event[ of a, b] row|statement[ disabled|replica|always]": every trigger, its
/// events joined with " or ". A trigger that does not fire (DISABLE TRIGGER) or fires only in another replication role reads so.</item>
/// </list>
/// The track tables (schema_migrations and *_schema_migrations) are left out: each engine makes its own.
/// </summary>
public static partial class SchemaShape
{
    /// <summary>
    /// Every column that holds a time: timestamptz on PostgreSQL, text ("O" format) on SQLite. Pinned: a time column added
    /// to the core is added here in the same pull request (<c>Every_column_named_like_a_time_is_on_the_pinned_lists</c> checks).
    /// </summary>
    public static readonly string[] TimeColumns =
    [
        "admin_actions.time", "audit_events.time", "auto_join_settings.changed_at", "cookie_keys.created",
        "decision_records.first_seen_at", "decision_records.last_seen_at", "decision_records.published_at",
        "feed_checks.checked_at", "feed_setup_scratch.expires_at", "form_nonces.expires_at", "job_leases.expires_at", "members.first_seen_at", "members.last_seen_at",
        "rate_counters.ends_at", "rate_counters.started_at",
        "shared_app_credentials.updated_at", "shared_app_verifications.verified_at", "sign_in_directories.bound_at",
        "tenant_purges.purged_at", "tenants.created_at",
        "tokens.created_at", "tokens.expires_at", "tokens.last_used_at", "tokens.revoked_at",
        "upstream_identities.updated_at",
        "waivers.created_at", "waivers.expires_at", "waivers.revoked_at",
    ];

    /// <summary>
    /// The triggers only PostgreSQL has, as their shape lines: TRUNCATE skips row triggers there, so each append-only table
    /// refuses it with a statement trigger. SQLite has no TRUNCATE. PostgresTriggerGuardTests has a test for each.
    /// </summary>
    public static readonly string[] PostgresOnlyTriggers =
    [
        "trigger admin_actions_no_truncate on admin_actions before truncate statement",
        "trigger audit_no_truncate on audit_events before truncate statement",
        "trigger tenant_purges_no_truncate on tenant_purges before truncate statement",
    ];

    /// <summary>A trigger's name, from its shape line.</summary>
    public static string TriggerName(string line) => line.Split(' ')[1];

    /// <summary>The columns that hold a day (no time): date on PostgreSQL.</summary>
    public static readonly string[] DateColumns = ["token_activity.day"];

    /// <summary>
    /// A SQLite column's declared type as PostgreSQL's baseline names it. Anything else stays unmapped, and differs.
    /// <paramref name="moduleTimeColumns"/> are a module track's time columns ("table.column"), read as <see cref="TimeColumns"/> are: a
    /// module pins its own list, so the core's stays the core's alone.
    /// </summary>
    public static string SqliteTypes(string table, string column, string declared, IReadOnlyCollection<string>? moduleTimeColumns = null) => declared.ToUpperInvariant() switch
    {
        "TEXT" when TimeColumns.Contains($"{table}.{column}") || moduleTimeColumns?.Contains($"{table}.{column}") == true => "timestamptz",
        "TEXT" when DateColumns.Contains($"{table}.{column}") => "date",
        "TEXT" => "text",
        "INTEGER" => "bigint",
        "BLOB" => "bytea",
        _ => "sqlite " + declared,
    };

    private static bool IsTrackTable(string name) => name == "schema_migrations" || name.EndsWith("_schema_migrations", StringComparison.Ordinal);

    /// <summary>
    /// An expression or predicate as both engines can agree on it. Outside quoted literals: casts, brackets, identifier quotes and spaces
    /// dropped, and lower case (identifiers are not case-sensitive). A quoted literal is kept exactly: 'shared' and 'SHARED' differ.
    /// </summary>
    internal static string Normalise(string expression) =>
        string.Concat(StringLiteral().Split(expression).Select((part, i) => i % 2 == 1 ? part
            : Casts().Replace(part, "").Replace("(", "").Replace(")", "").Replace("\"", "").Replace(" ", "").ToLowerInvariant()));

    [GeneratedRegex(@"::[a-z ]+(\[\])?")]
    private static partial Regex Casts();

    [GeneratedRegex(@"\bCHECK\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex Check();

    /// <summary>A quoted SQL string literal ('' inside it is a quote), captured so Split keeps it.</summary>
    [GeneratedRegex(@"('(?:[^']|'')*')")]
    private static partial Regex StringLiteral();

    /// <summary>A number standing alone (not part of a name or of a longer number).</summary>
    [GeneratedRegex(@"(?<![\w.])\d+(?:\.\d+)?(?![\w.])")]
    private static partial Regex Number();

    [GeneratedRegex(@"\bCOLLATE\s+""?(\w+)""?", RegexOptions.IgnoreCase)]
    private static partial Regex Collate();

    [GeneratedRegex(@"(?<!\bNOT\s+)\bDEFERRABLE\b", RegexOptions.IgnoreCase)]
    private static partial Regex Deferrable();

    [GeneratedRegex(@"\bDEFERRABLE\s+INITIALLY\s+DEFERRED\b", RegexOptions.IgnoreCase)]
    private static partial Regex InitiallyDeferred();

    /// <summary>
    /// The string literals and numbers in <paramref name="text"/>, literals exactly as written. PostgreSQL rewrites a CHECK
    /// (<c>kind IN ('a','b')</c> becomes <c>kind = ANY (ARRAY['a'::text, 'b'::text])</c>) but keeps every literal, so the two engines'
    /// CHECKs are compared by the multiset of what they test against.
    /// </summary>
    internal static IEnumerable<string> Literals(string text)
    {
        var strings = StringLiteral().Matches(text).Select(m => m.Value).ToList();
        return strings.Concat(Number().Matches(StringLiteral().Replace(text, " ")).Select(m => m.Value));
    }

    /// <summary>The body of every CHECK(...) in a CREATE TABLE statement, brackets balanced and string literals skipped.</summary>
    internal static List<string> CheckBodies(string sql)
    {
        var bodies = new List<string>();
        foreach (Match m in Check().Matches(sql))
        {
            var start = m.Index + m.Length;
            var depth = 0;
            var quoted = false;
            var i = start;
            for (; i < sql.Length; i++)
            {
                if (sql[i] == '\'') quoted = !quoted;
                else if (quoted) continue;
                else if (sql[i] == '(') depth++;
                else if (sql[i] == ')' && depth-- == 0) break;
            }
            bodies.Add(sql[start..i]);
        }
        return bodies;
    }

    private static string CheckLiterals(string table, IEnumerable<string> literals) =>
        $"{table} check-literals=[{string.Join(", ", literals.Order(StringComparer.Ordinal))}]";

    private static string Collations(string table, IEnumerable<string> names) =>
        $"{table} collations=[{string.Join(", ", names.Order(StringComparer.Ordinal))}]";

    private static string Deferral(string table, int deferrable, int deferred) => $"{table} fks deferrable={deferrable} initially-deferred={deferred}";

    private static string Column(string table, int position, string name, string type, bool notNull, int pk, string? defaultValue, string? identity) =>
        $"{table}#{position:D2} {name} {type} {(notNull ? "NOT NULL" : "NULL")}{(pk > 0 ? $" pk{pk}" : "")}" +
        $"{(defaultValue is null ? "" : $" default={defaultValue}")}{(identity is null ? "" : $" identity {identity}")}";

    private static string Trigger(string name, string table, string timing, string events, bool row, string state) =>
        $"trigger {name} on {table} {timing} {events} {(row ? "row" : "statement")}{state}";

    /// <summary>A SQLite CREATE TRIGGER's head: its timing (BEFORE when none is written), its one event and an UPDATE's OF columns.</summary>
    [GeneratedRegex(@"^\s*CREATE\s+(?:TEMP\s+|TEMPORARY\s+)?TRIGGER\s+(?:IF\s+NOT\s+EXISTS\s+)?""?\w+""?\s+(?:(BEFORE|AFTER|INSTEAD\s+OF)\s+)?(DELETE|INSERT|UPDATE)(?:\s+OF\s+(.+?))?\s+ON\s",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SqliteTriggerHead();

    /// <summary>A SQLite trigger's shape line, read from its CREATE TRIGGER text. SQLite's triggers are row triggers, always enabled.</summary>
    internal static string SqliteTrigger(string name, string table, string sql)
    {
        var m = SqliteTriggerHead().Match(sql);
        if (!m.Success) throw new FormatException("Unread trigger text: " + sql);
        var timing = m.Groups[1].Success ? Regex.Replace(m.Groups[1].Value, @"\s+", " ").ToLowerInvariant() : "before";
        var events = m.Groups[2].Value.ToLowerInvariant();
        if (m.Groups[3].Success)
            events += " of " + string.Join(", ", m.Groups[3].Value.Split(',').Select(c => c.Trim().Trim('"').ToLowerInvariant()));
        return Trigger(name, table, timing, events, true, "");
    }

    /// <summary>
    /// A PostgreSQL trigger's shape line from pg_trigger: tgtype's bits (1 row, 2 before, 4 insert, 8 delete, 16 update, 32 truncate,
    /// 64 instead of), an UPDATE's OF columns (tgattr) and tgenabled ('O' fires as normal, 'D' disabled, 'R' in replica mode only, 'A' always).
    /// </summary>
    internal static string PostgresTrigger(string name, string table, int type, string? updateColumns, char enabled)
    {
        var timing = (type & 2) != 0 ? "before" : (type & 64) != 0 ? "instead of" : "after";
        var events = new List<string>();
        if ((type & 4) != 0) events.Add("insert");
        if ((type & 8) != 0) events.Add("delete");
        if ((type & 16) != 0) events.Add(updateColumns is null ? "update" : "update of " + updateColumns);
        if ((type & 32) != 0) events.Add("truncate");
        var state = enabled switch
        {
            'O' => "",
            'D' => " disabled",
            'R' => " replica",
            'A' => " always",
            _ => " enabled=" + enabled,
        };
        return Trigger(name, table, timing, string.Join(" or ", events), (type & 1) != 0, state);
    }

    private static string Index(string name, string table, IEnumerable<string> columns, bool unique, string? predicate) =>
        $"index {name} on {table}({string.Join(", ", columns.Select(Normalise))}){(unique ? " unique" : "")}{(predicate is null ? "" : " where " + Normalise(predicate))}";

    // ---- SQLite ----

    /// <summary>
    /// The SQLite database's shape. Its TEXT columns read as timestamptz when on <see cref="TimeColumns"/> or on
    /// <paramref name="moduleTimeColumns"/> (a module track's own pinned list, for a database with its tables too).
    /// </summary>
    public static async Task<List<string>> ReadAsync(SqliteConnection c, IReadOnlyCollection<string>? moduleTimeColumns = null)
    {
        var lines = new List<string>();
        var tables = (await Rows(c, "SELECT name, sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite%' ORDER BY name"))
            .Where(r => !IsTrackTable((string)r[0]!)).Select(r => (Name: (string)r[0]!, Sql: (string)r[1]!)).ToList();
        foreach (var (table, sql) in tables)
        {
            lines.Add("table " + table);
            var columns = await Rows(c, $"SELECT cid, name, type, \"notnull\", dflt_value, pk FROM pragma_table_info('{table}') ORDER BY cid");
            var pkCount = columns.Count(r => Convert.ToInt32(r[5], CultureInfo.InvariantCulture) > 0);
            foreach (var r in columns)
            {
                var name = (string)r[1]!;
                var declared = (string)r[2]!;
                var pk = Convert.ToInt32(r[5], CultureInfo.InvariantCulture);
                // A primary key's column is never NULL on PostgreSQL; SQLite allows it unless the column says NOT NULL, a legacy quirk.
                var notNull = Convert.ToInt32(r[3], CultureInfo.InvariantCulture) == 1 || pk > 0;
                // AUTOINCREMENT counts from 1 in steps of 1, and the database always picks the value (an explicit id is the exception).
                var identity = pk == 1 && pkCount == 1 && declared.Equals("INTEGER", StringComparison.OrdinalIgnoreCase)
                    && sql.Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase) ? "always start 1 increment 1" : null;
                lines.Add(Column(table, Convert.ToInt32(r[0], CultureInfo.InvariantCulture) + 1, name, SqliteTypes(table, name, declared, moduleTimeColumns), notNull, pk, r[4] as string, identity));
            }
            var uniques = new List<string>();
            foreach (var u in await Rows(c, $"SELECT name FROM pragma_index_list('{table}') WHERE origin = 'u'"))
                uniques.Add($"{table} unique({string.Join(", ", (await Rows(c, $"SELECT name FROM pragma_index_info('{u[0]}') ORDER BY seqno")).Select(x => (string)x[0]!))})");
            lines.AddRange(uniques.Order(StringComparer.Ordinal));
            var fks = new List<string>();
            foreach (var fk in (await Rows(c, $"SELECT id, seq, \"table\", \"from\", \"to\", on_update, on_delete FROM pragma_foreign_key_list('{table}') ORDER BY id, seq"))
                .GroupBy(r => Convert.ToInt32(r[0], CultureInfo.InvariantCulture)))
            {
                var first = fk.First();
                fks.Add($"{table} fk({string.Join(", ", fk.Select(x => (string)x[3]!))}) -> {first[2]}({string.Join(", ", fk.Select(x => (string)x[4]!))}) " +
                    $"on update {first[5]} on delete {first[6]}");
            }
            lines.AddRange(fks.Order(StringComparer.Ordinal));
            // SQLite keeps these only in the table's text: read from it, outside string literals.
            var code = StringLiteral().Replace(sql, "''");
            lines.Add(Deferral(table, Deferrable().Matches(code).Count, InitiallyDeferred().Matches(code).Count));
            lines.Add(Collations(table, Collate().Matches(code).Select(m => m.Groups[1].Value).Where(n => !n.Equals("BINARY", StringComparison.OrdinalIgnoreCase))));
            var bodies = CheckBodies(sql);
            lines.Add($"{table} checks={bodies.Count}");
            lines.Add(CheckLiterals(table, bodies.SelectMany(Literals)));
        }
        foreach (var r in await Rows(c, "SELECT name, tbl_name, sql FROM sqlite_master WHERE type='index' AND sql IS NOT NULL ORDER BY name"))
        {
            var (name, table, sql) = ((string)r[0]!, (string)r[1]!, (string)r[2]!);
            var (columns, predicate) = ParseIndex(sql, table);
            lines.Add(Index(name, table, columns, sql.StartsWith("CREATE UNIQUE INDEX", StringComparison.OrdinalIgnoreCase), predicate));
        }
        foreach (var r in await Rows(c, "SELECT name, tbl_name, sql FROM sqlite_master WHERE type='trigger' ORDER BY name"))
            lines.Add(SqliteTrigger((string)r[0]!, (string)r[1]!, (string)r[2]!));
        return lines;
    }

    /// <summary>A CREATE INDEX statement's column list (split at top-level commas) and its WHERE predicate, if any.</summary>
    internal static (List<string> Columns, string? Predicate) ParseIndex(string sql, string table)
    {
        var on = Regex.Match(sql, $@"\bON\s+""?{Regex.Escape(table)}""?\s*\(", RegexOptions.IgnoreCase);
        if (!on.Success) throw new FormatException("Not an index on " + table + ": " + sql);
        var columns = new List<string>();
        var depth = 0;
        var start = on.Index + on.Length;
        var i = start;
        for (; i < sql.Length; i++)
        {
            if (sql[i] == '(') depth++;
            else if (sql[i] == ')' && depth-- == 0) break;
            else if (sql[i] == ',' && depth == 0) { columns.Add(sql[start..i].Trim()); start = i + 1; }
        }
        columns.Add(sql[start..i].Trim());
        var rest = sql[(i + 1)..].Trim().TrimEnd(';').Trim();
        var where = Regex.Match(rest, @"^WHERE\s+(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return (columns, where.Success ? where.Groups[1].Value : rest.Length == 0 ? null : throw new FormatException("Unread index text: " + rest));
    }

    private static async Task<List<object?[]>> Rows(SqliteConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await r.ReadAsync())
        {
            var row = new object?[r.FieldCount];
            for (var k = 0; k < r.FieldCount; k++) row[k] = r.IsDBNull(k) ? null : r.GetValue(k);
            rows.Add(row);
        }
        return rows;
    }

    // ---- PostgreSQL ----

    private const string Here = "(SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = current_schema())";

    private static string PostgresType(string dataType) => dataType switch
    {
        "timestamp with time zone" => "timestamptz",
        _ => dataType,
    };

    private static string Action(char code) => code switch
    {
        'a' => "NO ACTION",
        'r' => "RESTRICT",
        'c' => "CASCADE",
        'n' => "SET NULL",
        'd' => "SET DEFAULT",
        _ => "unknown " + code,
    };

    /// <summary>
    /// The oid of PostgreSQL's built-in "C" collation, byte order: the one in pg_catalog. A column is compared with it
    /// by oid, never by name, since any schema can hold a collation named "C" that orders some other way.
    /// </summary>
    public const string BuiltInC = "(SELECT oid FROM pg_catalog.pg_collation WHERE collname = 'C' AND collnamespace = 'pg_catalog'::regnamespace)";

    /// <summary>A collation's name (<c>l</c>): bare when it is a built-in one (pg_catalog's), else with its schema, so a "C" of another schema reads as itself.</summary>
    internal const string CollationName =
        "CASE WHEN l.collnamespace = 'pg_catalog'::regnamespace THEN l.collname::text ELSE l.collnamespace::regnamespace::text || '.' || l.collname::text END";

    public static async Task<List<string>> ReadAsync(NpgsqlConnection c)
    {
        var lines = new List<string>();
        var tables = (await Rows(c, $"SELECT relname::text FROM pg_catalog.pg_class WHERE relnamespace = {Here} AND relkind IN ('r', 'p') ORDER BY relname COLLATE \"C\""))
            .Select(r => (string)r[0]!).Where(t => !IsTrackTable(t)).ToList();
        foreach (var table in tables)
        {
            lines.Add("table " + table);
            var pk = (await Rows(c,
                $"""
                SELECT a.attname::text, k.ord::int FROM pg_catalog.pg_constraint p
                CROSS JOIN LATERAL unnest(p.conkey) WITH ORDINALITY AS k(attnum, ord)
                JOIN pg_catalog.pg_attribute a ON a.attrelid = p.conrelid AND a.attnum = k.attnum
                WHERE p.contype = 'p' AND p.conrelid = '{Ident(table)}'::regclass
                """)).ToDictionary(r => (string)r[0]!, r => (int)r[1]!);
            foreach (var r in await Rows(c,
                $"""
                SELECT ordinal_position::int, column_name::text, data_type::text, is_nullable::text, column_default::text, is_identity::text, identity_generation::text,
                       identity_start::text, identity_increment::text
                FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = '{table}' ORDER BY ordinal_position
                """))
            {
                var name = (string)r[1]!;
                var identity = (string)r[5]! == "YES" ? $"{((string)r[6]!).ToLowerInvariant()} start {r[7]} increment {r[8]}" : null;
                var defaultValue = r[4] is string d ? Casts().Replace(d, "") : null;
                lines.Add(Column(table, (int)r[0]!, name, PostgresType((string)r[2]!), (string)r[3]! == "NO", pk.GetValueOrDefault(name), defaultValue, identity));
            }
            lines.AddRange((await Rows(c,
                $"""
                SELECT (SELECT string_agg(a.attname::text, ', ' ORDER BY k.ord) FROM unnest(p.conkey) WITH ORDINALITY AS k(attnum, ord)
                        JOIN pg_catalog.pg_attribute a ON a.attrelid = p.conrelid AND a.attnum = k.attnum)
                FROM pg_catalog.pg_constraint p WHERE p.contype = 'u' AND p.conrelid = '{Ident(table)}'::regclass
                """)).Select(r => $"{table} unique({r[0]})").Order(StringComparer.Ordinal));
            var fks = new List<string>();
            var deferrable = 0;
            var deferred = 0;
            foreach (var r in await Rows(c,
                $"""
                SELECT (SELECT string_agg(a.attname::text, ', ' ORDER BY k.ord) FROM unnest(p.conkey) WITH ORDINALITY AS k(attnum, ord)
                        JOIN pg_catalog.pg_attribute a ON a.attrelid = p.conrelid AND a.attnum = k.attnum),
                       p.confrelid::regclass::text,
                       (SELECT string_agg(a.attname::text, ', ' ORDER BY k.ord) FROM unnest(p.confkey) WITH ORDINALITY AS k(attnum, ord)
                        JOIN pg_catalog.pg_attribute a ON a.attrelid = p.confrelid AND a.attnum = k.attnum),
                       p.confupdtype::text, p.confdeltype::text, p.condeferrable, p.condeferred
                FROM pg_catalog.pg_constraint p WHERE p.contype = 'f' AND p.conrelid = '{Ident(table)}'::regclass
                """))
            {
                fks.Add($"{table} fk({r[0]}) -> {r[1]}({r[2]}) on update {Action(((string)r[3]!)[0])} on delete {Action(((string)r[4]!)[0])}");
                if ((bool)r[5]!) deferrable++;
                if ((bool)r[6]!) deferred++;
            }
            lines.AddRange(fks.Order(StringComparer.Ordinal));
            lines.Add(Deferral(table, deferrable, deferred));
            lines.Add(Collations(table, (await Rows(c,
                $"""
                SELECT {CollationName} FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_collation l ON l.oid = a.attcollation
                WHERE a.attrelid = '{Ident(table)}'::regclass AND a.attnum > 0 AND NOT a.attisdropped AND a.attcollation <> {BuiltInC}
                """)).Select(r => (string)r[0]!)));
            var checks = (await Rows(c, $"SELECT pg_catalog.pg_get_constraintdef(oid) FROM pg_catalog.pg_constraint WHERE contype = 'c' AND conrelid = '{Ident(table)}'::regclass"))
                .Select(r => (string)r[0]!).ToList();
            lines.Add($"{table} checks={checks.Count}");
            lines.Add(CheckLiterals(table, checks.SelectMany(Literals)));
        }
        // Indexes a constraint made (primary keys and UNIQUE) are the lines above; these are the ones CREATE INDEX made.
        foreach (var r in await Rows(c,
            $"""
            SELECT i.relname::text, t.relname::text, x.indisunique,
                   (SELECT array_agg(pg_catalog.pg_get_indexdef(x.indexrelid, k, true) ORDER BY k) FROM generate_series(1, x.indnatts) AS k),
                   pg_catalog.pg_get_expr(x.indpred, x.indrelid, true)
            FROM pg_catalog.pg_index x
            JOIN pg_catalog.pg_class i ON i.oid = x.indexrelid
            JOIN pg_catalog.pg_class t ON t.oid = x.indrelid
            WHERE t.relnamespace = {Here} AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint p WHERE p.conindid = x.indexrelid)
            ORDER BY i.relname COLLATE "C"
            """))
        {
            var (name, table) = ((string)r[0]!, (string)r[1]!);
            if (IsTrackTable(table)) continue;
            lines.Add(Index(name, table, (string[])r[3]!, (bool)r[2]!, r[4] as string));
        }
        foreach (var r in await Rows(c,
            $"""
            SELECT g.tgname::text, t.relname::text, g.tgtype::int, g.tgenabled::text,
                   (SELECT string_agg(a.attname::text, ', ' ORDER BY k.ord) FROM unnest(g.tgattr::int2[]) WITH ORDINALITY AS k(attnum, ord)
                    JOIN pg_catalog.pg_attribute a ON a.attrelid = g.tgrelid AND a.attnum = k.attnum)
            FROM pg_catalog.pg_trigger g JOIN pg_catalog.pg_class t ON t.oid = g.tgrelid
            WHERE NOT g.tgisinternal AND t.relnamespace = {Here} ORDER BY g.tgname COLLATE "C"
            """))
            lines.Add(PostgresTrigger((string)r[0]!, (string)r[1]!, (int)r[2]!, r[4] as string, ((string)r[3]!)[0]));
        return lines;
    }

    /// <summary>A table name, as a quoted identifier for ::regclass (names here are lower-case letters, digits and _).</summary>
    private static string Ident(string table) => "\"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static async Task<List<object?[]>> Rows(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await r.ReadAsync())
        {
            var row = new object?[r.FieldCount];
            for (var k = 0; k < r.FieldCount; k++) row[k] = r.IsDBNull(k) ? null : r.GetValue(k);
            rows.Add(row);
        }
        return rows;
    }
}
