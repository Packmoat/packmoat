using System.Text.RegularExpressions;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The rules the schema enforces itself, with triggers (#232 S1): append-only logs, the 90-day audit floor and purged tenants. A second
/// engine (Postgres) has to enforce the same rules, so its dialect must match this list. A trigger added to, dropped from or renamed in a
/// core migration fails here until the list, and so the other dialect's to-do, says so.
/// </summary>
[Collection("sqlite-pools")]
public class SchemaTriggerTests
{
    /// <summary>Every CREATE TRIGGER in the core's migrations, in order, with the migration that runs it. Some are dropped and made again.</summary>
    public static readonly (int Migration, string Trigger)[] Created =
    [
        (1, "audit_no_update"),
        (1, "audit_no_delete"),
        (5, "admin_actions_no_update"),
        (5, "admin_actions_no_delete"),
        (15, "tenant_purges_only_when_gone"),
        (15, "tenant_purges_no_delete"),
        (15, "tenant_purges_no_update"),
        (15, "tenants_no_purged_id"),
        (15, "tenants_no_rename_to_purged_id"),
        (15, "audit_no_delete"),          // made again: a running purge may delete its tenant's rows
        (15, "audit_purged_tenant"),
        (17, "decision_records_purged_tenant"),
        (18, "admin_actions_no_delete"),  // made again: a row may go once it is more than 365 days old
        (19, "audit_no_delete"),          // made again: a row may go once it is more than 90 days old
    ];

    /// <summary>The triggers a database at the current schema holds: the rules a second engine's schema must enforce.</summary>
    public static readonly string[] Current =
    [
        "admin_actions_no_delete", "admin_actions_no_update", "audit_no_delete", "audit_no_update", "audit_purged_tenant",
        "decision_records_purged_tenant", "tenant_purges_no_delete", "tenant_purges_no_update", "tenant_purges_only_when_gone",
        "tenants_no_purged_id", "tenants_no_rename_to_purged_id",
    ];

    private static readonly Regex CreateTrigger = new(@"CREATE\s+TRIGGER\s+(?:IF\s+NOT\s+EXISTS\s+)?(\w+)", RegexOptions.IgnoreCase);

    [Fact]
    public void The_core_migrations_create_exactly_the_listed_triggers_in_order()
    {
        var found = SqliteDatabase.Migrations
            .SelectMany((statements, i) => statements.SelectMany(sql => CreateTrigger.Matches(sql).Select(m => (Migration: i + 1, Trigger: m.Groups[1].Value))))
            .ToArray();
        Assert.Equal(14, Created.Length);
        Assert.Equal(Created, found);
    }

    [Fact]
    public async Task A_database_at_the_current_schema_holds_exactly_the_listed_triggers()
    {
        await using var t = await TestDb.CreateAsync();
        await using var c = await t.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' ORDER BY name";
        var names = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) names.Add(r.GetString(0));
        Assert.Equal(Current, names);
        Assert.Equal(Current, Created.Select(c => c.Trigger).Distinct().Order(StringComparer.Ordinal));
    }
}
