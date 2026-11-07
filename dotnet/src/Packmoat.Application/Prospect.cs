namespace Packmoat.Application;

/// <summary>
/// What a sign-in from a directory that is not on Packmoat yet may lead to, when self sign-up allows that directory. Pure, and run only
/// after <see cref="CustomerSignIn.Decide"/> refused with <see cref="SignInDenial.DirectoryNotLinked"/>; <c>Decide</c> itself is unchanged.
/// </summary>
public abstract record ProspectDecision
{
    private ProspectDecision() { }

    /// <summary>A Packmoat.Admin of the directory: may set up an organisation for it.</summary>
    public sealed record Eligible(string DirectoryId, Guid ObjectId, string DisplayName) : ProspectDecision;

    /// <summary>Anyone else: someone with the Packmoat Admin role must do it.</summary>
    public sealed record NeedsAdmin(string DirectoryId) : ProspectDecision;

    /// <summary>
    /// Null unless <paramref name="decision"/> is a not-linked refusal for exactly the token's one directory (never the consumer one).
    /// Eligible only when the token's <c>roles</c> claim holds exactly "Packmoat.Admin" (ordinal: never case-insensitive, never Developer,
    /// never a directory role from <c>wids</c>, groups or scopes, which <see cref="VerifiedClaims"/> does not even carry).
    /// </summary>
    public static ProspectDecision? For(VerifiedClaims claims, SignInDecision decision)
    {
        if (decision is not SignInDecision.Denied { Reason: SignInDenial.DirectoryNotLinked, DirectoryId: { } directory }) return null;
        if (claims.DirectoryIds.Distinct().Count() != 1 || CustomerSignIn.CanonicalDirectory(claims.DirectoryIds[0]) != directory) return null;
        if (Guid.Parse(directory) == CustomerSignIn.ConsumerDirectory) return null;
        if (claims.ObjectIds.Distinct().Count() != 1 || !Guid.TryParse(claims.ObjectIds[0], out var oid) || oid == Guid.Empty) return null;
        if (!claims.Roles.Contains(CustomerSignIn.AdminRole, StringComparer.Ordinal)) return new NeedsAdmin(directory);
        var name = string.IsNullOrWhiteSpace(claims.DisplayName) ? "Signed-in user" : claims.DisplayName.Trim();
        if (name.Length > 100) name = name[..100];
        return new Eligible(directory, oid, name);
    }
}

/// <summary>
/// Someone signed in from a directory that is not on Packmoat yet, who may set up an organisation for it: built only from the prospect
/// session. <c>SignedInAt</c> is Microsoft's auth_time from an explicit "Set up your organisation" sign-in (prompt=login), or null.
/// </summary>
public sealed record Prospect(string DirectoryId, Guid ObjectId, string DisplayName, bool IsAdmin, DateTimeOffset? SignedInAt);
