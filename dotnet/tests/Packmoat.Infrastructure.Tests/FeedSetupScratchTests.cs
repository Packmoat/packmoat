using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary><see cref="FeedSetupScratchContract"/> on PostgreSQL, each test on a database of its own.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresFeedSetupScratchTests : FeedSetupScratchContract
{
    protected override IStoreEngine Engine => PostgresStoreEngine.Instance;
}

/// <summary>
/// <see cref="FeedSetupScratchContract"/> on SQLite, migration 26, which existing SQLite databases run from 25, and the equivalence with the
/// in-memory state the scratch and the check lease replaced.
/// </summary>
[Collection("sqlite-pools")]
public class FeedSetupScratchTests : FeedSetupScratchContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    [Fact]   // mutations: put a row in the table; add a column; the index left out; leave the table out of the purge, or not as deleted
    public async Task Migration_26_on_a_v25_database_adds_one_empty_table_and_changes_no_row()
    {
        await using var t = new TestDb();
        await t.Db.MigrateAsync(25);
        await t.AddTenantAsync("acme");
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%feed_setup_scratch%'"));

        await t.Db.MigrateAsync(TestTracks.Modules);
        Assert.Equal(26L, await ScalarAsync(t, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM feed_setup_scratch"));
        Assert.Equal(["tenant_id|TEXT|1|1", "person_oid|TEXT|1|2", "kind|TEXT|1|3", "payload|TEXT|1|0", "expires_at|TEXT|1|0"],
            await ColumnAsync(t, "SELECT name || '|' || type || '|' || \"notnull\" || '|' || pk FROM pragma_table_info('feed_setup_scratch') ORDER BY cid"));
        Assert.Equal(["expires_at"], await ColumnAsync(t, "SELECT name FROM pragma_index_info('ix_feed_setup_scratch_expires_at')"));
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM tenants WHERE id='acme'"));
        // Tenant data (it may hold admin names): the purge deletes it by tenant.
        var purge = Assert.Single(SqliteTenantPurgeStore.CoreTables, c => c.Table == "feed_setup_scratch");
        Assert.Equal((Lifecycle.PurgeTableFate.Deleted, "tenant_id"), (purge.Fate, purge.TenantColumn));
        // The schema refuses a second row for one tenant, person and kind.
        await ExecAsync(t, "INSERT INTO feed_setup_scratch VALUES('acme', 'p', 'draft', '{}', 'a')");
        await RefusedAsync(() => ExecAsync(t, "INSERT INTO feed_setup_scratch VALUES('acme', 'p', 'draft', '{}', 'b')"));
        await ExecAsync(t, "INSERT INTO feed_setup_scratch VALUES('acme', 'p', 'settings', '{}', 'a')");
    }

    [Fact]   // a row's expiry is UTC "O" text, as job_leases' are
    public async Task The_expiry_is_utc_O_text()
    {
        await using var t = await TestDb.CreateAsync();
        t.Clock.UtcNow = new DateTimeOffset(2026, 11, 6, 9, 30, 0, TimeSpan.FromHours(-5));   // a local time: stored as UTC
        await new SqliteFeedSetupScratch(t.Db, t.Clock).PutAsync("acme", Guid.NewGuid(), FeedSetupScratchKinds.Draft, "{}", TimeSpan.FromMinutes(30));
        Assert.Equal("2026-11-06T15:00:00.0000000+00:00", await ScalarAsync(t, "SELECT expires_at FROM feed_setup_scratch"));
    }

    private static async Task ExecAsync(TestDb t, string sql)
    {
        await using var c = await t.Db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------------------------------------------------------------- equivalence with the in-memory state it replaced

    /// <summary>
    /// AzureSetupState as it was before the shared scratch and the check lease, copied verbatim: the reference the new state must agree with.
    /// Its clean-up past 2000 entries removed only expired ones, so it never changed an answer.
    /// </summary>
    private sealed class OldAzureSetupState
    {
        public static readonly TimeSpan HoldFor = TimeSpan.FromMinutes(30);
        private readonly IClock _clock;
        private readonly Dictionary<string, HeldFeedCheck> _held = new(StringComparer.Ordinal);
        private readonly HashSet<string> _running = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FeedNamesDraft> _drafts = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        public OldAzureSetupState(IClock clock) => _clock = clock;

        private static string Key(string tenantId, Guid oid, FeedCheckKind kind) => tenantId + "|" + oid.ToString("D") + "|" + kind;

        public void Hold(string tenantId, Guid oid, HeldFeedCheck check, FeedCheckKind kind = FeedCheckKind.Settings)
        {
            lock (_lock)
            {
                _held[Key(tenantId, oid, kind)] = check;
                if (_held.Count > 2_000)
                    foreach (var k in _held.Where(kv => _clock.UtcNow - kv.Value.At >= HoldFor).Select(kv => kv.Key).ToList()) _held.Remove(k);
            }
        }

        public HeldFeedCheck? Get(string tenantId, Guid oid, FeedCheckKind kind = FeedCheckKind.Settings)
        {
            lock (_lock)
            {
                if (!_held.TryGetValue(Key(tenantId, oid, kind), out var c)) return null;
                var age = _clock.UtcNow - c.At;
                if (age < TimeSpan.Zero || age >= HoldFor) { _held.Remove(Key(tenantId, oid, kind)); return null; }
                return c;
            }
        }

        private static string DraftKey(string tenantId, Guid oid) => tenantId + "|" + oid.ToString("D");

        public void KeepDraft(string tenantId, Guid oid, FeedNamesDraft draft)
        {
            lock (_lock)
            {
                _drafts[DraftKey(tenantId, oid)] = draft;
                if (_drafts.Count > 2_000)
                    foreach (var k in _drafts.Where(kv => _clock.UtcNow - kv.Value.At >= HoldFor).Select(kv => kv.Key).ToList()) _drafts.Remove(k);
            }
        }

        public void ClearDraft(string tenantId, Guid oid) { lock (_lock) _drafts.Remove(DraftKey(tenantId, oid)); }

        public FeedNamesDraft? GetDraft(string tenantId, Guid oid)
        {
            lock (_lock)
            {
                if (!_drafts.TryGetValue(DraftKey(tenantId, oid), out var d)) return null;
                var age = _clock.UtcNow - d.At;
                if (age < TimeSpan.Zero || age >= HoldFor) { _drafts.Remove(DraftKey(tenantId, oid)); return null; }
                return d;
            }
        }

        public bool TryBegin(string tenantId) { lock (_lock) return _running.Add(tenantId); }
        public void End(string tenantId) { lock (_lock) _running.Remove(tenantId); }
    }

    private static string? Text(HeldFeedCheck? c) => c is null ? null : FeedSetupPayload.Write(c);
    private static string? Text(FeedNamesDraft? d) => d is null ? null : FeedSetupPayload.Write(d);

    /// <summary>
    /// The equivalence pin: a seeded random run by two tenants' three people (one of them in both tenants), with the clock moving forward
    /// by steps from a tick to past the 30 minutes, gets the same answers from the new state over the SQLite scratch and job lease as from
    /// the in-memory state it replaced: every held check of either kind, every draft (kept, replaced, cleared, expired), and every
    /// "is a check running" answer, as the commands use them (a run ends before the next of its tenant begins, at least a tick later, and
    /// within the 60 s deadline).
    /// </summary>
    [Theory]   // mutation: any of the contract's mutations, made in SqliteFeedSetupScratch; or the 30 minutes, the kind or the person dropped
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task The_shared_state_answers_as_the_in_memory_state_did(int seed)
    {
        await using var t = await TestDb.CreateAsync();
        var old = new OldAzureSetupState(t.Clock);
        var shared = new AzureSetupState(new SqliteFeedSetupScratch(t.Db, t.Clock), new SqliteJobLease(t.Db, t.Clock), t.Clock);
        var random = new Random(2503 + seed);
        var hold = AzureSetupState.HoldFor;
        TimeSpan[] steps = [TimeSpan.FromTicks(1), TimeSpan.FromSeconds(1), hold / 7, hold / 2, hold - TimeSpan.FromTicks(1), hold, hold + TimeSpan.FromSeconds(3)];
        string[] tenants = ["selfco", "acme"];
        Guid[] people = [Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"), Guid.Parse("aaaaaaaa-0000-4000-8000-000000000002"), Guid.Parse("aaaaaaaa-0000-4000-8000-000000000003")];
        FeedCheckKind[] kinds = [FeedCheckKind.Settings, FeedCheckKind.Developer];
        var running = new Dictionary<string, (string Holder, DateTimeOffset Since)>(StringComparer.Ordinal);
        int heldFound = 0, heldMissing = 0, draftsFound = 0, draftsMissing = 0, refusedRunning = 0, begun = 0, n = 0;
        for (var i = 0; i < 2500; i++)
        {
            // Mostly small steps; now and then a long one, but never while a check runs (a run ends within its deadline).
            t.Clock.Advance(random.Next(25) == 0 && running.Count == 0 ? steps[random.Next(steps.Length)] : TimeSpan.FromTicks(1 + random.Next(1000)));
            var tenant = tenants[random.Next(tenants.Length)];
            var person = people[random.Next(people.Length)];
            switch (random.Next(9))
            {
                case 0 or 1:
                {
                    var kind = kinds[random.Next(kinds.Length)];
                    var admins = Enumerable.Range(0, random.Next(3)).Select(a => new NamedFeedAdmin($"Admin {n} {a} (feed creator)", $"Admin {n} {a}", $"aad.{n}.{a}")).ToList();
                    var check = new HeldFeedCheck(t.Clock.UtcNow, random.Next(2) == 0 ? null : new FeedNames("contoso", random.Next(2) == 0 ? null : "Platform Team", "feed-" + n),
                        "https://pkgs.dev.azure.com/contoso/_packaging/" + n + "/nuget/v3/index.json", (FeedCheckOutcome)random.Next(3),
                        [new CheckResult("feed", "The feed", (CheckStatus)random.Next(4), "detail " + n, random.Next(2) == 0 ? null : ["a", "b"])], admins, random.Next(2) == 0)
                    { Finding = kind == FeedCheckKind.Developer ? (DeveloperFinding)random.Next(5) : null, Withdrawn = random.Next(4) == 0, WithdrawFailed = random.Next(4) == 0 };
                    n++;
                    old.Hold(tenant, person, check, kind);
                    await shared.HoldAsync(tenant, person, check, kind);
                    break;
                }
                case 2 or 3:
                {
                    var kind = kinds[random.Next(kinds.Length)];
                    var expected = Text(old.Get(tenant, person, kind));
                    Assert.Equal(expected, Text(await shared.GetAsync(tenant, person, kind)));
                    if (expected is null) heldMissing++; else heldFound++;
                    break;
                }
                case 4:
                {
                    var draft = FeedNamesDraft.From(t.Clock.UtcNow, random.Next(4) == 0 ? "bad org!" : "contoso", random.Next(2) == 0 ? null : "Team", "feed-" + n++);
                    if (draft is null) continue;
                    old.KeepDraft(tenant, person, draft);
                    await shared.KeepDraftAsync(tenant, person, draft);
                    break;
                }
                case 5:
                    old.ClearDraft(tenant, person);
                    await shared.ClearDraftAsync(tenant, person);
                    break;
                case 6:
                {
                    var expected = Text(old.GetDraft(tenant, person));
                    Assert.Equal(expected, Text(await shared.GetDraftAsync(tenant, person)));
                    if (expected is null) draftsMissing++; else draftsFound++;
                    break;
                }
                case 7:
                {
                    var began = old.TryBegin(tenant);
                    var holder = await shared.TryBeginAsync(tenant);
                    Assert.Equal(began, holder is not null);
                    if (began) { running[tenant] = (holder!, t.Clock.UtcNow); begun++; } else refusedRunning++;
                    break;
                }
                default:
                    if (!running.Remove(tenant, out var run)) continue;
                    old.End(tenant);
                    await shared.EndAsync(tenant, run.Holder);
                    break;
            }
        }
        Assert.True(heldFound > 50 && heldMissing > 50 && draftsFound > 20 && draftsMissing > 20 && begun > 20 && refusedRunning > 20,
            $"the run found {heldFound} held and {heldMissing} not, {draftsFound} drafts and {draftsMissing} not, began {begun} checks and refused {refusedRunning}: it proves too little");
    }
}
