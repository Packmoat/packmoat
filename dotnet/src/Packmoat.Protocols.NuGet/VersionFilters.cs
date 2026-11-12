using System.Text.Json.Nodes;

namespace Packmoat.Protocols.NuGet;

/// <summary>Removes hidden versions from NuGet JSON. The decision itself is made elsewhere (the policy engine).</summary>
public static class VersionFilters
{
    /// <summary>
    /// Versions listed in a flat-container <c>index.json</c> body: <c>{"versions":[...]}</c>. A version that holds a control character is
    /// left out (as a registration leaf's): no NuGet version holds one, so it is never decided on or recorded, and
    /// <see cref="FilterFlat"/> removes it (fail closed).
    /// </summary>
    public static IReadOnlyList<string> FlatVersions(string json) =>
        (JsonNode.Parse(json)?["versions"] as JsonArray ?? new JsonArray())
            .Select(v => v!.GetValue<string>()).Where(v => ReadableVersion(v) is not null).ToArray();

    /// <summary>The flat-container list without the versions <paramref name="isHidden"/> hides, nor any that holds a control character.</summary>
    public static string FilterFlat(string json, Func<string, bool> isHidden, UrlMap map)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("Version list is not a JSON object.");
        if (root["versions"] is JsonArray versions)
            foreach (var v in versions.ToList())
                if (ReadableVersion(v!.GetValue<string>()) is not { } version || isHidden(version)) versions.Remove(v);
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

    /// <summary>
    /// A leaf's version: its catalog entry's, else the last segment of its @id, unescaped. Null when neither can be read, or when what was
    /// read holds a control character once unescaped (as the route parser refuses one): no NuGet version holds
    /// one, and this text is the tenant's upstream's, not a request's. Null is no version, so the leaf is hidden (fail closed) and the
    /// version is never decided on or recorded (PostgreSQL's text, for one, cannot hold a NUL).
    /// </summary>
    private static string? LeafVersion(JsonNode leaf) => ReadableVersion(RawLeafVersion(leaf));

    private static string? ReadableVersion(string? version) => version is not null && version.Any(char.IsControl) ? null : version;

    private static string? RawLeafVersion(JsonNode leaf)
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
