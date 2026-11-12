using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Customer;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// <see cref="FormNonceStoreContract"/> on PostgreSQL, each test on a database of its own. Its race tests are the store's many-writer
/// races: one DELETE … RETURNING lets exactly one of twelve consumes through, and the issue's named lock keeps a key at its cap.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresFormNonceStoreTests : FormNonceStoreContract
{
    protected override IStoreEngine Engine => PostgresStoreEngine.Instance;
}

/// <summary>
/// <see cref="FormNonceStoreContract"/> on SQLite, migration 25, which existing SQLite databases run from 24, and the equivalence with the
/// in-memory nonces the store replaced.
/// </summary>
[Collection("sqlite-pools")]
public class FormNonceStoreTests : FormNonceStoreContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    [Fact]   // mutations: put a row in the table; add a column; an index left out; leave the table out of the purge's classification
    public async Task Migration_25_on_a_v24_database_adds_one_empty_table_and_changes_no_row()
    {
        await using var t = new TestDb();
        await t.Db.MigrateAsync(24);
        await t.AddTenantAsync("acme");
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%form_nonces%'"));

        await t.Db.MigrateAsync(TestTracks.Modules);
        Assert.Equal(26L, await ScalarAsync(t, "SELECT MAX(version) FROM schema_migrations"));   // and 26 (the feed setup scratch)
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM form_nonces"));
        Assert.Equal(["scope|TEXT|1|1", "nonce_hash|TEXT|1|2", "subject|TEXT|1|0", "expires_at|TEXT|1|0"],
            await ColumnAsync(t, "SELECT name || '|' || type || '|' || \"notnull\" || '|' || pk FROM pragma_table_info('form_nonces') ORDER BY cid"));
        Assert.Equal(["scope", "subject", "expires_at"], await ColumnAsync(t, "SELECT name FROM pragma_index_info('ix_form_nonces_subject') ORDER BY seqno"));
        Assert.Equal(["expires_at"], await ColumnAsync(t, "SELECT name FROM pragma_index_info('ix_form_nonces_expires_at')"));
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM tenants WHERE id='acme'"));
        // No tenant data: the purge's guard knows it (an unclassified table refuses every purge).
        Assert.Equal(Lifecycle.PurgeTableFate.NoTenantData, Assert.Single(SqliteTenantPurgeStore.CoreTables, c => c.Table == "form_nonces").Fate);
        // The schema refuses a second row for one nonce in one scope.
        await ExecAsync(t, "INSERT INTO form_nonces VALUES('app', 'h', 's', 'a')");
        await RefusedAsync(() => ExecAsync(t, "INSERT INTO form_nonces VALUES('app', 'h', 's2', 'b')"));
        await ExecAsync(t, "INSERT INTO form_nonces VALUES('admin', 'h', 's', 'a')");
    }

    [Fact]   // a nonce's expiry is UTC "O" text, as job_leases' are
    public async Task The_expiry_is_utc_O_text()
    {
        await using var t = await TestDb.CreateAsync();
        t.Clock.UtcNow = new DateTimeOffset(2026, 11, 6, 9, 30, 0, TimeSpan.FromHours(-5));   // a local time: stored as UTC
        await new SqliteFormNonceStore(t.Db, t.Clock).IssueAsync(FormNonceScopes.App, "n", "k", TimeSpan.FromMinutes(30), 20);
        Assert.Equal("2026-11-06T15:00:00.0000000+00:00", await ScalarAsync(t, "SELECT expires_at FROM form_nonces"));
    }

    private static async Task ExecAsync(TestDb t, string sql)
    {
        await using var c = await t.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------------------------------------------------------------- equivalence with the nonces it replaced

    /// <summary>
    /// The core's FormNonces.Issue and Consume as they were before the shared store, copied verbatim: the reference the store must agree
    /// with. Its global cap (2000 nonces in all, half dropped when full) is never reached here: it only bounded the process's memory.
    /// </summary>
    private sealed class OldFormNonces(IClock clock)
    {
        private static readonly TimeSpan Life = TimeSpan.FromMinutes(30);
        public const int Max = 2000;
        public const int MaxPerKey = 20;
        private readonly IClock _clock = clock;
        private readonly Dictionary<string, (string Key, DateTimeOffset Expires)> _live = new();
        private readonly object _lock = new();

        public string Issue(string key)
        {
            var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var now = _clock.UtcNow;
            lock (_lock)
            {
                // This key's own oldest go first: opening a form many times only ever replaces the same key's earlier nonces.
                var mine = _live.Where(kv => kv.Value.Key == key).OrderBy(kv => kv.Value.Expires).Select(kv => kv.Key).ToList();
                foreach (var k in mine.Take(Math.Max(0, mine.Count - (MaxPerKey - 1)))) _live.Remove(k);
                if (_live.Count >= Max)
                {
                    foreach (var k in _live.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList()) _live.Remove(k);
                    if (_live.Count >= Max) foreach (var k in _live.OrderBy(kv => kv.Value.Expires).Take(Max / 2).Select(kv => kv.Key).ToList()) _live.Remove(k);
                }
                _live[nonce] = (key, now + Life);
            }
            return nonce;
        }

        /// <summary>True once for a live nonce issued for this key; false for anything else (and the nonce is gone either way).</summary>
        public bool Consume(string? nonce, string key)
        {
            if (string.IsNullOrEmpty(nonce)) return false;
            lock (_lock)
            {
                if (!_live.Remove(nonce, out var e)) return false;
                return e.Key == key && e.Expires > _clock.UtcNow;
            }
        }
    }

    /// <summary>
    /// The equivalence pin: a seeded random run of form shows and posts by three people, with the clock moving forward by steps from a tick
    /// to past a nonce's life, gets the same answers from the customer site's nonces over the SQLite store as from the in-memory nonces
    /// they replaced: the same true or false for every post, whether it is the form's own person, another person, a replay, an unknown
    /// nonce or none. Every form is shown at a time of its own (at least a tick apart), so "oldest first" names one nonce on both.
    /// </summary>
    [Theory]   // mutation: any of the contract's mutations, made in SqliteFormNonceStore; or FormNonces' life or cap changed
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task The_shared_store_answers_as_the_in_memory_nonces_did(int seed)
    {
        await using var t = await TestDb.CreateAsync();
        var old = new OldFormNonces(t.Clock);
        var shared = new AppFormNonces(new SqliteFormNonceStore(t.Db, t.Clock));
        var life = TimeSpan.FromMinutes(30);
        var random = new Random(250 + seed);
        TimeSpan[] steps = [TimeSpan.FromTicks(1), TimeSpan.FromMicroseconds(1), TimeSpan.FromSeconds(1), life / 7, life / 2, life - TimeSpan.FromTicks(1), life, life + TimeSpan.FromSeconds(3)];
        string[] keys = ["dir/aaaa", "dir/bbbb", "dir/cccc"];
        var forms = new List<(string Old, string Shared, string Key, DateTimeOffset Shown)>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        int accepted = 0, refused = 0, issued = 0, pushedOut = 0;
        for (var i = 0; i < 1200; i++)
        {
            var roll = random.Next(10);
            if (roll < 5 && issued < OldFormNonces.Max - 30)
            {
                // One form, or a person opening it again and again (past the cap). Mostly small steps, so many forms are live at once;
                // sometimes past a nonce's life.
                var key = keys[random.Next(keys.Length)];
                for (var n = random.Next(5) == 0 ? 10 + random.Next(20) : 1; n > 0; n--)
                {
                    t.Clock.Advance(random.Next(40) == 0 ? steps[random.Next(steps.Length)] : TimeSpan.FromTicks(1 + random.Next(3)));
                    forms.Add((old.Issue(key), await shared.IssueAsync(key, default), key, t.Clock.UtcNow));
                    issued++;
                }
                continue;
            }
            if (random.Next(40) == 0) t.Clock.Advance(steps[random.Next(steps.Length)]);
            bool expected, actual;
            if (roll == 9 || forms.Count == 0)
            {
                var junk = random.Next(2) == 0 ? null : random.Next(2) == 0 ? "" : "ffffffffffffffffffffffffffffffff";
                var key = keys[random.Next(keys.Length)];
                (expected, actual) = (old.Consume(junk, key), await shared.ConsumeAsync(junk, key, default));
            }
            else
            {
                // A recent one (some pushed out by the person's cap: 90 forms is about 30 each), sometimes already used: a replay.
                var form = forms[forms.Count - 1 - random.Next(Math.Min(forms.Count, 90))];
                var key = random.Next(5) == 0 ? keys[random.Next(keys.Length)] : form.Key;   // sometimes another person
                (expected, actual) = (old.Consume(form.Old, key), await shared.ConsumeAsync(form.Shared, key, default));
                if (!expected && key == form.Key && t.Clock.UtcNow < form.Shown + life && !used.Contains(form.Old)) pushedOut++;
                used.Add(form.Old);
            }
            Assert.Equal(expected, actual);
            if (expected) accepted++; else refused++;
        }
        Assert.True(accepted > 50 && refused > 50 && pushedOut > 10,
            $"the run accepted {accepted}, refused {refused}, of them {pushedOut} pushed out by the cap: it proves too little");
    }
}
