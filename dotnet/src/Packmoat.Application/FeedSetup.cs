using Packmoat.Domain;

namespace Packmoat.Application;

/// <summary>
/// Who sets up a tenant's feed (tenants.feed_setup, core migration 22). <see cref="Operator"/>: the operator, on the command line
/// (every tenant made before customers could connect their own feed). <see cref="Customer"/>: the tenant's own Admins, with the Azure setup wizard and Connect, as a
/// self-made tenant's do; set by <c>tenant create … --azure-tenant</c> with no upstream options. Any operator write of the upstream or
/// identity sets it back to <see cref="Operator"/>, so the wizard never replaces what an operator set.
/// </summary>
public enum FeedSetup { Operator, Customer }

/// <summary>A tenant's origin and who sets up its feed: what the wizard's rule reads (from the tenant store, never from a module).</summary>
public sealed record TenantSetup(TenantOrigin Origin, FeedSetup FeedSetup);

/// <summary>The one rule for who may set up a tenant's feed, and what the Azure setup wizard offers. Pure.</summary>
public static class FeedSetupRules
{
    /// <summary>
    /// The tenant's own Admins may set up its feed (the wizard, Connect): a self-made tenant, or one the operator made for the customer to
    /// connect. Connect's first step (the self-service feed store's) is the same rule in SQL: <c>origin='self' OR feed_setup='customer'</c>.
    /// </summary>
    public static bool CustomerSetsUpFeed(TenantOrigin origin, FeedSetup feedSetup) => origin == TenantOrigin.Self || feedSetup == FeedSetup.Customer;

    /// <summary>
    /// What the wizard offers. A suspended tenant: read only. A tenant whose customer sets up the feed, with no upstream or a shared binding
    /// in the person's own directory: the wizard. Otherwise a shared binding in the person's directory: check only. Anything else (own app,
    /// another directory, an operator-made tenant with nothing bound): read only.
    /// </summary>
    /// <param name="sameDirectory">The binding is in the signed-in person's directory.</param>
    public static AzureSetupKind Kind(bool active, bool customerSetsUpFeed, UpstreamBinding? upstream, bool sameDirectory) =>
        !active ? AzureSetupKind.ReadOnly
        : customerSetsUpFeed && (upstream is null || (upstream.Kind == IdentityKind.Shared && sameDirectory)) ? AzureSetupKind.Wizard
        : !customerSetsUpFeed && upstream is { Kind: IdentityKind.Shared } && sameDirectory ? AzureSetupKind.CheckOnly
        : AzureSetupKind.ReadOnly;
}
