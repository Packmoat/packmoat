namespace Packmoat.Domain;

/// <summary>Whether a tenant serves feed traffic, and if not, why (the serving rule, <c>ServingRule</c> in the Application layer).</summary>
public enum ServingDecision
{
    Serve,
    /// <summary>The operator suspended the tenant (<see cref="Tenant.Active"/> is false).</summary>
    TenantSuspended,
    /// <summary>The serving gate refused it (a module's gate; see <c>IServingGate</c>).</summary>
    Refused,
    /// <summary>The tenant has no upstream yet: no app bound to reach it, or feed URLs that fail the rules for its binding (see <see cref="UpstreamReadiness.NotSetUp"/>).</summary>
    NotSetUp,
    /// <summary>A shared-app tenant without a passing onboarding check for its current binding.</summary>
    NotVerified,
}

/// <summary>
/// Whether a tenant's upstream is ready to serve: its feed is set up and, for the shared app, checked. Worked out in the Application layer
/// (<c>UpstreamReadiness.Of</c>), where the binding and the check record live.
/// </summary>
public enum UpstreamReadiness
{
    Ready,
    /// <summary>No app bound to reach the feed, or feed URLs that fail the rules for its binding: empty or invalid for an own app (the create-time rule), not strict (https and a path prefix) for the shared app.</summary>
    NotSetUp,
    /// <summary>Bound to the shared app, but with no passing onboarding check for exactly this directory, feed and shared client id.</summary>
    NotVerified,
}
