using System.Security.Claims;

namespace Packmoat.Host.Admin;

/// <summary>
/// The people allowed into the admin site, as (tid, oid) pairs: the Microsoft directory id and the person's object id inside it.
/// Email and UPN are never consulted: they can be changed or reused, the pair cannot be chosen by the user.
/// An empty list lets nobody in.
/// </summary>
public class AdminAllowlist
{
    public const string TenantClaim = "tid";
    public const string ObjectClaim = "oid";

    private readonly HashSet<(Guid Tid, Guid Oid)> _pairs;
    private readonly Guid? _requiredTenant;

    public AdminAllowlist(IEnumerable<(Guid Tid, Guid Oid)> pairs, Guid? requiredTenant = null)
    {
        _pairs = new HashSet<(Guid, Guid)>(pairs);
        _requiredTenant = requiredTenant;
    }

    public int Count => _pairs.Count;

    /// <summary>
    /// Parses "tid:oid,tid:oid" (comma, space or semicolon separated). An entry that is not two GUIDs is skipped and counted in
    /// <paramref name="rejected"/>: a typo can only shrink the list, never widen it.
    /// </summary>
    public static AdminAllowlist Parse(string? text, Guid? requiredTenant, out int rejected)
    {
        rejected = 0;
        var pairs = new List<(Guid, Guid)>();
        foreach (var entry in (text ?? "").Split(new[] { ',', ' ', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(':');
            if (parts.Length == 2 && Guid.TryParse(parts[0], out var tid) && Guid.TryParse(parts[1], out var oid) && tid != Guid.Empty && oid != Guid.Empty)
                pairs.Add((tid, oid));
            else rejected++;
        }
        return new AdminAllowlist(pairs, requiredTenant);
    }

    /// <summary>True only when the principal carries exactly one tid and one oid claim, both GUIDs, and that pair is listed.</summary>
    public virtual bool IsAllowed(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true || _pairs.Count == 0) return false;
        var tids = principal.FindAll(TenantClaim).Select(c => c.Value).Distinct().ToArray();
        var oids = principal.FindAll(ObjectClaim).Select(c => c.Value).Distinct().ToArray();
        if (tids.Length != 1 || oids.Length != 1) return false;
        if (!Guid.TryParse(tids[0], out var tid) || !Guid.TryParse(oids[0], out var oid)) return false;
        if (_requiredTenant is { } t && t != tid) return false;
        return _pairs.Contains((tid, oid));
    }
}

/// <summary>Raised when something tries to start an admin session for a person who is not on the list. Mapped to 403; no cookie is written.</summary>
public sealed class AdminSignInDeniedException : Packmoat.Host.Web.SignInDeniedException
{
    public AdminSignInDeniedException() : base("This account is not allowed to use the admin site.") { }
}
