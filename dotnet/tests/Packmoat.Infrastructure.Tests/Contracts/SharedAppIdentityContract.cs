using System.Security.Cryptography;
using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// Shared-app identity mode as a contract (#232 S1): the store's refusals and the sign-in choice. Migration 6, the encrypted blobs, the
/// credential source's re-checks (which plant rows by hand) and the CLI stay in <see cref="SharedAppIdentityTests"/>.
/// </summary>
public abstract class SharedAppIdentityContract : StoreContract
{
    protected const string Dir = "11111111-2222-4333-8444-555555555555";
    protected const string AppId = "99999999-8888-4777-8666-555555555555";
    protected const string Dir2 = "22222222-2222-4333-8444-555555555555";
    protected const string AppId2 = "88888888-8888-4777-8666-555555555555";
    protected static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    // ---- migration 6

    [Fact]
    public async Task The_store_refuses_a_tenant_id_with_a_colon()
    {
        await using var t = await CreateDbAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => Engine.Tenants(t).CreateAsync(new NewTenant("shared-app:active", "x", "https://x/", "o/", "https://x/o/i.json", 1, TenantOrigin.Operator), default));
    }

    // ---- choosing the sign-in

    protected static readonly UpstreamIdentity Own = new("own-dir", "own-app", "OWN-PEM");
    protected static readonly SharedAppCredential Shared = new(AppId, "SHARED-PEM");

    [Fact]
    public void Own_mode_uses_the_tenants_own_app_and_directory()
    {
        var s = AppSignInChooser.Choose("acme", new UpstreamBinding(IdentityKind.Own, "own-dir", "own-app"), Own, Shared);
        Assert.Equal(new AppSignIn("own-dir", "own-app", "OWN-PEM"), s);
    }

    protected const string Feed = "https://pkgs.dev.azure.com/example-org/p/_packaging/f/nuget/v3/index.json";
    protected static readonly UpstreamBinding SharedBinding = new(IdentityKind.Shared, Dir, null);
    protected static SharedAppVerification Verified(string dir = Dir, string feed = Feed, string client = AppId) => new("acme", dir, feed, client, Now, "op");

    [Fact]
    public void Shared_mode_uses_our_app_and_certificate_in_the_customers_directory_once_verified()
    {
        var s = AppSignInChooser.Choose("acme", SharedBinding, null, Shared, Verified(), Feed);
        Assert.Equal(new AppSignIn(Dir, AppId, "SHARED-PEM"), s);
    }

    [Fact]
    public void Shared_mode_without_a_matching_verification_fails_closed()
    {
        var none = Assert.Throws<InvalidOperationException>(() => AppSignInChooser.Choose("acme", SharedBinding, null, Shared, null, Feed));
        Assert.Contains("no passing onboarding check", none.Message);
        Assert.Throws<InvalidOperationException>(() => AppSignInChooser.Choose("acme", SharedBinding, null, Shared, Verified(dir: Dir2), Feed));
        Assert.Throws<InvalidOperationException>(() => AppSignInChooser.Choose("acme", SharedBinding, null, Shared, Verified(feed: Feed + "x"), Feed));
        Assert.Throws<InvalidOperationException>(() => AppSignInChooser.Choose("acme", SharedBinding, null, Shared, Verified(client: AppId2), Feed));
        Assert.Throws<InvalidOperationException>(() => AppSignInChooser.Choose("acme", SharedBinding, null, Shared, Verified(), null));
        // Only the onboarding check itself may go without (it is what creates the record).
        Assert.Equal(AppId, AppSignInChooser.Choose("acme", SharedBinding, null, Shared, null, null, requireVerification: false).ClientId);
    }

    [Fact]
    public void Anything_missing_fails_closed()
    {
        Assert.Throws<InvalidOperationException>(() => AppSignInChooser.Choose("acme", null, Own, Shared));
        Assert.Throws<InvalidOperationException>(() => AppSignInChooser.Choose("acme", new UpstreamBinding(IdentityKind.Own, "d", "c"), null, Shared));
        var ex = Assert.Throws<InvalidOperationException>(() => AppSignInChooser.Choose("acme", new UpstreamBinding(IdentityKind.Shared, Dir, null), Own, null));
        Assert.Contains("no active shared app credential", ex.Message);
    }

    [Fact]
    public void A_sign_in_is_the_same_only_with_the_same_directory_app_and_pem()
    {
        var one = CertificateTools.Generate("a", 365, Now).CombinedPem;
        var two = CertificateTools.Generate("a", 365, Now).CombinedPem;   // same subject, new key
        Assert.Equal(new AppSignIn(Dir, AppId, one), new AppSignIn(Dir, AppId, new string(one.ToCharArray())));
        Assert.NotEqual(new AppSignIn(Dir, AppId, one), new AppSignIn(Dir, AppId, two));
        Assert.NotEqual(new AppSignIn(Dir, AppId, one), new AppSignIn(Dir2, AppId, one));
        Assert.NotEqual(new AppSignIn(Dir, AppId, one), new AppSignIn(Dir, AppId2, one));
    }

    [Fact]
    public void The_sign_in_never_prints_its_key()
    {
        Assert.DoesNotContain("SHARED-PEM", new AppSignIn(Dir, AppId, "SHARED-PEM").ToString());
        Assert.DoesNotContain("SHARED-PEM", Shared.ToString());
    }

    [Fact]
    public async Task The_credential_source_fails_closed_for_a_shared_tenant_without_a_shared_credential()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t);
        await Engine.Tenants(t).BindSharedIdentityAsync("acme", Dir, default);
        var source = new EntraCertificateCredentialSource(Engine.Tenants(t), t.Clock, Engine.SharedApp(t));
        var tenant = (await Engine.Tenants(t).GetAsync("acme", default))!;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await source.GetAsync(tenant, default));
        var noStore = new EntraCertificateCredentialSource(Engine.Tenants(t), t.Clock);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await noStore.GetAsync(tenant, default));
    }

    // ---- GetAsync end to end, with a fake Entra credential (no network)

    /// <summary>Stands in for Entra: the "access token" it returns is the thumbprint of the certificate it was built with.</summary>
    protected sealed class FakeTokenCredential : Azure.Core.TokenCredential
    {
        private readonly string _token;
        public FakeTokenCredential(AppSignIn signIn)
        {
            using var cert = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(signIn.CertificatePem);
            _token = signIn.AuthorityTenantId + "/" + signIn.ClientId + "/" + cert.Thumbprint;
        }
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext c, CancellationToken ct) => new(_token, DateTimeOffset.MaxValue);
        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext c, CancellationToken ct) => ValueTask.FromResult(GetToken(c, ct));
    }

    [Fact]
    public void The_verification_is_rechecked_as_often_as_tokens_are()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), EntraCertificateCredentialSource.RecheckInterval);
        Assert.Equal(CachedTenantAuthenticator.DefaultLifetime, EntraCertificateCredentialSource.RecheckInterval);
    }
}
