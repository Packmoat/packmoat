using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// The job lease in the server and the command (#232 M2): the retention pass runs under the lease the operator's command also takes, so
/// the two never run it at once; the production composition hands the service the database's lease; each pass that runs logs its
/// duration, and one that loses the lease is skipped and says so at Information level.
/// </summary>
[Collection("sqlite-pools")]
public class JobLeaseHostTests : IDisposable
{
    private readonly TempDir _temp = TempDir.Create("packmoat-lease-", clearSqlitePools: true);
    private readonly string _key = AesGcmSecretProtector.GenerateKeyBase64();
    private readonly FakeClock _clock = new();
    private string DbPath => _temp.File("packmoat.db");

    // xunit 2 disposes a test class through IDisposable only (an IAsyncDisposable one is never called): #292.
    public void Dispose() => _temp.Dispose();

    internal sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>A feed check store whose blanking (the pass's first step) waits until it is let go, so a pass can be held mid-way.</summary>
    private sealed class HeldFeedChecks : IFeedCheckStore
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Go = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Blanks;
        public async Task<int> BlankPersonalDataAsync(DateTimeOffset checkedBefore, CancellationToken ct)
        {
            Interlocked.Increment(ref Blanks);
            Entered.TrySetResult();
            await Go.Task.WaitAsync(ct);
            return 0;
        }
        public Task<long> RecordAsync(FeedCheck check, CancellationToken ct) => throw new NotSupportedException();
        public Task<FeedCheck?> LatestAsync(string tenantId, FeedCheckKind kind, CancellationToken ct) => throw new NotSupportedException();
    }

    private async Task<SqliteDatabase> DatabaseAsync()
    {
        var db = new SqliteDatabase(DbPath);
        await db.MigrateAsync(TestTracks.Modules);
        return db;
    }

