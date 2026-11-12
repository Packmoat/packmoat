using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Azure.Core;
using Azure.Identity;
using Packmoat.Application;
using Packmoat.Domain;

namespace Packmoat.Infrastructure;

/// <summary>What one sign-in uses: the directory to ask for a token, the app (client) id and its certificate + key. Never print the PEM.</summary>
public sealed record AppSignIn(string AuthorityTenantId, string ClientId, string CertificatePem)
{
    public override string ToString() => $"AppSignIn({AuthorityTenantId}, {ClientId}, <redacted>)";
}

/// <summary>
/// Chooses the sign-in for a tenant from what is stored. Pure: no I/O, so every case is testable. Fails closed: anything missing throws,
/// and the proxy then refuses the request rather than calling upstream without the right identity.
/// </summary>
public static class AppSignInChooser
{
    /// <param name="verification">Shared mode: the recorded passing onboarding check. It must match the current directory, <paramref name="feedIndexUrl"/> and the active client id.</param>
    /// <param name="requireVerification">False only for the onboarding check itself, which is what creates the record.</param>
    public static AppSignIn Choose(string tenantId, UpstreamBinding? binding, UpstreamIdentity? own, SharedAppCredential? shared,
        SharedAppVerification? verification = null, string? feedIndexUrl = null, bool requireVerification = true) => binding?.Kind switch
    {
        null => throw new InvalidOperationException($"No upstream identity is configured for tenant '{tenantId}'."),
        IdentityKind.Own => own is null
            ? throw new InvalidOperationException($"Tenant '{tenantId}' is in own-app mode but its certificate could not be loaded.")
            : new AppSignIn(own.AzureTenantId, own.ClientId, own.CertificatePem),
        // Shared mode: OUR app and certificate, but the token is requested from the CUSTOMER's directory, so it is their service principal that signs in.
        IdentityKind.Shared => shared is null
            ? throw new InvalidOperationException($"Tenant '{tenantId}' uses the shared app, but no active shared app credential is set.")
            : requireVerification && (verification is null || feedIndexUrl is null || !verification.Matches(binding, feedIndexUrl, shared.ClientId))
            ? throw new InvalidOperationException($"Tenant '{tenantId}' uses the shared app but has no passing onboarding check for its current directory, feed and app. " +
                "It serves nothing until 'packmoat-admin onboard check' passes (exit 0) for it.")
            : new AppSignIn(binding.AzureTenantId, shared.ClientId, shared.CertificatePem),
        _ => throw new InvalidOperationException("Unknown identity kind."),
    };
}

/// <summary>
/// The real thing: an Entra token for the tenant's service principal (own app or shared app), sent as the Basic password.
/// <para>
/// Two lifetimes, kept apart. What is stored (the binding, the active shared credential and the passing onboarding check) is read
/// again every <see cref="RecheckInterval"/>, so a withdrawn or deleted verification stops serving within 5 s. The Entra credential
/// itself lives as long as the sign-in it was built for is unchanged, so its own token cache survives every re-check.
/// </para>
/// </summary>
public sealed class EntraCertificateCredentialSource : IUpstreamCredentialSource
{
    public const string AzureDevOpsScope = "499b84ac-1321-427f-aa17-267ca6975798/.default";

    /// <summary>How long a stored sign-in (and its verification) is trusted before it is read again: the same bound as a token lookup.</summary>
    public static readonly TimeSpan RecheckInterval = CachedTenantAuthenticator.DefaultLifetime;

