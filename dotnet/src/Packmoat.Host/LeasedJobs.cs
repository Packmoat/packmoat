using Packmoat.Application;

namespace Packmoat.Host;

/// <summary>
/// A scheduled job's pass under its lease, with its log lines: the pass's duration (Information), or that it was skipped because
/// another copy holds the lease (Information), or that the lease could not be taken (Error, by kind only; the pass is skipped and tried
/// again next time). The core's retention pass and a module's own jobs all run through it; a module gets the lease from
/// <see cref="Modules.PackmoatModuleContext.JobLease"/>.
/// </summary>
public static class LeasedJobs
{
    /// <summary>Runs <paramref name="pass"/> under <paramref name="job"/>'s lease and logs what happened. Returns whether it ran.</summary>
    public static async Task<bool> RunAsync(IJobLease lease, string job, string holder, ILogger log, Func<CancellationToken, Task> pass, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(log);
        var run = await LeasedJob.RunAsync(lease, job, holder, pass, ct);
        if (run.AcquireFailure is { } failed)
            log.LogError("Job {Job}: taking its lease failed ({Kind}); this pass is skipped and tried again next time", job, failed.GetType().Name);
        else if (!run.Ran)
            log.LogInformation("Job {Job}: another copy holds its lease; this pass is skipped", job);
        else
            log.LogInformation("Job {Job}: pass took {Milliseconds} ms", job, (long)run.Duration.TotalMilliseconds);
        if (run.ReleaseFailure is { } release)
            log.LogWarning("Job {Job}: ending its lease failed ({Kind}); it runs out by itself within {Minutes} minutes", job, release.GetType().Name, (int)JobLeases.Ttl.TotalMinutes);
        return run.Ran;
    }
}

/// <summary>
/// A job lease whose every lease runs for <see cref="Ttl"/>, whatever length the job asks for: the test seam
/// <see cref="PackmoatOptions.JobLeaseTtl"/>, never production's. Taking and ending a lease are the inner lease's, unchanged.
/// </summary>
internal sealed class FixedTtlJobLease : IJobLease
{
    public FixedTtlJobLease(IJobLease inner, TimeSpan ttl)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Ttl = ttl > TimeSpan.Zero ? ttl : throw new ArgumentOutOfRangeException(nameof(ttl), "A lease must run for some time.");
    }

    public IJobLease Inner { get; }
    public TimeSpan Ttl { get; }

    public Task<bool> TryAcquireAsync(string job, string holder, TimeSpan ttl, CancellationToken ct = default) => Inner.TryAcquireAsync(job, holder, Ttl, ct);

    public Task<bool> ReleaseAsync(string job, string holder, CancellationToken ct = default) => Inner.ReleaseAsync(job, holder, ct);
}
