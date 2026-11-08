using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// Members and the tokens they own (migration 8) as a contract (#232 S1): expiry, the cap, issued-via and revoked-by, and the store.
/// Migration 8, bytes on disk, backup and the CLI list stay in <see cref="MemberTokenTests"/>.
/// </summary>
public abstract class MemberTokenContract : StoreContract
{
    protected const string Dir = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    protected const string Ada = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    protected const string Bob = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";

    protected Task<IssuedToken?> IssueFor(IEngineDb t, string oid, DateTimeOffset expires, int max = 5, string tenant = "acme", TokenKind kind = TokenKind.Developer, string label = "laptop") =>
        Engine.Tokens(t).IssueForMemberAsync(tenant, Dir, oid, label, expires, kind, max, default);

    [Fact]
    public async Task A_token_past_its_expiry_does_not_authenticate()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        var issued = (await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(90)))!;
        Assert.NotNull(await Engine.Tokens(t).AuthenticateAsync(issued.Plaintext, default));
        t.Clock.Advance(TimeSpan.FromDays(90) - TimeSpan.FromSeconds(1));
        var ok = await Engine.Tokens(t).AuthenticateAsync(issued.Plaintext, default);
        Assert.Equal(t.Clock.UtcNow.AddSeconds(1), ok!.TokenExpiresAt);
        t.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await Engine.Tokens(t).AuthenticateAsync(issued.Plaintext, default));
    }

    [Fact]
    public async Task Through_the_cached_authenticator_an_expired_token_stops_within_the_cache_window()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        var issued = (await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(1)))!;
        var cached = new CachedTenantAuthenticator(Engine.Tokens(t), t.Clock);
        t.Clock.Advance(TimeSpan.FromDays(1) - TimeSpan.FromSeconds(2));
        Assert.NotNull(await cached.AuthenticateAsync(issued.Plaintext, default));   // cached now, two seconds before expiry
        t.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(await cached.AuthenticateAsync(issued.Plaintext, default));      // at expiry: refused, though the 5-second window has not passed
        t.Clock.Advance(CachedTenantAuthenticator.DefaultLifetime);
        Assert.Null(await cached.AuthenticateAsync(issued.Plaintext, default));
    }

    [Fact]
    public async Task The_store_enforces_the_cap_per_person_counting_only_active_self_issued_tokens()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        await AddTenantAsync(t, "globex");
        await Engine.Tokens(t).IssueAsync("acme", "operator", default);   // operator-issued: never counted
        Assert.NotNull(await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(1)));
        Assert.NotNull(await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(90), kind: TokenKind.Automation));
        Assert.Null(await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(90), max: 2));
        Assert.NotNull(await IssueFor(t, Bob, t.Clock.UtcNow.AddDays(90), max: 2));              // someone else
        Assert.NotNull(await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(90), max: 2, tenant: "globex"));   // another tenant
        t.Clock.Advance(TimeSpan.FromDays(1));                                                       // the first one expires
        Assert.NotNull(await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(90), max: 2));
        Assert.Null(await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(90), max: 2));
        var id = (await Engine.Tokens(t).ListForMemberAsync("acme", Dir, Ada, default)).First(x => x.IsActiveAt(t.Clock.UtcNow)).TokenId;
        Assert.True(await Engine.Tokens(t).RevokeForMemberAsync("acme", Dir, Ada, id, "Ada", default));   // a revoked one frees a place
        Assert.NotNull(await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(90), max: 2));
    }

    [Fact]
    public async Task Concurrent_issues_never_pass_the_cap()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => IssueFor(t, Ada, t.Clock.UtcNow.AddDays(90)))));
        Assert.Equal(5, results.Count(r => r is not null));
        Assert.Equal(5L, await ScalarAsync(t, "SELECT COUNT(*) FROM tokens WHERE issued_via='self'"));
    }

    [Fact]
    public async Task Revoke_for_member_needs_the_owner_and_the_tenant_and_records_who()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        await AddTenantAsync(t, "globex");
        var adas = (await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(90)))!;
        Assert.False(await Engine.Tokens(t).RevokeForMemberAsync("acme", Dir, Bob, adas.TokenId, "Bob", default));       // not the owner
        Assert.False(await Engine.Tokens(t).RevokeForMemberAsync("globex", Dir, Ada, adas.TokenId, "Ada", default));     // not the tenant
        Assert.False(await Engine.Tokens(t).RevokeInTenantAsync("globex", adas.TokenId, "Mallory", default));            // admin of another tenant
        Assert.NotNull(await Engine.Tokens(t).AuthenticateAsync(adas.Plaintext, default));
        Assert.True(await Engine.Tokens(t).RevokeInTenantAsync("acme", adas.TokenId, "Grace (ffffffff-ffff-4fff-8fff-ffffffffffff)", default));
        Assert.Null(await Engine.Tokens(t).AuthenticateAsync(adas.Plaintext, default));
        Assert.Equal("Grace (ffffffff-ffff-4fff-8fff-ffffffffffff)", Assert.Single(await Engine.Tokens(t).ListAsync("acme", default)).RevokedBy);
        Assert.False(await Engine.Tokens(t).RevokeInTenantAsync("acme", adas.TokenId, "again", default));
    }

    [Fact]
    public async Task List_for_member_returns_only_that_persons_tokens_in_that_tenant()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        await AddTenantAsync(t, "globex");
        await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(1), label: "ada");
        await IssueFor(t, Bob, t.Clock.UtcNow.AddDays(1), label: "bob");
        await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(1), tenant: "globex", label: "ada-globex");
        await Engine.Tokens(t).IssueAsync("acme", "operator", default);
        Assert.Equal(new[] { "ada" }, (await Engine.Tokens(t).ListForMemberAsync("acme", Dir, Ada, default)).Select(x => x.Label));
    }

    [Fact]
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

    [Fact]
    public async Task A_failed_unbind_revokes_nothing()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        var store = Engine.SignInDirectory(t);
        var adas = (await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(90)))!;
        Assert.Null(await store.UnbindAsync("acme", Dir, t.Clock.UtcNow, "op", default));   // never linked
        Assert.NotNull(await Engine.Tokens(t).AuthenticateAsync(adas.Plaintext, default));
    }

    [Fact]
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

    [Fact]
    public async Task An_expiry_in_the_past_is_refused_by_the_store()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        await Assert.ThrowsAsync<ArgumentException>(() => IssueFor(t, Ada, t.Clock.UtcNow));
    }

    [Fact]
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
        await RefusedAsync(() => ScalarAsync(t, $"INSERT INTO members VALUES('{Dir.ToUpperInvariant()}','{Bob}','acme','x','t','t')"));
        await RefusedAsync(() => members.UpsertAsync(Dir, Bob, "nobody", "x", first, default));   // unknown tenant
    }

    [Fact]
    public async Task Self_issued_developer_tokens_count_as_active_developers_and_automation_tokens_do_not()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        var activity = Engine.TokenActivity(t);
        var dev = (await IssueFor(t, Ada, t.Clock.UtcNow.AddDays(90)))!;
        var auto = (await IssueFor(t, Bob, t.Clock.UtcNow.AddDays(90), kind: TokenKind.Automation))!;
        var op = await Engine.Tokens(t).IssueAsync("acme", "operator-dev", default);
        var today = DateOnly.FromDateTime(t.Clock.UtcNow.UtcDateTime);
        foreach (var id in new[] { dev.TokenId, auto.TokenId, op.TokenId }) await activity.RecordAsync(id, today, default);
        var active = await activity.GetAsync("acme", 30, default);
        Assert.Equal(2, active.Count);
        Assert.Equal(new[] { dev.TokenId, op.TokenId }.Order(), active.Tokens.Select(x => x.TokenId).Order());
    }
}
