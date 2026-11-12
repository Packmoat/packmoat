using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>The audit log's contract: events read back newest first, and the table refuses updates and deletes.</summary>
public abstract class AuditContract : StoreContract
{
    protected static AuditEvent Event(DateTimeOffset at, string pkg = "moq", string version = "4.21.0", AuditKind kind = AuditKind.Download, Decision d = Decision.Hide) =>
        new(at, "req-1", "acme", "tok_1", kind, pkg, version, d, d == Decision.Hide ? VerdictReason.Cooldown : VerdictReason.CooldownElapsed, null);

    [EngineFact]
    public async Task Events_are_stored_and_read_back_newest_first()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        await Engine.Audit(t).AppendAsync(Event(t.Clock.UtcNow, version: "1.0.0"), default);
        await Engine.Audit(t).AppendAsync(Event(t.Clock.UtcNow, version: "2.0.0", d: Decision.Allow), default);

        var rows = await Engine.AuditQuery(t).QueryAsync("acme", 10, default);

        Assert.Equal(new[] { "2.0.0", "1.0.0" }, rows.Select(r => r.Version));
        Assert.Equal(Decision.Allow, rows[0].Decision);
        Assert.Equal(VerdictReason.CooldownElapsed, rows[0].Reason);
    }

    /// <summary>The query's tenant scope and its limit had no contract test.</summary>
    [EngineFact]   // mutations: the query without its tenant_id scope; the limit dropped; oldest first
    public async Task A_tenants_query_holds_only_its_events_newest_first_up_to_the_limit_and_no_tenant_reads_them_all()
    {
        await using var t = await CreateDbAsync();
        var at = t.Clock.UtcNow;
        foreach (var (tenant, version) in new[] { ("acme", "1.0.0"), ("globex", "9.0.0"), ("acme", "2.0.0"), ("acme", "3.0.0") })
            await Engine.Audit(t).AppendAsync(Event(at, version: version) with { TenantId = tenant }, default);

        Assert.Equal(new[] { "3.0.0", "2.0.0", "1.0.0" }, (await Engine.AuditQuery(t).QueryAsync("acme", 10, default)).Select(r => r.Version));
        Assert.Equal(new[] { "3.0.0", "2.0.0" }, (await Engine.AuditQuery(t).QueryAsync("acme", 2, default)).Select(r => r.Version));
        Assert.Equal(new[] { "9.0.0" }, (await Engine.AuditQuery(t).QueryAsync("globex", 10, default)).Select(r => r.Version));
        Assert.Empty(await Engine.AuditQuery(t).QueryAsync("nobody", 10, default));
        Assert.Equal(new[] { "3.0.0", "2.0.0", "9.0.0", "1.0.0" }, (await Engine.AuditQuery(t).QueryAsync(null, 10, default)).Select(r => r.Version));
        // every column round-trips, the time as the same instant with offset 0
        Assert.Equal(Event(at, version: "9.0.0") with { TenantId = "globex" }, Assert.Single(await Engine.AuditQuery(t).QueryAsync("globex", 1, default)));
    }

    [EngineFact]
    [Trait(Needs, Triggers)]
    public async Task The_audit_table_refuses_updates_and_deletes()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        await Engine.Audit(t).AppendAsync(Event(DateTimeOffset.UtcNow), default);   // the delete floor (90 days, migration 19) uses the real time

        foreach (var sql in new[] { "UPDATE audit_events SET decision='Allow'", "DELETE FROM audit_events" })
        {
            var ex = await RefusedAsync(() => ExecAsync(t, sql));
            Assert.Contains("append-only", ex.Message);
        }
        Assert.Single(await Engine.AuditQuery(t).QueryAsync(null, 10, default));
    }
}