    private async Task<(int Code, string Out, string Err)> CommandAsync()
    {
        var o = new StringWriter(); var e = new StringWriter();
        var code = await AdminCli.RunAsync(["jobs", "run", "retention"], o, e, new CliEnvironment(DbPath, () => AesGcmSecretProtector.FromBase64(_key), _clock), []);
        return (code, o.ToString(), e.ToString());
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    [Fact]   // mutations: the command skips the lease; the service skips the lease; either holds it under the other's holder id
    public async Task The_command_and_the_server_never_run_the_retention_pass_at_once()
    {
        var db = await DatabaseAsync();
        var checks = new HeldFeedChecks();
        var log = new RecordingLogger();
        var service = new RetentionService(checks, _clock, log, lease: new SqliteJobLease(db, _clock), holder: "server-copy");

        // The server's pass is running (held in its first step): the command skips, changing nothing, and exits 0.
        var serverPass = service.PassAsync(default);
        await checks.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var (code, output, error) = await CommandAsync();
        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.StartsWith("retention pass skipped: another copy", output);
        Assert.DoesNotContain("feed checks blanked", output);
        Assert.Equal("server-copy", await ScalarAsync("SELECT holder FROM job_leases WHERE job='retention'"));

        // The server's pass ends and ends its lease: the command then runs.
        checks.Go.SetResult();
        Assert.True(await serverPass.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Information && System.Text.RegularExpressions.Regex.IsMatch(l.Message, @"^Job retention: pass took \d+ ms$"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        (code, output, _) = await CommandAsync();
        Assert.Equal(0, code);
        Assert.Contains("feed checks blanked\t0", output);
        Assert.Contains("retention pass done", output);

        // The other way round: while the command holds the lease (as its pass does, under this process's holder id), the server skips,
        // runs no step, and says so at Information level.
        Assert.True(await new SqliteJobLease(db, _clock).TryAcquireAsync(JobLeases.Retention, JobLeases.ProcessHolder, JobLeases.Ttl));
        var blanks = checks.Blanks;
        log.Lines.Clear();
        Assert.False(await service.PassAsync(default));
        Assert.Equal(blanks, checks.Blanks);
        Assert.Equal([(LogLevel.Information, "Job retention: another copy holds its lease; this pass is skipped")], log.Lines);
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM job_leases"));   // one row for the job, whoever ran it
    }

    [Fact]   // mutation: PackmoatApp gives the retention pass no lease (or the always-granted one) over a database
    public async Task The_production_composition_gives_the_retention_pass_the_databases_lease()
    {
        await using var app = PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], TestModules.Server, null, o =>
        {
            o.DatabasePath = DbPath;
            o.MasterKeyBase64 = _key;
            o.AllowedHosts = "feeds.example.test";
        });
        var service = app.Services.GetServices<IHostedService>().OfType<RetentionService>().Single();
        Assert.IsType<SqliteJobLease>(service.Lease);
    }

    [Fact]   // a lease that cannot be taken (the database is failing) skips the pass, logged by kind, and never throws out of the service
    public async Task A_lease_that_cannot_be_taken_skips_the_pass_and_logs_the_kind_only()
    {
        var checks = new HeldFeedChecks();
        checks.Go.SetResult();
        var log = new RecordingLogger();
        var service = new RetentionService(checks, _clock, log, lease: new FailingLease());
        Assert.False(await service.PassAsync(default));
        Assert.Equal(0, checks.Blanks);
        Assert.Equal([(LogLevel.Error, "Job retention: taking its lease failed (IOException); this pass is skipped and tried again next time")], log.Lines);
    }

    [Fact]   // mutation: end the lease only when the pass succeeds (a failed pass would hold it for the whole hour)
    public async Task A_pass_that_throws_still_ends_its_lease()
    {
        var db = await DatabaseAsync();
        var lease = new SqliteJobLease(db, _clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => LeasedJob.RunAsync(lease, "job", "copy-a", _ => throw new InvalidOperationException("boom"), default));
        Assert.Equal(_clock.UtcNow.ToString("O"), await ScalarAsync("SELECT expires_at FROM job_leases WHERE job='job'"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await lease.TryAcquireAsync("job", "copy-b", JobLeases.Ttl));
    }

    /// <summary>Grants every lease; its release throws when told to, refuses a cancelled token, and records the token it was given.</summary>
    private sealed class ReleaseRecordingLease : IJobLease
    {
        public bool ReleaseThrows { get; init; }
        public int Releases;
        public CancellationToken ReleaseToken;
        public Task<bool> TryAcquireAsync(string job, string holder, TimeSpan ttl, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ReleaseAsync(string job, string holder, CancellationToken ct = default)
        {
            ReleaseToken = ct;
            ct.ThrowIfCancellationRequested();
            if (ReleaseThrows) throw new IOException("disk gone /data/packmoat.db");
            Interlocked.Increment(ref Releases);
            return Task.FromResult(true);
        }
    }

    [Fact]   // mutations: let the release's exception out of the finally; drop the warning
    public async Task A_release_that_throws_still_counts_the_pass_as_run_and_logs_a_warning_by_kind()
    {
        var log = new RecordingLogger();
        var ran = 0;
        var result = await LeasedJobs.RunAsync(new ReleaseRecordingLease { ReleaseThrows = true }, "retention", "copy-a", log, _ => { ran++; return Task.CompletedTask; }, default);
        Assert.True(result);
        Assert.Equal(1, ran);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Information && l.Message.StartsWith("Job retention: pass took ", StringComparison.Ordinal));
        Assert.Contains((LogLevel.Warning, "Job retention: ending its lease failed (IOException); it runs out by itself within 60 minutes"), log.Lines);
        Assert.DoesNotContain(log.Lines, l => l.Message.Contains("/data", StringComparison.Ordinal));   // the kind only
    }

    [Fact]   // mutation: release with the caller's (stopping) token: a server that is stopping would leave its lease held for the hour
    public async Task The_release_is_not_cancelled_by_a_stopping_token()
    {
        var lease = new ReleaseRecordingLease();
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        var run = await LeasedJob.RunAsync(lease, "retention", "copy-a", _ => Task.CompletedTask, stopping.Token);
        Assert.True(run.Ran);
        Assert.Null(run.ReleaseFailure);
        Assert.Equal(1, lease.Releases);
        Assert.False(lease.ReleaseToken.IsCancellationRequested);
    }

    private sealed class FailingLease : IJobLease
    {
        public Task<bool> TryAcquireAsync(string job, string holder, TimeSpan ttl, CancellationToken ct = default) => throw new IOException("disk gone /data/packmoat.db");
        public Task<bool> ReleaseAsync(string job, string holder, CancellationToken ct = default) => throw new IOException("disk gone");
    }
}
