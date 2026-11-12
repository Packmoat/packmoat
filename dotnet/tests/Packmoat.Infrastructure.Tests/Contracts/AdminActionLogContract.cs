using Packmoat.Application;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>The admin action log's contract: rows round trip newest first, and the latest-by lookup.</summary>
public abstract class AdminActionLogContract : StoreContract
{
    protected static AdminActionRecord Row(int i, string action = "prefix.add") =>
        new(new DateTimeOffset(2026, 10, 10, 12, 0, i % 60, TimeSpan.Zero), "tid", "oid", "Ada", action, "acme", "detail-" + i);

    [EngineFact]
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

    [EngineFact]   // mutation: the lookup ignores the person, the tenant or the action
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

    // ---------------------------------------------------------------- the retention prune

    /// <summary>The real time cut to whole seconds: the trigger's floor reads the database's own clock, and both engines store a second exactly.</summary>
    private static DateTimeOffset WholeSecondNow()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

    private static AdminActionRecord Dated(DateTimeOffset time, string detail) => new(time, "tid", "oid", "Ada", "prefix.add", "acme", detail);

    [EngineFact]   // mutations: "time <= cutoff" (a row at the cutoff goes); no record row, or one when nothing was deleted; the wrong count
    public async Task The_prune_deletes_only_rows_before_the_cutoff_and_records_itself_once()
    {
        await using var db = await CreateDbAsync();
        var log = Engine.AdminActions(db);
        var now = WholeSecondNow();
        var before = now.AddDays(-400);   // every row before it is far past the 365-day floor, whatever either clock says
        await log.AppendAsync(Dated(before.AddDays(-30), "old"), default);
        await log.AppendAsync(Dated(before.AddSeconds(-1), "a second before the cutoff"), default);
        await log.AppendAsync(Dated(before, "at the cutoff"), default);
        await log.AppendAsync(Dated(now.AddDays(-10), "young"), default);

        Assert.Equal(2, await log.PruneAsync(before, now, default));

        var rows = await log.LatestAsync(10, default);
        Assert.Equal([$"rows=2; before={before.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}", "young", "at the cutoff"], rows.Select(r => r.Detail));
        var record = rows[0];
        Assert.Equal((now, "", "", "retention", IAdminActionLog.PruneAction, "", "server"),
            (record.Time, record.ActorTid, record.ActorOid, record.ActorName, record.Action, record.TenantId, record.Site));
        Assert.Equal(before, rows[2].Time);

        Assert.Equal(0, await log.PruneAsync(before, now.AddMinutes(1), default));   // nothing left to delete: no second record row
        Assert.Equal(3, (await log.LatestAsync(10, default)).Count);
    }

    [EngineFact]   // mutation: catch the trigger's refusal and carry on (return 0, or commit what is left)
    [Trait(Needs, Triggers)]
    public async Task A_prune_the_floor_refuses_throws_and_deletes_nothing()
    {
        await using var db = await CreateDbAsync();
        var log = Engine.AdminActions(db);
        var now = WholeSecondNow();
        await log.AppendAsync(Dated(now.AddDays(-400), "old"), default);
        await log.AppendAsync(Dated(now.AddDays(-10), "young"), default);

        // A cutoff of now (the app's clock running a year ahead): the young row is under the floor, so the database refuses the whole delete.
        await RefusedAsync(() => log.PruneAsync(now, now, default));

        Assert.Equal(["young", "old"], (await log.LatestAsync(10, default)).Select(r => r.Detail));   // the old row too, and no record row
        Assert.Equal(1, await log.PruneAsync(now.AddDays(-365).AddDays(-1), now, default));       // the old row alone still goes
    }
}
