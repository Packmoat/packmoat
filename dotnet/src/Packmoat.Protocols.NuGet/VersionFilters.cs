using System.Text.Json.Nodes;

namespace Packmoat.Protocols.NuGet;

/// <summary>Removes hidden versions from NuGet JSON. The decision itself is made elsewhere (the policy engine).</summary>
public static class VersionFilters
{
    /// <summary>Versions listed in a flat-container <c>index.json</c> body: <c>{"versions":[...]}</c>.</summary>
    public static IReadOnlyList<string> FlatVersions(string json) =>
        (JsonNode.Parse(json)?["versions"] as JsonArray ?? new JsonArray())
            .Select(v => v!.GetValue<string>()).ToArray();

    public static string FilterFlat(string json, Func<string, bool> isHidden, UrlMap map)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("Version list is not a JSON object.");
        if (root["versions"] is JsonArray versions)
            foreach (var v in versions.ToList())
                if (isHidden(v!.GetValue<string>())) versions.Remove(v);
        return map.Rewrite(root.ToJsonString());
    }

    /// <summary>
    /// Versions present in a registration index or page. A leaf whose version cannot be read is reported as an
    /// empty string and therefore treated as hidden (fail closed).
    /// </summary>
    public static IReadOnlyList<string> RegistrationVersions(string json)
    {
        var versions = new List<string>();
        foreach (var leaf in Leaves(JsonNode.Parse(json)!)) versions.Add(LeafVersion(leaf) ?? string.Empty);
        return versions;
    }

    public static string FilterRegistration(string json, Func<string, bool> isHidden, UrlMap map)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("Registration is not a JSON object.");
        var index = IsIndex(root);

        foreach (var items in ItemArrays(root))
        {
            foreach (var leaf in items.OfType<JsonObject>().ToList())
            {
                var version = LeafVersion(leaf);
                if (version is null || isHidden(version)) items.Remove(leaf);
            }
        }

        // Keep advertised counts truthful.
        if (index)
        {
            foreach (var page in ((JsonArray)root["items"]!).OfType<JsonObject>())
                if (page["items"] is JsonArray inner) page["count"] = inner.Count;
        }
        else if (root["items"] is JsonArray leaves)
        {
            root["count"] = leaves.Count;
        }
        return map.Rewrite(root.ToJsonString());
    }

    // A registration INDEX has pages in "items"; a PAGE has leaves directly in "items".
    // A page that is only a link (no inline "items") is fetched by the client through us and filtered then.
    private static bool IsIndex(JsonObject root) =>
        root["items"] is JsonArray a && a.OfType<JsonObject>().Any(o => o["lower"] is not null || o["upper"] is not null || o["items"] is JsonArray);

    private static IEnumerable<JsonObject> Leaves(JsonNode root) =>
        ItemArrays(root as JsonObject ?? new JsonObject()).SelectMany(a => a.OfType<JsonObject>());

    private static IEnumerable<JsonArray> ItemArrays(JsonObject root)
    {
        if (root["items"] is not JsonArray top) yield break;
        if (IsIndex(root))
        {
            foreach (var page in top.OfType<JsonObject>())
                if (page["items"] is JsonArray inner) yield return inner;
        }
        else
        {
            yield return top; // a page: leaves are directly in "items"
        }
    }

    private static string? LeafVersion(JsonNode leaf)
    {
        if (leaf["catalogEntry"] is JsonObject entry && entry["version"] is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (leaf["@id"] is JsonValue id && id.TryGetValue<string>(out var url))
        {
            var last = url.Split('/').LastOrDefault();
            if (last is not null && last.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return Uri.UnescapeDataString(last[..^".json".Length]);
        }
        return null;
    }
}
