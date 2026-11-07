using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Packmoat.Infrastructure;

/// <summary>
/// A database's schema, normalised so two databases built by different routes compare equal when their structure is equal: every
/// object in sqlite_master (sorted by type, then name; no rootpage), its SQL with each run of whitespace collapsed to one space, and
/// for each table its columns (table_xinfo), foreign keys (foreign_key_list) and indexes (index_list with index_xinfo). Reads only.
/// </summary>
public sealed partial class SqliteSchemaSnapshot
{
    /// <summary>One object: its type and name, its SQL as stored and normalised (null for an automatic index), and its structure lines.</summary>
    public sealed record SchemaObject(string Type, string Name, string? RawSql, string? Sql, IReadOnlyList<string> Structure)
    {
        public string Key => Type + " " + Name;
    }

    public IReadOnlyList<SchemaObject> Objects { get; }

    private SqliteSchemaSnapshot(IReadOnlyList<SchemaObject> objects) => Objects = objects;

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>The SQL with each run of whitespace collapsed to one space and the ends trimmed.</summary>
    public static string Normalise(string sql) => Whitespace().Replace(sql, " ").Trim();

    public static async Task<SqliteSchemaSnapshot> ReadAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        var master = new List<(string Type, string Name, string Table, string? Sql)>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_master";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) master.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        }
        var objects = new List<SchemaObject>();
        foreach (var (type, name, _, sql) in master.OrderBy(m => m.Type, StringComparer.Ordinal).ThenBy(m => m.Name, StringComparer.Ordinal))
        {
            var structure = new List<string>();
            if (type == "table")
            {
                foreach (var row in await RowsAsync(connection, "SELECT cid, name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo(@a)", name, ct))
                    structure.Add($"col {row[0]} {row[1]} {row[2]} notnull={row[3]} default={row[4]} pk={row[5]} hidden={row[6]}");
                var fks = (await RowsAsync(connection, "SELECT id, seq, \"table\", \"from\", \"to\", on_update, on_delete, \"match\" FROM pragma_foreign_key_list(@a)", name, ct))
                    .Select(row => $"fk {row[0]}.{row[1]} {row[3]} -> {row[2]}({row[4]}) on update {row[5]} on delete {row[6]} match {row[7]}");
                structure.AddRange(fks.Order(StringComparer.Ordinal));
                var indexes = (await RowsAsync(connection, "SELECT name, \"unique\", origin, partial FROM pragma_index_list(@a)", name, ct))
                    .OrderBy(row => row[0], StringComparer.Ordinal).ToList();
                foreach (var index in indexes)
                {
                    structure.Add($"index {index[0]} unique={index[1]} origin={index[2]} partial={index[3]}");
                    foreach (var key in await RowsAsync(connection, "SELECT seqno, cid, name, \"desc\", coll, \"key\" FROM pragma_index_xinfo(@a)", index[0], ct))
                        structure.Add($"  key {key[0]} cid={key[1]} {key[2]} desc={key[3]} coll={key[4]} key={key[5]}");
                }
            }
            objects.Add(new SchemaObject(type, name, sql, sql is null ? null : Normalise(sql), structure));
        }
        return new SqliteSchemaSnapshot(objects);
    }

    /// <summary>The snapshot as text: one block per object (all, or those <paramref name="include"/> keeps), the SQL then the structure lines indented.</summary>
    public IEnumerable<string> Lines(Func<SchemaObject, bool>? include = null)
    {
        foreach (var o in Objects.Where(include ?? (_ => true)))
        {
            yield return o.Key;
            yield return "  sql " + (o.Sql ?? "(none)");
            foreach (var s in o.Structure) yield return "  " + s;
        }
    }

    private static async Task<List<string[]>> RowsAsync(SqliteConnection connection, string sql, string argument, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@a", argument);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<string[]>();
        while (await r.ReadAsync(ct))
        {
            var cells = new string[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++) cells[i] = r.IsDBNull(i) ? "NULL" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)!;
            rows.Add(cells);
        }
        return rows;
    }
}
