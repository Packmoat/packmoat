using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using static Packmoat.Host.Tests.ProxyFixture;

namespace Packmoat.Host.Tests;

/// <summary>Direct HTTP checks of what the proxy returns and, just as important, what it sends upstream.</summary>
public class ProxyHttpTests
{
    private static string[] Versions(string json) =>
        JsonNode.Parse(json)!["versions"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();

    [Fact]
    public async Task The_service_index_keeps_only_restore_resources_and_points_back_at_the_proxy()
    {
        await using var p = await StartAsync();
        using var http = p.Client(AcmeToken);

        var body = await http.GetStringAsync("/v3/index.json");

        var types = JsonNode.Parse(body)!["resources"]!.AsArray().Select(r => r!["@type"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "PackageBaseAddress/3.0.0", "RegistrationsBaseUrl/3.6.0" }, types);
        Assert.DoesNotContain(p.Upstream.BaseUrl, body);
        Assert.Contains(p.BaseUrl + "/v3/u/", body);
    }

    [Fact]
    public async Task The_version_list_hides_young_versions_for_one_tenant_and_not_another()
    {
        await using var p = await StartAsync();
        p.Publish("Acme.Lib", "1.0.0", 100);
        p.Publish("Acme.Lib", "2.0.0", 3);
        var flat = "/v3/u/org/proj/_packaging/feed/nuget/v3/flat2/acme.lib/index.json";

        using var acme = p.Client(AcmeToken);
        using var globex = p.Client(GlobexToken);

        Assert.Equal(new[] { "1.0.0" }, Versions(await acme.GetStringAsync(flat)));
        Assert.Equal(new[] { "1.0.0", "2.0.0" }, Versions(await globex.GetStringAsync(flat)));
    }

    [Fact]
    public async Task A_hidden_download_is_a_404_with_a_reason_and_never_asks_upstream_for_the_file()
    {
        await using var p = await StartAsync();
        p.Publish("Acme.Lib", "2.0.0", 3);
        using var http = p.Client(AcmeToken);

        var response = await http.GetAsync("/v3/u/org/proj/_packaging/feed/nuget/v3/flat2/acme.lib/2.0.0/acme.lib.2.0.0.nupkg");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Cooldown", response.Headers.GetValues("X-Packmoat-Reason").Single());
        Assert.DoesNotContain(p.Upstream.Requests, r => r.Path.Contains(".nupkg"));
    }

    [Fact]
    public async Task Inline_registrations_are_filtered()
    {
        await using var p = await StartAsync();
        p.Publish("Acme.Lib", "1.0.0", 100);
        p.Publish("Acme.Lib", "2.0.0", 3);
        using var http = p.Client(AcmeToken);

        var json = await http.GetStringAsync("/v3/u/org/proj/_packaging/feed/nuget/v3/registrations2/acme.lib/index.json");

        var versions = JsonNode.Parse(json)!["items"]!.AsArray().SelectMany(pg => pg!["items"]!.AsArray()).Select(i => i!["catalogEntry"]!["version"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "1.0.0" }, versions);
    }

    [Fact]
    public async Task Paged_registrations_are_filtered_when_the_client_follows_the_page_links_through_the_proxy()
    {
        await using var p = await StartAsync();
        p.Upstream.PagedRegistrations = true;
        foreach (var (v, age) in new[] { ("1.0.0", 200), ("1.1.0", 100), ("2.0.0", 3), ("2.1.0", 1) }) p.Publish("Acme.Lib", v, age);
        using var http = p.Client(AcmeToken);

        var index = JsonNode.Parse(await http.GetStringAsync("/v3/u/org/proj/_packaging/feed/nuget/v3/registrations2/acme.lib/index.json"))!;
        var pageUrls = index["items"]!.AsArray().Select(pg => pg!["@id"]!.GetValue<string>()).ToArray();
        Assert.All(pageUrls, u => Assert.StartsWith(p.BaseUrl + "/v3/u/", u)); // the client is kept on the proxy

        var seen = new List<string>();
        foreach (var url in pageUrls)
            seen.AddRange(JsonNode.Parse(await http.GetStringAsync(url))!["items"]!.AsArray().Select(i => i!["catalogEntry"]!["version"]!.GetValue<string>()));
        Assert.Equal(new[] { "1.0.0", "1.1.0" }, seen);
    }

    [Fact]
    public async Task A_hidden_registration_leaf_is_a_404()
    {
        await using var p = await StartAsync();
        p.Publish("Acme.Lib", "2.0.0", 3);
        using var http = p.Client(AcmeToken);

        var response = await http.GetAsync("/v3/u/org/proj/_packaging/feed/nuget/v3/registrations2/acme.lib/2.0.0.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_upstream_secret_never_reaches_the_client_and_the_clients_token_never_reaches_upstream()
    {
        await using var p = await StartAsync();
        p.Publish("Acme.Lib", "1.0.0", 100);
        using var http = p.Client(AcmeToken);

        var seen = new List<string>();
        foreach (var path in new[]
        {
            "/v3/index.json",
            "/v3/u/org/proj/_packaging/feed/nuget/v3/flat2/acme.lib/index.json",
            "/v3/u/org/proj/_packaging/feed/nuget/v3/registrations2/acme.lib/index.json",
            "/v3/u/org/proj/_packaging/feed/nuget/v3/flat2/acme.lib/1.0.0/acme.lib.1.0.0.nupkg",
        })
        {
            var response = await http.GetAsync(path);
            seen.Add(string.Join("\n", response.Headers.Select(h => $"{h.Key}: {string.Join(",", h.Value)}")));
            seen.Add(Convert.ToHexString(await response.Content.ReadAsByteArrayAsync()));
            seen.Add(await response.Content.ReadAsStringAsync());
        }
        var clientVisible = string.Concat(seen);
        Assert.DoesNotContain(FakeUpstream.Secret, clientVisible);
        Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{FakeUpstream.User}:{FakeUpstream.Secret}")), clientVisible);

        Assert.NotEmpty(p.Upstream.Requests);
        foreach (var r in p.Upstream.Requests)
            Assert.DoesNotContain(r.Headers, h => h.Value.Contains(AcmeToken) || h.Value.Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x:" + AcmeToken))));
    }

    [Fact]
    public async Task The_redirect_hop_to_blob_storage_carries_no_credential_but_the_feed_hop_does()
    {
        await using var p = await StartAsync();
        p.Publish("Acme.Lib", "1.0.0", 100);
        using var http = p.Client(AcmeToken);

        var response = await http.GetAsync("/v3/u/org/proj/_packaging/feed/nuget/v3/flat2/acme.lib/1.0.0/acme.lib.1.0.0.nupkg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(p.Upstream.NupkgBytes("Acme.Lib", "1.0.0"), await response.Content.ReadAsByteArrayAsync());
        var feedHop = p.Upstream.Requests.Single(r => r.Path.EndsWith(".nupkg"));
        var blobHop = p.Upstream.Requests.Single(r => r.Path.StartsWith("blob/"));
        Assert.Equal(FakeUpstream.ExpectedAuth, feedHop.Authorization);
        Assert.Null(blobHop.Authorization);
    }

    [Fact]
    public async Task A_large_download_arrives_byte_for_byte_with_its_length()
    {
        await using var p = await StartAsync();
        p.Publish("Acme.Big", "1.0.0", 100, extraBytes: 30 * 1024 * 1024);
        using var http = p.Client(AcmeToken);

        using var response = await http.GetAsync("/v3/u/org/proj/_packaging/feed/nuget/v3/flat2/acme.big/1.0.0/acme.big.1.0.0.nupkg", HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(p.Upstream.NupkgBytes("Acme.Big", "1.0.0").Length, response.Content.Headers.ContentLength);
        Assert.Equal(SHA256.HashData(p.Upstream.NupkgBytes("Acme.Big", "1.0.0")), SHA256.HashData(await response.Content.ReadAsByteArrayAsync()));
    }

    [Theory]
    [InlineData("/v3/u/other-org/proj/_packaging/feed/nuget/v3/flat2/x/index.json")]
    [InlineData("/v3/u/org/../other/flat2/x/index.json")]
    [InlineData("/v3/u/org/proj/%2e%2e/x")]
    [InlineData("/v3/u/org%2fproj/x")]
    [InlineData("/v3/u/org/proj/_packaging/feed/nuget/v3/query2/?q=moq")]
    [InlineData("/v3/u/org/proj/_packaging/feed/nuget/v2/package")]
    [InlineData("/v3/u/org/anything-else")]
    public async Task Paths_outside_the_tenants_feed_or_not_recognised_are_refused_without_calling_upstream(string path)
    {
        await using var p = await StartAsync();
        using var http = p.Client(AcmeToken);

        var response = await http.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, p.Upstream.FeedRequestCount);
    }

    [Fact]
    public async Task Unauthenticated_requests_get_401_with_a_challenge_and_cost_no_upstream_call()
    {
        await using var p = await StartAsync();
        using var http = p.Client(null);

        var response = await http.GetAsync("/v3/index.json");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Basic", response.Headers.WwwAuthenticate.ToString());
        Assert.Equal(0, p.Upstream.FeedRequestCount);
        Assert.Equal(0, p.Dates.Calls);
    }

    [Fact]
    public async Task A_tenant_cannot_see_the_other_tenants_policy_effects()
    {
        await using var p = await StartAsync();
        p.Publish("Contoso.Core", "1.0.0", 500);
        using var acme = p.Client(AcmeToken);   // Contoso. is an internal prefix for Acme only
        using var globex = p.Client(GlobexToken);
        var flat = "/v3/u/org/proj/_packaging/feed/nuget/v3/flat2/contoso.core/index.json";

        Assert.Empty(Versions(await acme.GetStringAsync(flat)));
        Assert.Equal(new[] { "1.0.0" }, Versions(await globex.GetStringAsync(flat)));
    }
}
