using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The decision record's read port as a contract: one tenant, newest last seen first, bounded, and an exact or
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

    [EngineFact]
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

    [EngineFact]
    public async Task Every_field_is_read_back()
    {
        var row = new DecisionRecord("acme", Ecosystem.NuGet, "moq", "4.21.0", Decision.Allow, VerdictReason.Waiver, "w-1", null, null, 7, T0, T0.AddHours(1), 12);
        var (t, q) = await SeedAsync(row);
        await using var _ = t;
        Assert.Equal(row, Assert.Single((await q.QueryAsync(new DecisionRecordQuery("acme"), Ct)).Rows));
    }

    [EngineFact]
    public async Task An_exact_package_match_is_exact_and_a_version_narrows_it()
    {
        var (t, q) = await SeedAsync(Rec("moq"), Rec("moq", "4.0.0"), Rec("moq.extensions"), Rec("xmoq"));
        await using var _ = t;

        Assert.Equal(new[] { "moq@1.0.0", "moq@4.0.0" }, Keys(await q.QueryAsync(new DecisionRecordQuery("acme", "moq"), Ct)).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "moq@4.0.0" }, Keys(await q.QueryAsync(new DecisionRecordQuery("acme", "moq", Version: "4.0.0"), Ct)));
        Assert.Empty((await q.QueryAsync(new DecisionRecordQuery("acme", "mo"), Ct)).Rows);
    }

    /// <summary>
    /// The prefix's upper bound is U+10FFFF, beyond the Basic Multilingual Plane. An id whose next character is an
    /// emoji (four bytes in UTF-8, from F0) is inside the range, as is one whose next character is U+FFFF itself; a bound of U+FFFF (EF BF
    /// BF) would stop short of both. A prefix that ends in an emoji takes exactly the ids that start with it.
    /// </summary>
    [EngineFact]
    public async Task A_prefix_match_takes_ids_with_characters_beyond_the_basic_multilingual_plane()
    {
        var (t, q) = await SeedAsync(Rec("moq"), Rec("moq😀"), Rec("moq😀x"), Rec("moq😁"), Rec("moq\uFFFF"), Rec("mor"));
        await using var _ = t;
        Assert.Equal(new[] { "moq@1.0.0", "moq😀@1.0.0", "moq😀x@1.0.0", "moq😁@1.0.0", "moq\uFFFF@1.0.0" },
            Keys(await q.QueryAsync(new DecisionRecordQuery("acme", "moq", PackagePrefix: true), Ct)).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "moq😀@1.0.0", "moq😀x@1.0.0" },
            Keys(await q.QueryAsync(new DecisionRecordQuery("acme", "moq😀", PackagePrefix: true), Ct)).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Rows with the same last seen come in one order, by package, version, decision and reason (their stored text,
    /// ordinal). Each is written as a batch of its own, in an order that is neither that order nor its reverse, so neither the order written
    /// nor an index walked backwards gives it by chance. The prefix search orders them the same way.
    /// </summary>
    [EngineFact]
    public async Task Rows_with_the_same_last_seen_come_by_package_version_decision_and_reason()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.DecisionRecords(t);
        var e1 = Rec("a", "1.0.0", decision: Decision.Allow, reason: VerdictReason.CooldownElapsed);
        var e2 = Rec("a", "1.0.0", decision: Decision.Allow, reason: VerdictReason.Waiver);
        var e3 = Rec("a", "1.0.0", decision: Decision.Hide, reason: VerdictReason.Cooldown);
        var e4 = Rec("a", "2.0.0", decision: Decision.Allow, reason: VerdictReason.Waiver);
        var e5 = Rec("b", "1.0.0", decision: Decision.Allow, reason: VerdictReason.Waiver);
        foreach (var e in new[] { e3, e5, e1, e4, e2 }) await store.UpsertAsync(new[] { e }, Ct);
        static (string, string, Decision, VerdictReason)[] Order(IEnumerable<DecisionRecord> rows) =>
            rows.Select(r => (r.PackageId, r.Version, r.Decision, r.Reason)).ToArray();

        Assert.Equal(Order([e1, e2, e3, e4, e5]), Order((await Engine.DecisionQuery(t).QueryAsync(new DecisionRecordQuery("acme"), Ct)).Rows));
        Assert.Equal(Order([e1, e2, e3, e4]), Order((await Engine.DecisionQuery(t).QueryAsync(new DecisionRecordQuery("acme", "a", PackagePrefix: true), Ct)).Rows));
    }

    [EngineFact]
    public async Task A_prefix_match_takes_every_id_that_starts_with_it()
    {
        // Ids just past the prefix's range ("mor", "mp") are not taken; a non-ASCII id inside it (the upper bound is U+10FFFF) is.
        var (t, q) = await SeedAsync(Rec("moq"), Rec("moq.extensions"), Rec("moqé"), Rec("xmoq"), Rec("mo"), Rec("mor"), Rec("mp"), Rec("moq", tenant: "other"));
        await using var _ = t;
        Assert.Equal(new[] { "moq.extensions@1.0.0", "moq@1.0.0", "moqé@1.0.0" },
            Keys(await q.QueryAsync(new DecisionRecordQuery("acme", "moq", PackagePrefix: true), Ct)).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Was a [Theory] over the same six searches, one database each. A contract test is an [EngineFact] (a PostgreSQL fixture
    /// skips it without a server, as it does a [PostgresFact]), so the searches are a loop over one database; the rows are only read.
    /// </summary>
    [EngineFact]
    public async Task Pattern_characters_and_quotes_are_ordinary_text_in_both_matches()
    {
        var (t, q) = await SeedAsync(Rec("abc"), Rec("axb"), Rec("zzz"));
        await using var _ = t;
        foreach (var search in new[] { "%", "_", "a_", "a%", "\\", "' or 1=1 --" })
        {
            Assert.True((await q.QueryAsync(new DecisionRecordQuery("acme", search, PackagePrefix: true), Ct)).Rows.Count == 0, $"the prefix {search} matched");
            Assert.True((await q.QueryAsync(new DecisionRecordQuery("acme", search), Ct)).Rows.Count == 0, $"the id {search} matched");
        }
    }

    [EngineFact]
    public async Task Ids_that_really_hold_pattern_characters_are_found_literally()
    {
        var (t, q) = await SeedAsync(Rec("a_b"), Rec("axb"), Rec("a%c"), Rec("abc"), Rec("a\\d"));
        await using var _ = t;
        Assert.Equal(new[] { "a_b@1.0.0" }, Keys(await q.QueryAsync(new DecisionRecordQuery("acme", "a_", PackagePrefix: true), Ct)));
        Assert.Equal(new[] { "a%c@1.0.0" }, Keys(await q.QueryAsync(new DecisionRecordQuery("acme", "a%", PackagePrefix: true), Ct)));
        Assert.Equal(new[] { "a\\d@1.0.0" }, Keys(await q.QueryAsync(new DecisionRecordQuery("acme", "a\\", PackagePrefix: true), Ct)));
        Assert.Equal(new[] { "a_b@1.0.0" }, Keys(await q.QueryAsync(new DecisionRecordQuery("acme", "a_b"), Ct)));
    }

    /// <summary>
    /// One prefix holding all three of LIKE's special characters. Only the ids that begin with exactly those characters are
    /// taken: the prefix itself, and ids that go on after it (one going on with a second backslash). An id where any of the three would act
    /// as a wildcard or an escape is not, nor one a character short.
    /// </summary>
    [EngineFact]   // mutations: the prefix as an unescaped LIKE (with or without the range); substr or the range off by one
    public async Task A_prefix_search_takes_percent_underscore_and_backslash_as_plain_characters()
    {
        var (t, q) = await SeedAsync(Rec("50%_\\"), Rec("50%_\\off"), Rec("50%_\\\\off"), Rec("50%_"), Rec("50x_\\off"), Rec("50%x\\off"), Rec("50%_x"),
            Rec("50%_\\off", tenant: "other"));
        await using var _ = t;
        Assert.Equal(new[] { "50%_\\@1.0.0", "50%_\\\\off@1.0.0", "50%_\\off@1.0.0" },
            Keys(await q.QueryAsync(new DecisionRecordQuery("acme", "50%_\\", PackagePrefix: true), Ct)).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "50%_\\@1.0.0" }, Keys(await q.QueryAsync(new DecisionRecordQuery("acme", "50%_\\"), Ct)));
    }

    [EngineFact]
    public async Task A_prefix_match_is_case_sensitive_on_the_stored_lower_case_ids()
    {
        // The store compares exactly; AdminQueries lower-cases what the operator types, as the recorder does.
        var (t, q) = await SeedAsync(Rec("moq"));
        await using var _ = t;
        Assert.Empty((await q.QueryAsync(new DecisionRecordQuery("acme", "MOQ", PackagePrefix: true), Ct)).Rows);
        Assert.Empty((await q.QueryAsync(new DecisionRecordQuery("acme", "MOQ"), Ct)).Rows);
    }
}
