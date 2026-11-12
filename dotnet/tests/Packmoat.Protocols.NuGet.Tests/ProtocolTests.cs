using Packmoat.Protocols.NuGet;

namespace Packmoat.Protocols.NuGet.Tests;

// The version filters and the service index rewrite are conformance vectors, run by the host's tests:
// spec/vectors/nuget.

public class RouteParserTests
{
    private const string Feed = "/example-org/proj/_packaging/feed/nuget/v3/";

    [Theory]
    [InlineData(Feed + "index.json", NuGetResourceKind.ServiceIndex, null, null)]
    [InlineData(Feed + "flat2/moq/index.json", NuGetResourceKind.FlatContainerVersions, "moq", null)]
    [InlineData(Feed + "flat2/moq/4.21.0/moq.4.21.0.nupkg", NuGetResourceKind.FlatContainerContent, "moq", "4.21.0")]
    [InlineData(Feed + "flat2/moq/4.21.0/moq.nuspec", NuGetResourceKind.FlatContainerContent, "moq", "4.21.0")]
    [InlineData(Feed + "registrations2/moq/index.json", NuGetResourceKind.RegistrationIndex, "moq", null)]
    [InlineData(Feed + "registrations2/moq/page/1.0.0/2.0.0.json", NuGetResourceKind.RegistrationPage, "moq", null)]
    [InlineData(Feed + "registrations2/moq/4.21.0.json", NuGetResourceKind.RegistrationLeaf, "moq", "4.21.0")]
    [InlineData("/example-org/proj/_apis/packaging/feedid/nuget/commitLog/abc123", NuGetResourceKind.CatalogEntry, null, null)]
    [InlineData(Feed + "query2?q=moq", NuGetResourceKind.Unknown, null, null)]
    [InlineData("/something/else", NuGetResourceKind.Unknown, null, null)]
    public void Classifies(string path, NuGetResourceKind kind, string? id, string? version)
    {
        var r = NuGetRouteParser.Parse(path);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(id, r.PackageId);
        Assert.Equal(version, r.Version);
    }

    [Fact]
    public void Package_ids_and_versions_are_case_folded_and_unescaped()
    {
        var r = NuGetRouteParser.Parse(Feed + "flat2/Newtonsoft.Json/13.0.3-BETA%2B1/Newtonsoft.Json.13.0.3-BETA%2B1.nupkg");
        Assert.Equal("newtonsoft.json", r.PackageId);
        Assert.Equal("13.0.3-beta+1", r.Version);
    }

    /// <summary>A control character once unescaped (a NUL, a tab, a newline) is never a package id or version: Unknown.</summary>
    [Theory]   // mutation: the control-character refusal removed
    [InlineData("flat2/moq%00x/index.json")]
    [InlineData("flat2/moq%00x/1.0.0/moq%00x.1.0.0.nupkg")]
    [InlineData("flat2/moq/1.0.0%09/moq.1.0.0%09.nupkg")]
    [InlineData("registrations2/moq%0ax/index.json")]
    [InlineData("registrations2-semver2/moq/1.0.0%00.json")]
    [InlineData("registrations2/moq%7fx/page/1.0.0/2.0.0.json")]
    public void A_control_character_once_unescaped_is_an_unknown_route(string tail) =>
        Assert.Equal(new NuGetRoute(NuGetResourceKind.Unknown), NuGetRouteParser.Parse(Feed + tail));
}
