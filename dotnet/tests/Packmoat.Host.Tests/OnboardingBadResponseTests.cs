using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// #63 H0: Azure DevOps answers a request it cannot authenticate with 203 and an HTML sign-in page, and other failures can come
/// back as a redirect or as a 200 that is not JSON. None of those is a settings answer. Each must become a FAILED or NOT VERIFIED
/// check that says what came back, never an exception out of the check (which crashed `onboard check`).
/// </summary>
public partial class OnboardingBadResponseTests
{
    [Theory]
    [MemberData(nameof(BadAnswers))]
    public async Task A_bad_answer_is_a_failed_or_not_verified_check_never_a_crash(string kind)
    {
        var fake = new FakeAzureDevOps { Bad = () => Make(kind) };

        var r = await Run(fake);   // must not throw

        // The service principal list could not be read: not verified (and so the role cannot be checked either).
        Assert.Equal(CheckStatus.Skip, Get(r, "identity").Status);
        Assert.StartsWith("NOT VERIFIED: could not list the organisation's service principals", Get(r, "identity").Detail);
        Assert.Equal(CheckStatus.Skip, Get(r, "permissions").Status);
        foreach (var id in new[] { "upstreams", "views" })
        {
            Assert.Equal(CheckStatus.Fail, Get(r, id).Status);
            Assert.StartsWith("could not complete:", Get(r, id).Detail);
        }
        foreach (var c in r)
        {
            Assert.DoesNotContain(AdminSecret, c.Detail);
            Assert.DoesNotContain("<html", c.Detail, StringComparison.OrdinalIgnoreCase);   // the page itself is never echoed
        }
        Assert.Equal(1, OnboardingSummary.Evaluate(r).ExitCode);
    }

    [Theory]
    [InlineData("203-html", "HTTP 203")]
    [InlineData("203-json", "HTTP 203")]
    [InlineData("302", "HTTP 302")]
    [InlineData("200-html", "not JSON")]
    [InlineData("200-empty", "not JSON")]
    [InlineData("200-not-an-object", "not a JSON object")]
    public async Task The_detail_says_what_came_back(string kind, string expected)
    {
        var r = await Run(new FakeAzureDevOps { Bad = () => Make(kind) });
        Assert.Contains(expected, Get(r, "upstreams").Detail);
        Assert.Contains(expected, Get(r, "identity").Detail);
    }

    [Fact]
    public async Task Valid_JSON_answers_still_pass()
    {
        var r = await Run(new FakeAzureDevOps());
        foreach (var id in new[] { "feed", "identity", "upstreams", "permissions", "views" })
            Assert.True(Get(r, id).Status == CheckStatus.Pass, $"{id}: {Get(r, id).Detail}");
    }
}
