using Packmoat.Domain;

namespace Packmoat.Policy.Tests;

public class NuGetVersionKeyTests
{
    [Theory]
    [InlineData("1.0", "1.0.0")]
    [InlineData("1", "1.0.0")]
    [InlineData("1.0.0.0", "1.0.0")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("01.02.03", "1.2.3")]
    [InlineData("4.21.0", "4.21.0")]
    [InlineData("1.0.0+abc123", "1.0.0")]
    [InlineData("2.0.0-Beta.1", "2.0.0-beta.1")]
    [InlineData("2.0-RC1+meta", "2.0.0-rc1")]
    [InlineData("  3.1.4  ", "3.1.4")]
    public void Normalises(string input, string expected) => Assert.Equal(expected, NuGetVersionKey.Normalize(input));

    [Fact]
    public void Non_numeric_versions_are_kept_literally_lowercased()
        => Assert.Equal("weird.version", NuGetVersionKey.Normalize("Weird.Version"));
}
