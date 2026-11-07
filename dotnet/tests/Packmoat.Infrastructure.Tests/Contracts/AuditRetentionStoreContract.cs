using System.Globalization;
using System.Text.RegularExpressions;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// #136 as a contract (#232 S1): batched audit writes, the 90-day floor in the database (migration 19), the audit prune and the
/// token_activity prune. The floor compares with the database's own clock, so rows here are dated from the real time and written by the
/// real writer, in the "O" format every build writes (7 fractional digits and an offset). Each test that kills one of the plan's mutations
/// says which. Tests that depend on SQLite's own time parsing, its query plans or the prune's chunk size stay in
/// <see cref="AuditRetentionStoreTests"/>.
/// </summary>
public abstract class AuditRetentionStoreContract : StoreContract
{
    protected static readonly CancellationToken Ct = CancellationToken.None;
    protected static readonly Regex OFormat = new(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{7}\+00:00$");
    protected static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    protected static AuditEvent Event(DateTimeOffset at, string request, string tenant = "acme") =>
        new(at, request, tenant, "tok_1", AuditKind.Download, "moq", "4.20.72", Decision.Allow, VerdictReason.CooldownElapsed, null);

    /// <summary>Writes through the real writer and checks the time text it stored is the real "O" format.</summary>
    protected async Task WriteAsync(TestDb t, DateTimeOffset at, string request, string tenant = "acme")
    {
        await Engine.Audit(t).AppendAsync(Event(at, request, tenant), Ct);
        var time = (string)(await ScalarAsync(t, $"SELECT time FROM audit_events WHERE request_id='{request}'"))!;
        Assert.Matches(OFormat, time);
    }

    protected Task<List<string>> RequestsAsync(TestDb t) => ColumnAsync(t, "SELECT request_id FROM audit_events ORDER BY id");

    // ---------------------------------------------------------------- batched writes

    [Fact]
    public async Task A_batch_is_stored_in_order_and_a_purged_tenants_events_are_dropped()
    {
        await using var t = await CreateDbAsync();
        await ExecAsync(t, "INSERT INTO tenant_purges(tenant_id, purged_at, reason, actor) VALUES('gone', 't', 'r', 'a')");
        var now = DateTimeOffset.UtcNow;

        await Engine.AuditBatch(t).AppendBatchAsync(new[] { Event(now, "a"), Event(now, "late", "gone"), Event(now, "b"), Event(now, "c") }, Ct);

        Assert.Equal(new[] { "a", "b", "c" }, await RequestsAsync(t));
        Assert.All(await ColumnAsync(t, "SELECT time FROM audit_events"), time => Assert.Matches(OFormat, time));
    }

    // ---------------------------------------------------------------- the trigger (migration 19)

    [Fact]   // mutations: the floor shortened or lengthened past 91 days (e.g. 900)
    public async Task A_row_89_days_old_cannot_be_deleted_and_one_91_days_old_can()
    {
        await using var t = await CreateDbAsync();
        var now = DateTimeOffset.UtcNow;
        await WriteAsync(t, now.AddDays(-89), "young");
        await WriteAsync(t, now.AddDays(-91), "old");

        var ex = await RefusedAsync(() => ExecAsync(t, "DELETE FROM audit_events WHERE request_id='young'"));
        Assert.Contains("append-only", ex.Message);
        await ExecAsync(t, "DELETE FROM audit_events WHERE request_id='old'");
        Assert.Equal(new[] { "young" }, await RequestsAsync(t));
    }

    [Fact]   // mutations: '-89 days'; 'start of day'; 'localtime'; ">" for ">=" moved the wrong way by a day
    public async Task The_floor_is_90_days_to_the_minute_by_the_databases_utc_clock()
    {
        await using var t = await CreateDbAsync();
        var floor = DateTimeOffset.UtcNow.AddDays(-90);
        await WriteAsync(t, floor.AddMinutes(1), "a minute young");
        await WriteAsync(t, floor.AddMinutes(-1), "a minute old");

        await RefusedAsync(() => ExecAsync(t, "DELETE FROM audit_events WHERE request_id='a minute young'"));
        await ExecAsync(t, "DELETE FROM audit_events WHERE request_id='a minute old'");
        Assert.Equal(new[] { "a minute young" }, await RequestsAsync(t));
    }

    [Fact]   // mutation: the purge branch of the trigger dropped (a purge could no longer delete a young row)
    public async Task A_running_purge_still_deletes_the_tenants_young_rows_and_only_those()
    {
        await using var t = await CreateDbAsync();
        var now = DateTimeOffset.UtcNow;
        await WriteAsync(t, now.AddDays(-1), "gone-1", "gone");
        await WriteAsync(t, now, "gone-2", "gone");
        await WriteAsync(t, now, "live", "acme");

        await ExecAsync(t, """
            BEGIN;
            INSERT INTO tenant_purges(tenant_id, purged_at, reason, actor, purging) VALUES('gone', 't', 'r', 'a', 1);
            DELETE FROM audit_events WHERE tenant_id='gone';
            UPDATE tenant_purges SET purging=0 WHERE tenant_id='gone';
            COMMIT;
            """);

        Assert.Equal(new[] { "live" }, await RequestsAsync(t));
        await RefusedAsync(() => ExecAsync(t, "DELETE FROM audit_events"));
    }

    [Fact]
    public async Task No_row_can_be_changed_however_old()
    {
        await using var t = await CreateDbAsync();
        await WriteAsync(t, DateTimeOffset.UtcNow.AddDays(-400), "old");
        var ex = await RefusedAsync(() => ExecAsync(t, "UPDATE audit_events SET reason='Waiver'"));
        Assert.Contains("append-only", ex.Message);
    }

    // ---------------------------------------------------------------- the audit prune

    [Fact]   // mutation: "<=" for "<" at the cutoff
    public async Task The_prune_deletes_only_rows_before_the_cutoff()
    {
        await using var t = await CreateDbAsync();
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-100);
        await WriteAsync(t, now.AddDays(-300), "oldest");
        await WriteAsync(t, cutoff.AddSeconds(-1), "just before");
        await WriteAsync(t, cutoff, "at the cutoff");
        await WriteAsync(t, now.AddDays(-95), "after the cutoff");
        await WriteAsync(t, now.AddDays(-1), "recent");

        Assert.Equal(2, await Engine.AuditRetention(t).PruneAsync(cutoff, Ct));

        Assert.Equal(new[] { "at the cutoff", "after the cutoff", "recent" }, await RequestsAsync(t));
    }

