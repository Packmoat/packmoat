using System.Net;
using System.Text.RegularExpressions;
using static Packmoat.Host.Tests.ProxyFixture;

namespace Packmoat.Host.Tests;

/// <summary>#166: /healthz says which commit is running, so a deploy check can tell whether a merge has landed.</summary>
public class BuildInfoTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData("1.0.0+" + Sha, "0123456789ab")]
    [InlineData("1.0.0+0123456789ABCDEF0123456789ABCDEF01234567", "0123456789ab")]   // upper case is normalised
    [InlineData("1.0.0-beta+meta." + Sha, "unknown")]                                 // only a bare SHA after the last '+' counts
    [InlineData("1.0.0+unknown", "unknown")]                                          // the Dockerfile default (a local build)
    [InlineData("1.0.0+", "unknown")]
    [InlineData("1.0.0", "unknown")]
    [InlineData("1.0.0+0123456789ab", "unknown")]                                     // a short SHA is not trusted
    [InlineData("1.0.0+" + Sha + "0", "unknown")]                                     // too long
    [InlineData("1.0.0+0123456789abcdef0123456789abcdef0123456g", "unknown")]         // not hex
    [InlineData("1.0.0+" + Sha + " ", "unknown")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void The_commit_is_the_first_12_hex_of_a_full_sha_after_the_plus_or_unknown(string? informationalVersion, string expected) =>
        Assert.Equal(expected, BuildInfo.ParseCommit(informationalVersion));

    [Fact]
    public void The_running_commit_always_has_the_healthz_shape() =>
        Assert.Matches("^([0-9a-f]{12}|unknown)$", BuildInfo.Commit);

    [Fact]
    public async Task Healthz_answers_ok_and_the_build_commit_without_a_token()
    {
        await using var p = await StartAsync();
        using var http = p.Client(null);

        var response = await http.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Matches(new Regex("^ok ([0-9a-f]{12}|unknown)$"), body);
        Assert.Equal("ok " + BuildInfo.Commit, body);
    }
}
