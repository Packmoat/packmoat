using System.Text.Json.Nodes;
using Packmoat.Protocols.NuGet;

namespace Packmoat.Protocols.NuGet.Tests;

/// <summary>
/// A flat-container version list, as a registration leaf's version (<see cref="RegistrationVersionTests"/>): the list is the
/// tenant's upstream's content, not a request, but each version in it is decided on and recorded. One that holds a control character is
/// no NuGet version: it is left out of the versions decided on, and removed from the list served (fail closed), whatever the decision.
/// </summary>
public class FlatVersionTests
{
    private static readonly UrlMap Map = new("https://up.example/", "https://feeds.example/");

    private static string List(string odd) => new JsonObject { ["versions"] = new JsonArray("1.0.0", odd, "2.0.0") }.ToJsonString();

    private static string[] Served(string json) =>
        JsonNode.Parse(json)!["versions"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();

    // mutations: FlatVersions takes a version that holds a control character; FilterFlat keeps one that nothing hides
    [Theory]
    [InlineData("1.5.0\u0000")]   // a NUL
    [InlineData("1.5.0\n")]       // a line feed
    [InlineData("\u001B1.5.0")]   // an escape
    [InlineData("1.5.0\u007F")]   // DEL
    [InlineData("1.5\u00850")]    // NEL (U+0085), a C1 control
    public void A_version_with_a_control_character_is_never_decided_on_and_never_served(string odd)
    {
        var json = List(odd);
        Assert.Equal(["1.0.0", "2.0.0"], VersionFilters.FlatVersions(json));
        var asked = new List<string>();
        var served = Served(VersionFilters.FilterFlat(json, v => { asked.Add(v); return false; }, Map));
        Assert.Equal(["1.0.0", "2.0.0"], served);
        Assert.Equal(["1.0.0", "2.0.0"], asked);   // the odd one is removed before anything is asked about it
    }

    [Fact]   // the control: an ordinary list is read and filtered as before
    public void An_ordinary_list_is_read_and_filtered_as_before()
    {
        var json = new JsonObject { ["versions"] = new JsonArray("1.0.0", "2.0.0-beta+1", "3.0.0-rc.1") }.ToJsonString();
        Assert.Equal(["1.0.0", "2.0.0-beta+1", "3.0.0-rc.1"], VersionFilters.FlatVersions(json));
        Assert.Equal(["1.0.0", "3.0.0-rc.1"], Served(VersionFilters.FilterFlat(json, v => v == "2.0.0-beta+1", Map)));
        // review (r4b): nothing hidden, every ordinary version kept, build metadata included. mutation: FilterFlat drops a "+" version
        Assert.Equal(["1.0.0", "2.0.0-beta+1", "3.0.0-rc.1"], Served(VersionFilters.FilterFlat(json, _ => false, Map)));
        Assert.Empty(VersionFilters.FlatVersions("{}"));
    }
}
