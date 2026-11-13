using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Packmoat.Domain;
using static Packmoat.Host.Tests.ProxyFixture;

namespace Packmoat.Host.Tests;

/// <summary>Regression tests for the NuGet proxy.</summary>
public class ReviewFixesM2Tests
{
    private const string Flat = "/v3/u/org/proj/_packaging/feed/nuget/v3/flat2/";

    // ---- (1) the file must belong to the version the decision was made on
    [Theory]
    [InlineData("acme.lib/1.0.0/acme.lib.2.0.0.nupkg")]      // another version's package behind an allowed version's path
    [InlineData("acme.lib/1.0.0/other.lib.1.0.0.nupkg")]     // another package
    [InlineData("acme.lib/1.0.0/other.nuspec")]
    [InlineData("acme.lib/1.0.0/acme.lib.1.0.0.nupkg.sha512")]
    [InlineData("acme.lib/1.0.0/acme.lib..nupkg")]
    public async Task A_file_that_does_not_match_the_version_in_the_path_is_refused_without_asking_upstream(string tail)
    {
        await using var p = await StartAsync();
        p.Publish("Acme.Lib", "1.0.0", 100);
        p.Publish("Acme.Lib", "2.0.0", 3);   // young: must stay unreachable
        using var http = p.Client(AcmeToken);

        var response = await http.GetAsync(Flat + tail);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, p.Upstream.FeedRequestCount);
    }

    [Fact]
    public async Task The_young_versions_bytes_cannot_be_fetched_by_naming_its_file_under_an_old_versions_path()
    {
        await using var p = await StartAsync();
        p.Publish("Acme.Lib", "1.0.0", 100);
        p.Publish("Acme.Lib", "2.0.0", 3);
        using var http = p.Client(AcmeToken);

        var bypass = await http.GetAsync(Flat + "acme.lib/1.0.0/acme.lib.2.0.0.nupkg");
        var proper = await http.GetAsync(Flat + "acme.lib/1.0.0/acme.lib.1.0.0.nupkg");

        Assert.Equal(HttpStatusCode.NotFound, bypass.StatusCode);
        Assert.Equal(HttpStatusCode.OK, proper.StatusCode);
        Assert.Equal(p.Upstream.NupkgBytes("Acme.Lib", "1.0.0"), await proper.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_matching_nupkg_and_nuspec_work_and_another_spelling_of_the_same_version_is_judged_as_that_version()
    {
        await using var p = await StartAsync();
        p.Publish("Acme.Lib", "1.0.0", 100);
        p.Publish("Acme.Lib", "2.0.0", 3);
        using var http = p.Client(AcmeToken);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(Flat + "acme.lib/1.0.0/acme.lib.nuspec")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(Flat + "acme.lib/1.0.0/acme.lib.1.0.0.0.nupkg")).StatusCode);   // "1.0.0.0" is 1.0.0
        var young = await http.GetAsync(Flat + "acme.lib/2.0.0/acme.lib.2.0.0.0.nupkg");                                      // ... and "2.0.0.0" is the young 2.0.0
        Assert.Equal(HttpStatusCode.NotFound, young.StatusCode);
        Assert.Equal("Cooldown", young.Headers.GetValues("X-Packmoat-Reason").Single());
    }

    // ---- (4) a date-source outage is a retryable 503, still failing closed
    [Fact]
    public async Task While_publish_dates_are_unavailable_downloads_and_lists_answer_503_with_retry_after_and_never_a_partial_list()
    {
        await using var p = await StartAsync();
        p.Publish("Acme.Lib", "1.0.0", 900);
        p.Publish("Acme.Lib", "2.0.0", 3);
        p.Dates.Unavailable = true;
        using var http = p.Client(AcmeToken);

        foreach (var path in new[]
        {
            Flat + "acme.lib/1.0.0/acme.lib.1.0.0.nupkg",
            Flat + "acme.lib/index.json",
            "/v3/u/org/proj/_packaging/feed/nuget/v3/registrations2/acme.lib/index.json",
            "/v3/u/org/proj/_packaging/feed/nuget/v3/registrations2/acme.lib/1.0.0.json",
        })
        {
            var response = await http.GetAsync(path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("30", response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString());
        }
        Assert.DoesNotContain(p.Upstream.Requests, r => r.Path.EndsWith(".nupkg"));
    }

    [Fact]
    public async Task During_an_outage_decisions_that_need_no_date_still_work()
    {
        var waiver = new Waiver("w1", "Acme.Lib", "2.0.0", new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), "hotfix", "olivia");
        await using var p = await StartAsync(_ => new TenantPolicy(14, new[] { "Contoso." }, new[] { waiver }));
        p.Publish("Acme.Lib", "2.0.0", 3);
        p.Publish("Acme.Lib", "1.0.0", 900);
        p.Dates.Unavailable = true;
        using var acme = p.Client(AcmeToken);
        using var globex = p.Client(GlobexToken);   // no cooldown: needs no date

        Assert.Equal(HttpStatusCode.OK, (await acme.GetAsync(Flat + "acme.lib/2.0.0/acme.lib.2.0.0.nupkg")).StatusCode);       // waived
        Assert.Equal(HttpStatusCode.OK, (await globex.GetAsync(Flat + "acme.lib/1.0.0/acme.lib.1.0.0.nupkg")).StatusCode);     // cooldown 0
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await acme.GetAsync(Flat + "acme.lib/1.0.0/acme.lib.1.0.0.nupkg")).StatusCode);
    }

    [Fact]
    public async Task A_genuinely_unknown_date_is_still_a_404_not_a_503()
    {
        await using var p = await StartAsync();
        p.Upstream.AddPackage("Acme.Lib", "1.0.0");      // present upstream, but nuget.org has no date for it
        using var http = p.Client(AcmeToken);

        var response = await http.GetAsync(Flat + "acme.lib/1.0.0/acme.lib.1.0.0.nupkg");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("DateUnknown", response.Headers.GetValues("X-Packmoat-Reason").Single());
    }

    // ---- (3) the size cap holds for chunked and compressed bodies
    private static async Task Chunked(Microsoft.AspNetCore.Http.HttpContext ctx, int bytes)
    {
        ctx.Response.ContentType = "application/json";            // no Content-Length: chunked
        await ctx.Response.WriteAsync("{\"versions\":[\"1.0.0\"],\"pad\":\"");
        var block = new string('x', 64 * 1024);
        for (var written = 0; written < bytes; written += block.Length) await ctx.Response.WriteAsync(block);
        await ctx.Response.WriteAsync("\"}");
    }

    [Fact]
    public async Task A_chunked_json_response_over_the_cap_is_refused_while_being_read()
    {
        await using var p = await StartWithOptionsAsync(new Packmoat.Host.PackmoatOptions { MaxJsonBytes = 256 * 1024 });
        p.Publish("Acme.Lib", "1.0.0", 100);
        p.Upstream.Intercept = async (path, ctx) => { if (!path.EndsWith("flat2/acme.lib/index.json")) return false; await Chunked(ctx, 2 * 1024 * 1024); return true; };
        using var http = p.Client(AcmeToken);

        var response = await http.GetAsync(Flat + "acme.lib/index.json");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task A_compressed_response_that_expands_past_the_cap_is_refused()
    {
        await using var p = await StartWithOptionsAsync(new Packmoat.Host.PackmoatOptions { MaxJsonBytes = 256 * 1024 });
        p.Publish("Acme.Lib", "1.0.0", 100);
        p.Upstream.Intercept = async (path, ctx) =>
        {
            if (!path.EndsWith("flat2/acme.lib/index.json")) return false;
            using var ms = new MemoryStream();
            await using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                await gz.WriteAsync(Encoding.UTF8.GetBytes("{\"versions\":[\"1.0.0\"],\"pad\":\"" + new string('0', 8 * 1024 * 1024) + "\"}"));
            }
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers.ContentEncoding = "gzip";
            await ctx.Response.Body.WriteAsync(ms.ToArray());   // a few kilobytes on the wire
            return true;
        };
        using var http = p.Client(AcmeToken);

        var response = await http.GetAsync(Flat + "acme.lib/index.json");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task A_json_response_under_the_cap_is_unaffected()
    {
        await using var p = await StartWithOptionsAsync(new Packmoat.Host.PackmoatOptions { MaxJsonBytes = 256 * 1024 });
        p.Publish("Acme.Lib", "1.0.0", 100);
        using var http = p.Client(AcmeToken);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(Flat + "acme.lib/index.json")).StatusCode);
    }
}

