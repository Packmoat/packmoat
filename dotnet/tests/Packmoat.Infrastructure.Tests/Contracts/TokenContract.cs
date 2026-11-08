using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

/// <summary>The token service's contract (#232 S1): issue, authenticate, revoke and list.</summary>
public abstract class TokenContract : StoreContract
{
    [Fact]
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

    [Fact]
    public async Task The_display_prefix_does_not_reveal_the_token()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        var issued = await Engine.Tokens(t).IssueAsync("acme", "ci", default);
        Assert.True(issued.DisplayPrefix.Length <= 9);
        Assert.DoesNotContain("<", issued.ToString()!.Replace("<redacted>", ""));
        Assert.DoesNotContain(issued.Plaintext, issued.ToString());
    }

    [Fact]
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

    [Fact]
    public async Task Revoking_one_token_leaves_the_others_working()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        var a = await Engine.Tokens(t).IssueAsync("acme", "a", default);
        var b = await Engine.Tokens(t).IssueAsync("acme", "b", default);
        await Engine.Tokens(t).RevokeAsync(a.TokenId, default);
        Assert.NotNull(await Engine.Tokens(t).AuthenticateAsync(b.Plaintext, default));
    }

    [Fact]
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

    [Fact]
    public async Task Issuing_for_a_missing_tenant_fails()
    {
        await using var t = await CreateDbAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Engine.Tokens(t).IssueAsync("nobody", "x", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Engine.Tokens(t).IssueForMemberAsync("nobody", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            "cccccccc-cccc-4ccc-8ccc-cccccccccccc", "x", t.Clock.UtcNow.AddDays(1), TokenKind.Developer, 5, default));
    }

    /// <summary>
    /// #161 review: the issue paths ask only whether the tenant row exists (<c>ExistsAsync</c>, no <c>active=1</c>). The operator may
    /// prepare tokens for a suspended tenant; they authenticate only once it is active again. The customer site refuses a suspended tenant
    /// itself (CustomerTokenGuard), before this is reached.
    /// </summary>
    [Fact]   // mutation: add "AND active=1" to SqliteTenantStore.ExistsAsync
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

    [Fact]
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

    [Fact]
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
}
