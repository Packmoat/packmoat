using Packmoat.Domain;

namespace Packmoat.Policy.Tests;

internal static class PolicyTestKit
{
    public static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public static PackageVersionFacts Facts(string id, string version, double? ageDays) =>
        new(Ecosystem.NuGet, id, version, ageDays is null ? null : Now - TimeSpan.FromDays(ageDays.Value));

    public static (PolicyEngine Engine, FakeClock Clock) Engine() { var c = new FakeClock(Now); return (PolicyEngine.CreateDefault(c), c); }
}
