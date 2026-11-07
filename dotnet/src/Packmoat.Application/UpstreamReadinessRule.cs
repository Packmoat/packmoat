using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>
/// Adds <c>UpstreamReadiness.Of</c>. It lives here, not in Domain, because the binding, the check record and the URL rules are
/// Application types.
/// </summary>
public static class UpstreamReadinessRule
{
    extension(UpstreamReadiness)
    {
        /// <summary>
        /// Pure. <see cref="UpstreamReadiness.NotSetUp"/> when no app is bound to reach the feed, or when the tenant's feed URLs fail the
        /// rules for its binding:
        /// <list type="bullet">
        /// <item>own app: the create-time rule (<see cref="TenantUrlRules.Check"/> with https and a prefix NOT required), as the CLI and admin
        /// site have always allowed, since a tenant's URLs never change; empty or invalid URLs still fail. Such a tenant is then
        /// <see cref="UpstreamReadiness.Ready"/>.</item>
        /// <item>shared app: the strict rule (https and a path prefix required), as binding requires. Ready only with a check record for this
        /// tenant that <see cref="SharedAppVerification.Matches"/> its directory, feed and the active shared client id
        /// (<paramref name="activeClientId"/>, null when there is none); otherwise <see cref="UpstreamReadiness.NotVerified"/>.</item>
        /// </list>
        /// </summary>
        public static UpstreamReadiness Of(Tenant tenant, UpstreamBinding? binding, SharedAppVerification? verification, string? activeClientId)
        {
            if (binding is null) return UpstreamReadiness.NotSetUp;
            var strict = binding.Kind != IdentityKind.Own;   // anything but own app gets the strict rule (fail closed)
            if (TenantUrlRules.Check(tenant.UpstreamBase, tenant.AllowedPathPrefix, tenant.UpstreamFeedIndexUrl, requireHttps: strict, requirePrefix: strict) is not null)
                return UpstreamReadiness.NotSetUp;
            if (!strict) return UpstreamReadiness.Ready;
            return activeClientId is not null && verification is not null
                   && string.Equals(verification.TenantId, tenant.Id, StringComparison.Ordinal)
                   && verification.Matches(binding, tenant.UpstreamFeedIndexUrl, activeClientId)
                ? UpstreamReadiness.Ready
                : UpstreamReadiness.NotVerified;
        }
    }
}
