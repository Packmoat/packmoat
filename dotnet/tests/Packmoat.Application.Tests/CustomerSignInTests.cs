using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Application.Tests;

/// <summary>The customer sign-in decision table: who gets in, as which tenant and role, and every way to be refused.</summary>
public class CustomerSignInTests
{
    private const string Audience = "55555555-5555-4555-8555-555555555555";
    private const string Dir = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string Other = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string Oid = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    private const string Consumer = "9188040d-6c67-4c5b-b112-36a304b66dad";
    private static readonly DirectoryBinding Acme = new(Dir, "acme", DateTimeOffset.UnixEpoch, "op");

    private static string Iss(string tid) => $"https://login.microsoftonline.com/{tid}/v2.0";

    private static VerifiedClaims Claims(string tid = Dir, string? iss = null, string aud = Audience, string oid = Oid, string? name = "Ada", params string[] roles) =>
        new(new[] { iss ?? Iss(tid) }, new[] { aud }, new[] { tid }, new[] { oid }, name, roles);

    private static SignInDecision.Denied Denied(SignInDecision d) => Assert.IsType<SignInDecision.Denied>(d);
    private static SignInDecision.Allowed Allowed(SignInDecision d) => Assert.IsType<SignInDecision.Allowed>(d);

    [Fact]
    public void A_developer_of_a_linked_directory_gets_in_as_developer_of_the_bound_tenant()
    {
        var a = Allowed(CustomerSignIn.Decide(Claims(roles: CustomerSignIn.DeveloperRole), Audience, Acme));
        Assert.Equal(CustomerRole.Developer, a.Role);
        Assert.Equal("acme", a.TenantId);
        Assert.Equal(Guid.Parse(Oid), a.ObjectId);
        Assert.Equal(Guid.Parse(Dir), a.DirectoryId);
        Assert.Equal("Ada", a.DisplayName);
    }

    [Fact]
    public void An_admin_gets_in_as_admin() =>
        Assert.Equal(CustomerRole.Admin, Allowed(CustomerSignIn.Decide(Claims(roles: CustomerSignIn.AdminRole), Audience, Acme)).Role);

    [Fact]
    public void Both_roles_mean_admin() =>
        Assert.Equal(CustomerRole.Admin, Allowed(CustomerSignIn.Decide(Claims(roles: new[] { CustomerSignIn.DeveloperRole, CustomerSignIn.AdminRole }), Audience, Acme)).Role);

    [Theory]
    [InlineData()]
    [InlineData("Packmoat.Reader")]
    [InlineData("packmoat.admin")]   // role values are case-sensitive, as Entra sends them
    public void No_packmoat_role_is_refused(params string[] roles) =>
        Assert.Equal(SignInDenial.NoRole, Denied(CustomerSignIn.Decide(Claims(roles: roles), Audience, Acme)).Reason);

    [Fact]
    public void An_unlinked_directory_is_refused_and_named()
    {
        var d = Denied(CustomerSignIn.Decide(Claims(roles: CustomerSignIn.AdminRole), Audience, null));
        Assert.Equal(SignInDenial.DirectoryNotLinked, d.Reason);
        Assert.Equal(Dir, d.DirectoryId);
    }

    [Fact]
    public void A_binding_for_another_directory_does_not_count() =>
        Assert.Equal(SignInDenial.DirectoryNotLinked,
            Denied(CustomerSignIn.Decide(Claims(roles: CustomerSignIn.AdminRole), Audience, Acme with { AzureTenantId = Other })).Reason);

