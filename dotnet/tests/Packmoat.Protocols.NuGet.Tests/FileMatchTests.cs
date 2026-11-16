using Packmoat.Protocols.NuGet;

namespace Packmoat.Protocols.NuGet.Tests;

public class FileMatchTests
{
    private const string Feed = "/o/p/_packaging/f/nuget/v3/flat2/";

    [Theory]
    [InlineData("moq/4.21.0/moq.4.21.0.nupkg")]
    [InlineData("moq/4.21.0/moq.nuspec")]
    [InlineData("moq/4.21.0/moq.4.21.0.0.nupkg")]        // the same version spelled with a fourth zero
    [InlineData("moq/4.21/moq.4.21.0.nupkg")]            // path spelled short, file spelled long
    [InlineData("newtonsoft.json/13.0.3-beta.1/newtonsoft.json.13.0.3-beta.1.nupkg")]
    public void The_file_of_the_same_package_and_version_is_content(string tail)
        => Assert.Equal(NuGetResourceKind.FlatContainerContent, NuGetRouteParser.Parse(Feed + tail).Kind);

    [Theory]
    [InlineData("moq/4.21.0/moq.4.22.0.nupkg")]           // another version's package
    [InlineData("moq/4.21.0/castle.core.4.21.0.nupkg")]   // another package
    [InlineData("moq/4.21.0/castle.core.nuspec")]
    [InlineData("moq/4.21.0/moq.4.21.0.nupkg.sha512")]
    [InlineData("moq/4.21.0/moq.4.21.0.zip")]
    [InlineData("moq/4.21.0/moq..nupkg")]
    [InlineData("moq/4.21.0/moq.4.21.0.nuspec")]          // the nuspec file has no version in its name
    [InlineData("moq/4.21.0/.nupkg")]
    public void Anything_else_is_unknown_and_therefore_refused(string tail)
        => Assert.Equal(NuGetResourceKind.Unknown, NuGetRouteParser.Parse(Feed + tail).Kind);
}

public class CatalogPathTests
{
    [Theory]
    [InlineData("/org/proj/_apis/packaging/feed00000000000001/nuget/commitLog/c0de0000000000000000000000000001")]
    [InlineData("/org/_apis/packaging/feed00000000000001/nuget/commitLog/abc123")]   // organisation-scoped feed: no project segment
    public void The_exact_azure_shape_is_a_catalog_entry(string path)
        => Assert.Equal(NuGetResourceKind.CatalogEntry, NuGetRouteParser.Parse(path).Kind);

    [Theory]
    [InlineData("/org/proj/_apis/packaging/f/nuget/commitLog/abc123/extra")]          // anything after the id
    [InlineData("/org/proj/_apis/packaging/f/nuget/commitLog/not-hex")]
    [InlineData("/org/proj/_apis/packaging/f/nuget/commitLog/")]
    [InlineData("/_apis/packaging/f/nuget/commitLog/abc123")]                         // no organisation
    [InlineData("/a/b/c/_apis/packaging/f/nuget/commitLog/abc123")]                   // too many segments before _apis
    [InlineData("/org/proj/_apis/packaging/f/npm/commitLog/abc123")]
    [InlineData("/org/proj/_apis/packaging/f/nuget/other/abc123")]
    [InlineData("/org/proj/_apis/git/f/nuget/commitLog/abc123")]
    [InlineData("/org/proj/commitlog/_apis/abc123")]                                  // both words present, wrong places
    [InlineData("/_apis/commitlog")]
    public void Any_other_path_that_merely_mentions_the_words_is_unknown(string path)
        => Assert.Equal(NuGetResourceKind.Unknown, NuGetRouteParser.Parse(path).Kind);
}
