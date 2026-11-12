using Packmoat.Application;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// The form nonces as a contract: a nonce is used once only, by the key it was issued for, before it expires, in the scope it was issued
/// in; the nonce is gone after any consume; a key holds at most the cap, its oldest dropped first; two copies share the nonces, and of many
/// racing for one nonce exactly one wins; only hashes are kept; and the prune deletes expired nonces only. Every time here is whole
/// microseconds, PostgreSQL's resolution. Migration 25 stays in <see cref="FormNonceStoreTests"/>.
/// </summary>
public abstract class FormNonceStoreContract : StoreContract
{
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(30);
    private const int Cap = 20;
    private const string Scope = FormNonceScopes.App;
    private const string Key = "dir-a/0b1c2d3e-0000-4000-8000-000000000001";
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    [EngineFact]   // mutation: the consume reads the row but does not delete it (a replay would be let through)
    public async Task A_nonce_is_used_once_only()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.FormNonces(t);
        await store.IssueAsync(Scope, Nonce, Key, Life, Cap);
        Assert.True(await store.ConsumeAsync(Scope, Nonce, Key));
        Assert.False(await store.ConsumeAsync(Scope, Nonce, Key));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM form_nonces"));
        Assert.False(await store.ConsumeAsync(Scope, "never-issued", Key));
    }

    [EngineFact]   // mutation: the key check dropped (a form shown to one person posted by another)
    public async Task Another_key_is_refused_and_the_nonce_is_gone_either_way()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.FormNonces(t);
        await store.IssueAsync(Scope, Nonce, Key, Life, Cap);
        Assert.False(await store.ConsumeAsync(Scope, Nonce, Key + "2"));
        Assert.False(await store.ConsumeAsync(Scope, Nonce, Key));   // gone: the wrong key's attempt spent it, as before
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM form_nonces"));
    }

    [EngineFact]   // mutation: the expiry check dropped, or made "expires_at >= now" (a nonce one tick past its life accepted)
    public async Task A_nonce_lives_exactly_its_life()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.FormNonces(t);
        await store.IssueAsync(Scope, "early", Key, Life, Cap);
        await store.IssueAsync(Scope, "at-end", Key, Life, Cap);
        await store.IssueAsync(Scope, "late", Key, Life, Cap);
        t.Clock.Advance(Life - TimeSpan.FromMicroseconds(1));
        Assert.True(await store.ConsumeAsync(Scope, "early", Key));
        t.Clock.Advance(TimeSpan.FromMicroseconds(1));   // exactly at the end of its life: expired
        Assert.False(await store.ConsumeAsync(Scope, "at-end", Key));
        t.Clock.Advance(TimeSpan.FromDays(1));
        Assert.False(await store.ConsumeAsync(Scope, "late", Key));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM form_nonces"));   // an expired one is spent by its consume too
    }

    [EngineFact]   // mutation: the scope left out of the consume or the trim (the admin and customer sites would share their nonces)
    public async Task Each_scope_keeps_its_own_nonces()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.FormNonces(t);
        await store.IssueAsync(FormNonceScopes.Admin, Nonce, Key, Life, Cap);
        Assert.False(await store.ConsumeAsync(FormNonceScopes.App, Nonce, Key));   // the other site's: not found, and not spent
        Assert.True(await store.ConsumeAsync(FormNonceScopes.Admin, Nonce, Key));

        // A key filling one scope never pushes out its nonces in the other.
        await store.IssueAsync(FormNonceScopes.Admin, "admin-form", Key, Life, Cap);
        for (var i = 0; i < Cap + 5; i++)
        {
            t.Clock.Advance(TimeSpan.FromMicroseconds(1));
            await store.IssueAsync(FormNonceScopes.App, "app-" + i, Key, Life, Cap);
        }
        Assert.True(await store.ConsumeAsync(FormNonceScopes.Admin, "admin-form", Key));
        Assert.Equal((long)Cap, await ScalarAsync(t, $"SELECT COUNT(*) FROM form_nonces WHERE scope = '{FormNonceScopes.App}'"));
    }

    [EngineFact]   // mutation: the cap's trim dropped, or keeping one more or one fewer than the cap
    public async Task A_key_holds_at_most_the_cap_and_its_oldest_go_first()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.FormNonces(t);
        await store.IssueAsync(Scope, "quiet", "quiet/person", Life, Cap);
        for (var i = 0; i < Cap + 1; i++)
        {
            t.Clock.Advance(TimeSpan.FromMicroseconds(1));
            await store.IssueAsync(Scope, "busy-" + i, Key, Life, Cap);
        }
        Assert.Equal((long)Cap, await ScalarAsync(t, $"SELECT COUNT(*) FROM form_nonces WHERE subject = '{FormNonceRules.Hash(Key)}'"));
        Assert.False(await store.ConsumeAsync(Scope, "busy-0", Key));   // the oldest went
        Assert.True(await store.ConsumeAsync(Scope, "busy-1", Key));    // the next oldest is still there
        Assert.True(await store.ConsumeAsync(Scope, "busy-" + Cap, Key));
        Assert.True(await store.ConsumeAsync(Scope, "quiet", "quiet/person"));   // another key's never goes
    }

    [EngineFact]   // mutation: the nonce or the key stored as given, not as its hash
    public async Task Only_hashes_are_kept()
    {
        await using var t = await CreateDbAsync();
        await Engine.FormNonces(t).IssueAsync(Scope, Nonce, Key, Life, Cap);
        Assert.Equal(FormNonceRules.Hash(Nonce), await ScalarAsync(t, "SELECT nonce_hash FROM form_nonces"));
        Assert.Equal(FormNonceRules.Hash(Key), await ScalarAsync(t, "SELECT subject FROM form_nonces"));
        Assert.Matches("^[0-9a-f]{64}$", FormNonceRules.Hash(Nonce));
        Assert.Equal(0L, await ScalarAsync(t, $"SELECT COUNT(*) FROM form_nonces WHERE nonce_hash = '{Nonce}' OR subject = '{Key}'"));
    }

    [EngineFact]   // two copies of the server: each its own store over the one database
    public async Task A_form_shown_by_one_copy_is_used_on_another_once()
    {
        await using var t = await CreateDbAsync();
        await Engine.FormNonces(t).IssueAsync(Scope, Nonce, Key, Life, Cap);
        Assert.True(await Engine.FormNonces(t).ConsumeAsync(Scope, Nonce, Key));
        Assert.False(await Engine.FormNonces(t).ConsumeAsync(Scope, Nonce, Key));
    }

    [EngineFact]   // the race: one statement per consume, so of many posting one form exactly one is let through
    public async Task Of_many_consumes_racing_for_one_nonce_exactly_one_wins()
    {
        await using var t = await CreateDbAsync();
        await Engine.FormNonces(t).IssueAsync(Scope, Nonce, Key, Life, Cap);
        var consumes = Enumerable.Range(0, 12).Select(_ => Task.Run(() => Engine.FormNonces(t).ConsumeAsync(Scope, Nonce, Key))).ToArray();
        var settled = await Task.WhenAll(consumes);   // every consume settled before anything is asserted
        Assert.Equal(1, settled.Count(won => won));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM form_nonces"));
    }

    [EngineFact]   // the cap under a race: the trim and the insert are one transaction, so racing issues never leave a key over it
    public async Task Of_many_issues_racing_for_one_key_the_cap_still_holds()
    {
        await using var t = await CreateDbAsync();
        var issues = Enumerable.Range(0, Cap + 10).Select(i => Task.Run(() => Engine.FormNonces(t).IssueAsync(Scope, "race-" + i, Key, Life, Cap))).ToArray();
        await Task.WhenAll(issues);   // every issue settled before anything is asserted
        Assert.Equal((long)Cap, await ScalarAsync(t, "SELECT COUNT(*) FROM form_nonces"));
    }

    [EngineFact]   // mutation: the prune deleting a live nonce (a form shown a minute ago would be refused)
    public async Task The_prune_deletes_expired_nonces_and_keeps_live_ones()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.FormNonces(t);
        await store.IssueAsync(Scope, "old", Key, TimeSpan.FromMinutes(10), Cap);
        await store.IssueAsync(Scope, "edge", Key, TimeSpan.FromMinutes(20), Cap);
        await store.IssueAsync(FormNonceScopes.Admin, Nonce, "acme", Life, Cap);
        t.Clock.Advance(TimeSpan.FromMinutes(20));   // "old" expired 10 minutes ago, "edge" expires exactly now, the admin one lives 10 more

        Assert.Equal(0, await store.PruneAsync(t.Clock.UtcNow - TimeSpan.FromMinutes(11)));
        Assert.Equal(2, await store.PruneAsync(t.Clock.UtcNow));
        Assert.Equal([FormNonceRules.Hash(Nonce)], await ColumnAsync(t, "SELECT nonce_hash FROM form_nonces"));
        Assert.True(await store.ConsumeAsync(FormNonceScopes.Admin, Nonce, "acme"));   // the live one still works after the prune
        Assert.Equal(0, await store.PruneAsync(t.Clock.UtcNow));
    }

    [EngineFact]   // nothing is written or spent for a call the store refuses to take
    public async Task A_bad_scope_nonce_key_life_or_cap_is_refused_and_nothing_is_written()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.FormNonces(t);
        await Assert.ThrowsAsync<ArgumentException>(() => store.IssueAsync("App Site", Nonce, Key, Life, Cap));
        await Assert.ThrowsAsync<ArgumentException>(() => store.IssueAsync(Scope, "", Key, Life, Cap));
        await Assert.ThrowsAsync<ArgumentException>(() => store.IssueAsync(Scope, Nonce, "", Life, Cap));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.IssueAsync(Scope, Nonce, Key, TimeSpan.Zero, Cap));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.IssueAsync(Scope, Nonce, Key, Life, 0));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM form_nonces"));
        await store.IssueAsync(Scope, Nonce, Key, Life, Cap);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ConsumeAsync("App Site", Nonce, Key));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ConsumeAsync(Scope, Nonce, ""));
        Assert.True(await store.ConsumeAsync(Scope, Nonce, Key));   // not spent by the refused calls
    }
}
