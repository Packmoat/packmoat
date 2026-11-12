using Packmoat.Domain;

namespace Packmoat.Protocols.NuGet;

public enum NuGetResourceKind
{
    ServiceIndex,
    FlatContainerVersions,
    FlatContainerContent,
    RegistrationIndex,
    RegistrationPage,
    RegistrationLeaf,
    /// <summary>A catalog/commit-log entry URL referenced by registration leaves. Carries no version list.</summary>
    CatalogEntry,
    /// <summary>Anything else. The proxy refuses it (fail closed).</summary>
    Unknown,
}

public sealed record NuGetRoute(NuGetResourceKind Kind, string? PackageId = null, string? Version = null);

/// <summary>Classifies an upstream path so the proxy knows what filtering a response needs.</summary>
public static class NuGetRouteParser
{
    public static NuGetRoute Parse(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Uri.UnescapeDataString(s).ToLowerInvariant())
            .ToArray();

        // A control character once unescaped ("%2500" reaches here as "%00", which unescapes to a NUL) is never part of a
        // package id or a version, so the path is refused before any decision, audit row or upstream call. No NuGet id or version holds one.
        if (segments.Any(s => s.Any(char.IsControl))) return new NuGetRoute(NuGetResourceKind.Unknown);

        var flat = Array.IndexOf(segments, "flat2");
        if (flat >= 0)
        {
            var rest = segments.Length - flat - 1;
            if (rest == 2 && segments[^1] == "index.json")
                return new NuGetRoute(NuGetResourceKind.FlatContainerVersions, segments[flat + 1]);
            if (rest == 3)
                return FileMatchesIdAndVersion(segments[flat + 1], segments[flat + 2], segments[flat + 3])
                    ? new NuGetRoute(NuGetResourceKind.FlatContainerContent, segments[flat + 1], segments[flat + 2])
                    : new NuGetRoute(NuGetResourceKind.Unknown); // the file named does not belong to the version we would decide on
            return new NuGetRoute(NuGetResourceKind.Unknown);
        }

        var reg = Array.FindIndex(segments, s => s.StartsWith("registrations", StringComparison.Ordinal));
        if (reg >= 0 && segments.Length > reg + 2)
        {
            var id = segments[reg + 1];
            var tail = segments[(reg + 2)..];
            if (tail.Length == 1 && tail[0] == "index.json")
                return new NuGetRoute(NuGetResourceKind.RegistrationIndex, id);
            if (tail[0] == "page")
                return new NuGetRoute(NuGetResourceKind.RegistrationPage, id);
            if (tail.Length == 1 && tail[0].EndsWith(".json", StringComparison.Ordinal))
                return new NuGetRoute(NuGetResourceKind.RegistrationLeaf, id, tail[0][..^".json".Length]);
            return new NuGetRoute(NuGetResourceKind.Unknown);
        }

        if (segments.Length >= 2 && segments[^1] == "index.json" && segments[^2] == "v3")
            return new NuGetRoute(NuGetResourceKind.ServiceIndex);

        if (IsCatalogEntryPath(segments))
            return new NuGetRoute(NuGetResourceKind.CatalogEntry);

        return new NuGetRoute(NuGetResourceKind.Unknown);
    }

    /// <summary>
    /// The decision is made on the version in the path, so the file must be that same version's file: either
    /// <c>{id}.{version}.nupkg</c> (version compared after normalisation) or <c>{id}.nuspec</c>. Anything else is refused.
    /// </summary>
    private static bool FileMatchesIdAndVersion(string id, string version, string file)
    {
        if (file == id + ".nuspec") return true;
        if (!file.EndsWith(".nupkg", StringComparison.Ordinal) || !file.StartsWith(id + ".", StringComparison.Ordinal)) return false;
        var fileVersion = file[(id.Length + 1)..^".nupkg".Length];
        return fileVersion.Length > 0 && NuGetVersionKey.Normalize(fileVersion) == NuGetVersionKey.Normalize(version);
    }

    /// <summary>
    /// The exact shape Azure Artifacts uses for the catalog link in a registration leaf:
    /// <c>/{org}[/{project}]/_apis/packaging/{feedId}/nuget/commitLog/{id}</c>, with nothing after the id.
    /// </summary>
    private static bool IsCatalogEntryPath(string[] segments)
    {
        var api = Array.IndexOf(segments, "_apis");
        if (api < 1 || api > 2) return false;               // an organisation, and at most one project, before _apis
        var tail = segments[(api + 1)..];
        return tail.Length == 5
            && tail[0] == "packaging"
            && tail[1].Length > 0
            && tail[2] == "nuget"
            && tail[3] == "commitlog"
            && tail[4].Length > 0 && tail[4].All(Uri.IsHexDigit);
    }
}
