using Microsoft.Data.Sqlite;
using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Lifecycle;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// `packmoat-admin jobs run retention` (#233): the server's daily retention pass by hand, for a server whose background jobs are off. Every
/// step runs and prints its count; a failed step is named by kind only, the others still run, and the exit code is 1. It counts the
/// tenants due for purge but never claims the day's nudge, so the server's own nudge still goes out.
/// </summary>
[Collection("sqlite-pools")]
public class JobsCliTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("packmoat-cli-jobs-").FullName;
    private readonly string _key = AesGcmSecretProtector.GenerateKeyBase64();
    private readonly FakeClock _clock = new() { UtcNow = T0 };
    private string DbPath => Path.Combine(_dir, "packmoat.db");
    private CliEnvironment Env => new(DbPath, () => AesGcmSecretProtector.FromBase64(_key), _clock);

    /// <summary>A module whose purge eligibility says every tenant is due (so the nudge would have something to claim).</summary>
    private sealed class EveryoneDue : IAdminCliModule, IPurgeRecheck
    {
        public string Group => "everyonedue";
        public string Usage => "";
        public string EnvironmentUsage => "";
        public Task<int> RunAsync(AdminCliContext context) => Task.FromResult(0);
        public IServingGate? Gate(SqliteDatabase db, CliEnvironment env, Action<string> warn) => null;
        public TenantLifecycle? Lifecycle(SqliteDatabase db, CliEnvironment env, Action<string> note) => new([], [], () => this);
        public MigrationTrack? Migrations => null;

        public Task<IReadOnlyList<PurgeCandidate>> ListAsync(DateTimeOffset now, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PurgeCandidate>>([new PurgeCandidate("acme", PurgeVerdict.Due, "test", now.AddDays(-1), "test", "")]);
        public Task<PurgeCandidate?> AssessAsync(string tenantId, DateTimeOffset now, CancellationToken ct) => Task.FromResult<PurgeCandidate?>(null);
        public Task ConfirmAsync(PurgeCandidate candidate, CancellationToken ct) => throw new PurgeRefusedException("test");
        public Task<bool> StillDueAsync(SqliteTransaction transaction, PurgeCandidate candidate, DateTimeOffset now, CancellationToken ct) => Task.FromResult(false);
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

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

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
        Assert.Matches(@"^retention pass took \d+ ms$", lines[0]);   // #232 M2: every pass that runs prints its duration
        Assert.Equal(new[]
        {
            "feed checks blanked\t0",
            "tenants due for purge (counted, nothing deleted)\t0",
            "decision records deleted\t1",
            "admin action records deleted\t0",
            "audit events deleted\t0",
            "per-day activity rows deleted\t0",
            "retention pass done",
        }, lines[1..]);
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM job_leases WHERE job='retention'"));   // one row per pass, ended by it
        Assert.Equal((T0 + DecisionRecordPolicy.Retention + TimeSpan.FromDays(1)).ToString("O"), await Scalar("SELECT expires_at FROM job_leases"));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM decision_records"));
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
        public IServingGate? Gate(SqliteDatabase db, CliEnvironment env, Action<string> warn) => null;
        public TenantLifecycle? Lifecycle(SqliteDatabase db, CliEnvironment env, Action<string> note) => null;
        public MigrationTrack? Migrations => null;
    }
}
