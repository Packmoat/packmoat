using Packmoat.Application;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The shared rate counter as a contract: a fixed window per (scope, key) that starts at the key's first attempt, at most max
/// attempts in it, the next refused with the time left; a new window once it has ended; one scope never counts another's; two copies
/// share the count and racing hits cannot both take the last place; the key is kept only as its hash; and the prune deletes ended windows
/// only. Every time here is whole microseconds, PostgreSQL's resolution. Migration 24 stays in <see cref="RateCounterTests"/>.
/// </summary>
public abstract class RateCounterContract : StoreContract
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private const string Scope = "token-issue";
    private const string Key = "dir-a/0b1c2d3e-0000-4000-8000-000000000001";

    [EngineFact]   // mutation: "<= max" made "< max" (one attempt fewer), or "hits + 1" capped at max (one more)
    public async Task Max_attempts_are_allowed_then_the_next_is_refused_until_the_window_ends()
    {
        await using var t = await CreateDbAsync();
        var counter = Engine.RateCounter(t);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(RateDecision.Allow, await counter.HitAsync(Scope, Key, 3, Window));
            t.Clock.Advance(TimeSpan.FromMinutes(5));
        }
        // 15 minutes in: the window has 45 minutes left.
        Assert.Equal(new RateDecision(false, TimeSpan.FromMinutes(45)), await counter.HitAsync(Scope, Key, 3, Window));
        t.Clock.Advance(TimeSpan.FromMinutes(10));
        // A refused attempt does not move the window: 35 minutes left now, not a fresh hour.
        Assert.Equal(new RateDecision(false, TimeSpan.FromMinutes(35)), await counter.HitAsync(Scope, Key, 3, Window));
        Assert.Equal(2100, (await counter.HitAsync(Scope, Key, 3, Window)).RetryAfterSeconds);
        // Refused attempts are counted no further than one above the limit.
        Assert.Equal(4L, await ScalarAsync(t, "SELECT hits FROM rate_counters"));
    }

    [EngineFact]   // mutation: the reset at "ends_at <= now" made "< now" (a window lasts one tick longer), or "<= now + 1µs" (shorter)
    public async Task The_window_ends_exactly_one_window_after_its_first_attempt()
    {
        await using var t = await CreateDbAsync();
        var counter = Engine.RateCounter(t);
        Assert.True((await counter.HitAsync(Scope, Key, 1, Window)).Allowed);
        t.Clock.Advance(Window - TimeSpan.FromMicroseconds(1));
        Assert.Equal(new RateDecision(false, TimeSpan.FromMicroseconds(1)), await counter.HitAsync(Scope, Key, 1, Window));
        Assert.Equal(1, (await counter.HitAsync(Scope, Key, 1, Window)).RetryAfterSeconds);   // under a second rounds up to 1
        t.Clock.Advance(TimeSpan.FromMicroseconds(1));   // exactly at the end: a new window, starting now
        Assert.True((await counter.HitAsync(Scope, Key, 1, Window)).Allowed);
        Assert.Equal(new RateDecision(false, Window), await counter.HitAsync(Scope, Key, 1, Window));
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM rate_counters"));
    }

    [EngineFact]   // mutation: the scope left out of the key (two limits would share one count)
    public async Task Each_scope_and_each_key_counts_alone()
    {
        await using var t = await CreateDbAsync();
        var counter = Engine.RateCounter(t);
        Assert.True((await counter.HitAsync("token-issue", Key, 1, Window)).Allowed);
        Assert.False((await counter.HitAsync("token-issue", Key, 1, Window)).Allowed);
        Assert.True((await counter.HitAsync("token-revoke", Key, 1, Window)).Allowed);   // the same person, another limit
        Assert.True((await counter.HitAsync("token-issue", Key + "2", 1, Window)).Allowed);   // another person, the same limit
        Assert.Equal(3L, await ScalarAsync(t, "SELECT COUNT(*) FROM rate_counters"));
    }

    [EngineFact]   // what the counter keeps of a key: its hash, never the key
    public async Task The_key_is_kept_only_as_its_hash()
    {
        await using var t = await CreateDbAsync();
        await Engine.RateCounter(t).HitAsync(Scope, Key, 3, Window);
        Assert.Equal(RateCounters.Subject(Key), await ScalarAsync(t, "SELECT subject FROM rate_counters"));
        Assert.Matches("^[0-9a-f]{64}$", RateCounters.Subject(Key));
        Assert.Equal(Scope, await ScalarAsync(t, "SELECT scope FROM rate_counters"));
        Assert.Equal(0L, await ScalarAsync(t, $"SELECT COUNT(*) FROM rate_counters WHERE subject = '{Key}'"));
    }

    [EngineFact]   // two copies of the server: each its own counter over the one database
    public async Task Two_counters_over_one_database_share_the_count()
    {
        await using var t = await CreateDbAsync();
        Assert.True((await Engine.RateCounter(t).HitAsync(Scope, Key, 2, Window)).Allowed);
        Assert.True((await Engine.RateCounter(t).HitAsync(Scope, Key, 2, Window)).Allowed);
        Assert.False((await Engine.RateCounter(t).HitAsync(Scope, Key, 2, Window)).Allowed);
    }

    [EngineFact]   // the race: one statement per hit, so of many racing for the last places exactly max win
    public async Task Of_many_hits_racing_exactly_max_are_allowed()
    {
        await using var t = await CreateDbAsync();
        var hits = Enumerable.Range(0, 12).Select(_ => Task.Run(() => Engine.RateCounter(t).HitAsync(Scope, Key, 5, Window))).ToArray();
        var settled = await Task.WhenAll(hits);   // every hit settled before anything is asserted
        Assert.Equal(5, settled.Count(d => d.Allowed));
        Assert.All(settled.Where(d => !d.Allowed), d => Assert.Equal(Window, d.RetryAfter));
        Assert.Equal(6L, await ScalarAsync(t, "SELECT hits FROM rate_counters"));
    }

    [EngineFact]   // a clock set back by more than a window (a broken clock) starts again; set back less, the window still counts
    public async Task A_window_that_starts_more_than_a_window_ahead_starts_again()
    {
        await using var t = await CreateDbAsync();
        var counter = Engine.RateCounter(t);
        Assert.True((await counter.HitAsync(Scope, Key, 1, Window)).Allowed);
        t.Clock.Advance(-TimeSpan.FromMinutes(30));   // another copy's clock, half an hour behind: still the same window
        Assert.Equal(new RateDecision(false, TimeSpan.FromMinutes(90)), await counter.HitAsync(Scope, Key, 1, Window));
        t.Clock.Advance(-Window);   // now the window started more than a window ahead
        Assert.True((await counter.HitAsync(Scope, Key, 1, Window)).Allowed);
        Assert.False((await counter.HitAsync(Scope, Key, 1, Window)).Allowed);
    }

    [EngineFact]   // mutation: the prune deleting a window still running (its count lost: the limit would start over)
    public async Task The_prune_deletes_ended_windows_and_keeps_running_ones()
    {
        await using var t = await CreateDbAsync();
        var counter = Engine.RateCounter(t);
        Assert.True((await counter.HitAsync("waiver-add", "old", 1, TimeSpan.FromMinutes(10))).Allowed);
        Assert.True((await counter.HitAsync("waiver-add", "edge", 1, TimeSpan.FromMinutes(20))).Allowed);
        Assert.True((await counter.HitAsync(Scope, Key, 1, Window)).Allowed);
        t.Clock.Advance(TimeSpan.FromMinutes(20));   // "old" ended 10 minutes ago, "edge" ends exactly now, Key runs 40 more minutes

        Assert.Equal(0, await counter.PruneAsync(t.Clock.UtcNow - TimeSpan.FromMinutes(11)));
        Assert.Equal(2, await counter.PruneAsync(t.Clock.UtcNow));
        Assert.Equal([RateCounters.Subject(Key)], await ColumnAsync(t, "SELECT subject FROM rate_counters"));
        // The running window still counts after the prune.
        Assert.Equal(new RateDecision(false, TimeSpan.FromMinutes(40)), await counter.HitAsync(Scope, Key, 1, Window));
        Assert.Equal(0, await counter.PruneAsync(t.Clock.UtcNow));
    }

    [EngineFact]   // nothing is counted for a hit the counter refuses to take
    public async Task A_bad_scope_limit_or_window_is_refused_and_nothing_is_written()
    {
        await using var t = await CreateDbAsync();
        var counter = Engine.RateCounter(t);
        await Assert.ThrowsAsync<ArgumentException>(() => counter.HitAsync("Token Issue", Key, 1, Window));
        await Assert.ThrowsAsync<ArgumentException>(() => counter.HitAsync(Scope, "", 1, Window));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => counter.HitAsync(Scope, Key, 0, Window));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => counter.HitAsync(Scope, Key, 1, TimeSpan.Zero));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM rate_counters"));
    }
}
