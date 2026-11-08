using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

/// <summary>The audit log's contract (#232 S1): events read back newest first, and the table refuses updates and deletes.</summary>
public abstract class AuditContract : StoreContract
{
    protected static AuditEvent Event(DateTimeOffset at, string pkg = "moq", string version = "4.21.0", AuditKind kind = AuditKind.Download, Decision d = Decision.Hide) =>
        new(at, "req-1", "acme", "tok_1", kind, pkg, version, d, d == Decision.Hide ? VerdictReason.Cooldown : VerdictReason.CooldownElapsed, null);

    [Fact]
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

    [Fact]
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