    [Fact]
    public async Task With_the_clock_ahead_the_database_refuses_and_keeps_the_young_rows()
    {
        await using var t = await CreateDbAsync();
        var real = DateTimeOffset.UtcNow;
        await WriteAsync(t, real.AddDays(-10), "recent");
        var aheadNow = real.AddDays(200);

        await RefusedAsync(() => Engine.AuditRetention(t).PruneAsync(aheadNow - AuditPolicy.Retention, Ct));

        Assert.Equal(new[] { "recent" }, await RequestsAsync(t));
    }

    [Fact]
    public async Task A_prune_with_nothing_old_deletes_nothing()
    {
        await using var t = await CreateDbAsync();
        var now = DateTimeOffset.UtcNow;
        await WriteAsync(t, now.AddDays(-10), "recent");
        Assert.Equal(0, await Engine.AuditRetention(t).PruneAsync(now - AuditPolicy.Retention, Ct));
        Assert.Equal(new[] { "recent" }, await RequestsAsync(t));
    }

    // ---------------------------------------------------------------- token_activity

    [Fact]   // mutations: "<=" for "<" (the first kept day deleted); the prune deleting every day
    public async Task The_token_activity_prune_deletes_only_days_before_the_first_kept_day()
    {
        await using var t = await CreateDbAsync();
        await t.AddTenantAsync();
        var token = await t.Tokens.IssueAsync("acme", "ci", Ct);
        await ExecAsync(t, $"""
            INSERT INTO token_activity(token_id, day) VALUES
              ('{token.TokenId}', '2025-01-01'), ('{token.TokenId}', '2025-10-11'), ('{token.TokenId}', '2025-10-12'), ('{token.TokenId}', '2026-10-10');
            """);

        Assert.Equal(2, await Engine.TokenActivity(t).PruneAsync(new DateOnly(2025, 10, 12), Ct));

        Assert.Equal(new[] { "2025-10-12", "2026-10-10" }, await ColumnAsync(t, "SELECT day FROM token_activity ORDER BY day"));
    }
}
