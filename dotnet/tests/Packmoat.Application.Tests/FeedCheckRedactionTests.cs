using Packmoat.Application;

namespace Packmoat.Application.Tests;

/// <summary>#107: the 30-day redaction of a check's results keeps what was checked and how it went, and no detail (which may name a person).</summary>
public class FeedCheckRedactionTests
{
    [Fact]
    public void Every_detail_is_replaced_and_id_title_status_and_order_are_kept()
    {
        var results = new[]
        {
            new CheckResult("feed", "The feed is the tenant's", CheckStatus.Pass, "the options match"),
            new CheckResult("permissions", "Only Packmoat holds a role", CheckStatus.Warn, "named admin \"Ada Lovelace\"; confirm"),
            new CheckResult("developer", "Developers can't reach it", CheckStatus.Skip, "Grace Hopper could not be checked"),
            new CheckResult("bypass", "No other feed", CheckStatus.Fail, "remove it", new[] { "private", "tools" }),
        };

        var redacted = FeedCheckRedaction.Redact(results);

        Assert.Equal(results.Select(r => (r.Id, r.Title, r.Status)), redacted.Select(r => (r.Id, r.Title, r.Status)));
        Assert.All(redacted, r => Assert.Equal(FeedCheckRedaction.RemovedDetail, r.Detail));
        Assert.All(redacted, r => Assert.Null(r.Names));
        Assert.Equal("Details removed after 30 days.", FeedCheckRedaction.RemovedDetail);
    }

    [Fact]
    public void Redacting_twice_gives_the_same_results_and_none_gives_none()
    {
        var once = FeedCheckRedaction.Redact(new[] { new CheckResult("feed", "F", CheckStatus.Pass, "Ada") });
        Assert.Equal(once, FeedCheckRedaction.Redact(once));
        Assert.Empty(FeedCheckRedaction.Redact(Array.Empty<CheckResult>()));
    }
}