    /// <summary>What a credential was built for: the same directory, app and PEM is the same credential.</summary>
    /// <param name="PemSha256">Hex SHA-256 of the PEM text exactly as loaded; the cache keeps the hash, not the key. A string, not a byte array, so record equality compares the value.</param>
    private sealed record SignInKey(string AuthorityTenantId, string ClientId, string PemSha256)
    {
        public static SignInKey For(AppSignIn signIn) =>
            new(signIn.AuthorityTenantId, signIn.ClientId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signIn.CertificatePem))));
    }

    /// <param name="Key">What <see cref="Credential"/> was built for.</param>
    private sealed record CachedCredential(TokenCredential Credential, SignInKey Key, DateTimeOffset CheckedAt);

    private readonly ITenantStore _store;
    private readonly IClock _clock;
    private readonly ISharedAppCredentialStore? _shared;
    private readonly bool _requireSharedVerification;
    private readonly Func<AppSignIn, TokenCredential> _createCredential;
    private readonly ConcurrentDictionary<string, CachedCredential> _credentials = new();

    /// <param name="requireSharedVerification">
    /// True everywhere except the onboarding check: a shared tenant gets no credential without a passing check for its current binding.
    /// </param>
    /// <param name="createCredential">Builds the Entra credential for a sign-in. Tests replace it so no network is needed.</param>
    public EntraCertificateCredentialSource(ITenantStore store, IClock clock, ISharedAppCredentialStore? shared = null,
        bool requireSharedVerification = true, Func<AppSignIn, TokenCredential>? createCredential = null)
    {
        _store = store;
        _clock = clock;
        _shared = shared;
        _requireSharedVerification = requireSharedVerification;
        _createCredential = createCredential ?? CreateEntraCredential;
    }

    /// <summary>The real Entra credential for a sign-in: the certificate and key from the combined PEM.</summary>
    public static TokenCredential CreateEntraCredential(AppSignIn signIn)
    {
        using var certificate = X509Certificate2.CreateFromPem(signIn.CertificatePem, signIn.CertificatePem); // combined PEM: certificate and key
        return new ClientCertificateCredential(signIn.AuthorityTenantId, signIn.ClientId, X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null));
    }

    public async ValueTask<UpstreamCredential> GetAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        if (!_credentials.TryGetValue(tenant.Id, out var cached) || now - cached.CheckedAt >= RecheckInterval)
        {
            // A refusal throws here and leaves the entry stale, so the next request checks again (and a new pass is honoured at once).
            var signIn = await LoadAsync(tenant, cancellationToken);
            // Unchanged: keep the credential (and Entra's token cache in it). Comparing the PEM's hash needs no certificate parse.
            var key = SignInKey.For(signIn);
            cached = cached is not null && cached.Key == key
                ? cached with { CheckedAt = now }
                : new CachedCredential(_createCredential(signIn), key, now);
            _credentials[tenant.Id] = cached;
        }

        var token = await cached.Credential.GetTokenAsync(new TokenRequestContext(new[] { AzureDevOpsScope }), cancellationToken);
        return new UpstreamCredential("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes("packmoat:" + token.Token)));
    }

    private async Task<AppSignIn> LoadAsync(Tenant tenant, CancellationToken ct)
    {
        var binding = await _store.GetUpstreamBindingAsync(tenant.Id, ct);
        var own = binding?.Kind == IdentityKind.Own ? await _store.GetUpstreamIdentityAsync(tenant.Id, ct) : null;
        var isShared = binding?.Kind == IdentityKind.Shared && _shared is not null;
        var shared = isShared ? await _shared!.GetAsync(SharedAppSlot.Active, ct) : null;
        var verification = isShared && _requireSharedVerification ? await _shared!.GetVerificationAsync(tenant.Id, ct) : null;
        return AppSignInChooser.Choose(tenant.Id, binding, own, shared, verification, tenant.UpstreamFeedIndexUrl, _requireSharedVerification);
    }
}

/// <summary>For tests and local development against a fake feed. Never used in production configuration.</summary>
public sealed class StaticBasicCredentialSource : IUpstreamCredentialSource
{
    private readonly UpstreamCredential _credential;

    public StaticBasicCredentialSource(string user, string secret) =>
        _credential = new UpstreamCredential("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}")));

    public ValueTask<UpstreamCredential> GetAsync(Tenant tenant, CancellationToken cancellationToken) => ValueTask.FromResult(_credential);
}
