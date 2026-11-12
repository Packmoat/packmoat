using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Host;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

[Collection("sqlite-pools")]
public class ReviewFixesM4HostTests
{
    private static string NewKey() => AesGcmSecretProtector.GenerateKeyBase64();

    private static Task<Microsoft.AspNetCore.Builder.WebApplication> Start(string dir, string key, string? hosts)
    {
        var app = PackmoatApp.Build(new[] { "--urls", "http://127.0.0.1:0" }, TestModules.Server, null, o =>
        {
            o.Database = Path.Combine(dir, "packmoat.db");
            o.MasterKeyBase64 = key;
            o.AllowedHosts = hosts;
        });
        return Task.FromResult(app);
    }

    private static string AddressOf(Microsoft.AspNetCore.Builder.WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("localhost", "127.0.0.1").TrimEnd('/');

    [Fact]
    public async Task The_host_refuses_to_start_with_a_master_key_that_does_not_match_the_database()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-hostkey-").FullName;
        try
        {
            var keyA = NewKey();
            await using (var first = await Start(dir, keyA, "feeds.example.test")) { }       // creates the database and records the marker
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            await using (await Start(dir, keyA, "feeds.example.test")) { }                   // same key: fine
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            var ex = Assert.Throws<InvalidOperationException>(() => PackmoatApp.Build(new[] { "--urls", "http://127.0.0.1:0" }, TestModules.Server, null, o =>
            {
                o.Database = Path.Combine(dir, "packmoat.db");
                o.MasterKeyBase64 = NewKey();
                o.AllowedHosts = "feeds.example.test";
            }));
            Assert.Contains("master key does not match", ex.Message);
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }

    [Fact]
    public void Production_refuses_to_start_without_a_list_of_hostnames()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-hosts-").FullName;
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Start(dir, NewKey(), null).GetAwaiter().GetResult());
            Assert.Contains("PACKMOAT_HOSTS", ex.Message);
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }

    [Fact]
    public async Task Only_the_listed_hostnames_and_loopback_are_answered()
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-hostfilter-").FullName;
        try
        {
            await using var app = await Start(dir, NewKey(), "feeds.example.test acme.example.test");
            await app.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri(AddressOf(app)) };

            async Task<HttpStatusCode> Get(string path, string host, string? forwardedHost = null)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Host = host;
                if (forwardedHost is not null) request.Headers.Add("X-Forwarded-Host", forwardedHost);
                return (await http.SendAsync(request)).StatusCode;
            }

            Assert.Equal(HttpStatusCode.Unauthorized, await Get("/v3/index.json", "feeds.example.test"));      // listed: reaches authentication
            Assert.Equal(HttpStatusCode.Unauthorized, await Get("/v3/index.json", "acme.example.test"));
            Assert.Equal(HttpStatusCode.BadRequest, await Get("/v3/index.json", "evil.example"));              // not listed: refused before anything else
            Assert.Equal(HttpStatusCode.BadRequest, await Get("/v3/index.json", "evil.example", forwardedHost: "feeds.example.test"));
            Assert.Equal(HttpStatusCode.OK, await Get("/healthz", AddressOf(app)["http://".Length..]));         // the container health check uses the loopback address
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }
}
