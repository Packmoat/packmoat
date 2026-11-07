using Packmoat.Application;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The job lease (#232 M2) as a contract: one holder at a time, a lease whose time ran out is taken over, its own holder renews it, and only
/// its holder ends it. Migration 23 stays in <see cref="JobLeaseTests"/>.
/// </summary>
public abstract class JobLeaseContract : StoreContract
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    private static string O(DateTimeOffset t) => t.ToUniversalTime().ToString("O");

    [Fact]   // mutation: drop the holder check from the takeover (the second holder takes a running lease)
    public async Task Two_holders_only_one_wins()
    {
        await using var t = await CreateDbAsync();
        var lease = Engine.JobLease(t);
        Assert.True(await lease.TryAcquireAsync("retention", "copy-a", Ttl));
        Assert.False(await lease.TryAcquireAsync("retention", "copy-b", Ttl));
        Assert.False(await Engine.JobLease(t).TryAcquireAsync("retention", "copy-b", Ttl));   // a second instance reads the same row
        Assert.Equal("copy-a", await ScalarAsync(t, "SELECT holder FROM job_leases WHERE job='retention'"));
        Assert.Equal(O(t.Clock.UtcNow + Ttl), await ScalarAsync(t, "SELECT expires_at FROM job_leases WHERE job='retention'"));

        // Another job is its own lease.
        Assert.True(await lease.TryAcquireAsync("other-job", "copy-b", Ttl));
        Assert.Equal(2L, await ScalarAsync(t, "SELECT COUNT(*) FROM job_leases"));
    }

    [Fact]   // the race: many holders at once, one statement each; exactly one wins
    public async Task Of_many_holders_racing_for_a_free_lease_exactly_one_wins()
    {
        await using var t = await CreateDbAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => Engine.JobLease(t).TryAcquireAsync("retention", $"copy-{i}", Ttl))));
        Assert.Equal(1, results.Count(r => r));
        Assert.Equal($"copy-{Array.IndexOf(results, true)}", await ScalarAsync(t, "SELECT holder FROM job_leases WHERE job='retention'"));
    }

    [Fact]   // mutation: drop "expires_at < now" (a crashed holder's lease is never taken over)
    public async Task An_expired_lease_is_taken_over()
    {
        await using var t = await CreateDbAsync();
        var lease = Engine.JobLease(t);
        Assert.True(await lease.TryAcquireAsync("retention", "copy-a", Ttl));

        t.Clock.Advance(Ttl);   // exactly at its end: still copy-a's
        Assert.False(await lease.TryAcquireAsync("retention", "copy-b", Ttl));
        t.Clock.Advance(TimeSpan.FromTicks(10));   // just past it
        Assert.True(await lease.TryAcquireAsync("retention", "copy-b", Ttl));
        Assert.Equal("copy-b", await ScalarAsync(t, "SELECT holder FROM job_leases WHERE job='retention'"));
        Assert.Equal(O(t.Clock.UtcNow + Ttl), await ScalarAsync(t, "SELECT expires_at FROM job_leases WHERE job='retention'"));
        Assert.False(await lease.TryAcquireAsync("retention", "copy-a", Ttl));   // and the old holder no longer has it
    }

    [Fact]   // mutation: drop "holder = @me" (a holder cannot renew its own running lease)
    public async Task The_same_holder_re_acquires_and_its_lease_is_renewed()
    {
        await using var t = await CreateDbAsync();
        var lease = Engine.JobLease(t);
        Assert.True(await lease.TryAcquireAsync("retention", "copy-a", Ttl));
        t.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(await lease.TryAcquireAsync("retention", "copy-a", Ttl));
        Assert.Equal(O(t.Clock.UtcNow + Ttl), await ScalarAsync(t, "SELECT expires_at FROM job_leases WHERE job='retention'"));
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM job_leases"));
    }

    [Fact]   // mutation: release regardless of holder (another copy could end a running lease)
    public async Task Release_ends_only_the_holders_own_lease()
    {
        await using var t = await CreateDbAsync();
        var lease = Engine.JobLease(t);
        Assert.False(await lease.ReleaseAsync("retention", "copy-a"));   // nothing to end
        Assert.True(await lease.TryAcquireAsync("retention", "copy-a", Ttl));
        var until = O(t.Clock.UtcNow + Ttl);

        Assert.False(await lease.ReleaseAsync("retention", "copy-b"));   // not its lease: nothing changes
        Assert.Equal(until, await ScalarAsync(t, "SELECT expires_at FROM job_leases WHERE job='retention'"));
        Assert.False(await lease.TryAcquireAsync("retention", "copy-b", Ttl));

        t.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(await lease.ReleaseAsync("retention", "copy-a"));
        Assert.Equal(O(t.Clock.UtcNow), await ScalarAsync(t, "SELECT expires_at FROM job_leases WHERE job='retention'"));
        Assert.Equal("copy-a", await ScalarAsync(t, "SELECT holder FROM job_leases WHERE job='retention'"));
        t.Clock.Advance(TimeSpan.FromTicks(10));
        Assert.True(await lease.TryAcquireAsync("retention", "copy-b", Ttl));   // the next pass, by anyone, takes it
    }
}
