using System.Diagnostics;
using System.Security.Cryptography;

namespace Packmoat.Application;

/// <summary>
/// The job lease: a row saying "holder H runs job J until time T", so two copies of the server, or a copy and the operator's
/// command, never run the same scheduled job at the same time. A copy that does not get the lease skips its pass.
/// </summary>
public interface IJobLease
{
    /// <summary>
    /// Takes the lease on <paramref name="job"/> for <paramref name="holder"/> until now plus <paramref name="ttl"/>, in one statement:
    /// granted when nobody holds it, when its holder's time has run out, or when <paramref name="holder"/> already holds it (which renews
    /// it). Returns false, changing nothing, while another holder's lease is still running.
    /// </summary>
    Task<bool> TryAcquireAsync(string job, string holder, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>
    /// Ends <paramref name="holder"/>'s lease on <paramref name="job"/> now (its time is set to now), so the next pass by anyone may take
    /// it. Changes nothing when another holder holds it. Returns whether it released one.
    /// </summary>
    Task<bool> ReleaseAsync(string job, string holder, CancellationToken ct = default);
}

/// <summary>The jobs that take a lease, this process's holder id and the lease's length.</summary>
public static class JobLeases
{
    /// <summary>The core's daily retention pass: the server's <c>RetentionService</c> and <c>packmoat-admin jobs run retention</c> share it.</summary>
    public const string Retention = "retention";

    /// <summary>
    /// How long a lease runs before another copy may take it over. Conservative, because a pass longer than this could overlap with a
    /// copy that takes over: production did not log a pass's duration before the pass logged it, and every pass now logs it. One hour is far above
    /// any plausible pass (the retention pass is a handful of indexed deletes and counts; a module's job may call an outside service once
    /// per tenant) and no longer than the shortest job interval today (an hour). A pass ends its lease when it finishes, so the length
    /// matters only when a copy stops mid-pass without ending it (a crash or a kill): the job then waits at most this long.
    /// <para>A module's job names its own lease (one name per job), never the core's.</para>
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    /// <summary>This process's holder id: random, made once per process, never written anywhere but the lease rows.</summary>
    public static string ProcessHolder { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
}

/// <summary>A lease that is always granted: where there is no database to hold one (tests with fakes). Production always has a real one.</summary>
public sealed class UnsharedJobLease : IJobLease
{
    public static UnsharedJobLease Instance { get; } = new();
    private UnsharedJobLease() { }
    public Task<bool> TryAcquireAsync(string job, string holder, TimeSpan ttl, CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> ReleaseAsync(string job, string holder, CancellationToken ct = default) => Task.FromResult(true);
}

/// <summary>What one leased pass did.</summary>
/// <param name="Ran">Whether the pass ran: false when another holder had the lease, or taking it failed.</param>
/// <param name="Duration">How long the pass took (zero when it did not run).</param>
/// <param name="AcquireFailure">Why taking the lease failed (the pass did not run), or null.</param>
/// <param name="ReleaseFailure">Why ending the lease failed (it then runs out by itself after <see cref="JobLeases.Ttl"/>), or null.</param>
public sealed record LeasedRun(bool Ran, TimeSpan Duration, Exception? AcquireFailure, Exception? ReleaseFailure);

/// <summary>
/// One pass of a job under its lease: take the lease, run the pass and time it, then end the lease whatever the pass did (even when the
/// server is stopping, so a restarted copy need not wait for it to run out). A pass that does not get the lease is skipped.
/// </summary>
public static class LeasedJob
{
    public static async Task<LeasedRun> RunAsync(IJobLease lease, string job, string holder, Func<CancellationToken, Task> pass, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(pass);
        try
        {
            if (!await lease.TryAcquireAsync(job, holder, JobLeases.Ttl, ct)) return new LeasedRun(false, TimeSpan.Zero, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new LeasedRun(false, TimeSpan.Zero, ex, null);
        }
        var clock = Stopwatch.StartNew();
        Exception? releaseFailure = null;
        try
        {
            await pass(ct);
        }
        finally
        {
            clock.Stop();
            try { await lease.ReleaseAsync(job, holder, CancellationToken.None); }
            catch (Exception ex) { releaseFailure = ex; }
        }
        return new LeasedRun(true, clock.Elapsed, null, releaseFailure);
    }
}