    [Fact]
    public void The_consumer_directory_is_refused_even_if_someone_bound_it()
    {
        var bound = new DirectoryBinding(Consumer, "acme", DateTimeOffset.UnixEpoch, "op");
        Assert.Equal(SignInDenial.ConsumerDirectory, Denied(CustomerSignIn.Decide(Claims(tid: Consumer, roles: CustomerSignIn.AdminRole), Audience, bound)).Reason);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb/v2.0")]   // another directory's issuer
    [InlineData("https://sts.windows.net/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa/")]                 // v1 issuer
    [InlineData("https://login.microsoftonline.com/{tenantid}/v2.0")]                             // the metadata template itself
    [InlineData("https://login.microsoftonline.com/common/v2.0")]
    [InlineData("https://login.microsoftonline.com/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa/v2.0/")]  // trailing slash
    [InlineData("http://login.microsoftonline.com/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa/v2.0")]
    public void An_issuer_that_is_not_exactly_the_tokens_own_directory_is_refused(string issuer) =>
        Assert.Equal(SignInDenial.IssuerMismatch, Denied(CustomerSignIn.Decide(Claims(iss: issuer, roles: CustomerSignIn.AdminRole), Audience, Acme)).Reason);

    [Fact]
    public void A_wrong_audience_is_refused()
    {
        Assert.Equal(SignInDenial.WrongAudience, Denied(CustomerSignIn.Decide(Claims(aud: Other, roles: CustomerSignIn.AdminRole), Audience, Acme)).Reason);
        var two = Claims(roles: CustomerSignIn.AdminRole) with { Audiences = new[] { Audience, Other } };
        Assert.Equal(SignInDenial.WrongAudience, Denied(CustomerSignIn.Decide(two, Audience, Acme)).Reason);
        Assert.Equal(SignInDenial.WrongAudience, Denied(CustomerSignIn.Decide(Claims(roles: CustomerSignIn.AdminRole), "", Acme)).Reason);
    }

    [Fact]
    public void A_missing_or_doubled_identity_is_refused()
    {
        var c = Claims(roles: CustomerSignIn.AdminRole);
        Assert.Equal(SignInDenial.MissingIdentity, Denied(CustomerSignIn.Decide(c with { ObjectIds = Array.Empty<string>() }, Audience, Acme)).Reason);
        Assert.Equal(SignInDenial.MissingIdentity, Denied(CustomerSignIn.Decide(c with { DirectoryIds = new[] { Dir, Other } }, Audience, Acme)).Reason);
        Assert.Equal(SignInDenial.MissingIdentity, Denied(CustomerSignIn.Decide(c with { ObjectIds = new[] { "not-a-guid" } }, Audience, Acme)).Reason);
        Assert.Equal(SignInDenial.MissingIdentity, Denied(CustomerSignIn.Decide(c with { DirectoryIds = new[] { Guid.Empty.ToString() } }, Audience, Acme)).Reason);
    }

    [Fact]
    public void An_upper_case_directory_id_still_matches_its_lower_case_binding()
    {
        var upper = Dir.ToUpperInvariant();
        var c = Claims(tid: upper, iss: Iss(Dir), roles: CustomerSignIn.DeveloperRole);
        Assert.Equal("acme", Allowed(CustomerSignIn.Decide(c, Audience, Acme)).TenantId);
        Assert.Equal(Dir, CustomerSignIn.CanonicalDirectory("  " + upper + " "));
        Assert.Null(CustomerSignIn.CanonicalDirectory("acme"));
        Assert.Null(CustomerSignIn.CanonicalDirectory(Guid.Empty.ToString()));
    }

    [Fact]
    public void A_customer_actor_can_reach_only_its_own_tenant()
    {
        var actor = new CustomerActor("acme", Guid.Parse(Oid), CustomerRole.Admin, "Ada");
        Assert.False(actor.Scope.IsOperator);
        Assert.True(actor.Scope.CanAccess("acme"));
        Assert.False(actor.Scope.CanAccess("globex"));
        Assert.Contains(Oid, actor.Scope.Name);
    }

    [Fact]
    public async Task Customer_queries_read_through_the_tenant_scope_and_count_like_the_cli()
    {
        var store = new FakeStore();
        store.Tenants.Add(new Tenant("acme", "Acme Ltd", "https://pkgs.dev.azure.com/", "o/", "https://x/index.json", new TenantPolicy(14)));
        var queries = new CustomerQueries(new AdminQueries(store, store, store, store), ServeAllGate.Instance);
        var home = await queries.HomeAsync(new CustomerActor("acme", Guid.Parse(Oid), CustomerRole.Developer, "Ada"), default);
        Assert.NotNull(home);
        Assert.Equal("Acme Ltd", home!.TenantName);
        Assert.Equal(30, home.Days);
        Assert.Equal(14, home.CooldownDays);
        Assert.Contains("active acme 30", store.Calls);
        Assert.Null(await queries.HomeAsync(new CustomerActor("gone", Guid.Parse(Oid), CustomerRole.Developer, "Ada"), default));
    }
}
