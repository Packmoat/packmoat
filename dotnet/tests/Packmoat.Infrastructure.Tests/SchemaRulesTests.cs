using System.Reflection;
using System.Text.RegularExpressions;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The schema's rules on SQLite: <see cref="SchemaRulesContract"/>, and the two checks that hold its coverage to the schema: every trigger has a rules test by name (test 4), and every CHECK has a probe.
/// </summary>
[Collection("sqlite-pools")]
public class SchemaRulesTests : SchemaRulesContract
{
    protected override ISqlEngine Engine => SqliteStoreEngine.Instance;

    /// <summary>The trigger names a fixture's tests are named after: the part before "__" of each test named so, distinct, in ordinal order.</summary>
    internal static List<string> RulePrefixes(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<FactAttribute>().Any() && m.Name.Contains("__", StringComparison.Ordinal))
            .Select(m => m.Name[..m.Name.IndexOf("__", StringComparison.Ordinal)])
            .Distinct().Order(StringComparer.Ordinal).ToList();

    // Test 4. mutations: a rules test's prefix renamed; a trigger added to SQLite with no rules test; a truncate guard's test dropped
    [Fact]
    public void Every_trigger_has_a_rules_test()
    {
        Assert.Equal(SchemaTriggerTests.Current, RulePrefixes(typeof(SchemaRulesContract)));
        Assert.Equal(SchemaShape.PostgresOnlyTriggers.Select(SchemaShape.TriggerName).Order(StringComparer.Ordinal), RulePrefixes(typeof(PostgresTriggerGuardTests)));
    }

    /// <summary>A CHECK's body as the probes are keyed: whitespace collapsed, then <see cref="SchemaShape.Normalise"/>.</summary>
    private static string Key(string table, string body) => table + ": " + SchemaShape.Normalise(Regex.Replace(body, @"\s+", " "));

    // mutations: a probe removed; a CHECK added to the schema with no probe
    [Fact]
    public async Task Every_check_has_a_probe()
    {
        await using var t = await TestDb.CreateAsync();
        var schema = new List<string>();
        await using (var c = await t.Db.OpenAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite%' ORDER BY name";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) schema.AddRange(SchemaShape.CheckBodies(r.GetString(1)).Select(b => Key(r.GetString(0), b)));
        }

        Assert.Equal(20, schema.Count);
        Assert.Equal(schema.Order(StringComparer.Ordinal), CheckProbes.Select(p => Key(p.Table, p.Check)).Order(StringComparer.Ordinal));
        Assert.All(CheckProbes, p => Assert.True(p.Accepted.Length > 0 && p.Refused.Length > 0, $"{p.Table} CHECK({p.Check}) needs a row each way"));
    }
}

/// <summary>The schema's rules on PostgreSQL: <see cref="SchemaRulesContract"/>, each test on a database of its own.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresSchemaRulesTests : SchemaRulesContract
{
    protected override ISqlEngine Engine => PostgresSqlEngine.Instance;
}
