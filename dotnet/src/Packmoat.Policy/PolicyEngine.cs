using Packmoat.Domain;

namespace Packmoat.Policy;

/// <summary>
/// Runs rules in order; the first rule that decides wins. If no rule decides, or a rule throws,
/// the version is hidden (fail closed).
/// </summary>
public sealed class PolicyEngine : IPolicyEngine
{
    private readonly IReadOnlyList<IPolicy> _policies;
    private readonly IClock _clock;

    public PolicyEngine(IEnumerable<IPolicy> policies, IClock clock)
    {
        _policies = policies.ToArray();
        _clock = clock;
    }

    /// <summary>Internal namespace first (nothing overrides it), then waivers, then the cooldown.</summary>
    public static PolicyEngine CreateDefault(IClock clock) =>
        new(new IPolicy[] { new InternalNamespacePolicy(), new WaiverPolicy(), new CooldownPolicy() }, clock);

    public Verdict Decide(PackageVersionFacts facts, TenantPolicy tenant)
    {
        var context = new PolicyContext(facts, tenant, _clock.UtcNow);
        try
        {
            foreach (var policy in _policies)
            {
                var outcome = policy.Decide(context);
                if (outcome.Verdict is { } verdict) return verdict;
            }
            return new Verdict(Decision.Hide, VerdictReason.NoPolicyDecided);
        }
        catch (Exception)
        {
            return new Verdict(Decision.Hide, VerdictReason.PolicyError);
        }
    }
}
