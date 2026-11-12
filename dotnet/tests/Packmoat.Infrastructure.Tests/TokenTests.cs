using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary><see cref="TokenContract"/> on SQLite, and the tests only SQLite has.</summary>
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

/// <summary><see cref="TokenContract"/> on PostgreSQL, each test on a database of its own.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestServer.Category)]
public class PostgresTokenTests : TokenContract
{
    protected override IStoreEngine Engine => PostgresStoreEngine.Instance;

    [PostgresFact]   // mutation: the plaintext stored in token_hash or the label
    public async Task Only_a_hash_is_stored_never_the_token()
    {
        await using var t = await Engine.CreateAsync();
        await AddTenantAsync(t);
        var issued = await Engine.Tokens(t).IssueAsync("acme", "ci", default);
        await Engine.Tokens(t).AuthenticateAsync(issued.Plaintext, default);

        Assert.Equal([TokenHasher.Hash(issued.Plaintext)], await Engine.ColumnAsync(t, "SELECT token_hash FROM tokens"));
        Assert.Equal(0L, await Engine.ScalarAsync(t, $"SELECT COUNT(*) FROM tokens WHERE strpos(tokens::text, '{issued.Plaintext}') > 0"));
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
}
