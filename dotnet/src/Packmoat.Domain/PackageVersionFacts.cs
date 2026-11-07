namespace Packmoat.Domain;

/// <summary>What the policy needs to know about one package version. PublishedAt is null when unknown.</summary>
public sealed record PackageVersionFacts(Ecosystem Ecosystem, string PackageId, string Version, DateTimeOffset? PublishedAt)
{
    public string NormalizedId => PackageId.Trim().ToLowerInvariant();
    public string NormalizedVersion => NuGetVersionKey.Normalize(Version);
}
