using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>A new token. Plaintext is shown once, at issue time, and is never stored.</summary>
public sealed record IssuedToken(string TokenId, string Plaintext, string DisplayPrefix)
{
    public override string ToString() => $"IssuedToken({TokenId}, {DisplayPrefix}, <redacted>)";
}

/// <summary>How a token came to be: issued by the operator (CLI or admin site) or by a signed-in customer for themselves on the customer site.</summary>
public enum TokenIssuer { Operator = 0, Self = 1 }

/// <summary>
/// A token's facts, never its value. <c>OwnerTid</c>/<c>OwnerOid</c> are the signed-in person who issued it for themselves (null for an
/// operator-issued token); <c>OwnerName</c> is that person's display name as last seen. <c>ExpiresAt</c> null means it never expires.
/// <c>RevokedBy</c> is who revoked it on the customer site or by unlinking a directory (null when the operator revoked it by id, or when it
/// is not revoked). <c>OwnerLastSeenAt</c> is when the owner last signed in on the customer site.
/// </summary>
public sealed record TokenInfo(string TokenId, string TenantId, string DisplayPrefix, string Label, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt, TokenKind Kind = TokenKind.Developer,
    string? OwnerTid = null, string? OwnerOid = null, string? OwnerName = null, DateTimeOffset? ExpiresAt = null, TokenIssuer IssuedVia = TokenIssuer.Operator, string? RevokedBy = null,
    DateTimeOffset? OwnerLastSeenAt = null)
{
    /// <summary>Not revoked and not past its expiry at <paramref name="now"/>: it would authenticate (if its tenant is active).</summary>
    public bool IsActiveAt(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}

public interface ITokenService
{
    Task<IssuedToken> IssueAsync(string tenantId, string label, CancellationToken cancellationToken, TokenKind kind = TokenKind.Developer);
    /// <summary>Takes effect for new requests immediately in this process and within the authenticator cache lifetime elsewhere.</summary>
    Task<bool> RevokeAsync(string tokenId, CancellationToken cancellationToken);
    /// <summary>Revokes only a token of this tenant (<c>WHERE id AND tenant_id</c>); another tenant's id changes nothing and returns false.</summary>
    Task<bool> RevokeAsync(string tenantId, string tokenId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TokenInfo>> ListAsync(string tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Issues a token owned by one signed-in person (<paramref name="ownerTid"/>, <paramref name="ownerOid"/>), marked issued via 'self', that
    /// stops working at <paramref name="expiresAt"/>. In the same statement, refuses (returns null, nothing written) when that person already
    /// holds <paramref name="maxActive"/> or more active self-issued tokens in this tenant, so two concurrent requests cannot both pass the cap.
    /// </summary>
    Task<IssuedToken?> IssueForMemberAsync(string tenantId, string ownerTid, string ownerOid, string label, DateTimeOffset expiresAt, TokenKind kind, int maxActive, CancellationToken cancellationToken);
    /// <summary>The tokens of this tenant owned by this person, oldest first.</summary>
    Task<IReadOnlyList<TokenInfo>> ListForMemberAsync(string tenantId, string ownerTid, string ownerOid, CancellationToken cancellationToken);
    /// <summary>
    /// Revokes a token only if it is in this tenant AND owned by this person (all in the <c>WHERE</c>); records <paramref name="revokedBy"/>.
    /// False (nothing changed) for anyone else's token, another tenant's, an unknown id or one already revoked.
    /// </summary>
    Task<bool> RevokeForMemberAsync(string tenantId, string ownerTid, string ownerOid, string tokenId, string revokedBy, CancellationToken cancellationToken);
    /// <summary>A customer admin's revoke: any token of this tenant (<c>WHERE id AND tenant_id</c>), recording <paramref name="revokedBy"/>.</summary>
    Task<bool> RevokeInTenantAsync(string tenantId, string tokenId, string revokedBy, CancellationToken cancellationToken);
}

/// <summary>
/// A tenant's "own app" identity: an app registration in the customer's directory, with a certificate Packmoat made for it.
/// The PEM holds the private key: never log or print it.
/// </summary>
public sealed record UpstreamIdentity(string AzureTenantId, string ClientId, string CertificatePem)
{
    public override string ToString() => $"UpstreamIdentity({AzureTenantId}, {ClientId}, <redacted>)";
}

/// <summary>
/// How Packmoat signs in to a tenant's feed. <see cref="Own"/>: the customer's own app registration (client id and certificate per tenant).
/// <see cref="Shared"/>: Packmoat's one multitenant app, added to the customer's directory; only the directory id is per tenant.
/// </summary>
public enum IdentityKind { Own, Shared }

/// <summary>Which app a tenant uses, with no key material. <c>ClientId</c> is the tenant's own app id, or null in shared mode.</summary>
public sealed record UpstreamBinding(IdentityKind Kind, string AzureTenantId, string? ClientId);

/// <summary>The shared app has one credential in use ("active") and, during a rotation, one waiting ("next").</summary>
public enum SharedAppSlot { Active, Next }

/// <summary>The shared multitenant app's client id and combined certificate + key PEM. Never log or print the PEM.</summary>
public sealed record SharedAppCredential(string ClientId, string CertificatePem)
{
    public override string ToString() => $"SharedAppCredential({ClientId}, <redacted>)";
}

/// <summary>
/// A passing onboarding check for a shared-app tenant, for exactly this binding: directory, feed and the shared app's client id.
/// A shared tenant serves no traffic without one that matches what is configured now.
/// </summary>
public sealed record SharedAppVerification(string TenantId, string AzureTenantId, string FeedIndexUrl, string ClientId, DateTimeOffset VerifiedAt, string VerifiedBy)
{
    /// <summary>True when this record was made for the tenant's current directory, feed and shared client id.</summary>
    public bool Matches(UpstreamBinding binding, string feedIndexUrl, string clientId) =>
        binding.Kind == IdentityKind.Shared
        && SameDirectory(AzureTenantId, binding.AzureTenantId)
        && string.Equals(FeedIndexUrl, feedIndexUrl, StringComparison.Ordinal)
        && Guid.TryParse(ClientId, out var a) && Guid.TryParse(clientId, out var b) && a == b;

    private static bool SameDirectory(string x, string y) => Guid.TryParse(x, out var a) && Guid.TryParse(y, out var b) && a == b;
}

/// <summary>Where the shared app's credentials live (encrypted). Separate from tenants: it belongs to no customer.</summary>
public interface ISharedAppCredentialStore
{
    Task SetAsync(SharedAppSlot slot, SharedAppCredential credential, CancellationToken cancellationToken);
    Task<SharedAppCredential?> GetAsync(SharedAppSlot slot, CancellationToken cancellationToken);
    /// <summary>
    /// Makes "next" the "active" credential and empties "next", in one transaction. <paramref name="validate"/> sees the "next"
    /// credential inside that transaction and throws to refuse it (nothing changes). False when there is no "next".
    /// </summary>
    Task<bool> PromoteAsync(Action<SharedAppCredential> validate, CancellationToken cancellationToken);
    /// <summary>
    /// Records a passing onboarding check for a shared tenant, replacing any earlier one, but only if, read again inside the same
    /// transaction, the tenant is still bound to the shared app in <c>verification.AzureTenantId</c>, its feed URL is still
    /// <c>verification.FeedIndexUrl</c> and the active shared client id is still <c>verification.ClientId</c>. False (nothing written) otherwise.
    /// The operator's onboard check takes the feed over: in the same transaction the tenant's feed_setup becomes 'operator' (#207).
    /// </summary>
    Task<bool> RecordVerificationAsync(SharedAppVerification verification, CancellationToken cancellationToken);
    /// <summary>Deletes the tenant's record only if it is exactly <paramref name="verification"/> (same values and time). False otherwise.</summary>
    Task<bool> DeleteVerificationAsync(SharedAppVerification verification, CancellationToken cancellationToken);
    Task<SharedAppVerification?> GetVerificationAsync(string tenantId, CancellationToken cancellationToken);
}

/// <summary>Public facts about a certificate + key PEM. Never the key itself.</summary>
public sealed record CertificateFacts(string Thumbprint, DateTimeOffset NotAfter, bool HasPrivateKey);

/// <summary>Reads a PEM's public facts. Throws <see cref="ArgumentException"/> when it holds no readable certificate.</summary>
public interface ICertificateInspector
{
    CertificateFacts Inspect(string pem);
}

/// <summary>
/// The shape rules for a tenant's upstream URLs, shared by every way a tenant is created or bound. Pure: no I/O.
/// <list type="bullet">
/// <item>The upstream base is absolute, ends with '/', has no user info, query, fragment or escapes.</item>
/// <item>The path prefix is empty or ends with '/', with no leading '/', '..', '//', '\', '%', '?', '#' or spaces.</item>
/// <item>The feed URL is on the same scheme, host and port, and starts with base + prefix.</item>
/// </list>
/// </summary>
public static class TenantUrlRules
{
    /// <summary>Null when the URLs are acceptable, otherwise a fixed message (it never echoes a value).</summary>
    public static string? Check(string upstreamBase, string pathPrefix, string feedIndexUrl, bool requireHttps, bool requirePrefix)
    {
        if (!CleanAbsolute(upstreamBase, requireHttps, out var b) || !upstreamBase.EndsWith('/'))
            return requireHttps ? "The upstream base must be an https URL ending with '/', with no user info, query or fragment." : "The upstream base must be an absolute URL ending with '/', with no user info, query or fragment.";
        if (pathPrefix.Length == 0)
        {
            if (requirePrefix) return "The path prefix is required (for example \"example-org/\").";
        }
        else if (!pathPrefix.EndsWith('/') || pathPrefix.StartsWith('/') || pathPrefix.Contains("..") || pathPrefix.Contains("//")
                 || pathPrefix.Any(c => c is '\\' or '%' or '?' or '#' or '@' || char.IsWhiteSpace(c) || char.IsControl(c)))
            return "The path prefix must end with '/' (for example \"example-org/\"), with no leading '/', '..', '//', '\\', '%', '?', '#' or spaces.";
        if (!CleanAbsolute(feedIndexUrl, requireHttps, out var f))
            return requireHttps ? "The feed URL must be an https URL with no user info, query, fragment or escapes." : "The feed URL must be an absolute URL with no user info, query, fragment or escapes.";
        if (!string.Equals(f.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) || !string.Equals(f.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase) || f.Port != b.Port
            || !feedIndexUrl.StartsWith(upstreamBase + pathPrefix, StringComparison.OrdinalIgnoreCase))
            return "The feed URL must be on the upstream host and under the path prefix (upstream base + path prefix).";
        return null;
    }

    private static bool CleanAbsolute(string value, bool requireHttps, out Uri uri)
    {
        uri = null!;
        // No escapes at all ('%'), so an encoded dot segment ("%2e%2e") or separator cannot get through.
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(new[] { '%', '?', '#', '\\', '@', ' ' }) >= 0) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return false;
        if (u.Scheme != Uri.UriSchemeHttps && (requireHttps || u.Scheme != Uri.UriSchemeHttp)) return false;
        // No "." or ".." path segment: "https://host/acme/../other/" would resolve outside "acme/" while its text starts with it.
        var afterScheme = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var rawPath = afterScheme.IndexOf('/') is var slash and >= 0 ? afterScheme[slash..] : "/";
        if (rawPath.Split('/').Any(s => s is "." or "..")) return false;
        // And the URL is already in its resolved form: what is checked is what is called.
        if (!string.Equals(u.AbsolutePath, rawPath, StringComparison.Ordinal) || !string.Equals(u.AbsoluteUri, value, StringComparison.OrdinalIgnoreCase)) return false;
        uri = u;
        return true;
    }

    /// <summary>
    /// The proxy's prefix rule: the upstream path must start with the prefix as whole path segments. A prefix stored without its
    /// trailing '/' (older rows) is treated as if it had one, so "acme" never admits "acme-secret/...". An empty prefix admits any path.
    /// </summary>
    public static bool PathIsUnderPrefix(string path, string pathPrefix)
    {
        if (pathPrefix.Length == 0) return true;
        var prefix = pathPrefix.EndsWith('/') ? pathPrefix : pathPrefix + "/";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// A tenant to create. <c>Origin</c> has no default: every caller says who is making it, and the store writes it with the tenant
/// (a module's creation participant may act on it in the same transaction). <c>CreatedByOid</c> is the creator's object id (self-made only).
/// <c>FeedSetup</c> is who sets up its feed (#207): <see cref="Application.FeedSetup.Customer"/> only for an operator-made tenant with no
/// upstream URLs at all, which its customer connects with the Azure setup wizard.
/// </summary>
public sealed record NewTenant(string Id, string Name, string UpstreamBase, string AllowedPathPrefix, string FeedIndexUrl, int CooldownDays, TenantOrigin Origin)
{
    public string? CreatedByOid { get; init; }
    public FeedSetup FeedSetup { get; init; } = FeedSetup.Operator;
}

/// <summary>Tenant configuration. Every policy change bumps the policy revision, so decision caches can be dropped.</summary>
public interface ITenantStore
{
    Task<Tenant?> GetAsync(string tenantId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken);
    Task CreateAsync(NewTenant tenant, CancellationToken cancellationToken);
    Task SetActiveAsync(string tenantId, bool active, CancellationToken cancellationToken);
    Task SetCooldownAsync(string tenantId, int days, CancellationToken cancellationToken);
    Task AddInternalPrefixAsync(string tenantId, string prefix, CancellationToken cancellationToken);
    Task RemoveInternalPrefixAsync(string tenantId, string prefix, CancellationToken cancellationToken);
    Task SetOverrideAsync(string tenantId, string packageId, int days, CancellationToken cancellationToken);
    Task RemoveOverrideAsync(string tenantId, string packageId, CancellationToken cancellationToken);
    /// <summary>
    /// An operator waiver (source 'operator', never capped). Throws <see cref="WaiverRefusedException"/> (<see cref="WaiverRefusal.Duplicate"/>)
    /// when the tenant already has an active waiver for the same package and version (compared normalised; "*" only matches "*").
    /// </summary>
    Task<Waiver> AddWaiverAsync(string tenantId, string packageId, string version, DateTimeOffset expiresAt, string reason, string createdBy, CancellationToken cancellationToken);
    /// <summary>
    /// A customer waiver (source 'customer'). In one write transaction: the duplicate check, then the count of the tenant's active customer
    /// waivers against <paramref name="maxActive"/>, then the insert, so concurrent inserts can never pass the cap. Refusals write nothing.
    /// </summary>
    Task<CustomerWaiverInsert> AddCustomerWaiverAsync(string tenantId, string packageId, string version, DateTimeOffset expiresAt, string reason, string createdBy, int maxActive, CancellationToken cancellationToken);
    /// <summary>
    /// Revokes an active waiver and records <paramref name="revokedBy"/>. With a <paramref name="tenantId"/> only a waiver of that tenant
    /// (<c>AND tenant_id</c>): another tenant's waiver id changes nothing and returns false. Null (the operator) revokes by id alone.
    /// </summary>
    Task<bool> RevokeWaiverAsync(string? tenantId, string waiverId, string revokedBy, CancellationToken cancellationToken);
    /// <summary>
    /// The tenant's waivers, at most <paramref name="limit"/>: the active ones (not revoked, expiring after now) first, then expired and
    /// revoked ones; newest first within each. So however long the history, no active waiver is cut off by the limit before an ended one.
    /// </summary>
    Task<IReadOnlyList<WaiverRecord>> ListWaiversAsync(string tenantId, int limit, CancellationToken cancellationToken);
    /// <summary>Sets an "own app" identity (replacing a shared binding if there was one). The operator now sets up the feed: in the same
    /// transaction the tenant's feed_setup becomes 'operator' (#207).</summary>
    Task SetUpstreamIdentityAsync(string tenantId, UpstreamIdentity identity, CancellationToken cancellationToken);
    /// <summary>The "own app" identity with its private key, or null when none is set OR the tenant is in shared mode.</summary>
    Task<UpstreamIdentity?> GetUpstreamIdentityAsync(string tenantId, CancellationToken cancellationToken);
    /// <summary>Binds the tenant to the shared app in directory <paramref name="azureTenantId"/>; any own-app client id and key are removed.
    /// The operator now sets up the feed: in the same transaction the tenant's feed_setup becomes 'operator' (#207).</summary>
    Task BindSharedIdentityAsync(string tenantId, string azureTenantId, CancellationToken cancellationToken);
    /// <summary>Which app the tenant uses. Reads no key material and decrypts nothing. Null when no identity is set.</summary>
    Task<UpstreamBinding?> GetUpstreamBindingAsync(string tenantId, CancellationToken cancellationToken);
    /// <summary>Who made the tenant and who sets up its feed (#207), from the tenant row itself. Null when there is no such tenant.</summary>
    Task<TenantSetup?> GetSetupAsync(string tenantId, CancellationToken cancellationToken);
}

public interface IAuditLog
{
    ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}

/// <summary>
/// One thing an operator did from the admin site. <c>Detail</c> is a short summary that never holds a token or other secret.
/// A row whose action ends in ".failed" records an attempt that did not take effect.
/// <c>Site</c> says where it was done: "admin" (the admin site) or "cli" (the operator command line; its actor tid and oid are empty).
/// </summary>
public sealed record AdminActionRecord(DateTimeOffset Time, string ActorTid, string ActorOid, string ActorName, string Action, string TenantId, string Detail, string Site = "admin");

/// <summary>
/// Append-only record of admin-site writes, plus the read side for the actions page. Newest first. Rows are kept for 12 months
/// (<see cref="TenantPurgePolicy.AdminActionsRetention"/>), then pruned by the daily retention pass.
/// </summary>
public interface IAdminActionLog
{
    ValueTask AppendAsync(AdminActionRecord record, CancellationToken cancellationToken);
    Task<IReadOnlyList<AdminActionRecord>> LatestAsync(int limit, CancellationToken cancellationToken);
    /// <summary>
    /// The newest row of <paramref name="action"/> for <paramref name="tenantId"/> made by one person (their directory and object id, both
    /// as written), or null when that person has none. Another person's row, or another tenant's, is never returned.
    /// </summary>
    Task<AdminActionRecord?> LatestByAsync(string tenantId, string action, string actorTid, string actorOid, CancellationToken cancellationToken);

    /// <summary>The action of the one row a prune that deleted something leaves behind (detail: "rows=N; before=cutoff").</summary>
    public const string PruneAction = "admin_actions.prune";

    /// <summary>
    /// The retention prune (#117), in one transaction: deletes the rows written before <paramref name="before"/> and, when it deleted
    /// any, appends one <see cref="PruneAction"/> row (time <paramref name="now"/>, actor "retention", site "server") saying how many and
    /// the cutoff. Returns how many were deleted. The database itself refuses to delete a row less than 12 months old (migration 18), so
    /// a cutoff that is too late (a clock running ahead) throws and deletes nothing.
    /// </summary>
    Task<int> PruneAsync(DateTimeOffset before, DateTimeOffset now, CancellationToken cancellationToken);
}
