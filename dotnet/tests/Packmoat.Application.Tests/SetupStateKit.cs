using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Application.Tests;

/// <summary>The setup wizard's state for the commands' tests: the in-process scratch and a lease that behaves as the database's.</summary>
internal static class SetupStates
{
    public static AzureSetupState New(IClock clock) => new(new InProcessFeedSetupScratch(clock), new TestJobLease(clock), clock);

    /// <summary>The same held check, read back from the scratch: equal field by field (lists by their items).</summary>
    public static void SameCheck(HeldFeedCheck expected, HeldFeedCheck? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(FeedSetupPayload.Write(expected), FeedSetupPayload.Write(actual!));
        Assert.Equal((expected.At, expected.Names, expected.FeedIndexUrl, expected.Outcome, expected.Recorded, expected.Finding, expected.Withdrawn, expected.WithdrawFailed),
            (actual!.At, actual.Names, actual.FeedIndexUrl, actual.Outcome, actual.Recorded, actual.Finding, actual.Withdrawn, actual.WithdrawFailed));
        Assert.Equal(expected.NamedAdmins, actual.NamedAdmins);
        Assert.Equal(expected.Results.Select(r => (r.Id, r.Title, r.Status, r.Detail, Names: string.Join("|", r.Names ?? []), HasNames: r.Names is not null)),
            actual.Results.Select(r => (r.Id, r.Title, r.Status, r.Detail, Names: string.Join("|", r.Names ?? []), HasNames: r.Names is not null)));
    }
}

/// <summary>
/// A job lease in this process with the database lease's rules (granted when free, run out or the holder's own; a release ends only the
/// holder's own), timed by the clock given.
/// </summary>
internal sealed class TestJobLease : IJobLease
{
    private readonly IClock _clock;
    private readonly Dictionary<string, (string Holder, DateTimeOffset Until)> _leases = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public TestJobLease(IClock clock) => _clock = clock;

    /// <summary>Every lease taken: job, holder and ttl.</summary>
    public List<(string Job, string Holder, TimeSpan Ttl)> Taken { get; } = new();
    /// <summary>Every lease released: job and holder.</summary>
    public List<(string Job, string Holder)> Released { get; } = new();
    /// <summary>Set: every take throws it.</summary>
    public Exception? FailAcquire { get; set; }
    /// <summary>Set: every release throws it.</summary>
    public Exception? FailRelease { get; set; }

    public Task<bool> TryAcquireAsync(string job, string holder, TimeSpan ttl, CancellationToken ct = default)
    {
        if (FailAcquire is { } e) throw e;
        var now = _clock.UtcNow;
        lock (_lock)
        {
            if (_leases.TryGetValue(job, out var l) && l.Until >= now && l.Holder != holder) return Task.FromResult(false);
            _leases[job] = (holder, now + ttl);
            Taken.Add((job, holder, ttl));
            return Task.FromResult(true);
        }
    }

    public Task<bool> ReleaseAsync(string job, string holder, CancellationToken ct = default)
    {
        if (FailRelease is { } e) throw e;
        lock (_lock)
        {
            Released.Add((job, holder));
            if (!_leases.TryGetValue(job, out var l) || l.Holder != holder) return Task.FromResult(false);
            _leases[job] = (holder, _clock.UtcNow);
            return Task.FromResult(true);
        }
    }
}
