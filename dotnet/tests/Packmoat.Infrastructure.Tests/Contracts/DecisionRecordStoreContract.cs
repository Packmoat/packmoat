using System.Globalization;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The per-version decision record (#123) as a contract (#232 S1): the upsert (first seen kept, last seen the latest, counts added), the
/// 365-day prune and a late write for a purged tenant. Migration 17 and the adapter's chunking stay in <see cref="DecisionRecordStoreTests"/>.
/// </summary>
public abstract class DecisionRecordStoreContract : StoreContract
{
    protected static readonly CancellationToken Ct = CancellationToken.None;
    protected static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    protected static DecisionRecord Rec(string tenant = "acme", string package = "moq", string version = "4.21.0", Decision decision = Decision.Hide,
        VerdictReason reason = VerdictReason.Cooldown, DateTimeOffset? first = null, DateTimeOffset? last = null, long count = 1,
        int? cooldown = 14, DateTimeOffset? published = null, long revision = 3, string? waiver = null) =>
        new(tenant, Ecosystem.NuGet, package, version, decision, reason, waiver, cooldown, published, revision, first ?? T0, last ?? first ?? T0, count);

    /// <summary>All of one tenant's rows (the tests write far fewer than the query's maximum), through the read port.</summary>
    protected async Task<IReadOnlyList<DecisionRecord>> ListAsync(TestDb t, string tenant)
    {
        var page = await Engine.DecisionQuery(t).QueryAsync(new DecisionRecordQuery(tenant, Limit: DecisionRecordQuery.MaxLimit), Ct);
        Assert.False(page.More);
        return page.Rows;
    }

    // ---------------------------------------------------------------- the upsert

    [Fact]
    public async Task Upsert_keeps_the_earliest_first_seen_takes_the_latest_last_seen_and_adds_the_counts()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        var store = Engine.DecisionRecords(t);

        await store.UpsertAsync(new[] { Rec(first: T0, last: T0.AddMinutes(5), count: 3, published: T0.AddDays(-2)) }, Ct);
        // A later batch: a later first seen (must not replace the earlier), a later last seen, new policy facts.
        await store.UpsertAsync(new[] { Rec(first: T0.AddHours(1), last: T0.AddHours(2), count: 4, cooldown: 7, revision: 4, published: T0.AddDays(-2)) }, Ct);
        // A batch that arrives late with older times: it adds its count and can only move first seen earlier, never last seen or the facts.
        await store.UpsertAsync(new[] { Rec(first: T0.AddMinutes(-10), last: T0.AddMinutes(-9), count: 2, cooldown: 30, revision: 1) }, Ct);

        var row = Assert.Single(await ListAsync(t, "acme"));
        Assert.Equal((T0.AddMinutes(-10), T0.AddHours(2), 9L), (row.FirstSeenAt, row.LastSeenAt, row.SeenCount));
        Assert.Equal(((int?)7, 4L, (DateTimeOffset?)T0.AddDays(-2)), (row.CooldownDays, row.PolicyRevision, row.PublishedAt));
        Assert.Equal(("moq", "4.21.0", Decision.Hide, VerdictReason.Cooldown, Ecosystem.NuGet), (row.PackageId, row.Version, row.Decision, row.Reason, row.Ecosystem));
    }

    [Fact]
    public async Task A_changed_decision_for_the_same_version_adds_a_row_so_the_history_is_kept()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        var store = Engine.DecisionRecords(t);

        await store.UpsertAsync(new[] { Rec(first: T0) }, Ct);
        await store.UpsertAsync(new[] { Rec(decision: Decision.Allow, reason: VerdictReason.CooldownElapsed, first: T0.AddDays(14)) }, Ct);
        await store.UpsertAsync(new[] { Rec(decision: Decision.Allow, reason: VerdictReason.Waiver, waiver: "w-1", cooldown: null, first: T0.AddDays(1)) }, Ct);

        var rows = await ListAsync(t, "acme");
        Assert.Equal(3, rows.Count);
        Assert.Equal("w-1", rows.Single(r => r.Reason == VerdictReason.Waiver).WaiverId);
        Assert.Null(rows.Single(r => r.Reason == VerdictReason.Waiver).CooldownDays);
        Assert.Null(rows.Single(r => r.Reason == VerdictReason.Cooldown).PublishedAt);
    }

    // ---------------------------------------------------------------- retention

    [Fact]
    public async Task Prune_deletes_rows_last_seen_before_the_cutoff_only()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        await t.AddTenantAsync("other");
        var store = Engine.DecisionRecords(t);
        var now = T0;
        var cutoff = now - DecisionRecordPolicy.Retention;
        await store.UpsertAsync(new[]
        {
            Rec(version: "1.0.0", first: cutoff.AddDays(-30), last: cutoff.AddSeconds(-1)),    // gone
            Rec(version: "2.0.0", first: cutoff.AddDays(-30), last: cutoff),                   // exactly at the cutoff: kept
            Rec(version: "3.0.0", first: cutoff.AddDays(-30), last: now),                      // first seen long ago, used today: kept
            Rec(tenant: "other", version: "1.0.0", first: cutoff.AddDays(-2), last: cutoff.AddDays(-1)),   // gone
        }, Ct);

        Assert.Equal(2, await store.PruneAsync(cutoff, Ct));
        Assert.Equal(new[] { "2.0.0", "3.0.0" }, (await ListAsync(t, "acme")).Select(r => r.Version).Order(StringComparer.Ordinal));
        Assert.Empty(await ListAsync(t, "other"));
        Assert.Equal(0, await store.PruneAsync(cutoff, Ct));
    }

    [Fact]
    public async Task A_late_write_for_a_purged_tenant_is_dropped_and_a_live_tenants_is_kept()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        var store = Engine.DecisionRecords(t);
        // As the purge leaves it: no tenant row, a closed purge record.
        await ExecAsync(t, "INSERT INTO tenant_purges(tenant_id, purged_at, reason, actor, purging) VALUES('gone', '2026-10-01T00:00:00.0000000+00:00', 'ended', 'olivia', 0)");

        await store.UpsertAsync(new[] { Rec(tenant: "gone"), Rec(tenant: "acme") }, Ct);

        Assert.Empty(await ListAsync(t, "gone"));
        Assert.Single(await ListAsync(t, "acme"));
    }

    [Fact]
    public async Task Times_are_stored_as_sortable_utc_text()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        var store = Engine.DecisionRecords(t);
        var local = new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.FromHours(2));   // the same instant as T0
        await store.UpsertAsync(new[] { Rec(first: local, last: local, published: local) }, Ct);

        Assert.Equal(T0.ToString("O", CultureInfo.InvariantCulture), await ScalarAsync(t, "SELECT last_seen_at FROM decision_records"));
        Assert.Equal(T0.ToString("O", CultureInfo.InvariantCulture), await ScalarAsync(t, "SELECT published_at FROM decision_records"));
    }
}
