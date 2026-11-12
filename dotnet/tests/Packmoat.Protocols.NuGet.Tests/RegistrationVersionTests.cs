using System.Text.Json.Nodes;
using Packmoat.Protocols.NuGet;

namespace Packmoat.Protocols.NuGet.Tests;

/// <summary>
/// A registration leaf's version: the upstream registration JSON is the tenant's feed's content, not a request, but a version
/// read from it is decided on and recorded. One that holds a control character once unescaped (from the @id's escaped segment, or straight
/// from the catalog entry's JSON) is no version: the leaf is hidden (fail closed), as the route parser refuses such a segment.
/// </summary>
public class RegistrationVersionTests
{
    private static readonly UrlMap Map = new("https://up.example/", "https://feeds.example/");

    /// <summary>A registration page whose one leaf has <paramref name="leaf"/>, beside a plain 1.0.0 leaf.</summary>
    private static string Page(JsonObject leaf) =>
        new JsonObject
        {
            ["count"] = 2,
            ["items"] = new JsonArray(
                new JsonObject { ["@id"] = "https://up.example/reg/moq/1.0.0.json", ["catalogEntry"] = new JsonObject { ["version"] = "1.0.0" } },
                leaf),
        }.ToJsonString();

    private static JsonObject ById(string segment) => new() { ["@id"] = $"https://up.example/reg/moq/{segment}.json" };

    private static JsonObject ByEntry(string version) => new() { ["catalogEntry"] = new JsonObject { ["version"] = version } };

    // mutation: LeafVersion takes a version that holds a control character
    [Theory]
    [InlineData("2.0.0%00")]       // a NUL, escaped once
    [InlineData("2.0.0%0A")]       // a line feed
    [InlineData("2.0.0%1B")]       // an escape
    [InlineData("2.0.0%7F")]       // DEL
    [InlineData("%C2%85" + "2.0")] // NEL (U+0085), a C1 control
    public void A_leaf_whose_id_unescapes_to_a_control_character_is_no_version_and_is_hidden(string segment)
    {
        var json = Page(ById(segment));
        Assert.Equal(["1.0.0", ""], VersionFilters.RegistrationVersions(json));   // "" is no version: never decided on
        var filtered = JsonNode.Parse(VersionFilters.FilterRegistration(json, _ => false, Map))!;
        Assert.Equal(1, (int)filtered["count"]!);
        Assert.Single(filtered["items"]!.AsArray());
    }

    [Theory]
    [InlineData("2.0.0\u0000")]
    [InlineData("2.0.0\n")]
    [InlineData("\u00012.0.0")]
    public void A_catalog_entry_version_with_a_control_character_is_no_version_and_is_hidden(string version)
    {
        var json = Page(ByEntry(version));
        Assert.Equal(["1.0.0", ""], VersionFilters.RegistrationVersions(json));
        Assert.Equal(1, (int)JsonNode.Parse(VersionFilters.FilterRegistration(json, _ => false, Map))!["count"]!);
    }

    [Theory]   // the control: an escaped character that is not a control, and an ordinary prerelease, are read as before and kept
    [InlineData("2.0.0-beta%2B1", "2.0.0-beta+1")]
    [InlineData("2.0.0-rc.1", "2.0.0-rc.1")]
    public void An_ordinary_version_is_read_unescaped_and_kept(string segment, string version)
    {
        var json = Page(ById(segment));
        Assert.Equal(["1.0.0", version], VersionFilters.RegistrationVersions(json));
        Assert.Equal(2, (int)JsonNode.Parse(VersionFilters.FilterRegistration(json, _ => false, Map))!["count"]!);
    }
}
