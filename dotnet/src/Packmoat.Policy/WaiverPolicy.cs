using Packmoat.Domain;

namespace Packmoat.Policy;

/// <summary>An unexpired waiver for this package (and exact version, or "*") allows the version.</summary>
public sealed class WaiverPolicy : IPolicy
{
    public PolicyOutcome Decide(PolicyContext context)
    {
        var id = context.Facts.NormalizedId;
        var version = context.Facts.NormalizedVersion;
        foreach (var waiver in context.Tenant.Waivers)
        {
            if (context.Now >= waiver.ExpiresAt) continue;
            if (!string.Equals(waiver.PackageId.Trim(), id, StringComparison.OrdinalIgnoreCase)) continue;
            var target = waiver.Version.Trim();
            if (target == "*" || NuGetVersionKey.Normalize(target) == version)
                return PolicyOutcome.Decide(new Verdict(Decision.Allow, VerdictReason.Waiver, waiver.Id));
        }
        return PolicyOutcome.Abstain;
    }
}
