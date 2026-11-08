using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The decision record's read port (#123 PR 2) as a contract (#232 S1): one tenant, newest last seen first, bounded, and an exact or
/// prefix package match that is never a pattern. The query plans (indexes, never a table scan) are SQLite's own and stay in
/// <see cref="DecisionRecordQueryTests"/>.
/// </summary>
public abstract class DecisionRecordQueryContract : StoreContract
{
    protected static readonly CancellationToken Ct = CancellationToken.None;
    protected static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    protected static DecisionRecord Rec(string package, string version = "1.0.0", string tenant = "acme", int minutes = 0,
        Decision decision = Decision.Hide, VerdictReason reason = VerdictReason.Cooldown, long count = 1) =>
        new(tenant, Ecosystem.NuGet, package, version, decision, reason, null, 14, T0.AddDays(-3), 2, T0, T0.AddMinutes(minutes), count);

    protected async Task<(IEngineDb Db, IDecisionRecordQuery Query)> SeedAsync(params DecisionRecord[] rows)
    {
        var t = await CreateDbAsync();
        await Engine.DecisionRecords(t).UpsertAsync(rows, Ct);
        return (t, Engine.DecisionQuery(t));
    }

    protected static string[] Keys(DecisionRecordPage page) => page.Rows.Select(r => r.PackageId + "@" + r.Version).ToArray();

    [Fact]
    public async Task Rows_come_newest_last_seen_first_one_tenant_only_and_bounded_by_the_limit()
    {
        var (t, q) = await SeedAsync(Rec("old", minutes: 1), Rec("newest", minutes: 30), Rec("middle", minutes: 10), Rec("theirs", tenant: "other", minutes: 99));
        await using var _ = t;

        var all = await q.QueryAsync(new DecisionRecordQuery("acme"), Ct);
        Assert.Equal(new[] { "newest@1.0.0", "middle@1.0.0", "old@1.0.0" }, Keys(all));
        Assert.False(all.More);

        var two = await q.QueryAsync(new DecisionRecordQuery("acme", Limit: 2), Ct);
        Assert.Equal(new[] { "newest@1.0.0", "middle@1.0.0" }, Keys(two));
        Assert.True(two.More);

        var exactlyThree = await q.QueryAsync(new DecisionRecordQuery("acme", Limit: 3), Ct);
        Assert.Equal(3, exactlyThree.Rows.Count);
        Assert.False(exactlyThree.More);

        Assert.Empty((await q.QueryAsync(new DecisionRecordQuery("nobody"), Ct)).Rows);
    }

    [Fact]
    public async Task Every_field_is_read_back()
    {
        var row = new DecisionRecord("acme", Ecosystem.NuGet, "moq", "4.21.0", Decision.Allow, VerdictReason.Waiver, "w-1", null, null, 7, T0, T0.AddHours(1), 12);
        var (t, q) = await SeedAsync(row);
        await using var _ = t;
        Assert.Equal(row, Assert.Single((await q.QueryAsync(new DecisionRecordQuery("acme"), Ct)).Rows));
    }

    [Fact]
    public async Task An_exact_package_match_is_exact_and_a_version_narrows_it()
    {
        var (t, q) = await SeedAsync(Rec("moq"), Rec("moq", "4.0.0"), Rec("moq.extensions"), Rec("xmoq"));
        await using var _ = t;

        Assert.Equal(new[] { "moq@1.0.0", "moq@4.0.0" }, Keys(await q.QueryAsync(new DecisionRecordQuery("acme", "moq"), Ct)).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "moq@4.0.0" }, Keys(await q.QueryAsync(new DecisionRecordQuery("acme", "moq", Version: "4.0.0"), Ct)));
        Assert.Empty((await q.QueryAsync(new DecisionRecordQuery("acme", "mo"), Ct)).Rows);
    }

    [Fact]
    public async Task A_prefix_match_takes_every_id_that_starts_with_it()
    {
        // Ids just past the prefix's range ("mor", "mp") are not taken; a non-ASCII id inside it (the upper bound is U+10FFFF) is.
        var (t, q) = await SeedAsync(Rec("moq"), Rec("moq.extensions"), Rec("moqé"), Rec("xmoq"), Rec("mo"), Rec("mor"), Rec("mp"), Rec("moq", tenant: "other"));
        await using var _ = t;
        Assert.Equal(new[] { "moq.extensions@1.0.0", "moq@1.0.0", "moqé@1.0.0" },
            Keys(await q.QueryAsync(new DecisionRecordQuery("acme", "moq", PackagePrefix: true), Ct)).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("a_")]
    [InlineData("a%")]
    [InlineData("\\")]
    [InlineData("' or 1=1 --")]
    public async Task Pattern_characters_and_quotes_are_ordinary_text_in_both_matches(string search)
    {
        var (t, q) = await SeedAsync(Rec("abc"), Rec("axb"), Rec("zzz"));
        await using var _ = t;
        Assert.Empty((await q.QueryAsync(new DecisionRecordQuery("acme", search, PackagePrefix: true), Ct)).Rows);
        Assert.Empty((await q.QueryAsync(new DecisionRecordQuery("acme", search), Ct)).Rows);
    }

    [Fact]
    public async Task Ids_that_really_hold_pattern_characters_are_found_literally()
    {
        var (t, q) = await SeedAsync(Rec("a_b"), Rec("axb"), Rec("a%c"), Rec("abc"), Rec("a\\d"));
        await using var _ = t;
        Assert.Equal(new[] { "a_b@1.0.0" }, Keys(await q.QueryAsync(new DecisionRecordQuery("acme", "a_", PackagePrefix: true), Ct)));
        Assert.Equal(new[] { "a%c@1.0.0" }, Keys(await q.QueryAsync(new DecisionRecordQuery("acme", "a%", PackagePrefix: true), Ct)));
        Assert.Equal(new[] { "a\\d@1.0.0" }, Keys(await q.QueryAsync(new DecisionRecordQuery("acme", "a\\", PackagePrefix: true), Ct)));
        Assert.Equal(new[] { "a_b@1.0.0" }, Keys(await q.QueryAsync(new DecisionRecordQuery("acme", "a_b"), Ct)));
    }

    [Fact]
    public async Task A_prefix_match_is_case_sensitive_on_the_stored_lower_case_ids()
    {
        // The store compares exactly; AdminQueries lower-cases what the operator types, as the recorder does.
        var (t, q) = await SeedAsync(Rec("moq"));
        await using var _ = t;
        Assert.Empty((await q.QueryAsync(new DecisionRecordQuery("acme", "MOQ", PackagePrefix: true), Ct)).Rows);
        Assert.Empty((await q.QueryAsync(new DecisionRecordQuery("acme", "MOQ"), Ct)).Rows);
    }
}
