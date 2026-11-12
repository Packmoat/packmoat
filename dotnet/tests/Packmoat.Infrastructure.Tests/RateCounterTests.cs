using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// <see cref="RateCounterContract"/> on PostgreSQL, each test on a database of its own. Its race test is the counter's
/// many-writer race: the one statement's ON CONFLICT lets exactly max of twelve hits through.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresRateCounterTests : RateCounterContract
{
    protected override IStoreEngine Engine => PostgresStoreEngine.Instance;
}

/// <summary>
/// <see cref="RateCounterContract"/> on SQLite, migration 24, which existing SQLite databases run from 23, and the
/// equivalence with the in-memory limiters the counter replaced, which the live site ran until this pull request.
/// </summary>
[Collection("sqlite-pools")]
public class RateCounterTests : RateCounterContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    [Fact]   // mutations: put a row in the table; add a column; leave the table out of the purge's classification
    public async Task Migration_24_on_a_v23_database_adds_one_empty_table_and_changes_no_row()
    {
        await using var t = new TestDb();
        await t.Db.MigrateAsync(23);
        await t.AddTenantAsync("acme");
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('rate_counters','ix_rate_counters_ends_at')"));

        await t.Db.MigrateAsync(TestTracks.Modules);
        Assert.Equal(26L, await ScalarAsync(t, "SELECT MAX(version) FROM schema_migrations"));   // and 25 (the form nonces), 26 (the feed setup scratch)
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM rate_counters"));
        Assert.Equal(["scope|TEXT|1|1", "subject|TEXT|1|2", "started_at|TEXT|1|0", "ends_at|TEXT|1|0", "hits|INTEGER|1|0"],
            await ColumnAsync(t, "SELECT name || '|' || type || '|' || \"notnull\" || '|' || pk FROM pragma_table_info('rate_counters') ORDER BY cid"));
        Assert.Equal(["ends_at"], await ColumnAsync(t, "SELECT name FROM pragma_index_info('ix_rate_counters_ends_at')"));
        Assert.Contains("rate_counters", await ColumnAsync(t, "SELECT name FROM sqlite_master WHERE type='table'"));
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM tenants WHERE id='acme'"));
        // No tenant data: the purge's guard knows it (an unclassified table refuses every purge).
        Assert.Equal(Lifecycle.PurgeTableFate.NoTenantData, Assert.Single(SqliteTenantPurgeStore.CoreTables, c => c.Table == "rate_counters").Fate);
        // The schema refuses a row with no hits.
        await RefusedAsync(() => ExecAsync(t, "INSERT INTO rate_counters VALUES('s', 'k', 'a', 'b', 0)"));
    }

    [Fact]   // a window's times are UTC "O" text, as job_leases' are
    public async Task The_window_times_are_utc_O_text()
    {
        await using var t = await TestDb.CreateAsync();
        t.Clock.UtcNow = new DateTimeOffset(2026, 11, 6, 9, 30, 0, TimeSpan.FromHours(-5));   // a local time: stored as UTC
        Assert.True((await new SqliteRateCounter(t.Db, t.Clock).HitAsync(RateScopes.TokenIssue, "k", 1, TimeSpan.FromHours(1))).Allowed);
        Assert.Equal("2026-11-06T14:30:00.0000000+00:00", await ScalarAsync(t, "SELECT started_at FROM rate_counters"));
        Assert.Equal("2026-11-06T15:30:00.0000000+00:00", await ScalarAsync(t, "SELECT ends_at FROM rate_counters"));
    }

    // ---------------------------------------------------------------- equivalence with the limiters it replaced

    /// <summary>
    /// The core's PersonRateLimiter.TryAcquire and AdminWriteLimiter.TryAcquire as they were before the shared counter, copied
    /// verbatim but for the 20,000-entry sweep (which only drops ended windows): the reference the shared counter must agree with.
    /// </summary>
    private sealed class OldLimiters(IClock clock, int max, TimeSpan window)
    {
        private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _person = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _admin = new();

        public bool PersonTryAcquire(string key)
        {
            var now = clock.UtcNow;
            if (!_person.TryGetValue(key, out var e) || now - e.Start >= window || now < e.Start) e = (now, 0);
            if (e.Count >= max) { _person[key] = e; return false; }
            _person[key] = (e.Start, e.Count + 1);
            return true;
        }

        public int? AdminTryAcquire(string address)
        {
            var now = clock.UtcNow;
            if (!_admin.TryGetValue(address, out var e) || now - e.Start >= window) e = (now, 0);
            if (e.Count >= max) { _admin[address] = e; return Math.Max(1, (int)Math.Ceiling((e.Start + window - now).TotalSeconds)); }
            _admin[address] = (e.Start, e.Count + 1);
            return null;
        }
    }

    /// <summary>
    /// The equivalence pin: for every limit the live site has (its max and window), a seeded random run of attempts by three keys,
    /// with the clock moving forward by steps from a microsecond to past the window, gets the same answers from the shared counter on
    /// SQLite, through <see cref="RateLimit"/> as every call site uses it, as from the in-memory limiters it replaced: the same allowed or
    /// refused for a person's limit, and the same allowed or retry-after seconds for the admin writes. The clock only moves forward: going
    /// back is the one declared difference (<see cref="RateCounterContract"/>'s set-back test).
    /// </summary>
    [Theory]   // mutation: any of the contract's mutations, made in SqliteRateCounter; or RateLimit failing open
    [InlineData(3, 3600)]      // a module's limit of 3 an hour
    [InlineData(5, 3600)]      // a module's limit of 5 an hour
    [InlineData(10, 3600)]     // tokens, waivers, auto-join, feed checks (and a module's 10 an hour)
    [InlineData(1, 3600)]      // the waiver cap alert: one per tenant per hour
    [InlineData(1, 60)]        // a module's one per tenant per minute
    [InlineData(30, 60)]       // admin writes: 30 a minute per address
    public async Task The_shared_counter_answers_as_the_in_memory_limiters_did(int max, int windowSeconds)
    {
        await using var t = await TestDb.CreateAsync();
        var window = TimeSpan.FromSeconds(windowSeconds);
        var old = new OldLimiters(t.Clock, max, window);
        var person = new RateLimit(new SqliteRateCounter(t.Db, t.Clock), RateScopes.TokenIssue, max, window);
        var admin = new Packmoat.Host.Admin.AdminWriteLimiter(new SqliteRateCounter(t.Db, t.Clock), max, window);
        var random = new Random(250 + max * 7 + windowSeconds);
        TimeSpan[] steps = [TimeSpan.Zero, TimeSpan.FromMicroseconds(1), TimeSpan.FromSeconds(1), window / 7, window / 2, window - TimeSpan.FromTicks(1), window, window + TimeSpan.FromSeconds(3)];
        string[] keys = ["dir/aaaa", "dir/bbbb", "10.0.0.7"];
        var refusedSeen = 0;
        for (var i = 0; i < 300 + 20 * max; i++)
        {
            // Mostly bursts (no time passes), so the limit is reached; sometimes a step, sometimes past the window.
            t.Clock.Advance(random.Next(max + 3) == 0 ? steps[random.Next(steps.Length)] : TimeSpan.Zero);
            var key = keys[random.Next(keys.Length)];
            var expectedPerson = old.PersonTryAcquire(key);
            Assert.Equal(expectedPerson, await person.TryAcquireAsync(key, default));
            var expectedAdmin = old.AdminTryAcquire(key);
            Assert.Equal(expectedAdmin, await admin.TryAcquireAsync(key, default));
            if (!expectedPerson) refusedSeen++;
        }
        Assert.True(refusedSeen > 10, $"the run reached the limit only {refusedSeen} times: it proves too little");
    }
}
