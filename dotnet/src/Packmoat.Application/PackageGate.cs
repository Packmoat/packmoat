using Packmoat.Domain;
using Packmoat.Policy;

namespace Packmoat.Application;

/// <summary>Answers "may this tenant see this version?" using the policy engine and, only when needed, publish dates.</summary>
public interface IPackageGate
{
    Task<Verdict> DecideAsync(Tenant tenant, string packageId, string version, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, Verdict>> DecideManyAsync(
        Tenant tenant, string packageId, IReadOnlyCollection<string> versions, CancellationToken cancellationToken);
}

public sealed class PackageGate : IPackageGate
{
    private readonly IPolicyEngine _engine;
    private readonly IPublishDateSource _dates;

    public PackageGate(IPolicyEngine engine, IPublishDateSource dates)
    {
        _engine = engine;
        _dates = dates;
    }

    public async Task<Verdict> DecideAsync(Tenant tenant, string packageId, string version, CancellationToken cancellationToken)
    {
        var many = await DecideManyAsync(tenant, packageId, new[] { version }, cancellationToken);
        return many[version];
    }

    public async Task<IReadOnlyDictionary<string, Verdict>> DecideManyAsync(
        Tenant tenant, string packageId, IReadOnlyCollection<string> versions, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, Verdict>(StringComparer.Ordinal);
        var needDates = new List<string>();

        // First pass without any date: internal namespaces, waivers and zero cooldowns need no network call.
        foreach (var version in versions)
        {
            var verdict = _engine.Decide(new PackageVersionFacts(Ecosystem.NuGet, packageId, version, null), tenant.Policy);
            if (verdict.Reason == VerdictReason.DateUnknown) needDates.Add(version);
            else result[version] = verdict;
        }

        if (needDates.Count == 0) return result;

        IReadOnlyDictionary<string, DateTimeOffset>? dates;
        try
        {
            dates = await _dates.GetPublishDatesAsync(Ecosystem.NuGet, packageId, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            dates = null; // the date source failed: fail closed below
        }

        foreach (var version in needDates)
        {
            if (dates is null)
            {
                // The source itself failed. That is an outage, not "no date": fail closed, but let the caller say "retry later".
                result[version] = new Verdict(Decision.Hide, VerdictReason.DateSourceUnavailable);
                continue;
            }
            DateTimeOffset? published = null;
            if (dates.TryGetValue(NuGetVersionKey.Normalize(version), out var p)) published = p;
            result[version] = _engine.Decide(new PackageVersionFacts(Ecosystem.NuGet, packageId, version, published), tenant.Policy);
        }
        return result;
    }
}
