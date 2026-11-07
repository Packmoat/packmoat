using System.Net;
using Packmoat.Domain;

namespace Packmoat.Host.Tests;

[Collection("sqlite-pools")]
public class ReviewFixesM3HostTests
{
    [Fact]
    public async Task A_publish_date_outage_is_recorded_in_the_audit_trail_as_an_outage_not_as_an_unknown_date()
    {
        await using var f = await SqliteHostFixture.StartAsync();
        f.Publish("Acme.Lib", "1.0.0", 900);
        f.Dates.Unavailable = true;
        var token = await f.Tokens.IssueAsync("acme", "ci", default);
        using var http = new HttpClient { BaseAddress = new Uri(f.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", token.Plaintext);

        var response = await http.GetAsync("/v3/u/org/proj/_packaging/feed/nuget/v3/flat2/acme.lib/1.0.0/acme.lib.1.0.0.nupkg");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var row = Assert.Single(await f.Audit.QueryAsync("acme", 10, default));
        Assert.Equal(VerdictReason.DateSourceUnavailable, row.Reason);
        Assert.Equal(Decision.Hide, row.Decision);
        Assert.Equal(token.TokenId, row.TokenId);
    }
}
