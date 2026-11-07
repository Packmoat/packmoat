using Packmoat.Application;

namespace Packmoat.Infrastructure.Tests;

/// <summary>The admin action log's contract (#232 S1): rows round trip newest first, and the latest-by lookup.</summary>
public abstract class AdminActionLogContract : StoreContract
{
    protected static AdminActionRecord Row(int i, string action = "prefix.add") =>
        new(new DateTimeOffset(2026, 10, 10, 12, 0, i % 60, TimeSpan.Zero), "tid", "oid", "Ada", action, "acme", "detail-" + i);

    [Fact]
    public async Task Rows_round_trip_newest_first_and_the_limit_applies()
    {
        await using var db = await CreateDbAsync();
        var log = Engine.AdminActions(db);
        for (var i = 0; i < 5; i++) await log.AppendAsync(Row(i), default);

        var latest = await log.LatestAsync(3, default);
        Assert.Equal(new[] { "detail-4", "detail-3", "detail-2" }, latest.Select(r => r.Detail));
        var first = latest[0];
        Assert.Equal(("tid", "oid", "Ada", "prefix.add", "acme"), (first.ActorTid, first.ActorOid, first.ActorName, first.Action, first.TenantId));
        Assert.Equal(Row(4).Time, first.Time);
    }

    [Fact]   // #119 item 1. mutation: the lookup ignores the person, the tenant or the action
    public async Task Latest_by_finds_only_this_persons_newest_row_of_this_action_for_this_tenant()
    {
        await using var db = await CreateDbAsync();
        var log = Engine.AdminActions(db);
        AdminActionRecord R(string tid, string oid, string action, string tenant, string detail) =>
            new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero), tid, oid, "Ada", action, tenant, detail, "app");
        await log.AppendAsync(R("tid", "oid", "tenant.self-signup", "acme", "first"), default);
        await log.AppendAsync(R("tid", "oid", "tenant.self-signup", "acme", "mine"), default);
        await log.AppendAsync(R("tid", "other", "tenant.self-signup", "acme", "another person"), default);
        await log.AppendAsync(R("tid2", "oid", "tenant.self-signup", "acme", "another directory"), default);
        await log.AppendAsync(R("tid", "oid", "tenant.self-signup", "globex", "another tenant"), default);
        await log.AppendAsync(R("tid", "oid", "module.something-else", "acme", "another action"), default);

        var row = await log.LatestByAsync("acme", "tenant.self-signup", "tid", "oid", default);
        Assert.Equal(("mine", "tid", "oid", "app"), (row!.Detail, row.ActorTid, row.ActorOid, row.Site));
        Assert.Null(await log.LatestByAsync("acme", "tenant.self-signup", "tid", "nobody", default));
        Assert.Null(await log.LatestByAsync("initech", "tenant.self-signup", "tid", "oid", default));
        Assert.Null(await log.LatestByAsync("acme", "tenant.purged", "tid", "oid", default));
    }
}
