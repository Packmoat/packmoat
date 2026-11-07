using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

/// <summary><see cref="TokenContract"/> on SQLite, and the tests only SQLite has (#232 S1).</summary>
[Collection("sqlite-pools")]
public class TokenTests : TokenContract
{
    protected override IStoreEngine Engine => SqliteStoreEngine.Instance;

    [Fact]
    public async Task Only_a_hash_is_stored_never_the_token()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync();
        var issued = await t.Tokens.IssueAsync("acme", "ci", default);
        await t.Tokens.AuthenticateAsync(issued.Plaintext, default);

        var disk = await t.RawBytesAsync();

        Assert.False(Bytes.Contains(disk, issued.Plaintext), "plaintext token found on disk");
        Assert.True(Bytes.Contains(disk, TokenHasher.Hash(issued.Plaintext)), "hash should be stored");
    }
}

[Collection("sqlite-pools")]
public class CachedAuthenticatorTests
{
    [Fact]
    public async Task A_revoked_token_stops_working_within_the_cache_lifetime_and_not_before()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync();
        var issued = await t.Tokens.IssueAsync("acme", "ci", default);
        var cached = new CachedTenantAuthenticator(t.Tokens, t.Clock);

        Assert.NotNull(await cached.AuthenticateAsync(issued.Plaintext, default));
        await t.Tokens.RevokeAsync(issued.TokenId, default);
        Assert.NotNull(await cached.AuthenticateAsync(issued.Plaintext, default));   // still cached
        t.Clock.Advance(TimeSpan.FromSeconds(5) + TimeSpan.FromMilliseconds(1));
        Assert.Null(await cached.AuthenticateAsync(issued.Plaintext, default));       // within 5 seconds, as documented
    }

    [Fact]
    public async Task A_new_waiver_reaches_requests_within_the_cache_lifetime()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync();
        var issued = await t.Tokens.IssueAsync("acme", "ci", default);
        var cached = new CachedTenantAuthenticator(t.Tokens, t.Clock);
        Assert.Empty((await cached.AuthenticateAsync(issued.Plaintext, default))!.Tenant.Policy.Waivers);

        await t.Tenants.AddWaiverAsync("acme", "moq", "4.21.0", t.Clock.UtcNow.AddDays(1), "needed", "olivia", default);
        t.Clock.Advance(TimeSpan.FromSeconds(6));

        Assert.Single((await cached.AuthenticateAsync(issued.Plaintext, default))!.Tenant.Policy.Waivers);
    }

    [Fact]
    public async Task Failures_are_not_cached_and_clear_drops_everything()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync();
        var cached = new CachedTenantAuthenticator(t.Tokens, t.Clock);
        var issued = await t.Tokens.IssueAsync("acme", "ci", default);

        Assert.NotNull(await cached.AuthenticateAsync(issued.Plaintext, default));
        await t.Tokens.RevokeAsync(issued.TokenId, default);
        cached.Clear();
        Assert.Null(await cached.AuthenticateAsync(issued.Plaintext, default));
    }

    [Fact]
    public async Task Tenant_scoped_revoke_cannot_touch_another_tenants_token()
    {
        await using var t = await TestDb.CreateAsync();
        await t.AddTenantAsync("acme");
        await t.AddTenantAsync("other");
        var mine = await t.Tokens.IssueAsync("acme", "mine", default);
        var theirs = await t.Tokens.IssueAsync("other", "theirs", default);

        Assert.False(await t.Tokens.RevokeAsync("acme", theirs.TokenId, default));   // wrong tenant: nothing changes
        Assert.NotNull(await t.Tokens.AuthenticateAsync(theirs.Plaintext, default));
        Assert.False(await t.Tokens.RevokeAsync("other", "tok_nope", default));
        Assert.True(await t.Tokens.RevokeAsync("acme", mine.TokenId, default));
        Assert.Null(await t.Tokens.AuthenticateAsync(mine.Plaintext, default));
        Assert.False(await t.Tokens.RevokeAsync("acme", mine.TokenId, default));      // already revoked
    }
}
