using Packmoat.Domain;
using static Packmoat.Policy.Tests.PolicyTestKit;

namespace Packmoat.Policy.Tests;

public class EngineFailClosedTests
{
    private sealed class Throwing : IPolicy { public PolicyOutcome Decide(PolicyContext c) => throw new InvalidOperationException("boom"); }
    private sealed class Abstaining : IPolicy { public PolicyOutcome Decide(PolicyContext c) => PolicyOutcome.Abstain; }

    [Fact]
    public void No_rules_means_hide()
    {
        var v = new PolicyEngine(Array.Empty<IPolicy>(), new FakeClock(Now)).Decide(Facts("a", "1.0.0", 900), new TenantPolicy(14));
        Assert.Equal(Decision.Hide, v.Decision);
        Assert.Equal(VerdictReason.NoPolicyDecided, v.Reason);
    }

    [Fact]
    public void All_rules_abstaining_means_hide()
    {
        var v = new PolicyEngine(new IPolicy[] { new Abstaining(), new Abstaining() }, new FakeClock(Now)).Decide(Facts("a", "1.0.0", 900), new TenantPolicy(14));
        Assert.Equal(VerdictReason.NoPolicyDecided, v.Reason);
    }

    [Fact]
    public void A_throwing_rule_means_hide_not_a_crash()
    {
        var v = new PolicyEngine(new IPolicy[] { new Throwing(), new CooldownPolicy() }, new FakeClock(Now)).Decide(Facts("a", "1.0.0", 900), new TenantPolicy(14));
        Assert.Equal(Decision.Hide, v.Decision);
        Assert.Equal(VerdictReason.PolicyError, v.Reason);
    }

    [Fact]
    public void First_deciding_rule_wins_over_later_ones()
    {
        var v = new PolicyEngine(new IPolicy[] { new Abstaining(), new CooldownPolicy(), new Throwing() }, new FakeClock(Now)).Decide(Facts("a", "1.0.0", 900), new TenantPolicy(14));
        Assert.True(v.IsAllowed);
    }

    [Fact]
    public void Negative_cooldown_is_rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new TenantPolicy(-1));
}
