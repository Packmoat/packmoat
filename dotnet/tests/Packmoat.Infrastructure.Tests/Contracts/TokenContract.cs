using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>The token service's contract: issue, authenticate, revoke and list.</summary>
public abstract class TokenContract : StoreContract
{
    [EngineFact]
    public async Task An_issued_token_authenticates_to_its_tenant_and_looks_random()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);

        var issued = await Engine.Tokens(t).IssueAsync("acme", "ci", default);
        var auth = await Engine.Tokens(t).AuthenticateAsync(issued.Plaintext, default);

        Assert.StartsWith("pmt_", issued.Plaintext);
        Assert.Equal(52, issued.Plaintext.Length); // "pmt_" + 48 hex characters = 192 random bits
        Assert.Equal("acme", auth!.Tenant.Id);
        Assert.Equal(issued.TokenId, auth.TokenId);
        var another = await Engine.Tokens(t).IssueAsync("acme", "dev", default);
        Assert.NotEqual(issued.Plaintext, another.Plaintext);
    }

    [EngineFact]
    public async Task The_display_prefix_does_not_reveal_the_token()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        var issued = await Engine.Tokens(t).IssueAsync("acme", "ci", default);
        Assert.True(issued.DisplayPrefix.Length <= 9);
        Assert.DoesNotContain("<", issued.ToString()!.Replace("<redacted>", ""));
        Assert.DoesNotContain(issued.Plaintext, issued.ToString());
    }

    [EngineFact]
    public async Task Unknown_and_revoked_tokens_are_rejected()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        var issued = await Engine.Tokens(t).IssueAsync("acme", "ci", default);

        Assert.Null(await Engine.Tokens(t).AuthenticateAsync("pmt_" + new string('0', 48), default));
        Assert.True(await Engine.Tokens(t).RevokeAsync(issued.TokenId, default));
        Assert.Null(await Engine.Tokens(t).AuthenticateAsync(issued.Plaintext, default));
        Assert.False(await Engine.Tokens(t).RevokeAsync(issued.TokenId, default));
    }

    [EngineFact]
    public async Task Revoking_one_token_leaves_the_others_working()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        var a = await Engine.Tokens(t).IssueAsync("acme", "a", default);
        var b = await Engine.Tokens(t).IssueAsync("acme", "b", default);
        await Engine.Tokens(t).RevokeAsync(a.TokenId, default);
        Assert.NotNull(await Engine.Tokens(t).AuthenticateAsync(b.Plaintext, default));
    }

    [EngineFact]
    public async Task A_suspended_tenant_kills_its_tokens_and_reactivation_restores_them()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        var issued = await Engine.Tokens(t).IssueAsync("acme", "ci", default);

        await Engine.Tenants(t).SetActiveAsync("acme", false, default);
        Assert.Null(await Engine.Tokens(t).AuthenticateAsync(issued.Plaintext, default));
        await Engine.Tenants(t).SetActiveAsync("acme", true, default);
        Assert.NotNull(await Engine.Tokens(t).AuthenticateAsync(issued.Plaintext, default));
    }

    [EngineFact]
    public async Task Issuing_for_a_missing_tenant_fails()
    {
        await using var t = await CreateDbAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Engine.Tokens(t).IssueAsync("nobody", "x", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Engine.Tokens(t).IssueForMemberAsync("nobody", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            "cccccccc-cccc-4ccc-8ccc-cccccccccccc", "x", t.Clock.UtcNow.AddDays(1), TokenKind.Developer, 5, default));
    }

    /// <summary>
    /// The issue paths ask only whether the tenant row exists (<c>ExistsAsync</c>, no <c>active=1</c>). The operator may
    /// prepare tokens for a suspended tenant; they authenticate only once it is active again. The customer site refuses a suspended tenant
    /// itself (CustomerTokenGuard), before this is reached.
    /// </summary>
    [EngineFact]   // mutation: add "AND active=1" to SqliteTenantStore.ExistsAsync
    public async Task A_suspended_tenant_can_still_be_issued_tokens_which_work_once_it_is_active()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        await Engine.Tenants(t).SetActiveAsync("acme", false, default);

        var op = await Engine.Tokens(t).IssueAsync("acme", "ci", default);
        var member = await Engine.Tokens(t).IssueForMemberAsync("acme", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "cccccccc-cccc-4ccc-8ccc-cccccccccccc", "laptop",
            t.Clock.UtcNow.AddDays(90), TokenKind.Developer, 5, default);

        Assert.NotNull(member);
        Assert.Null(await Engine.Tokens(t).AuthenticateAsync(op.Plaintext, default));
        await Engine.Tenants(t).SetActiveAsync("acme", true, default);
        Assert.NotNull(await Engine.Tokens(t).AuthenticateAsync(op.Plaintext, default));
        Assert.NotNull(await Engine.Tokens(t).AuthenticateAsync(member!.Plaintext, default));
    }

    [EngineFact]
    public async Task Listing_shows_state_without_secrets()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        var issued = await Engine.Tokens(t).IssueAsync("acme", "ci", default);
        await Engine.Tokens(t).AuthenticateAsync(issued.Plaintext, default);
        await Engine.Tokens(t).RevokeAsync(issued.TokenId, default);

        var info = Assert.Single(await Engine.Tokens(t).ListAsync("acme", default));

        Assert.Equal("ci", info.Label);
        Assert.NotNull(info.RevokedAt);
        Assert.NotNull(info.LastUsedAt);
        Assert.DoesNotContain(issued.Plaintext, info.ToString());
    }

    [EngineFact]
    public async Task Tenant_scoped_revoke_cannot_touch_another_tenants_token()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        await AddTenantAsync(t, "other");
        var mine = await Engine.Tokens(t).IssueAsync("acme", "mine", default);
        var theirs = await Engine.Tokens(t).IssueAsync("other", "theirs", default);

        Assert.False(await Engine.Tokens(t).RevokeAsync("acme", theirs.TokenId, default));   // wrong tenant: nothing changes
        Assert.NotNull(await Engine.Tokens(t).AuthenticateAsync(theirs.Plaintext, default));
        Assert.False(await Engine.Tokens(t).RevokeAsync("other", "tok_nope", default));
        Assert.True(await Engine.Tokens(t).RevokeAsync("acme", mine.TokenId, default));
        Assert.Null(await Engine.Tokens(t).AuthenticateAsync(mine.Plaintext, default));
        Assert.False(await Engine.Tokens(t).RevokeAsync("acme", mine.TokenId, default));      // already revoked
    }

    /// <summary>
    /// The per-member cap holds under concurrency: twenty issues at once for one member, with a cap of five, leave
    /// exactly five live tokens; every other issue is refused (null), never a sixth row.
    /// </summary>
    [EngineFact]   // mutations: the tenant row's FOR NO KEY UPDATE dropped from the member issue (PostgreSQL); a deferred transaction (SQLite)
    public async Task Twenty_concurrent_member_issues_leave_exactly_the_cap()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        const int max = 5;
        const string tid = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", oid = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";

        var issued = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            Engine.Tokens(t).IssueForMemberAsync("acme", tid, oid, "laptop" + i, t.Clock.UtcNow.AddDays(90), TokenKind.Developer, max, default))));

        Assert.Equal(max, issued.Count(x => x is not null));
        Assert.Equal((long)max, await ScalarAsync(t, $"SELECT COUNT(*) FROM tokens WHERE tenant_id='acme' AND owner_oid='{oid}'"));
        Assert.Equal(max, (await Engine.Tokens(t).ListForMemberAsync("acme", tid, oid, default)).Count);
    }

    // ---------------------------------------------------------------- member tokens (from MemberTokenContract)

    protected const string Dir = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    protected const string Ada = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    protected const string Bob = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";

    protected Task<IssuedToken?> IssueFor(IEngineDb t, string oid, DateTimeOffset expires, int max = 5, string tenant = "acme", TokenKind kind = TokenKind.Developer, string label = "laptop") =>
        Engine.Tokens(t).IssueForMemberAsync(tenant, Dir, oid, label, expires, kind, max, default);

    [EngineFact]   // mutation: the expiry check `e <= now` as `e < now` (a token at its expiry still authenticates)
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

    [EngineFact]
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

    [EngineFact]
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

    [EngineFact]
    public async Task Concurrent_issues_never_pass_the_cap()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => IssueFor(t, Ada, t.Clock.UtcNow.AddDays(90)))));
        Assert.Equal(5, results.Count(r => r is not null));
        Assert.Equal(5L, await ScalarAsync(t, "SELECT COUNT(*) FROM tokens WHERE issued_via='self'"));
    }

    [EngineFact]   // mutation: RevokeForMemberAsync without its owner_tid/owner_oid scope (one member revokes another's token)
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

    [EngineFact]
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

    [EngineFact]
    public async Task An_expiry_in_the_past_is_refused_by_the_store()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        await Assert.ThrowsAsync<ArgumentException>(() => IssueFor(t, Ada, t.Clock.UtcNow));
    }

    /// <summary>
    /// The member issue's errors come in one order on every engine: a missing tenant is reported as that, even when the expiry is in the
    /// past too (the customer site maps the two to different answers).
    /// </summary>
    [EngineFact]   // mutation: the expiry checked before the tenant (PostgresTokenService.IssueForMemberAsync)
    public async Task A_member_issue_for_a_missing_tenant_with_a_past_expiry_fails_as_a_missing_tenant()
    {
        await using var t = await CreateDbAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => IssueFor(t, Ada, t.Clock.UtcNow.AddDays(-1), tenant: "nobody"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => IssueFor(t, Ada, t.Clock.UtcNow, tenant: "nobody"));
        Assert.Equal(0L, await ScalarAsync(t, "SELECT COUNT(*) FROM tokens"));
    }
}
