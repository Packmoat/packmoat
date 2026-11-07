using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>What a customer may do on the customer site. Both Entra app roles mean Admin.</summary>
public enum CustomerRole { Developer, Admin }

/// <summary>
/// An operator-made link: people signing in from the customer's Microsoft directory <c>AzureTenantId</c> belong to Packmoat tenant <c>TenantId</c>.
/// One directory maps to at most one tenant. <c>AzureTenantId</c> is always the lower-case canonical GUID.
/// </summary>
public sealed record DirectoryBinding(string AzureTenantId, string TenantId, DateTimeOffset BoundAt, string BoundBy);

/// <summary>
/// Where directory bindings live. The operator writes them (the CLI); the customer site only reads them here. A module that lets
/// organisations sign themselves up writes one for a new organisation only, in the same transaction as the tenant; a binding is never moved.
/// </summary>
public interface ISignInDirectoryStore
{
    /// <summary>Links the directory to the tenant. False (nothing written) when the directory is already linked to any tenant.</summary>
    Task<bool> BindAsync(DirectoryBinding binding, CancellationToken cancellationToken);
    /// <summary>
    /// Removes the link only if it is to this tenant and, in the same transaction, revokes every active self-issued token that people from
    /// this directory hold in this tenant (recording <paramref name="revokedBy"/>): once the directory is not linked, nobody from it may
    /// keep a working token there. Null when there was no such link (nothing changed); otherwise how many tokens were revoked.
    /// </summary>
    Task<int?> UnbindAsync(string tenantId, string azureTenantId, DateTimeOffset at, string revokedBy, CancellationToken cancellationToken);
    /// <summary>The link for one directory (canonical id), or null.</summary>
    Task<DirectoryBinding?> GetAsync(string azureTenantId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DirectoryBinding>> ListForTenantAsync(string tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// The claims of an ID token that Microsoft's signature, lifetime, issuer and audience checks already passed, as plain values.
/// <c>Issuers</c>, <c>Audiences</c>, <c>DirectoryIds</c> and <c>ObjectIds</c> are lists so that a token carrying a claim twice can be refused.
/// <c>AccountStatuses</c> is the <c>acct</c> optional claim (0 a member of the directory, 1 a guest) and <c>IdentityProviders</c> the
/// <c>idp</c> claim (where the person really signed in). Null means none were sent. Both are read only for auto-join.
/// </summary>
public sealed record VerifiedClaims(
    IReadOnlyList<string> Issuers,
    IReadOnlyList<string> Audiences,
    IReadOnlyList<string> DirectoryIds,
    IReadOnlyList<string> ObjectIds,
    string? DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string>? AccountStatuses = null,
    IReadOnlyList<string>? IdentityProviders = null);

/// <summary>Why a customer sign-in was refused. The page shown and the log line name only this, never a claim.</summary>
public enum SignInDenial
{
    /// <summary>No single, valid directory id (tid) or object id (oid).</summary>
    MissingIdentity,
    /// <summary>A personal Microsoft account (the consumer directory). Only work or school accounts can sign in.</summary>
    ConsumerDirectory,
    /// <summary>The issuer is not exactly https://login.microsoftonline.com/{tid}/v2.0 for the token's own tid.</summary>
    IssuerMismatch,
    /// <summary>The token was not issued to Packmoat's customer sign-in app.</summary>
    WrongAudience,
    /// <summary>The directory has no operator-made binding to a Packmoat tenant.</summary>
    DirectoryNotLinked,
    /// <summary>The person holds neither Packmoat.Admin nor Packmoat.Developer, and the organisation has auto-join off.</summary>
    NoRole,
    /// <summary>
    /// The person holds no Packmoat role and auto-join is on, but the token shows a guest (<c>acct</c> is exactly "1") or an account from
    /// elsewhere (<c>acct</c> "0" with an <c>idp</c> that is not this directory: an external member).
    /// </summary>
    GuestNoRole,
    /// <summary>
    /// The person holds no Packmoat role and auto-join is on, but the token carries no usable <c>acct</c> claim (missing, repeated or any
    /// value other than "0" or "1"), so member or guest is unknown. Refused (fail closed), like a guest.
    /// </summary>
    MemberStatusUnknown,
}

/// <summary>The outcome of a customer sign-in.</summary>
public abstract record SignInDecision
{
    private SignInDecision() { }

    /// <summary>Let in, as this person of this tenant. <c>AutoJoined</c>: let in by auto-join with no role (always a Developer).</summary>
    public sealed record Allowed(Guid DirectoryId, Guid ObjectId, string TenantId, CustomerRole Role, string DisplayName, bool AutoJoined = false) : SignInDecision;

    /// <summary>Refused. <c>DirectoryId</c> is set only when it is useful to show the person (a directory that is not linked).</summary>
    public sealed record Denied(SignInDenial Reason, string? DirectoryId = null) : SignInDecision;
}

/// <summary>
/// The customer sign-in rule, pure. The tenant comes ONLY from an operator-made binding looked up by the verified token's tid: never from an
/// email, a user name, a display name, the URL or a form. The person is identified by (tid, oid).
/// </summary>
public static class CustomerSignIn
{
    public const string AdminRole = "Packmoat.Admin";
    public const string DeveloperRole = "Packmoat.Developer";

    /// <summary>The directory Microsoft uses for personal accounts (outlook.com and similar).</summary>
    public static readonly Guid ConsumerDirectory = Guid.Parse("9188040d-6c67-4c5b-b112-36a304b66dad");

    /// <summary>The only issuer accepted for a token from directory <paramref name="directoryId"/>.</summary>
    public static string ExpectedIssuer(Guid directoryId) => $"https://login.microsoftonline.com/{directoryId:D}/v2.0";

    /// <summary>The lower-case canonical form of a directory id, or null when it is not a GUID (or is the empty GUID).</summary>
    public static string? CanonicalDirectory(string? text) =>
        Guid.TryParse(text?.Trim(), out var g) && g != Guid.Empty ? g.ToString("D") : null;

    /// <summary>
    /// True when <paramref name="issuer"/> is exactly the v2.0 issuer of directory <paramref name="tid"/> and that directory is not the
    /// consumer one. Used by the token validator (before any session exists) and again by <see cref="Decide"/>.
    /// </summary>
    public static bool IssuerMatches(string? issuer, string? tid) =>
        Guid.TryParse(tid, out var d) && d != Guid.Empty && d != ConsumerDirectory
        && string.Equals(issuer, ExpectedIssuer(d), StringComparison.Ordinal);

    /// <summary>
    /// Decides who this person is on the customer site. <paramref name="binding"/> is whatever the store returned for the token's tid (or null).
    /// Checks, in order: one tid and one oid (GUIDs); not the consumer directory; the issuer is exactly the tid's; the audience is exactly
    /// <paramref name="expectedAudience"/>; the binding exists and is for this tid; a Packmoat role (both means Admin). With no role:
    /// refused when <paramref name="autoJoin"/> (the tenant's setting) is off; refused as MemberStatusUnknown without a usable acct, as a guest for acct "1" or an idp from elsewhere (<see cref="IsDirectoryMember"/>);
    /// otherwise let in as a Developer, marked auto-joined. Auto-join never grants Admin.
    /// </summary>
    public static SignInDecision Decide(VerifiedClaims claims, string expectedAudience, DirectoryBinding? binding, bool autoJoin = false)
    {
        if (claims.DirectoryIds.Distinct().Count() != 1 || claims.ObjectIds.Distinct().Count() != 1
            || !Guid.TryParse(claims.DirectoryIds[0], out var tid) || tid == Guid.Empty
            || !Guid.TryParse(claims.ObjectIds[0], out var oid) || oid == Guid.Empty)
            return new SignInDecision.Denied(SignInDenial.MissingIdentity);
        if (tid == ConsumerDirectory) return new SignInDecision.Denied(SignInDenial.ConsumerDirectory);
        if (claims.Issuers.Distinct().Count() != 1 || !IssuerMatches(claims.Issuers[0], claims.DirectoryIds[0]))
            return new SignInDecision.Denied(SignInDenial.IssuerMismatch);
        if (string.IsNullOrWhiteSpace(expectedAudience) || claims.Audiences.Count != 1 || !string.Equals(claims.Audiences[0], expectedAudience, StringComparison.OrdinalIgnoreCase))
            return new SignInDecision.Denied(SignInDenial.WrongAudience);
        var directory = tid.ToString("D");
        if (binding is null || !string.Equals(binding.AzureTenantId, directory, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(binding.TenantId))
            return new SignInDecision.Denied(SignInDenial.DirectoryNotLinked, directory);
        var admin = claims.Roles.Contains(AdminRole, StringComparer.Ordinal);
        var developer = claims.Roles.Contains(DeveloperRole, StringComparer.Ordinal);
        var name = string.IsNullOrWhiteSpace(claims.DisplayName) ? "Signed-in user" : claims.DisplayName.Trim();
        if (name.Length > 100) name = name[..100];
        if (!admin && !developer)
        {
            if (!autoJoin) return new SignInDecision.Denied(SignInDenial.NoRole);
            var acct = claims.AccountStatuses ?? Array.Empty<string>();
            if (acct.Count == 1 && string.Equals(acct[0], "1", StringComparison.Ordinal)) return new SignInDecision.Denied(SignInDenial.GuestNoRole);
            if (acct.Count != 1 || !string.Equals(acct[0], "0", StringComparison.Ordinal)) return new SignInDecision.Denied(SignInDenial.MemberStatusUnknown);
            if (!IsDirectoryMember(claims, tid)) return new SignInDecision.Denied(SignInDenial.GuestNoRole);   // a member elsewhere: the idp refuses
            return new SignInDecision.Allowed(tid, oid, binding.TenantId, CustomerRole.Developer, name, AutoJoined: true);
        }
        return new SignInDecision.Allowed(tid, oid, binding.TenantId, admin ? CustomerRole.Admin : CustomerRole.Developer, name);
    }

    /// <summary>
    /// True only when the token shows a member of directory <paramref name="tid"/> who signed in there. <c>acct</c> is the authority:
    /// exactly one value, and it is exactly "0" (a member; a guest is "1"). Missing, repeated or anything else is not a member (fail
    /// closed). <c>idp</c> can only refuse: none, the token's own issuer, or https://sts.windows.net/{tid}/ is accepted; any other value
    /// (live.com, the consumer directory, another directory's STS, a federated provider) or more than one is refused.
    /// </summary>
    public static bool IsDirectoryMember(VerifiedClaims claims, Guid tid)
    {
        var acct = claims.AccountStatuses ?? Array.Empty<string>();
        if (acct.Count != 1 || !string.Equals(acct[0], "0", StringComparison.Ordinal)) return false;
        var idp = claims.IdentityProviders ?? Array.Empty<string>();
        if (idp.Count == 0) return true;
        if (idp.Count != 1) return false;
        return string.Equals(idp[0], ExpectedIssuer(tid), StringComparison.Ordinal)
            || string.Equals(idp[0], $"https://sts.windows.net/{tid:D}/", StringComparison.Ordinal);
    }
}

/// <summary>
/// A signed-in customer, built ONLY from the server-side session (which holds the directory id, object id, display name and role) and the
/// directory binding read again on every request. Never from a request field. <c>DirectoryId</c> is the directory (tid) the person signed in
/// from: the binding's lower-case canonical <c>AzureTenantId</c>. The token commands need it (a person is (tid, oid)) and refuse an actor
/// built without it. <c>SignedInAt</c> is when the person last proved who they are to Microsoft (from the session); admin-only writes need
/// it to be recent (<see cref="CustomerTokenRules.FreshSignIn"/>). Null: unknown, never fresh.
/// </summary>
public sealed record CustomerActor(string TenantId, Guid ObjectId, CustomerRole Role, string DisplayName, string? DirectoryId = null, DateTimeOffset? SignedInAt = null)
{
    /// <summary>The tenant-limited scope every read goes through: this tenant and nothing else.</summary>
    public ActorScope Scope => ActorScope.ForTenant(TenantId, $"{DisplayName} ({ObjectId:D})");
}

/// <summary>
/// What the customer home page shows. CooldownDays is the tenant's own default cooldown, read from its own tenant record (per-package overrides
/// are not shown). <c>Notice</c> is the serving gate's notice, the banner (null: none). A refusal still shows the page, so an admin can see why.
/// </summary>
public sealed record CustomerHome(string TenantName, string DisplayName, CustomerRole Role, int ActiveDevelopers, int Days, int CooldownDays, bool Suspended = false,
    ServingNotice? Notice = null)
{
    /// <summary>A suspended tenant's people learn only that it is suspended: no name, no numbers.</summary>
    public static CustomerHome SuspendedFor(CustomerActor actor) => new("", actor.DisplayName, actor.Role, 0, CustomerQueries.ActiveDays, 0, Suspended: true);
}

/// <summary>The customer site's reads. Every one goes through <see cref="AdminQueries"/> with the actor's tenant scope, so it cannot reach another tenant.</summary>
public sealed class CustomerQueries
{
    public const int ActiveDays = 30;
    private readonly AdminQueries _queries;
    private readonly IServingGate _gate;

    /// <param name="gate">The serving gate, whose notice the home page shows (the core's, <see cref="ServeAllGate"/>, has none).</param>
    public CustomerQueries(AdminQueries queries, IServingGate gate)
    {
        _queries = queries;
        _gate = gate;
    }

    /// <summary>The home page facts, or null when the actor's tenant no longer exists. The active count is the same number as the CLI's `token active`.</summary>
    public async Task<CustomerHome?> HomeAsync(CustomerActor actor, CancellationToken ct)
    {
        var scope = actor.Scope;
        var tenant = await _queries.GetTenantAsync(scope, actor.TenantId, ct);
        if (tenant is null) return null;
        if (!tenant.Active) return CustomerHome.SuspendedFor(actor);   // nothing else about the tenant is read
        var active = await _queries.ActiveDevelopersAsync(scope, actor.TenantId, ActiveDays, ct);
        var notice = (await _gate.AskAsync(tenant.Id, ct)).Notice;
        return new CustomerHome(tenant.Name, actor.DisplayName, actor.Role, active.Count, ActiveDays, tenant.Policy.CooldownDays, Notice: notice);
    }
}

/// <summary>
/// The operator's directory-binding commands (the CLI calls them). Operator only. Kept apart from <see cref="AdminCommands"/>.
/// </summary>
public sealed class DirectoryBindingCommands
{
    private readonly ITenantStore _tenants;
    private readonly ISignInDirectoryStore _directories;
    private readonly ISharedAppCredentialStore? _shared;
    private readonly IClock _clock;

    public DirectoryBindingCommands(ITenantStore tenants, ISignInDirectoryStore directories, IClock clock, ISharedAppCredentialStore? shared = null)
    {
        _tenants = tenants;
        _directories = directories;
        _clock = clock;
        _shared = shared;
    }

    /// <summary>Operator only. Refuses a non-GUID, the consumer directory, an unknown tenant and a directory already linked to any tenant.</summary>
    public async Task<DirectoryBinding> BindAsync(ActorScope actor, string tenantId, string azureTenantId, CancellationToken ct)
    {
        AdminCommands.RequireOperator(actor);
        var directory = Directory(azureTenantId);
        if (await _tenants.GetAsync(tenantId, ct) is null) throw new KeyNotFoundException($"No tenant '{tenantId}'.");
        var binding = new DirectoryBinding(directory, tenantId, _clock.UtcNow, actor.Name);
        if (!await _directories.BindAsync(binding, ct))
        {
            var existing = await _directories.GetAsync(directory, ct);
            throw new ArgumentException(existing is not null && existing.TenantId == tenantId
                ? $"Directory {directory} is already linked to tenant {tenantId}."
                : $"Directory {directory} is already linked to another tenant. Unbind it there first.");
        }
        return binding;
    }

    /// <summary>
    /// Operator only. Null when the directory was not linked to this tenant; otherwise the number of that directory's self-issued tokens in
    /// this tenant that were revoked with it (same transaction). Re-linking a directory elsewhere is an unbind then a bind, so it revokes too.
    /// </summary>
    public Task<int?> UnbindAsync(ActorScope actor, string tenantId, string azureTenantId, CancellationToken ct)
    {
        AdminCommands.RequireOperator(actor);
        return _directories.UnbindAsync(tenantId, Directory(azureTenantId), _clock.UtcNow, $"operator {actor.Name} (directory unlinked)", ct);
    }

    /// <summary>
    /// Operator only. The tenant's bindings and, for a shared-app tenant with a recorded onboarding check, the directory that check was for
    /// as a suggestion (null otherwise). Nothing is ever bound automatically.
    /// </summary>
    public async Task<(IReadOnlyList<DirectoryBinding> Bindings, string? Suggestion)> ShowAsync(ActorScope actor, string tenantId, CancellationToken ct)
    {
        AdminCommands.RequireOperator(actor);
        if (await _tenants.GetAsync(tenantId, ct) is null) throw new KeyNotFoundException($"No tenant '{tenantId}'.");
        var bindings = await _directories.ListForTenantAsync(tenantId, ct);
        string? suggestion = null;
        if (_shared is not null && await _tenants.GetUpstreamBindingAsync(tenantId, ct) is { Kind: IdentityKind.Shared }
            && await _shared.GetVerificationAsync(tenantId, ct) is { } v && CanonicalOrNull(v.AzureTenantId) is { } d
            && bindings.All(b => b.AzureTenantId != d))
            suggestion = d;
        return (bindings, suggestion);
    }

    private static string? CanonicalOrNull(string text) => CustomerSignIn.CanonicalDirectory(text);

    /// <summary>
    /// A directory that may be linked to a tenant: a GUID (returned lower-case), and not the directory for personal Microsoft accounts.
    /// Anything else is refused, naming the option. Also used by the command line's create-and-link (#195).
    /// </summary>
    public static string LinkableDirectory(string azureTenantId) => Directory(azureTenantId);

    private static string Directory(string azureTenantId)
    {
        var d = CustomerSignIn.CanonicalDirectory(azureTenantId) ?? throw new ArgumentException("--azure-tenant must be a directory (tenant) id: a GUID.");
        if (Guid.Parse(d) == CustomerSignIn.ConsumerDirectory) throw new ArgumentException("That is the directory for personal Microsoft accounts; it cannot be linked to a tenant.");
        return d;
    }
}
