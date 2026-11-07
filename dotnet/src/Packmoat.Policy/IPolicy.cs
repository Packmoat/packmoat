using Packmoat.Domain;

namespace Packmoat.Policy;

public sealed record PolicyContext(PackageVersionFacts Facts, TenantPolicy Tenant, DateTimeOffset Now);

/// <summary>The result of one rule: it decides, or it abstains and leaves the choice to later rules.</summary>
public sealed record PolicyOutcome
{
    private PolicyOutcome(Verdict? verdict) => Verdict = verdict;

    public Verdict? Verdict { get; }
    public bool Decided => Verdict is not null;

    public static PolicyOutcome Abstain { get; } = new((Verdict?)null);
    public static PolicyOutcome Decide(Verdict verdict) => new(verdict);
}

/// <summary>One rule. Pure: no clock, network or storage, only what is in the context.</summary>
public interface IPolicy
{
    PolicyOutcome Decide(PolicyContext context);
}

/// <summary>Combines rules into one verdict. Fails closed.</summary>
public interface IPolicyEngine
{
    Verdict Decide(PackageVersionFacts facts, TenantPolicy tenant);
}
