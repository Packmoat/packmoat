using System.Data.Common;
using Microsoft.Data.Sqlite;
using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// `packmoat-admin jobs run retention`: the server's daily retention pass by hand, for a server whose background jobs are off. Every
/// step runs and prints its count; a failed step is named by kind only, the others still run, and the exit code is 1. It counts the
/// tenants due for purge but never claims the day's nudge, so the server's own nudge still goes out.
/// </summary>
[Collection("sqlite-pools")]
public class JobsCliTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDir _temp = TempDir.Create("packmoat-cli-jobs-", clearSqlitePools: true);
    private readonly string _key = AesGcmSecretProtector.GenerateKeyBase64();
    private readonly FakeClock _clock = new() { UtcNow = T0 };
    private string DbPath => _temp.File("packmoat.db");
    private CliEnvironment Env => new(DbPath, () => AesGcmSecretProtector.FromBase64(_key), _clock);

    /// <summary>A module whose purge eligibility says every tenant is due (so the nudge would have something to claim).</summary>
    private sealed class EveryoneDue : IAdminCliModule, IPurgeRecheck
    {
        public string Group => "everyonedue";
        public string Usage => "";
        public string EnvironmentUsage => "";
        public Task<int> RunAsync(AdminCliContext context) => Task.FromResult(0);
        public IServingGate? Gate(IRelationalDatabase db, CliEnvironment env, Action<string> warn) => null;
        public TenantLifecycle? Lifecycle(IRelationalDatabase db, CliEnvironment env, Action<string> note) => new([], [], () => this);
        public MigrationTrack? Migrations => null;

        public Task<IReadOnlyList<PurgeCandidate>> ListAsync(DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PurgeCandidate>>([new PurgeCandidate("acme", PurgeVerdict.Due, "test", now.AddDays(-1), "test", "")]);
        public Task<PurgeCandidate?> AssessAsync(string tenantId, DateTimeOffset now, CancellationToken ct) => Task.FromResult<PurgeCandidate?>(null);
        public Task ConfirmAsync(PurgeCandidate candidate, CancellationToken ct) => throw new PurgeRefusedException("test");
        public Task<bool> StillDueAsync(DbTransaction transaction, PurgeCandidate candidate, DateTimeOffset now, CancellationToken ct) => Task.FromResult(false);
    }

    private async Task<(int Code, string Out, string Err)> Run(IReadOnlyList<IAdminCliModule> modules, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var code = await AdminCli.RunAsync(args, o, e, Env, modules);
        return (code, o.ToString(), e.ToString());
    }

    private Task<(int Code, string Out, string Err)> Run(params string[] args) => Run(TestCli.Modules, args);

    private async Task Sql(string sql)
    {
        await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> Scalar(string sql)
    {
        await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    // xunit 2 disposes a test class through IDisposable only (an IAsyncDisposable one is never called).
    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task It_runs_every_step_prints_each_count_and_exits_0()
    {
        Assert.Equal(0, (await Run("tenant", "create", "acme", "Acme", "--upstream-base", "https://x/", "--feed", "https://x/i.json")).Code);
        await new SqliteDecisionRecordLog(new SqliteDatabase(DbPath)).UpsertAsync(
            [new DecisionRecord("acme", Ecosystem.NuGet, "moq", "1.0.0", Decision.Hide, VerdictReason.Cooldown, null, 14, null, 3, T0, T0, 1)], default);
        _clock.UtcNow = T0 + DecisionRecordPolicy.Retention + TimeSpan.FromDays(1);

        var (code, output, error) = await Run("jobs", "run", "retention");

        Assert.Equal(0, code);
        Assert.Equal("", error);
        var lines = output.TrimEnd('\n').Split('\n');
        Assert.Matches(@"^retention pass took \d+ ms$", lines[0]);   // Every pass that runs prints its duration
        Assert.Equal(new[]
        {
            "feed checks blanked\t0",
            "tenants due for purge (counted, nothing deleted)\t0",
            "decision records deleted\t1",
            "admin action records deleted\t0",
            "audit events deleted\t0",
            "per-day activity rows deleted\t0",
            "ended rate-limit windows deleted\t0",
            "expired form nonces deleted\t0",
            "expired feed setup results and drafts deleted\t0",
            "retention pass done",
        }, lines[1..]);
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM job_leases WHERE job='retention'"));   // one row per pass, ended by it
        Assert.Equal((T0 + DecisionRecordPolicy.Retention + TimeSpan.FromDays(1)).ToString("O"), await Scalar("SELECT expires_at FROM job_leases"));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM decision_records"));
    }

    /// <summary>the command's pass deletes the database's ended rate-limit windows, as the server's does.</summary>
    [Fact]   // mutation: the command not handing the pass the database's counter (it would print 0 and leave the row)
    public async Task It_deletes_the_ended_rate_limit_windows_and_keeps_running_ones()
    {
        Assert.Equal(0, (await Run("tenant", "list")).Code);   // makes the database
        var rates = new SqliteRateCounter(new SqliteDatabase(DbPath), _clock);
        await rates.HitAsync(RateScopes.AdminWrite, "10.0.0.7", 30, TimeSpan.FromMinutes(1));
        await rates.HitAsync(RateScopes.TokenIssue, "dir/oid", 10, TimeSpan.FromHours(1));
        _clock.UtcNow = T0 + TimeSpan.FromMinutes(5);

        var (code, output, error) = await Run("jobs", "run", "retention");

        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.Contains("ended rate-limit windows deleted\t1\n", output);
        Assert.Equal(RateScopes.TokenIssue, await Scalar("SELECT group_concat(scope) FROM rate_counters"));
    }

    /// <summary>the command's pass deletes the database's expired form nonces, as the server's does.</summary>
    [Fact]   // mutation: the command not handing the pass the database's form nonces (it would print 0 and leave the row)
    public async Task It_deletes_the_expired_form_nonces_and_keeps_live_ones()
    {
        Assert.Equal(0, (await Run("tenant", "list")).Code);   // makes the database
        var nonces = new SqliteFormNonceStore(new SqliteDatabase(DbPath), _clock);
        await nonces.IssueAsync(FormNonceScopes.Admin, "expired", "acme", TimeSpan.FromMinutes(1), 20);
        await nonces.IssueAsync(FormNonceScopes.App, "live", "dir/oid", TimeSpan.FromMinutes(30), 20);
        _clock.UtcNow = T0 + TimeSpan.FromMinutes(5);

        var (code, output, error) = await Run("jobs", "run", "retention");

        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.Contains("expired form nonces deleted\t1\n", output);
        Assert.Equal(FormNonceScopes.App, await Scalar("SELECT group_concat(scope) FROM form_nonces"));
    }

    /// <summary>the command's pass deletes the feed setup wizard's expired results and drafts (they may hold admin names), as the server's does.</summary>
    [Fact]   // mutation: the command not handing the pass the database's feed setup scratch (it would print 0 and leave the row)
    public async Task It_deletes_the_expired_feed_setup_scratch_and_keeps_live_rows()
    {
        Assert.Equal(0, (await Run("tenant", "list")).Code);   // makes the database
        var scratch = new SqliteFeedSetupScratch(new SqliteDatabase(DbPath), _clock);
        await scratch.PutAsync("acme", Guid.Parse("0b1c2d3e-0000-4000-8000-000000000001"), FeedSetupScratchKinds.Settings, "{}", TimeSpan.FromMinutes(1));
        await scratch.PutAsync("acme", Guid.Parse("0b1c2d3e-0000-4000-8000-000000000002"), FeedSetupScratchKinds.Draft, "{}", TimeSpan.FromMinutes(30));
        _clock.UtcNow = T0 + TimeSpan.FromMinutes(5);

        var (code, output, error) = await Run("jobs", "run", "retention");

        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.Contains("expired feed setup results and drafts deleted\t1\n", output);
        Assert.Equal(FeedSetupScratchKinds.Draft, await Scalar("SELECT group_concat(kind) FROM feed_setup_scratch"));
    }

    [Fact]   // mutation: the command stops at the first failed step, or exits 0 with a failed step
    public async Task A_failed_step_is_named_by_kind_the_others_still_run_and_the_exit_code_is_1()
    {
        Assert.Equal(0, (await Run("tenant", "list")).Code);   // makes the database
        await Sql("DROP TABLE decision_records");

        var (code, output, error) = await Run("jobs", "run", "retention");

        Assert.Equal(1, code);
        Assert.Contains("decision records deleted\tFAILED (SqliteException)", output);
        Assert.Contains("admin action records deleted\t0", output);   // after the failed step
        Assert.Contains("per-day activity rows deleted\t0", output);
        Assert.DoesNotContain("decision_records", output + error);     // the exception's message is not shown
        Assert.DoesNotContain("retention pass done", output);
        Assert.Contains("1 retention step(s) failed", error);
    }

    [Fact]   // mutation: the command runs the pass with the nudge (it would claim the day and the server's nudge would not go out)
    public async Task It_counts_the_tenants_due_but_never_claims_the_days_nudge()
    {
        IReadOnlyList<IAdminCliModule> modules = [new EveryoneDue()];
        Assert.Equal(0, (await Run(modules, "tenant", "create", "acme", "Acme", "--upstream-base", "https://x/", "--feed", "https://x/i.json")).Code);

        var (code, output, _) = await Run(modules, "jobs", "run", "retention");

        Assert.Equal(0, code);
        Assert.Contains("tenants due for purge (counted, nothing deleted)\t1", output);
        Assert.Contains("Tenants are due for purge: run packmoat-admin tenant purge-due", output);
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM meta WHERE key='purge_nudge_day'"));
        // The server's nudge, later the same day, still claims it.
        var store = new SqliteTenantPurgeStore(new SqliteDatabase(DbPath), null, []);
        Assert.True(await store.TryClaimNudgeDayAsync(DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime), default));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM tenants WHERE id='acme'"));   // and nothing was deleted
    }

    [Theory]
    [InlineData("jobs")]
    [InlineData("jobs", "run")]
    [InlineData("jobs", "run", "everything")]
    [InlineData("jobs", "list")]
    [InlineData("jobs", "run", "retention", "--dry-run")]   // mutation: extra arguments accepted (the real pass would run)
    [InlineData("jobs", "run", "retention", "now")]
    [InlineData("jobs", "run", "retention", "--by", "ops")]
    public async Task Anything_but_run_retention_prints_the_usage_and_exits_2(params string[] args)
    {
        var (code, output, error) = await Run(args);
        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.Contains("packmoat-admin jobs run retention", error);
    }

    [Fact]   // mutation: the help line removed
    public async Task The_help_lists_the_command()
    {
        var (code, output, _) = await Run("--help");
        Assert.Equal(0, code);
        Assert.Contains("\n  jobs run retention ", output);
        Assert.Contains("PACKMOAT_BACKGROUND_JOBS is off", output);
    }

    [Fact]
    public async Task A_module_may_not_take_the_jobs_group()
    {
        var (code, _, error) = await Run([new Named("jobs")], "--help");
        Assert.Equal(2, code);
        Assert.Contains("\"jobs\" is one of the core's commands", error);
    }

    private sealed class Named(string group) : IAdminCliModule
    {
        public string Group => group;
        public string Usage => "";
        public string EnvironmentUsage => "";
        public Task<int> RunAsync(AdminCliContext context) => Task.FromResult(0);
        public IServingGate? Gate(IRelationalDatabase db, CliEnvironment env, Action<string> warn) => null;
        public TenantLifecycle? Lifecycle(IRelationalDatabase db, CliEnvironment env, Action<string> note) => null;
        public MigrationTrack? Migrations => null;
    }
}
