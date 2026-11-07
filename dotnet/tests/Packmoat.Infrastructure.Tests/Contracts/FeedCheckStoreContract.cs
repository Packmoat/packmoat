using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The feed-check store (#63 H1) as a contract (#232 S1): a check's round trip, the latest check, refusals, shared_app_verifications
/// knowing how each record was made, and the 30-day redaction. Migrations 13 and 16 stay in <see cref="FeedCheckStoreTests"/>.
/// </summary>
public abstract class FeedCheckStoreContract : StoreContract
{
    protected const string Dir = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    protected const string Client = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";
    protected const string Feed = "https://pkgs.dev.azure.com/example-org/example-project/_packaging/f/nuget/v3/index.json";

    protected static FeedCheck Check(TestDb t, string tenant = "acme", FeedCheckOutcome outcome = FeedCheckOutcome.Passed, TimeSpan? after = null) =>
        new(tenant, t.Clock.UtcNow + (after ?? TimeSpan.Zero), Dir, "ffffffff-ffff-4fff-8fff-ffffffffffff", "Grace Hopper", Dir, Feed, Client, outcome,
            new[]
            {
                new CheckResult("feed", "The feed is the tenant's", CheckStatus.Pass, "the options match"),
                new CheckResult("permissions", "Only Packmoat holds a role", CheckStatus.Warn, "named admin \"Ada\"; confirm"),
                new CheckResult("developer", "Developers can't reach it", CheckStatus.Skip, "needs a developer credential"),
                new CheckResult("upstreams", "nuget.org only", CheckStatus.Fail, "remove 1 extra upstream source(s): https://evil.example/"),
            },
            new[] { "Ada Lovelace", "Grace \"G\" Hopper" });

    [Fact]
    public async Task Verified_via_takes_only_operator_or_wizard()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        const string insert = "INSERT INTO shared_app_verifications(tenant_id, azure_tenant_id, feed_index_url, client_id, verified_at, verified_by, verified_via) VALUES('acme','d','f','c','t','by',";
        await RefusedAsync(() => ExecAsync(t, insert + "'self')"));
        await RefusedAsync(() => ExecAsync(t, insert + "NULL)"));
        await ExecAsync(t, insert + "'wizard')");
        Assert.Equal("wizard", await ScalarAsync(t, "SELECT verified_via FROM shared_app_verifications"));
    }

    [Fact]
    public async Task A_verification_can_name_only_a_feed_check_that_exists()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        const string insert = "INSERT INTO shared_app_verifications(tenant_id, azure_tenant_id, feed_index_url, client_id, verified_at, verified_by, verified_via, feed_check_id) VALUES('acme','d','f','c','t','by','wizard',";
        await RefusedAsync(() => ExecAsync(t, insert + "4242)"));   // dangling
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM shared_app_verifications"));
        var id = await Engine.FeedChecks(t).RecordAsync(Check(t), default);
        await ExecAsync(t, insert + id + ")");
        Assert.Equal(id, await ScalarAsync(t, "SELECT feed_check_id FROM shared_app_verifications"));
        // and the check it names cannot then be deleted from under it
        await RefusedAsync(() => ExecAsync(t, "DELETE FROM feed_checks"));
    }

    [Fact]
    public async Task The_operator_check_records_its_verification_as_operator_made()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        await t.Tenants.BindSharedIdentityAsync("acme", Dir, default);
        var shared = Engine.SharedApp(t);
        await shared.SetAsync(SharedAppSlot.Active, new SharedAppCredential(Client, "PEM"), default);
        Assert.True(await shared.RecordVerificationAsync(new SharedAppVerification("acme", Dir, Feed, Client, t.Clock.UtcNow, "onboard check"), default));
        Assert.Equal("operator", await ScalarAsync(t, "SELECT verified_via FROM shared_app_verifications WHERE tenant_id='acme'"));
        Assert.Equal(DBNull.Value, await ScalarAsync(t, "SELECT feed_check_id FROM shared_app_verifications WHERE tenant_id='acme'"));
    }

    [Fact]
    public async Task A_check_round_trips_with_its_results_and_confirmed_admins()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        var store = Engine.FeedChecks(t);
        var check = Check(t);
        var id = await store.RecordAsync(check, default);
        Assert.True(id > 0);

        var read = await store.LatestAsync("acme", FeedCheckKind.Settings, default);
        Assert.NotNull(read);
        Assert.Equal(id, read.Id);
        Assert.Equal((check.TenantId, check.CheckedAt, check.CheckedByTid, check.CheckedByOid, check.CheckedByName),
            (read.TenantId, read.CheckedAt, read.CheckedByTid, read.CheckedByOid, read.CheckedByName));
        Assert.Equal((check.AzureTenantId, check.FeedIndexUrl, check.ClientId, check.Outcome), (read.AzureTenantId, read.FeedIndexUrl, read.ClientId, read.Outcome));
        Assert.Equal(check.Results, read.Results);
        Assert.Equal(check.ConfirmedAdmins, read.ConfirmedAdmins);
        Assert.Equal("passed", await ScalarAsync(t, "SELECT outcome FROM feed_checks"));
    }

    [Theory]
    [InlineData(FeedCheckOutcome.Passed, "passed")]
    [InlineData(FeedCheckOutcome.Failed, "failed")]
    [InlineData(FeedCheckOutcome.Incomplete, "incomplete")]
    public async Task Each_outcome_is_stored_as_its_word(FeedCheckOutcome outcome, string word)
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        var store = Engine.FeedChecks(t);
        await store.RecordAsync(Check(t, outcome: outcome), default);
        Assert.Equal(word, await ScalarAsync(t, "SELECT outcome FROM feed_checks"));
        Assert.Equal(outcome, (await store.LatestAsync("acme", FeedCheckKind.Settings, default))!.Outcome);
    }

    [Fact]
    public async Task Latest_is_the_newest_check_of_that_tenant_only()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        await t.AddTenantAsync("globex");
        var store = Engine.FeedChecks(t);
        Assert.Null(await store.LatestAsync("acme", FeedCheckKind.Settings, default));

        await store.RecordAsync(Check(t, outcome: FeedCheckOutcome.Failed), default);
        var newest = await store.RecordAsync(Check(t, outcome: FeedCheckOutcome.Incomplete, after: TimeSpan.FromMinutes(5)), default);
        await store.RecordAsync(Check(t, outcome: FeedCheckOutcome.Passed, after: TimeSpan.FromMinutes(1)), default);   // older, written later
        await store.RecordAsync(Check(t, "globex", FeedCheckOutcome.Passed, TimeSpan.FromHours(1)), default);

        var latest = await store.LatestAsync("acme", FeedCheckKind.Settings, default);
        Assert.Equal((newest, FeedCheckOutcome.Incomplete), (latest!.Id, latest.Outcome));
        Assert.Equal("globex", (await store.LatestAsync("globex", FeedCheckKind.Settings, default))!.TenantId);
        Assert.Null(await store.LatestAsync("initech", FeedCheckKind.Settings, default));
    }

    [Fact]
    public async Task Two_checks_at_the_same_moment_read_back_as_the_one_written_last()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        var store = Engine.FeedChecks(t);
        await store.RecordAsync(Check(t, outcome: FeedCheckOutcome.Failed), default);
        var second = await store.RecordAsync(Check(t, outcome: FeedCheckOutcome.Passed), default);
        Assert.Equal(second, (await store.LatestAsync("acme", FeedCheckKind.Settings, default))!.Id);
    }

    [Fact]
    public async Task A_check_for_a_tenant_that_does_not_exist_is_refused_and_nothing_is_written()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.FeedChecks(t);
        await RefusedAsync(() => store.RecordAsync(Check(t, "ghost"), default));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM feed_checks"));
    }

    [Fact]
    public async Task The_table_refuses_an_unknown_outcome()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        await RefusedAsync(() => ExecAsync(t,
            "INSERT INTO feed_checks(tenant_id, checked_at, checked_by_tid, checked_by_oid, checked_by_name, azure_tenant_id, feed_index_url, client_id, outcome, results) " +
            "VALUES('acme','t','d','o','n','d','f','c','verified','[]')"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Engine.FeedChecks(t).RecordAsync(Check(t, outcome: (FeedCheckOutcome)42), default));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM feed_checks"));
    }

    [Fact]
    public async Task A_stored_status_this_build_does_not_know_reads_as_fail()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        await ExecAsync(t,
            "INSERT INTO feed_checks(tenant_id, checked_at, checked_by_tid, checked_by_oid, checked_by_name, azure_tenant_id, feed_index_url, client_id, outcome, results) " +
            "VALUES('acme','2026-10-10T12:00:00.0000000+00:00','d','o','n','d','f','c','passed','[{\"id\":\"feed\",\"title\":\"T\",\"status\":\"glorious\",\"detail\":\"x\"}]')");
        var read = await Engine.FeedChecks(t).LatestAsync("acme", FeedCheckKind.Settings, default);
        Assert.Equal(CheckStatus.Fail, Assert.Single(read!.Results).Status);
        Assert.Empty(read.ConfirmedAdmins);   // the column's default
    }

    /// <summary>#119 item 9: a result's feed names (the bypass advisory's) are stored, and a row written before them reads with none.</summary>
    [Fact]
    public async Task A_results_names_are_stored_and_an_older_row_reads_without_them()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        var store = Engine.FeedChecks(t);
        var check = Check(t) with { Results = new[] { new CheckResult("bypass", "B", CheckStatus.Warn, "d", new[] { "private", "tools" }), new CheckResult("feed", "F", CheckStatus.Pass, "ok") } };
        await store.RecordAsync(check, default);
        var read = (await store.LatestAsync("acme", FeedCheckKind.Settings, default))!;
        Assert.Equal(new[] { "private", "tools" }, read.Results[0].Names);
        Assert.Null(read.Results[1].Names);

        await ExecAsync(t, "UPDATE feed_checks SET results = '[{\"id\":\"bypass\",\"title\":\"B\",\"status\":\"warn\",\"detail\":\"d\"}]'");
        Assert.Null(Assert.Single((await store.LatestAsync("acme", FeedCheckKind.Settings, default))!.Results).Names);
    }

    // ---------------------------------------------------------------- migration 16 (#119 item 6)

    [Fact]
    public async Task Feed_names_round_trip_and_a_row_without_names_or_with_bad_ones_reads_none()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        var store = Engine.FeedChecks(t);
        var names = new FeedNames("Fabrikam", "platform", "public-packages");
        var id = await store.RecordAsync(Check(t), default);
        Assert.Equal(DBNull.Value, await ScalarAsync(t, $"SELECT feed_names FROM feed_checks WHERE id={id}"));   // no names: NULL, not "null"
        Assert.Null((await store.LatestAsync("acme", FeedCheckKind.Settings, default))!.Names);

        await store.RecordAsync(Check(t, after: TimeSpan.FromMinutes(1)) with { Names = names }, default);
        Assert.Equal(names, (await store.LatestAsync("acme", FeedCheckKind.Settings, default))!.Names);
        await store.RecordAsync(Check(t, after: TimeSpan.FromMinutes(2)) with { Names = new FeedNames("contoso", null, "f") }, default);
        Assert.Equal(new FeedNames("contoso", null, "f"), (await store.LatestAsync("acme", FeedCheckKind.Settings, default))!.Names);

        foreach (var bad in new[] { "not json", "{\"organization\":\"has space\",\"feed\":\"f\"}", "{\"organization\":\"contoso\"}", "null" })
        {
            await ExecAsync(t, $"UPDATE feed_checks SET feed_names = '{bad.Replace("'", "''")}'");
            Assert.Null((await store.LatestAsync("acme", FeedCheckKind.Settings, default))!.Names);
        }
    }

    /// <summary>
    /// #107: a row whose confirmed admins an older build already blanked still has its name and details removed, and a row whose results
    /// cannot be read has them emptied (fail closed).
    /// </summary>
    [Fact]
    public async Task A_row_blanked_by_an_older_build_or_with_unreadable_results_is_still_redacted()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        var store = Engine.FeedChecks(t);
        var blankedBefore = await store.RecordAsync(Check(t, after: TimeSpan.FromDays(-40)) with { ConfirmedAdmins = Array.Empty<string>() }, default);
        var oddShapes = new[]
        {
            "not json, Grace Hopper", "[null]", "[{\"id\":\"feed\",\"title\":\"F\",\"status\":\"pass\",\"detail\":\"Grace Hopper\"},null]",
            "5", "[1,2]", "{\"detail\":\"Grace Hopper\"}", "\"Grace Hopper\"", "null", "[[\"Grace Hopper\"]]",
        };
        var unreadable = new List<long>();
        foreach (var shape in oddShapes)
        {
            var id = await store.RecordAsync(Check(t, after: TimeSpan.FromDays(-35)), default);
            await ExecAsync(t, $"UPDATE feed_checks SET results='{shape.Replace("'", "''")}' WHERE id={id}");
            unreadable.Add(id);
        }
        // Blanked by hand except one result that still carries the old feed names (a key the redaction drops) and one still detailed.
        var nearlyRedacted = await store.RecordAsync(Check(t, after: TimeSpan.FromDays(-33)) with { CheckedByName = "", ConfirmedAdmins = Array.Empty<string>() }, default);
        await ExecAsync(t, $"UPDATE feed_checks SET results='[{{\"id\":\"bypass\",\"title\":\"B\",\"status\":\"warn\",\"detail\":\"{FeedCheckRedaction.RemovedDetail}\",\"names\":[\"private\"]}}]' WHERE id={nearlyRedacted}");
        var oneDetail = await store.RecordAsync(Check(t, after: TimeSpan.FromDays(-32)) with { CheckedByName = "", ConfirmedAdmins = Array.Empty<string>() }, default);

        Assert.Equal(3 + oddShapes.Length, await store.BlankPersonalDataAsync(t.Clock.UtcNow - TimeSpan.FromDays(30), default));

        Assert.Equal("", await ScalarAsync(t, $"SELECT checked_by_name FROM feed_checks WHERE id={blankedBefore}"));
        Assert.DoesNotContain("Ada", (string)(await ScalarAsync(t, $"SELECT results FROM feed_checks WHERE id={blankedBefore}"))!);
        foreach (var id in unreadable) Assert.Equal("[]", await ScalarAsync(t, $"SELECT results FROM feed_checks WHERE id={id}"));
        Assert.DoesNotContain("private", (string)(await ScalarAsync(t, $"SELECT results FROM feed_checks WHERE id={nearlyRedacted}"))!);
        Assert.DoesNotContain("Ada", (string)(await ScalarAsync(t, $"SELECT results FROM feed_checks WHERE id={oneDetail}"))!);
        Assert.Equal(0, await store.BlankPersonalDataAsync(t.Clock.UtcNow - TimeSpan.FromDays(30), default));
    }

    /// <summary>
    /// #155 re-review: an old row that is fully redacted except for exactly ONE thing is still selected and changed, whichever thing it
    /// is, so no part of the SQL pre-filter can be dropped. "none" is the control: a fully redacted row is left alone.
    /// </summary>
    [Theory]
    [InlineData("none", null)]
    [InlineData("checked_by_name", "'Grace Hopper'")]
    [InlineData("confirmed_admins", "'[\"Grace Hopper\"]'")]
    [InlineData("results", "'not json, Grace Hopper'")]
    [InlineData("results", "'[[\"Grace Hopper\"]]'")]
    [InlineData("results", "'[\"Grace Hopper\"]'")]
    [InlineData("results", "'[{\"id\":\"feed\",\"title\":\"F\",\"status\":\"pass\",\"detail\":\"Details removed after 30 days.\",\"detail\":\"Grace Hopper\"}]'")]   // duplicate key
    public async Task An_old_row_holding_only_one_thing_to_remove_is_still_redacted(string column, string? value)
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync("acme");
        var store = Engine.FeedChecks(t);
        var clean = Check(t, after: TimeSpan.FromDays(-31)) with { CheckedByName = "", ConfirmedAdmins = Array.Empty<string>() };
        var id = await store.RecordAsync(clean with { Results = FeedCheckRedaction.Redact(clean.Results) }, default);
        if (value is not null) await ExecAsync(t, $"UPDATE feed_checks SET {column}={value} WHERE id={id}");
        const string all = "checked_by_name || '|' || confirmed_admins || '|' || results";
        if (value is not null) Assert.Contains("Grace", (string)(await ScalarAsync(t, $"SELECT {all} FROM feed_checks WHERE id={id}"))!);

        Assert.Equal(value is null ? 0 : 1, await store.BlankPersonalDataAsync(t.Clock.UtcNow - TimeSpan.FromDays(30), default));

        Assert.DoesNotContain("Grace", (string)(await ScalarAsync(t, $"SELECT {all} FROM feed_checks WHERE id={id}"))!);
        Assert.Equal(0, await store.BlankPersonalDataAsync(t.Clock.UtcNow - TimeSpan.FromDays(30), default));
    }
}
