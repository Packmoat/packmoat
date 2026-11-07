using System.Text.Json.Nodes;

namespace Packmoat.Protocols.NuGet;

/// <summary>
/// Keeps only the resources a read-only restore needs (version lists and registrations) and points them back
/// at Packmoat. Search, publish and every other resource are dropped, so clients cannot browse around the filter.
/// </summary>
public static class ServiceIndexRewriter
{
    private static readonly string[] KeptTypePrefixes = { "PackageBaseAddress", "RegistrationsBaseUrl" };

    public static string Rewrite(string json, UrlMap map)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("Service index is not a JSON object.");
        var resources = root["resources"] as JsonArray ?? throw new FormatException("Service index has no resources.");

        foreach (var resource in resources.ToList())
        {
            var type = resource?["@type"]?.GetValue<string>() ?? string.Empty;
            if (!KeptTypePrefixes.Any(p => type.StartsWith(p, StringComparison.Ordinal)))
                resources.Remove(resource);
        }
        return map.Rewrite(root.ToJsonString());
    }
}
