using System.Text.RegularExpressions;

namespace Packmoat.Host.Tests;

// Shared with the hosted tests, which compile this file in: the customer pages they test must not show operator words either.
public partial class OnboardingChecksTests
{
    /// <summary>Operator words no customer page may show.</summary>
    internal static void AssertNoOperatorWords(string text)
    {
        foreach (var word in new[]
                 {
                     "checklist", "--confirm-admin", "credential", "onboard check", "operator", "--probe-url", "rerun", "public-only", "identities API",
                     "tenant", "principal", "customer", "shared app", "could not complete",
                 })
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HTTP", text, StringComparison.Ordinal);   // a status code (https:// addresses are fine)
        Assert.DoesNotMatch(new Regex(@"\bPAT\b"), text);
    }
}
