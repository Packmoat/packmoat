namespace Packmoat.Domain;

/// <summary>
/// An explicit, expiring exception. Version is an exact version or "*" for every version of the package (operator only).
/// <c>Source</c> says who made it: the operator (CLI or admin site) or a customer Admin on the customer site (#157).
/// </summary>
public sealed record Waiver(string Id, string PackageId, string Version, DateTimeOffset ExpiresAt, string Reason, string CreatedBy, WaiverSource Source = WaiverSource.Operator);

/// <summary>Who made a waiver. Stored as waivers.source: 'operator' or 'customer'.</summary>
public enum WaiverSource { Operator, Customer }

/// <summary>A per-package cooldown that replaces the tenant default. 0 means no cooldown for that package.</summary>
public sealed record PackageOverride(string PackageId, int CooldownDays);

/// <summary>
/// Everything the rules need to know about one tenant.
/// InternalPrefixes are package-id prefixes (a trailing '*' is allowed and ignored), matched case-insensitively.
/// Revision changes whenever the policy changes, so decision caches can be invalidated.
/// </summary>
public sealed record TenantPolicy
{
    public TenantPolicy(
        int cooldownDays,
        IReadOnlyList<string>? internalPrefixes = null,
        IReadOnlyList<Waiver>? waivers = null,
        IReadOnlyList<PackageOverride>? packageOverrides = null,
        long revision = 0)
    {
        if (cooldownDays < 0) throw new ArgumentOutOfRangeException(nameof(cooldownDays), "Cooldown cannot be negative.");
        CooldownDays = cooldownDays;
        InternalPrefixes = internalPrefixes ?? Array.Empty<string>();
        Waivers = waivers ?? Array.Empty<Waiver>();
        PackageOverrides = packageOverrides ?? Array.Empty<PackageOverride>();
        Revision = revision;
    }

    public int CooldownDays { get; }
    public IReadOnlyList<string> InternalPrefixes { get; }
    public IReadOnlyList<Waiver> Waivers { get; }
    public IReadOnlyList<PackageOverride> PackageOverrides { get; }
    public long Revision { get; }
}