public class VerdictFilterTests
{
    private static readonly Verdict Allow = new(Decision.Allow, VerdictReason.CooldownElapsed);
    private static readonly Verdict Hide = new(Decision.Hide, VerdictReason.Cooldown);

    // ---- (6) a version with no verdict at all must be hidden
    [Fact]
    public void A_version_without_a_verdict_is_hidden()
        => Assert.True(Proxy.VerdictFilter.IsHidden(new Dictionary<string, Verdict>(), "1.0.0"));

    [Fact]
    public void Allowed_versions_are_shown_and_hidden_or_undecided_ones_are_not()
    {
        var verdicts = new Dictionary<string, Verdict> { ["1.0.0"] = Allow, ["2.0.0"] = Hide };
        Assert.False(Proxy.VerdictFilter.IsHidden(verdicts, "1.0.0"));
        Assert.True(Proxy.VerdictFilter.IsHidden(verdicts, "2.0.0"));
        Assert.True(Proxy.VerdictFilter.IsHidden(verdicts, "3.0.0"));
    }

    [Fact]
    public void An_outage_verdict_makes_the_list_unavailable_rather_than_shorter()
    {
        var verdicts = new Dictionary<string, Verdict> { ["1.0.0"] = new(Decision.Hide, VerdictReason.DateSourceUnavailable) };
        Assert.Throws<Proxy.TemporarilyUnavailableException>(() => Proxy.VerdictFilter.IsHidden(verdicts, "1.0.0"));
    }
}
