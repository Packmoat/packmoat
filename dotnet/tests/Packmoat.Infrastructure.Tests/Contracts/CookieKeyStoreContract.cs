using Packmoat.Application;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The cookie key store (#232 B1) as a contract: keys round trip in a fixed order, and the one-time import from the key folder stores a
/// set only into an empty store, once, even when several servers import at the same moment. The encryption is in
/// <see cref="SqliteCookieKeyStoreTests"/>.
/// </summary>
public abstract class CookieKeyStoreContract : StoreContract
{
    private static string Xml(int i) => $"<key id=\"{i:D8}-0000-4000-8000-000000000000\" version=\"1\"><masterKey>secret-{i}</masterKey></key>";

    private static IReadOnlyList<CookieKey> Keys(int count, string prefix = "key") =>
        Enumerable.Range(1, count).Select(i => new CookieKey($"{prefix}-{i}", Xml(i))).ToList();

    [Fact]   // mutation: store the friendly name in place of the XML (or the other way round)
    public async Task Keys_round_trip_with_their_names_and_xml()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.CookieKeys(t);
        Assert.Empty(await store.GetAllAsync(default));
        await store.AddAsync("key-a", Xml(1), default);
        t.Clock.Advance(TimeSpan.FromMinutes(1));
        await store.AddAsync("key-b", Xml(2), default);

        var keys = await Engine.CookieKeys(t).GetAllAsync(default);   // another instance reads the same rows
        Assert.Equal([new CookieKey("key-a", Xml(1)), new CookieKey("key-b", Xml(2))], keys);
        Assert.Equal(2L, await ScalarAsync(t, "SELECT COUNT(*) FROM cookie_keys"));
    }

    [Fact]   // mutations: drop the ORDER BY (rows come newest first); order by id only (a random order: right once in 8! runs)
    public async Task Keys_come_oldest_first_then_by_id_the_same_every_time()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.CookieKeys(t);
        var start = t.Clock.UtcNow;
        for (var minute = 8; minute >= 1; minute--)   // written newest first, so neither the write order nor the ids give the time order
        {
            t.Clock.UtcNow = start + TimeSpan.FromMinutes(minute);
            await store.AddAsync($"minute-{minute}", Xml(minute), default);
        }
        t.Clock.UtcNow = start + TimeSpan.FromMinutes(3);
        await store.AddAsync("minute-3-again", Xml(30), default);   // a tie on created: the id decides

        var names = (await store.GetAllAsync(default)).Select(k => k.FriendlyName).ToList();
        Assert.Equal(["minute-1", "minute-2"], names[..2]);
        Assert.Equal(["minute-3", "minute-3-again"], names[2..4].Order(StringComparer.Ordinal));
        Assert.Equal(["minute-4", "minute-5", "minute-6", "minute-7", "minute-8"], names[4..]);
        Assert.Equal(await ColumnAsync(t, "SELECT friendly_name FROM cookie_keys ORDER BY created, id"), names);
        for (var i = 0; i < 3; i++) Assert.Equal(names, (await Engine.CookieKeys(t).GetAllAsync(default)).Select(k => k.FriendlyName).ToList());
    }

    [Fact]   // mutation: return 0 (or keys.Count - 1) from a successful import
    public async Task An_import_into_an_empty_store_stores_every_key_and_returns_the_count()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.CookieKeys(t);
        Assert.Equal(0, await store.ImportIfEmptyAsync([], default));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM cookie_keys"));

        Assert.Equal(3, await store.ImportIfEmptyAsync(Keys(3), default));
        Assert.Equal(Keys(3).OrderBy(k => k.FriendlyName, StringComparer.Ordinal), (await store.GetAllAsync(default)).OrderBy(k => k.FriendlyName, StringComparer.Ordinal));
    }

    [Fact]   // mutation: import whatever the store holds (every start imports again)
    public async Task A_second_import_adds_nothing_and_returns_zero()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.CookieKeys(t);
        Assert.Equal(2, await store.ImportIfEmptyAsync(Keys(2), default));
        Assert.Equal(0, await store.ImportIfEmptyAsync(Keys(5, "again"), default));
        Assert.Equal(2L, await ScalarAsync(t, "SELECT COUNT(*) FROM cookie_keys"));

        // A key the key ring made itself counts too: once any row exists, the folder is never read into the store again.
        await using var u = await CreateDbAsync();
        await Engine.CookieKeys(u).AddAsync("made-here", Xml(7), default);
        Assert.Equal(0, await Engine.CookieKeys(u).ImportIfEmptyAsync(Keys(3), default));
        Assert.Equal(["made-here"], (await Engine.CookieKeys(u).GetAllAsync(default)).Select(k => k.FriendlyName));
    }

    [Fact]   // mutation: count and insert outside one write transaction (deferred, or none): two servers both import
    public async Task Of_eight_importers_racing_exactly_one_imports_and_one_set_is_left()
    {
        for (var round = 0; round < 5; round++)
        {
            await using var t = await CreateDbAsync();
            var stores = Enumerable.Range(0, 8).Select(_ => Engine.CookieKeys(t)).ToList();
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var racers = stores.Select((store, i) => Task.Run(async () => { await go.Task; return await store.ImportIfEmptyAsync(Keys(20, $"copy{i}"), default); })).ToList();
            go.SetResult();   // every importer starts at once
            var results = await Task.WhenAll(racers);
            Assert.Single(results, r => r > 0);
            Assert.Equal(20, results.Sum());
            var winner = Array.FindIndex(results, r => r > 0);
            Assert.Equal(20L, await ScalarAsync(t, "SELECT COUNT(*) FROM cookie_keys"));
            Assert.All(await Engine.CookieKeys(t).GetAllAsync(default), k => Assert.StartsWith($"copy{winner}-", k.FriendlyName));
        }
    }
}
