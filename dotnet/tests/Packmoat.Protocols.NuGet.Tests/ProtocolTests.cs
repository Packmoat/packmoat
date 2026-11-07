using Packmoat.Protocols.NuGet;

namespace Packmoat.Protocols.NuGet.Tests;

// The version filters and the service index rewrite are conformance vectors, run by the host's tests:
// core/spec/vectors/nuget (#209).

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
}
