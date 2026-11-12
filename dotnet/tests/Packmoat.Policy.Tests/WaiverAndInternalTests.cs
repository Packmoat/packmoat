using Packmoat.Domain;
using static Packmoat.Policy.Tests.PolicyTestKit;

namespace Packmoat.Policy.Tests;

/// <summary>
/// Waivers and internal prefixes as the engine decides them are conformance vectors: spec/vectors/policy/decisions.json.
/// What stays here is the shared prefix rule itself, which the customer waiver check also uses.
/// </summary>
public class WaiverAndInternalTests
{
    private static Verdict Decide(PackageVersionFacts f, TenantPolicy t) => Engine().Engine.Decide(f, t);

    // ---- the internal-prefix rule, shared by the engine and the waiver command

    [Theory]
    [InlineData("contoso.core", true)]
    [InlineData("Contoso.Core", true)]
    [InlineData("contoso.", true)]
    [InlineData("contosox", false)]
    [InlineData("acme.contoso.core", false)]
    [InlineData("moq", false)]
    public void InternalPrefixes_Matches_is_the_rule_the_engine_uses(string id, bool expected)
    {
        var prefixes = new[] { "Contoso.*", "  ", "*" };   // blank and bare '*' prefixes are ignored, never "match everything"
        Assert.Equal(expected, InternalPrefixes.Matches(prefixes, id));
        var engineHides = Decide(Facts(id, "1.0.0", 100), new TenantPolicy(14, prefixes)).Reason == VerdictReason.InternalNamespace;
        Assert.Equal(expected, engineHides);
    }
}
