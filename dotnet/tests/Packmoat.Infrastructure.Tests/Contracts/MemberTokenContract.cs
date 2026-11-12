using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// Members and the tokens they own (migration 8) as a contract: what needs the member or sign-in directory stores, which
/// PostgreSQL has. The member token's own behaviour (expiry, the cap, revoke for a member, the member's list), which
/// needs only the tenant store and the token service, is in <see cref="TokenContract"/>; the count of self-issued tokens
/// as active developers, which needs the token-activity store, is in <see cref="DeveloperCountingContract"/>. Migration 8,
/// bytes on disk, backup and the CLI list stay in <see cref="MemberTokenTests"/>.
/// </summary>
public abstract class MemberTokenContract : StoreContract
{
    protected const string Dir = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    protected const string Ada = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    protected const string Bob = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";

    protected Task<IssuedToken?> IssueFor(IEngineDb t, string oid, DateTimeOffset expires, int max = 5, string tenant = "acme", TokenKind kind = TokenKind.Developer, string label = "laptop") =>
        Engine.Tokens(t).IssueForMemberAsync(tenant, Dir, oid, label, expires, kind, max, default);

    [EngineFact]
    public async Task Unbinding_a_directory_revokes_its_self_issued_tokens_in_that_tenant_in_the_same_transaction()
    {
        const string Other = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        await AddTenantAsync(t, "globex");
        var store = Engine.SignInDirectory(t);
        await store.BindAsync(new DirectoryBinding(Dir, "acme", t.Clock.UtcNow, "op"), default);
        var adas = (await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(90)))!;
        var bobs = (await IssueFor(t, Bob, t.Clock.UtcNow.AddDays(90), kind: TokenKind.Automation))!;
        var elsewhere = (await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(90), tenant: "globex"))!;          // same directory, other tenant
        var otherDir = (await Engine.Tokens(t).IssueForMemberAsync("acme", Other, Ada, "x", t.Clock.UtcNow.AddDays(9), TokenKind.Developer, 5, default))!;
        var operatorIssued = await Engine.Tokens(t).IssueAsync("acme", "operator", default);

        Assert.Null(await store.UnbindAsync("globex", Dir, t.Clock.UtcNow, "op", default));   // not linked there: nothing changes
        Assert.NotNull(await Engine.Tokens(t).AuthenticateAsync(adas.Plaintext, default));

        Assert.Equal(2, await store.UnbindAsync("acme", Dir, t.Clock.UtcNow, "operator founder (directory unlinked)", default));
        Assert.Null(await Engine.Tokens(t).AuthenticateAsync(adas.Plaintext, default));
        Assert.Null(await Engine.Tokens(t).AuthenticateAsync(bobs.Plaintext, default));
        foreach (var still in new[] { elsewhere, otherDir, operatorIssued }) Assert.NotNull(await Engine.Tokens(t).AuthenticateAsync(still.Plaintext, default));
        Assert.All((await Engine.Tokens(t).ListForMemberAsync("acme", Dir, Ada, default)), x => Assert.Equal("operator founder (directory unlinked)", x.RevokedBy));
        Assert.Null(await store.GetAsync(Dir, default));
    }

    /// <summary>
    /// The unbind revokes only self-issued tokens (<c>issued_via='self'</c>), a second guard no test reached. An
    /// operator-issued token that carries the directory as its owner (written here directly; no port issues one) is left working.
    /// </summary>
    [EngineFact]   // mutation: the unbind's revoke without its issued_via='self' clause
    public async Task An_unbind_leaves_an_operator_issued_token_working_even_one_carrying_the_directory_as_owner()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        var store = Engine.SignInDirectory(t);
        await store.BindAsync(new DirectoryBinding(Dir, "acme", t.Clock.UtcNow, "op"), default);
        var adas = (await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(90)))!;
        var operatorIssued = await Engine.Tokens(t).IssueAsync("acme", "operator", default);
        await ExecAsync(t, $"UPDATE tokens SET owner_tid='{Dir}', owner_oid='{Bob}' WHERE id='{operatorIssued.TokenId}'");
        Assert.Equal("operator", await ScalarAsync(t, $"SELECT issued_via FROM tokens WHERE id='{operatorIssued.TokenId}'"));

        Assert.Equal(1, await store.UnbindAsync("acme", Dir, t.Clock.UtcNow, "op", default));

        Assert.Null(await Engine.Tokens(t).AuthenticateAsync(adas.Plaintext, default));
        Assert.NotNull(await Engine.Tokens(t).AuthenticateAsync(operatorIssued.Plaintext, default));
        Assert.Equal(DBNull.Value, await ScalarAsync(t, $"SELECT revoked_at FROM tokens WHERE id='{operatorIssued.TokenId}'"));
    }

    [EngineFact]
    public async Task A_failed_unbind_revokes_nothing()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        var store = Engine.SignInDirectory(t);
        var adas = (await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(90)))!;
        Assert.Null(await store.UnbindAsync("acme", Dir, t.Clock.UtcNow, "op", default));   // never linked
        Assert.NotNull(await Engine.Tokens(t).AuthenticateAsync(adas.Plaintext, default));
    }

    [EngineFact]
    public async Task The_token_list_carries_the_owners_last_seen_time()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        var members = Engine.Members(t);
        await members.UpsertAsync(Dir, Ada, "acme", "Ada", t.Clock.UtcNow, default);
        await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(90));
        await members.UpsertAsync(Dir, Ada, "acme", "Ada", t.Clock.UtcNow.AddDays(2), default);
        Assert.Equal(t.Clock.UtcNow.AddDays(2), Assert.Single(await Engine.Tokens(t).ListAsync("acme", default)).OwnerLastSeenAt);
    }

    [EngineFact]
    public async Task Member_upsert_keeps_first_seen_and_updates_the_rest_and_the_schema_refuses_non_canonical_ids()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        var members = Engine.Members(t);
        var first = t.Clock.UtcNow;
        await members.UpsertAsync(Dir.ToUpperInvariant(), Ada, "acme", "Ada", first, default);
        await members.UpsertAsync(Dir, Ada.ToUpperInvariant(), "acme", "Ada L.", first.AddDays(3), default);
        var m = (await members.GetAsync(Dir, Ada, default))!;
        Assert.Equal((Dir, Ada, "acme", "Ada L.", first, first.AddDays(3)), (m.AzureTenantId, m.ObjectId, m.TenantId, m.DisplayName, m.FirstSeenAt, m.LastSeenAt));
        Assert.Single(await members.ListForTenantAsync("acme", default));
        Assert.Null(await members.GetAsync("not-a-guid", Ada, default));
        await Assert.ThrowsAsync<ArgumentException>(() => members.UpsertAsync("nope", Ada, "acme", "x", first, default));
        // The times are real ones, which PostgreSQL can read, so the refusal is the CHECK's and not a malformed value's.
        const string Time = "'2026-01-01T00:00:00.0000000+00:00'";
        await RefusedAsync(() => ScalarAsync(t, $"INSERT INTO members VALUES('{Dir.ToUpperInvariant()}','{Bob}','acme','x',{Time},{Time})"));
        await RefusedAsync(() => members.UpsertAsync(Dir, Bob, "nobody", "x", first, default));   // unknown tenant
    }

    [EngineFact]   // mutations: members.display_name without COLLATE "C" (PostgreSQL); the list without its tenant_id scope
    public async Task A_tenants_members_are_listed_by_the_bytes_of_their_names_then_by_object_id()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        await AddTenantAsync(t, "globex");
        var members = Engine.Members(t);
        // Written out of order. By bytes (SQLite's BINARY, PostgreSQL's "C"): upper case before lower case, and "É" (0xC3 0x89) after both.
        // A language's collation would put them ann, Émile, Zed.
        string[] oids = ["11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222", "33333333-3333-4333-8333-333333333333",
            "44444444-4444-4444-8444-444444444444"];
        await members.UpsertAsync(Dir, oids[3], "acme", "Émile", t.Clock.UtcNow, default);
        await members.UpsertAsync(Dir, oids[2], "acme", "ann", t.Clock.UtcNow, default);
        await members.UpsertAsync(Dir, oids[1], "acme", "Zed", t.Clock.UtcNow, default);
        await members.UpsertAsync(Dir, oids[0], "acme", "ann", t.Clock.UtcNow, default);   // the same name: the object id decides
        await members.UpsertAsync(Dir, Bob, "globex", "Aaron", t.Clock.UtcNow, default);  // another tenant's: never listed

        var list = await members.ListForTenantAsync("acme", default);
        Assert.Equal(["Zed", "ann", "ann", "Émile"], list.Select(m => m.DisplayName));
        Assert.Equal([oids[1], oids[0], oids[2], oids[3]], list.Select(m => m.ObjectId));
        Assert.Equal(["Aaron"], (await members.ListForTenantAsync("globex", default)).Select(m => m.DisplayName));
    }
}
