using System.Security.Cryptography;
using Packmoat.Admin;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure.Tests.Postgres;

namespace Packmoat.Infrastructure.Tests;

/// <summary>
/// Shared-app identity mode as a contract: the store's refusals and the sign-in choice. Migration 6, the credential source's
/// re-checks (which plant rows by hand) and the CLI stay in <see cref="SharedAppIdentityTests"/>. the shared credential
/// store's own tests (the slots, the promote, the verification records) and the one-directory rule are here, so they run on every engine.
/// </summary>
public abstract class SharedAppIdentityContract : StoreContract
{
    protected const string Dir = "11111111-2222-4333-8444-555555555555";
    protected const string AppId = "99999999-8888-4777-8666-555555555555";
    protected const string Dir2 = "22222222-2222-4333-8444-555555555555";
    protected const string AppId2 = "88888888-8888-4777-8666-555555555555";
    protected static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    // ---- migration 6

    [EngineFact]
    public async Task The_store_refuses_a_tenant_id_with_a_colon()
    {
        await using var t = await CreateDbAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => Engine.Tenants(t).CreateAsync(new NewTenant("shared-app:active", "x", "https://x/", "o/", "https://x/o/i.json", 1, TenantOrigin.Operator), default));
    }

    // ---- the shared credential store (moved here from the SQLite fixture, or new)

    [EngineFact]
    public async Task Shared_credentials_round_trip_encrypted_and_promote_moves_next_to_active()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.SharedApp(t);
        await store.SetAsync(SharedAppSlot.Active, new SharedAppCredential(AppId, "SHARED-KEY-ONE"), default);
        await store.SetAsync(SharedAppSlot.Next, new SharedAppCredential(AppId, "SHARED-KEY-TWO"), default);

        Assert.Equal("SHARED-KEY-ONE", (await store.GetAsync(SharedAppSlot.Active, default))!.CertificatePem);
        var plain = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("SHARED-KEY"));
        Assert.All(await ColumnAsync(t, $"SELECT {Engine.HexSql("cert_encrypted")} FROM shared_app_credentials"), blob => Assert.DoesNotContain(plain, blob));

        Assert.True(await store.PromoteAsync(_ => { }, default));
        Assert.Equal("SHARED-KEY-TWO", (await store.GetAsync(SharedAppSlot.Active, default))!.CertificatePem);
        Assert.Null(await store.GetAsync(SharedAppSlot.Next, default));
        Assert.False(await store.PromoteAsync(_ => { }, default));
    }

    /// <summary>A promote the caller's check refuses throws that refusal and changes nothing: both slots are as they were.</summary>
    [EngineFact]
    public async Task A_refused_promote_throws_and_changes_neither_slot()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.SharedApp(t);
        await store.SetAsync(SharedAppSlot.Active, new SharedAppCredential(AppId, "ACTIVE"), default);
        await store.SetAsync(SharedAppSlot.Next, new SharedAppCredential(AppId2, "NEXT"), default);
        var seen = new List<string>();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => store.PromoteAsync(next => { seen.Add(next.ClientId); throw new InvalidOperationException("expired"); }, default));

        Assert.Equal("expired", refusal.Message);
        Assert.Equal(new[] { AppId2 }, seen);
        Assert.Equal(new SharedAppCredential(AppId, "ACTIVE"), await store.GetAsync(SharedAppSlot.Active, default));
        Assert.Equal(new SharedAppCredential(AppId2, "NEXT"), await store.GetAsync(SharedAppSlot.Next, default));
    }

    [EngineFact]
    public async Task A_blob_copied_between_slots_does_not_decrypt()
    {
        await using var t = await CreateDbAsync();
        var store = Engine.SharedApp(t);
        await store.SetAsync(SharedAppSlot.Active, new SharedAppCredential(AppId, "ACTIVE"), default);
        await store.SetAsync(SharedAppSlot.Next, new SharedAppCredential(AppId, "NEXT"), default);

        await ExecAsync(t, "UPDATE shared_app_credentials SET cert_encrypted=(SELECT cert_encrypted FROM shared_app_credentials WHERE slot='next') WHERE slot='active'");

        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.GetAsync(SharedAppSlot.Active, default));
    }

    /// <summary>
    /// The operator's record of a passing onboard check is written only while the tenant's shared binding, its feed and the active client
    /// id are still what was checked (GUIDs compared whatever their case), and only for a tenant in shared mode with an active credential.
    /// It reads back as written, and a delete removes it only when every field, the time included, is the one written.
    /// </summary>
    [EngineFact]
    public async Task A_verification_is_recorded_only_for_the_current_binding_feed_and_client_and_deleted_only_exactly()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        var shared = Engine.SharedApp(t);
        var feed = (await Engine.Tenants(t).GetAsync("acme", default))!.UpstreamFeedIndexUrl;
        SharedAppVerification Rec(string dir = Dir, string? f = null, string client = AppId) => new("acme", dir, f ?? feed, client, Now, "op");

        Assert.False(await shared.RecordVerificationAsync(Rec(), default));   // no binding yet
        await Engine.Tenants(t).BindSharedIdentityAsync("acme", Dir, default);
        Assert.False(await shared.RecordVerificationAsync(Rec(), default));   // no active credential
        await shared.SetAsync(SharedAppSlot.Active, new SharedAppCredential(AppId, "PEM"), default);

        Assert.False(await shared.RecordVerificationAsync(Rec(dir: Dir2), default));
        Assert.False(await shared.RecordVerificationAsync(Rec(f: feed + "x"), default));
        Assert.False(await shared.RecordVerificationAsync(Rec(client: AppId2), default));
        Assert.False(await shared.RecordVerificationAsync(Rec() with { TenantId = "ghost" }, default));
        Assert.Null(await shared.GetVerificationAsync("acme", default));

        var rec = Rec();
        Assert.True(await shared.RecordVerificationAsync(rec, default));
        Assert.Equal(rec, await shared.GetVerificationAsync("acme", default));
        await AddTenantAsync(t, "globex");
        Assert.Null(await shared.GetVerificationAsync("globex", default));   // Another tenant's record is never this one's
        Assert.Equal("operator", await ScalarAsync(t, "SELECT verified_via FROM shared_app_verifications WHERE tenant_id='acme'"));

        Assert.False(await shared.DeleteVerificationAsync(rec with { VerifiedAt = Now.AddSeconds(1) }, default));
        Assert.False(await shared.DeleteVerificationAsync(rec with { VerifiedAt = Now.AddTicks(10) }, default));   // a microsecond later
        Assert.False(await shared.DeleteVerificationAsync(rec with { VerifiedBy = "other" }, default));
        Assert.False(await shared.DeleteVerificationAsync(rec with { ClientId = AppId2 }, default));
        Assert.NotNull(await shared.GetVerificationAsync("acme", default));
        Assert.True(await shared.DeleteVerificationAsync(rec, default));
        Assert.Null(await shared.GetVerificationAsync("acme", default));

        // back in own mode: no record
        await Engine.Tenants(t).SetUpstreamIdentityAsync("acme", new UpstreamIdentity(Dir, AppId, "pem"), default);
        Assert.False(await shared.RecordVerificationAsync(Rec(), default));
    }

    [EngineFact]
    public async Task Two_shared_rows_can_never_hold_the_same_directory_in_the_database()
    {
        await using var t = await CreateDbAsync();
        await AddTenantAsync(t, "acme");
        await AddTenantAsync(t, "globex");
        await Engine.Tenants(t).BindSharedIdentityAsync("acme", Dir, default);
        await RefusedAsync(() => Engine.Tenants(t).BindSharedIdentityAsync("globex", Dir.ToUpperInvariant(), default));
        Assert.Null(await Engine.Tenants(t).GetUpstreamBindingAsync("globex", default));
        await Engine.Tenants(t).SetUpstreamIdentityAsync("globex", new UpstreamIdentity(Dir, AppId, "pem"), default);   // an own row may
    }

    /// <summary>
    /// The one-directory rule folds ASCII letters only, on every engine (SQLite's lower(); PostgreSQL's lower() under
    /// COLLATE "C"), so 'Ä…' and 'ä…' are two different shared directories, while 'A…' and 'a…' are one. A directory id is a GUID, so no
    /// real one holds either; the probe pins that the two engines draw the line in the same place.
    /// </summary>
    [EngineFact]
    public async Task Only_ascii_letters_fold_so_directories_differing_in_a_non_ascii_letters_case_are_two()
    {
        await using var t = await CreateDbAsync();
        foreach (var id in new[] { "upper", "lower", "ascii-upper", "ascii-lower" }) await AddTenantAsync(t, id);
        const string rest = "1111111-2222-4333-8444-555555555555";
        await Engine.Tenants(t).BindSharedIdentityAsync("upper", "\u00C4" + rest, default);   // Ä
        await Engine.Tenants(t).BindSharedIdentityAsync("lower", "\u00E4" + rest, default);   // ä: another directory
        Assert.Equal("\u00E4" + rest, (await Engine.Tenants(t).GetUpstreamBindingAsync("lower", default))!.AzureTenantId);
        Assert.Equal("\u00C4" + rest, (await Engine.Tenants(t).GetUpstreamBindingAsync("upper", default))!.AzureTenantId);

        await Engine.Tenants(t).BindSharedIdentityAsync("ascii-upper", "A" + rest, default);
        await RefusedAsync(() => Engine.Tenants(t).BindSharedIdentityAsync("ascii-lower", "a" + rest, default));   // the same directory
        Assert.Equal(1L, await ScalarAsync(t, "SELECT COUNT(*) FROM upstream_identities WHERE lower(azure_tenant_id) <> azure_tenant_id"));   // only 'A…'; 'Ä…' does not fold
    }

    // ---- choosing the sign-in

    protected static readonly UpstreamIdentity Own = new("own-dir", "own-app", "OWN-PEM");
    protected static readonly SharedAppCredential Shared = new(AppId, "SHARED-PEM");

    [EngineFact]
    public void Own_mode_uses_the_tenants_own_app_and_directory()
    {
        var s = AppSignInChooser.Choose("acme", new UpstreamBinding(IdentityKind.Own, "own-dir", "own-app"), Own, Shared);
        Assert.Equal(new AppSignIn("own-dir", "own-app", "OWN-PEM"), s);
    }

    protected const string Feed = "https://pkgs.dev.azure.com/example-org/p/_packaging/f/nuget/v3/index.json";
    protected static readonly UpstreamBinding SharedBinding = new(IdentityKind.Shared, Dir, null);
    protected static SharedAppVerification Verified(string dir = Dir, string feed = Feed, string client = AppId) => new("acme", dir, feed, client, Now, "op");

    [EngineFact]
    public void Shared_mode_uses_our_app_and_certificate_in_the_customers_directory_once_verified()
    {
        var s = AppSignInChooser.Choose("acme", SharedBinding, null, Shared, Verified(), Feed);
        Assert.Equal(new AppSignIn(Dir, AppId, "SHARED-PEM"), s);
    }

    [EngineFact]
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

    [EngineFact]
    public void Anything_missing_fails_closed()
    {
        Assert.Throws<InvalidOperationException>(() => AppSignInChooser.Choose("acme", null, Own, Shared));
        Assert.Throws<InvalidOperationException>(() => AppSignInChooser.Choose("acme", new UpstreamBinding(IdentityKind.Own, "d", "c"), null, Shared));
        var ex = Assert.Throws<InvalidOperationException>(() => AppSignInChooser.Choose("acme", new UpstreamBinding(IdentityKind.Shared, Dir, null), Own, null));
        Assert.Contains("no active shared app credential", ex.Message);
    }

    [EngineFact]
    public void A_sign_in_is_the_same_only_with_the_same_directory_app_and_pem()
    {
        var one = CertificateTools.Generate("a", 365, Now).CombinedPem;
        var two = CertificateTools.Generate("a", 365, Now).CombinedPem;   // same subject, new key
        Assert.Equal(new AppSignIn(Dir, AppId, one), new AppSignIn(Dir, AppId, new string(one.ToCharArray())));
        Assert.NotEqual(new AppSignIn(Dir, AppId, one), new AppSignIn(Dir, AppId, two));
        Assert.NotEqual(new AppSignIn(Dir, AppId, one), new AppSignIn(Dir2, AppId, one));
        Assert.NotEqual(new AppSignIn(Dir, AppId, one), new AppSignIn(Dir, AppId2, one));
    }

    [EngineFact]
    public void The_sign_in_never_prints_its_key()
    {
        Assert.DoesNotContain("SHARED-PEM", new AppSignIn(Dir, AppId, "SHARED-PEM").ToString());
        Assert.DoesNotContain("SHARED-PEM", Shared.ToString());
    }

    [EngineFact]
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

    [EngineFact]
    public void The_verification_is_rechecked_as_often_as_tokens_are()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), EntraCertificateCredentialSource.RecheckInterval);
        Assert.Equal(CachedTenantAuthenticator.DefaultLifetime, EntraCertificateCredentialSource.RecheckInterval);
    }
}
