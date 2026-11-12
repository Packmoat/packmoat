using Packmoat.Application;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The feed setup scratch as a contract: a row is read back only by its whole key (tenant, person, kind), for exactly its life; a put
/// replaces it; a delete forgets that row only; two copies see each other's rows, and of many writing one key one row is left; the prune
/// deletes expired rows only. Every time here is whole microseconds, PostgreSQL's resolution. Migration 26 stays in
/// <see cref="FeedSetupScratchTests"/>.
/// </summary>
public abstract class FeedSetupScratchContract : StoreContract
{
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(30);
    private const string Tenant = "acme";
    private static readonly Guid Ada = Guid.Parse("0b1c2d3e-0000-4000-8000-000000000001");
    private static readonly Guid Grace = Guid.Parse("0b1c2d3e-0000-4000-8000-000000000002");
    private const string Kind = FeedSetupScratchKinds.Settings;

    [EngineFact]   // mutations: the kind, the person or the tenant left out of the read (one row would answer for another)
    public async Task A_row_is_read_back_by_its_whole_key_only()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.FeedSetupScratch(t);
        await store.PutAsync(Tenant, Ada, Kind, "ada's check", Life);

        Assert.Equal("ada's check", await store.GetAsync(Tenant, Ada, Kind));
        Assert.Null(await store.GetAsync(Tenant, Ada, FeedSetupScratchKinds.Draft));        // another kind
        Assert.Null(await store.GetAsync(Tenant, Ada, FeedSetupScratchKinds.Developer));
        Assert.Null(await store.GetAsync(Tenant, Grace, Kind));                              // another person of the tenant
        Assert.Null(await store.GetAsync("globex", Ada, Kind));                              // the same person id under another tenant
    }

    [EngineFact]   // mutations: the kind, the person or the tenant left out of the key (a put would replace another's row)
    public async Task Each_tenant_person_and_kind_keeps_its_own_row()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.FeedSetupScratch(t);
        foreach (var tenant in new[] { Tenant, "globex" })
        foreach (var person in new[] { Ada, Grace })
        foreach (var kind in FeedSetupScratchKinds.All)
            await store.PutAsync(tenant, person, kind, $"{tenant}/{person:D}/{kind}", Life);

        Assert.Equal(12L, await ScalarAsync(t, "SELECT COUNT(*) FROM feed_setup_scratch"));
        foreach (var tenant in new[] { Tenant, "globex" })
        foreach (var person in new[] { Ada, Grace })
        foreach (var kind in FeedSetupScratchKinds.All)
            Assert.Equal($"{tenant}/{person:D}/{kind}", await store.GetAsync(tenant, person, kind));
    }

    [EngineFact]   // mutation: a put that inserts a second row, or keeps the first payload or expiry
    public async Task A_put_replaces_the_row_and_its_life()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.FeedSetupScratch(t);
        await store.PutAsync(Tenant, Ada, Kind, "first", Life);
        t.Clock.Advance(TimeSpan.FromMinutes(20));
        await store.PutAsync(Tenant, Ada, Kind, "second", Life);
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM feed_setup_scratch"));
        t.Clock.Advance(TimeSpan.FromMinutes(20));   // 40 minutes after the first put, 20 after the second
        Assert.Equal("second", await store.GetAsync(Tenant, Ada, Kind));
    }

    [EngineFact]   // mutation: the expiry check dropped, or made "expires_at >= now" (a row one tick past its life read back)
    public async Task A_row_lives_exactly_its_life()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.FeedSetupScratch(t);
        await store.PutAsync(Tenant, Ada, Kind, "kept", Life);
        t.Clock.Advance(Life - TimeSpan.FromMicroseconds(1));
        Assert.Equal("kept", await store.GetAsync(Tenant, Ada, Kind));
        t.Clock.Advance(TimeSpan.FromMicroseconds(1));   // exactly at the end of its life: gone
        Assert.Null(await store.GetAsync(Tenant, Ada, Kind));
        t.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(await store.GetAsync(Tenant, Ada, Kind));
    }

    [EngineFact]   // mutation: the delete keyed by fewer than its three parts (it would forget another's row)
    public async Task A_delete_forgets_that_row_only()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.FeedSetupScratch(t);
        await store.PutAsync(Tenant, Ada, FeedSetupScratchKinds.Draft, "draft", Life);
        await store.PutAsync(Tenant, Ada, Kind, "check", Life);
        await store.PutAsync(Tenant, Grace, FeedSetupScratchKinds.Draft, "grace's draft", Life);
        await store.PutAsync("globex", Ada, FeedSetupScratchKinds.Draft, "globex draft", Life);

        await store.DeleteAsync(Tenant, Ada, FeedSetupScratchKinds.Draft);
        await store.DeleteAsync(Tenant, Ada, FeedSetupScratchKinds.Developer);   // nothing kept: no change

        Assert.Null(await store.GetAsync(Tenant, Ada, FeedSetupScratchKinds.Draft));
        Assert.Equal("check", await store.GetAsync(Tenant, Ada, Kind));
        Assert.Equal("grace's draft", await store.GetAsync(Tenant, Grace, FeedSetupScratchKinds.Draft));
        Assert.Equal("globex draft", await store.GetAsync("globex", Ada, FeedSetupScratchKinds.Draft));
    }

    [EngineFact]   // two copies of the server: each its own store over the one database
    public async Task What_one_copy_keeps_another_reads_and_forgets()
    {
        await using var t = await CreateDbAsync();
        await Engine.FeedSetupScratch(t).PutAsync(Tenant, Ada, Kind, "held on one copy", Life);
        Assert.Equal("held on one copy", await Engine.FeedSetupScratch(t).GetAsync(Tenant, Ada, Kind));
        await Engine.FeedSetupScratch(t).DeleteAsync(Tenant, Ada, Kind);
        Assert.Null(await Engine.FeedSetupScratch(t).GetAsync(Tenant, Ada, Kind));
    }

    [EngineFact]   // the race: one statement per put, so of many copies writing one key exactly one row is left, holding one of their payloads
    public async Task Of_many_puts_racing_for_one_key_one_row_is_left()
    {
        await using var t = await CreateDbAsync();
        var puts = Enumerable.Range(0, 12).Select(i => Task.Run(() => Engine.FeedSetupScratch(t).PutAsync(Tenant, Ada, Kind, "copy-" + i, Life))).ToArray();
        await Task.WhenAll(puts);   // every put settled before anything is asserted
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM feed_setup_scratch"));
        Assert.Matches("^copy-([0-9]|1[01])$", await Engine.FeedSetupScratch(t).GetAsync(Tenant, Ada, Kind));
    }

    [EngineFact]   // the payload is the wizard's own text: kept as given, every character
    public async Task The_payload_is_kept_as_given()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.FeedSetupScratch(t);
        var payload = "{\"Name\":\"Zoë O'Brien \\\"Ops\\\"\",\"Label\":\"管理者\",\"Line\":\"a\\nb\"}" + new string('x', 64 * 1024);
        await store.PutAsync(Tenant, Ada, Kind, payload, Life);
        Assert.Equal(payload, await store.GetAsync(Tenant, Ada, Kind));
        Assert.Equal(Ada.ToString("D"), await ScalarAsync(t, "SELECT person_oid FROM feed_setup_scratch"));
    }

    [EngineFact]   // mutation: the prune deleting a live row (a person's check would be gone before its 30 minutes)
    public async Task The_prune_deletes_expired_rows_and_keeps_live_ones()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.FeedSetupScratch(t);
        await store.PutAsync(Tenant, Ada, Kind, "old", TimeSpan.FromMinutes(10));
        await store.PutAsync(Tenant, Ada, FeedSetupScratchKinds.Draft, "edge", TimeSpan.FromMinutes(20));
        await store.PutAsync("globex", Grace, Kind, "live", Life);
        t.Clock.Advance(TimeSpan.FromMinutes(20));   // "old" expired 10 minutes ago, "edge" expires exactly now, globex's lives 10 more

        Assert.Equal(0, await store.PruneAsync(t.Clock.UtcNow - TimeSpan.FromMinutes(11)));
        Assert.Equal(2, await store.PruneAsync(t.Clock.UtcNow));
        Assert.Equal(["globex"], await ColumnAsync(t, "SELECT tenant_id FROM feed_setup_scratch"));
        Assert.Equal("live", await store.GetAsync("globex", Grace, Kind));
        Assert.Equal(0, await store.PruneAsync(t.Clock.UtcNow));
    }

    [EngineFact]   // nothing is written or forgotten for a call the store refuses to take
    public async Task A_bad_tenant_person_kind_payload_or_life_is_refused_and_nothing_is_written()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.FeedSetupScratch(t);
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync("", Ada, Kind, "p", Life));
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync(Tenant, Guid.Empty, Kind, "p", Life));
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync(Tenant, Ada, "Settings", "p", Life));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.PutAsync(Tenant, Ada, Kind, null!, Life));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.PutAsync(Tenant, Ada, Kind, new string('x', FeedSetupScratchRules.MaxPayload + 1), Life));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.PutAsync(Tenant, Ada, Kind, "p", TimeSpan.Zero));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM feed_setup_scratch"));
        await store.PutAsync(Tenant, Ada, Kind, "p", Life);
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(Tenant, Ada, "unknown"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(Tenant, Guid.Empty, Kind));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync("", Ada, Kind));
        Assert.Equal("p", await store.GetAsync(Tenant, Ada, Kind));   // not forgotten by the refused calls
    }
}
