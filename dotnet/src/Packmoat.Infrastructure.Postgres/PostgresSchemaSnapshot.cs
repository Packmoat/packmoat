using Npgsql;

namespace Packmoat.Infrastructure.Postgres;

/// <summary>
/// One schema's objects as PostgreSQL's catalogue describes them, for <see cref="PostgresMigrationCheck"/>: each table's
/// columns (name, type, NOT NULL, default, identity, collation, in order), each constraint, index, trigger (with whether it fires),
/// sequence and function (arguments, result, body, settings), keyed "kind name". The schema's own name is taken out of every definition, so
/// a schema and a fresh build of it in another schema compare equal when they are the same.
/// </summary>
internal static class PostgresSchemaSnapshot
{
    /// <summary>The objects of <paramref name="schema"/>, by "kind name", each with its definition.</summary>
    public static async Task<SortedDictionary<string, string>> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string schema, CancellationToken ct)
    {
        var objects = new SortedDictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand(Sql, connection, tx);
        cmd.Parameters.AddWithValue("schema", schema);
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) objects[r.GetString(0)] = r.GetString(1);
        // A definition names the schema (an index's ON s.t, a function's SET search_path): taken out, as "@." and "@".
        var qualified = Prefix(schema);
        foreach (var key in objects.Keys.ToList())
            objects[key] = objects[key].Replace(qualified, "@.", StringComparison.Ordinal)
                .Replace("search_path=" + PostgresDatabase.QuotedSchema(schema), "search_path=@", StringComparison.Ordinal)
                .Replace("search_path=" + schema + ",", "search_path=@,", StringComparison.Ordinal);
        return objects;
    }

    /// <summary>How PostgreSQL writes an object qualified by <paramref name="schema"/>: quoted only when it must be.</summary>
    private static string Prefix(string schema) =>
        (System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_$]*$") ? schema : PostgresDatabase.QuotedSchema(schema)) + ".";

    /// <summary>What differs between <paramref name="migrated"/> and a fresh build (<paramref name="fresh"/>), by kind and name only.</summary>
    public static List<string> Compare(IReadOnlyDictionary<string, string> migrated, IReadOnlyDictionary<string, string> fresh)
    {
        var differences = new List<string>();
        foreach (var key in migrated.Keys.Union(fresh.Keys).Order(StringComparer.Ordinal))
        {
            if (!fresh.TryGetValue(key, out var f)) differences.Add($"{key}: only in this database");
            else if (!migrated.TryGetValue(key, out var m)) differences.Add($"{key}: only in a fresh build");
            else if (!string.Equals(m, f, StringComparison.Ordinal)) differences.Add($"{key}: differs from a fresh build");
        }
        return differences;
    }

    private const string Sql = """
        WITH s AS (SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = @schema)
        SELECT 'table ' || c.relname::text,
               c.relkind::text || ' ' || COALESCE((
                 SELECT string_agg(a.attname::text || ' ' || pg_catalog.format_type(a.atttypid, a.atttypmod)
                          || CASE WHEN a.attnotnull THEN ' not null' ELSE '' END
                          || COALESCE(' default ' || pg_catalog.pg_get_expr(d.adbin, d.adrelid), '')
                          || CASE a.attidentity WHEN 'a' THEN ' identity always' WHEN 'd' THEN ' identity by default' ELSE '' END
                          || CASE a.attgenerated WHEN 's' THEN ' generated stored' ELSE '' END
                          || COALESCE(' collate ' || co.collname::text, ''), ', ' ORDER BY a.attnum)
                 FROM pg_catalog.pg_attribute a
                   LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
                   LEFT JOIN pg_catalog.pg_collation co ON co.oid = a.attcollation AND a.attcollation <> 0
                 WHERE a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped), '')
        FROM pg_catalog.pg_class c JOIN s ON c.relnamespace = s.oid WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')
        UNION ALL
        SELECT 'constraint ' || c.relname::text || '.' || k.conname::text, k.contype::text || ' ' || pg_catalog.pg_get_constraintdef(k.oid)
          || CASE WHEN k.condeferrable THEN ' deferrable' ELSE '' END || CASE WHEN k.condeferred THEN ' deferred' ELSE '' END
        FROM pg_catalog.pg_constraint k JOIN pg_catalog.pg_class c ON c.oid = k.conrelid JOIN s ON c.relnamespace = s.oid
        UNION ALL
        SELECT 'index ' || i.relname::text, pg_catalog.pg_get_indexdef(x.indexrelid)
        FROM pg_catalog.pg_index x JOIN pg_catalog.pg_class i ON i.oid = x.indexrelid JOIN s ON i.relnamespace = s.oid
        UNION ALL
        SELECT 'trigger ' || c.relname::text || '.' || t.tgname::text, pg_catalog.pg_get_triggerdef(t.oid) || ' enabled=' || t.tgenabled::text
        FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid JOIN s ON c.relnamespace = s.oid
        WHERE NOT t.tgisinternal
        UNION ALL
        SELECT 'sequence ' || c.relname::text, pg_catalog.format_type(q.seqtypid, NULL) || ' ' || q.seqstart || ' ' || q.seqincrement || ' ' || q.seqmin
          || ' ' || q.seqmax || ' ' || q.seqcache || CASE WHEN q.seqcycle THEN ' cycle' ELSE '' END
        FROM pg_catalog.pg_sequence q JOIN pg_catalog.pg_class c ON c.oid = q.seqrelid JOIN s ON c.relnamespace = s.oid
        UNION ALL
        SELECT 'function ' || p.proname::text || '(' || pg_catalog.pg_get_function_identity_arguments(p.oid) || ')',
               p.prokind::text || ' returns ' || pg_catalog.pg_get_function_result(p.oid) || ' language ' || l.lanname::text
               || ' volatility ' || p.provolatile::text || CASE WHEN p.prosecdef THEN ' security definer' ELSE '' END
               || ' config ' || COALESCE(pg_catalog.array_to_string(p.proconfig, ';'), '') || ' body ' || p.prosrc
        FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_language l ON l.oid = p.prolang JOIN s ON p.pronamespace = s.oid
        """;
}
