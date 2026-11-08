using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Host.Modules;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// #233, probed through the whole production server (a database, PACKMOAT_HOSTS): with PACKMOAT_TRUSTED_PROXIES set, a trusted proxy that
/// passes a client's X-Forwarded-Host through cannot make the server answer as a host name it does not serve. A probe module's site sees
/// the Host every later branch sees.
/// </summary>
[Collection("sqlite-pools")]
public class TrustedProxiesHostTests
{
    private const string ProbePath = "/__seen-host";

    /// <summary>Writes back the Host it sees, for one path; everything else passes through.</summary>
    private sealed class HostProbeModule : IPackmoatModule
    {
        public void ConfigureServices(PackmoatModuleContext context) { }

        public ModuleSite? ConfigureSite(PackmoatModuleContext context, IReadOnlyCollection<string> takenHosts) =>
            new("probe.example.test", app => app.Use(async (http, next) =>
            {
                if (http.Request.Path == ProbePath) await http.Response.WriteAsync(http.Request.Host.Value ?? "");
                else await next();
            }));

        public MigrationTrack? Migrations => null;
    }

    private static async Task<string> SeenHost(string? trustedProxies, string forwardedHost)
    {
        var dir = Directory.CreateTempSubdirectory("packmoat-forwarded-host-").FullName;
        try
        {
            await using var app = PackmoatApp.Build(["--urls", "http://127.0.0.1:0"], [new HostProbeModule()], null, o =>
            {
                o.DatabasePath = Path.Combine(dir, "packmoat.db");
                o.MasterKeyBase64 = AesGcmSecretProtector.GenerateKeyBase64();
                o.AllowedHosts = "feeds.example.test acme.example.test";
                o.TrustedProxies = trustedProxies;
            });
            await app.StartAsync();
            var baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("localhost", "127.0.0.1");
            using var http = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + ProbePath);
            request.Headers.Host = "feeds.example.test";   // allowed by the host filter
            request.Headers.Add("X-Forwarded-Host", forwardedHost);
            using var response = await http.SendAsync(request);
            var seen = await response.Content.ReadAsStringAsync();
            await app.StopAsync();
            return seen;
        }
        catch when (TempDir.Failing(dir)) { throw; }
        finally { TempDir.Delete(dir, clearSqlitePools: true); }
    }

    [Fact]   // mutation: PackmoatApp passes no host names to TrustedProxies.Options
    public async Task With_the_setting_a_forwarded_host_the_server_does_not_serve_is_not_applied()
    {
        Assert.Equal("feeds.example.test", await SeenHost("10.0.0.0/8", "evil.example"));
        Assert.Equal("acme.example.test", await SeenHost("10.0.0.0/8", "acme.example.test"));
        Assert.Equal("probe.example.test", await SeenHost("10.0.0.0/8", "probe.example.test"));   // a module's site host is one of them
    }

    [Fact]   // unset: as before #233, loopback's X-Forwarded-Host is applied
    public async Task Unset_the_forwarded_host_from_loopback_is_applied_as_before() =>
        Assert.Equal("evil.example", await SeenHost(null, "evil.example"));
}
