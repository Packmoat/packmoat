using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Packmoat.Host.Tests;

/// <summary>
/// A tiny NuGet v3 feed on a real socket, shaped like Azure Artifacts: it needs Basic auth on feed paths, answers
/// nupkg downloads with a 303 to a "blob" path that must NOT receive credentials, and can page its registrations.
/// It records every request so tests can see exactly what the proxy sent upstream.
/// </summary>
public sealed class FakeUpstream : IAsyncDisposable
{
    public const string User = "up-user";
    public const string Secret = "UP-SECRET-VALUE-123";
    public static readonly string ExpectedAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Secret}"));

    public sealed record Request(string Path, string? Authorization, IReadOnlyDictionary<string, string> Headers);

    private readonly WebApplication _app;
    private readonly Dictionary<string, SortedDictionary<string, byte[]>> _packages = new();
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();

    public ConcurrentQueue<Request> Requests { get; } = new();
    public bool PagedRegistrations { get; set; }
    /// <summary>Lets a test take over a feed path (after authentication) to serve something unusual. Return true if handled.</summary>
    public Func<string, HttpContext, Task<bool>>? Intercept { get; set; }
    public string BaseUrl { get; private set; } = "";
    public string FeedPath => "org/proj/_packaging/feed/nuget/v3/";
    public string FeedIndexUrl => BaseUrl + FeedPath + "index.json";

    private FakeUpstream(WebApplication app) => _app = app;

    public static async Task<FakeUpstream> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeUpstream(app);
        app.Run(fake.HandleAsync);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        fake.BaseUrl = address.Replace("localhost", "127.0.0.1").TrimEnd('/') + "/";
        return fake;
    }

    public void AddPackage(string id, string version, int extraBytes = 0)
    {
        if (!_packages.TryGetValue(id.ToLowerInvariant(), out var versions)) _packages[id.ToLowerInvariant()] = versions = new();
        versions[version.ToLowerInvariant()] = BuildNupkg(id, version, extraBytes);
    }

    public byte[] NupkgBytes(string id, string version) => _packages[id.ToLowerInvariant()][version.ToLowerInvariant()];

    public int FeedRequestCount => Requests.Count(r => !r.Path.StartsWith("blob/"));

    private async Task HandleAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value!.TrimStart('/');
        var auth = ctx.Request.Headers.Authorization.ToString();
        Requests.Enqueue(new Request(path, auth.Length == 0 ? null : auth, ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString())));

        if (path.StartsWith("blob/"))
        {
            if (!_blobs.TryGetValue(path, out var blob)) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength = blob.Length;
            await ctx.Response.Body.WriteAsync(blob);
            return;
        }

        if (auth != ExpectedAuth) { ctx.Response.StatusCode = 401; ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"fake\""; return; }
        if (!path.StartsWith(FeedPath)) { ctx.Response.StatusCode = 404; return; }
        if (Intercept is not null && await Intercept(path, ctx)) return;

        var rest = path[FeedPath.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var feed = BaseUrl + FeedPath;

        if (rest is ["index.json"])
        {
            await Json(ctx, new JsonObject
            {
                ["version"] = "3.0.0",
                ["resources"] = new JsonArray
                {
                    Resource(feed + "flat2/", "PackageBaseAddress/3.0.0"),
                    Resource(feed + "registrations2/", "RegistrationsBaseUrl/3.6.0"),
                    Resource(feed + "query2/", "SearchQueryService"),
                    Resource(BaseUrl + "org/proj/_packaging/feed/nuget/v2/", "PackagePublish/2.0.0"),
                },
            });
            return;
        }

        if (rest is ["flat2", var id, "index.json"] && _packages.TryGetValue(id, out var vs))
        {
            await Json(ctx, new JsonObject { ["versions"] = new JsonArray(vs.Keys.Select(v => (JsonNode)v).ToArray()) });
            return;
        }

        if (rest is ["flat2", var pid, var ver, var file] && _packages.TryGetValue(pid, out var pv) && pv.TryGetValue(ver, out var bytes))
        {
            if (file.EndsWith(".nupkg"))
            {
                var blobPath = "blob/" + Guid.NewGuid().ToString("N");
                _blobs[blobPath] = bytes;
                ctx.Response.StatusCode = 303;
                ctx.Response.Headers.Location = "/" + blobPath; // relative, like a storage redirect on another path
                return;
            }
            ctx.Response.ContentType = "text/xml";
            await ctx.Response.WriteAsync(Nuspec(pid, ver));
            return;
        }

        if (rest.Length >= 3 && rest[0] == "registrations2" && _packages.TryGetValue(rest[1], out var rv))
        {
            var reg = feed + "registrations2/" + rest[1] + "/";
            var versions = rv.Keys.ToArray();
            if (rest is [_, _, "index.json"])
            {
                var pages = new JsonArray();
                var chunks = PagedRegistrations ? versions.Chunk(2).ToArray() : new[] { versions };
                foreach (var chunk in chunks)
                {
                    var page = new JsonObject
                    {
                        ["@id"] = $"{reg}page/{chunk[0]}/{chunk[^1]}.json",
                        ["count"] = chunk.Length, ["lower"] = chunk[0], ["upper"] = chunk[^1],
                    };
                    if (!PagedRegistrations) page["items"] = Leaves(rest[1], chunk, feed);
                    pages.Add(page);
                }
                await Json(ctx, new JsonObject { ["count"] = pages.Count, ["items"] = pages });
                return;
            }
            if (rest is [_, _, "page", var lower, var upperFile])
            {
                var upper = upperFile[..^".json".Length];
                var chunk = versions.Where(v => string.CompareOrdinal(v, lower) >= 0 && string.CompareOrdinal(v, upper) <= 0).ToArray();
                await Json(ctx, new JsonObject
                {
                    ["@id"] = $"{reg}page/{lower}/{upperFile}", ["count"] = chunk.Length, ["lower"] = lower, ["upper"] = upper,
                    ["items"] = Leaves(rest[1], chunk, feed),
                });
                return;
            }
            if (rest is [_, _, var leaf] && leaf.EndsWith(".json") && rv.ContainsKey(leaf[..^5]))
            {
                await Json(ctx, Leaf(rest[1], leaf[..^5], feed));
                return;
            }
        }

        ctx.Response.StatusCode = 404;
    }

    private static JsonObject Resource(string id, string type) => new() { ["@id"] = id, ["@type"] = type };

    private static JsonArray Leaves(string id, IEnumerable<string> versions, string feed) =>
        new(versions.Select(v => (JsonNode)Leaf(id, v, feed)).ToArray());

    private static JsonObject Leaf(string id, string version, string feed) => new()
    {
        ["@id"] = $"{feed}registrations2/{id}/{version}.json",
        ["catalogEntry"] = new JsonObject { ["@id"] = $"{feed}../../commitLog/{Guid.NewGuid():N}", ["id"] = id, ["version"] = version, ["listed"] = true, ["dependencyGroups"] = new JsonArray() },
        ["packageContent"] = $"{feed}flat2/{id}/{version}/{id}.{version}.nupkg",
    };

    private static async Task Json(HttpContext ctx, JsonNode body)
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(body.ToJsonString());
    }

    private static string Nuspec(string id, string version) =>
        $"""<?xml version="1.0"?><package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>{id}</id><version>{version}</version><authors>test</authors><description>test package</description></metadata></package>""";

    public static byte[] BuildNupkg(string id, string version, int extraBytes)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, $"{id}.nuspec", Encoding.UTF8.GetBytes(Nuspec(id, version)));
            Add(zip, "[Content_Types].xml", Encoding.UTF8.GetBytes("""<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="nuspec" ContentType="application/octet"/><Default Extension="bin" ContentType="application/octet"/></Types>"""));
            if (extraBytes > 0)
            {
                var data = new byte[extraBytes];
                new Random(42).NextBytes(data); // incompressible, so the zip really is that large
                Add(zip, "content/blob.bin", data, CompressionLevel.NoCompression);
            }
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string name, byte[] content, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(name, level);
        using var s = entry.Open();
        s.Write(content);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
