using Packmoat.Domain;
using static Packmoat.Policy.Tests.PolicyTestKit;

namespace Packmoat.Policy.Tests;

/// <summary>
/// The per-version decision record keeps the publish date and cooldown a verdict used. The cooldown rule reports both; nothing
/// about the decision itself changes. The cases a request can reach (a young, an old and an undated version, an override, the 1900
/// placeholder, waivers and internal prefixes) are conformance vectors: spec/vectors/policy/decisions.json. These are the
/// rule's own cases, which the package gate never asks.
/// </summary>
public class VerdictFactsTests
{
    private static Verdict Decide(PackageVersionFacts f, TenantPolicy t) => Engine().Engine.Decide(f, t);

    /// <summary>The gate asks a zero cooldown without a date (so its vectors carry none); given a date, the rule reports it.</summary>
    [Fact]
    public void A_zero_cooldown_reports_zero_days_and_the_date_it_was_given_if_any()
    {
        var none = Decide(Facts("a", "1.0.0", null), new TenantPolicy(0));
        Assert.Equal((Decision.Allow, 0, (DateTimeOffset?)null), (none.Decision, none.CooldownDaysApplied, none.PublishedAt));
        var f = Facts("a", "1.0.0", 2);
        Assert.Equal(f.PublishedAt, Decide(f, new TenantPolicy(0)).PublishedAt);
    }

    /// <summary>
    /// Kept by hand: the gate decides waivers and internal prefixes before it fetches any date, so their vectors never give the
    /// rule a date to leave out. Here the rule is given one, and still reports none.
    /// </summary>
    [Fact]
    public void Waiver_and_internal_namespace_verdicts_carry_no_publish_date()
    {
        var t = new TenantPolicy(14, internalPrefixes: new[] { "Contoso." },
            waivers: new[] { new Waiver("w1", "Moq", "4.21.0", Now.AddDays(1), "hotfix", "ops") });
        Assert.Null(Decide(Facts("Moq", "4.21.0", 1), t).PublishedAt);
        Assert.Null(Decide(Facts("Contoso.Lib", "1.0.0", 400), t).PublishedAt);
    }

    [Fact]
    public void Fail_closed_verdicts_carry_no_publish_date()
        => Assert.Null(new PolicyEngine(Array.Empty<IPolicy>(), new FakeClock(Now)).Decide(Facts("a", "1.0.0", 400), new TenantPolicy(14)).PublishedAt);
}
