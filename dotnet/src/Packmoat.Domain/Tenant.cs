namespace Packmoat.Domain;

/// <summary>
/// One customer. UpstreamBase is the only host the proxy will ever call for this tenant
/// (for example https://pkgs.dev.azure.com/); AllowedPathPrefix limits it to the customer's organisation
/// (for example "example-org/"); UpstreamFeedIndexUrl is the feed's service index.
/// </summary>
public sealed record Tenant(
    string Id,
    string Name,
    string UpstreamBase,
    string AllowedPathPrefix,
    string UpstreamFeedIndexUrl,
    TenantPolicy Policy,
    bool Active = true);
