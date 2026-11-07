using Packmoat.Domain;

namespace Packmoat.Policy;

/// <summary>
/// Packages whose id matches a tenant internal prefix are never served from the public feed, whatever else is
/// configured. This stops a public package with an internal name (dependency confusion) from being restored.
/// The rule itself is <see cref="InternalPrefixes.Matches"/>, shared with the customer waiver check.
/// </summary>
public sealed class InternalNamespacePolicy : IPolicy
{
    public PolicyOutcome Decide(PolicyContext context) =>
        InternalPrefixes.Matches(context.Tenant.InternalPrefixes, context.Facts.NormalizedId)
            ? PolicyOutcome.Decide(new Verdict(Decision.Hide, VerdictReason.InternalNamespace))
            : PolicyOutcome.Abstain;
}
