using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => UtcNow += by;
}

public sealed class FakeDateSource : IPublishDateSource
{
    private readonly Dictionary<string, Dictionary<string, DateTimeOffset>> _dates = new();
    public bool Unavailable { get; set; }
    public int Calls;

    public void Set(string id, string version, DateTimeOffset published)
    {
        if (!_dates.TryGetValue(id.ToLowerInvariant(), out var d)) _dates[id.ToLowerInvariant()] = d = new();
        d[NuGetVersionKey.Normalize(version)] = published;
    }

    public Task<IReadOnlyDictionary<string, DateTimeOffset>?> GetPublishDatesAsync(Ecosystem ecosystem, string packageId, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (Unavailable) return Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>?>(null);
        IReadOnlyDictionary<string, DateTimeOffset> result = _dates.TryGetValue(packageId.ToLowerInvariant(), out var d) ? d : new Dictionary<string, DateTimeOffset>();
        return Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>?>(result);
    }
}

public sealed class FakeTenants : ITenantAuthenticator
{
    private readonly Dictionary<string, Tenant> _byTokenHash = new();
    /// <summary>How many tokens were looked up (each one is a database call in production).</summary>
    public int Calls;
    public void Add(string token, Tenant tenant) => _byTokenHash[TokenHasher.Hash(token)] = tenant;

    public ValueTask<AuthResult?> AuthenticateAsync(string token, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return ValueTask.FromResult(_byTokenHash.TryGetValue(TokenHasher.Hash(token), out var t) ? new AuthResult(t, "tok_test_" + t.Id) : null);
    }
}

public sealed class RecordingAuditLog : IAuditLog
{
    public System.Collections.Concurrent.ConcurrentQueue<AuditEvent> Events { get; } = new();
    public ValueTask AppendAsync(AuditEvent e, CancellationToken ct) { Events.Enqueue(e); return ValueTask.CompletedTask; }
}

/// <summary>The real Packmoat host on a real socket, wired to fakes for time, dates and tenants.</summary>
public sealed partial class ProxyFixture : IAsyncDisposable
{
    public const string AcmeToken = "pmt_test_token_acme_0123456789abcdef";
    public const string GlobexToken = "pmt_test_token_globex_0123456789abcdef";

    public FakeUpstream Upstream { get; private set; } = null!;
    public FakeDateSource Dates { get; } = new();
    public FakeClock Clock { get; } = new();
    public FakeTenants Tenants { get; } = new();
    public RecordingAuditLog Audit { get; } = new();
    public string BaseUrl { get; private set; } = "";
    public Tenant Acme { get; private set; } = null!;
    private WebApplication _app = null!;

    public static async Task<ProxyFixture> StartAsync(Func<string, TenantPolicy>? acmePolicy = null, int? tokenRequestsPerMinute = null, Action<IServiceCollection>? configure = null)
    {
        var f = new ProxyFixture { Upstream = await FakeUpstream.StartAsync() };
        TenantPolicy Policy(string name) => acmePolicy?.Invoke(name) ?? new TenantPolicy(14, new[] { "Contoso." });
        f.Acme = new Tenant("acme", "Acme", f.Upstream.BaseUrl, "org/", f.Upstream.FeedIndexUrl, Policy("acme"));
        f.Tenants.Add(AcmeToken, f.Acme);
        f.Tenants.Add(GlobexToken, new Tenant("globex", "Globex", f.Upstream.BaseUrl, "org/", f.Upstream.FeedIndexUrl, new TenantPolicy(0)));

        f._app = PackmoatApp.Build(new[] { "--urls", "http://127.0.0.1:0" }, TestModules.Server, services =>
        {
            services.AddSingleton<IClock>(f.Clock);
            services.AddSingleton<IPublishDateSource>(f.Dates);
            services.AddSingleton<ITenantAuthenticator>(f.Tenants);
            services.AddSingleton<IAuditLog>(f.Audit);
            if (tokenRequestsPerMinute is { } rpm) services.AddSingleton(new Packmoat.Host.PackmoatOptions { TokenRequestsPerMinute = rpm });
            services.AddSingleton<IUpstreamCredentialSource>(new StaticBasicCredentialSource(FakeUpstream.User, FakeUpstream.Secret));
            configure?.Invoke(services);
        });
        await f._app.StartAsync();
        f.BaseUrl = f._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("localhost", "127.0.0.1").TrimEnd('/');
        return f;
    }

    public DateTimeOffset Ago(double days) => Clock.UtcNow - TimeSpan.FromDays(days);

    /// <summary>Adds a package version to the upstream feed with its publish date.</summary>
    public void Publish(string id, string version, double ageDays, int extraBytes = 0)
    {
        Upstream.AddPackage(id, version, extraBytes);
        Dates.Set(id, version, Ago(ageDays));
    }

    public string SourceUrl => BaseUrl + "/v3/index.json";

    public HttpClient Client(string? token)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(BaseUrl) };
        if (token is not null)
            http.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x:" + token)));
        return http;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await Upstream.DisposeAsync();
    }
}

public sealed record RestoreResult(int ExitCode, string Output, string? ResolvedVersion, TimeSpan Elapsed)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>Every package in the restored graph: lower-case id to version.</summary>
    public IReadOnlyDictionary<string, string> Libraries { get; init; } = new Dictionary<string, string>();
}

/// <summary>Runs a REAL `dotnet restore` against the proxy, in an isolated folder with an empty package cache.</summary>
public static class Restore
{
    public static Task<RestoreResult> RunAsync(string sourceUrl, string? token, string packageId, string version) =>
        RunAsync(sourceUrl, token, new[] { (packageId, version) });

    /// <summary>One project referencing every package given; <see cref="RestoreResult.ResolvedVersion"/> is the first one's.</summary>
    public static async Task<RestoreResult> RunAsync(string sourceUrl, string? token, IReadOnlyList<(string Id, string Version)> packages)
    {
        var packageId = packages[0].Id;
        var references = string.Concat(packages.Select(p => $"""<PackageReference Include="{p.Id}" Version="{p.Version}" />"""));
        var dir = Directory.CreateTempSubdirectory("packmoat-restore-").FullName;
        try
        {
            var creds = token is null ? "" :
                $"""<packageSourceCredentials><feed><add key="Username" value="dev" /><add key="ClearTextPassword" value="{token}" /></feed></packageSourceCredentials>""";
            await File.WriteAllTextAsync(Path.Combine(dir, "nuget.config"),
                $"""<configuration><packageSources><clear /><add key="feed" value="{sourceUrl}" allowInsecureConnections="true" /></packageSources>{creds}</configuration>""");
            await File.WriteAllTextAsync(Path.Combine(dir, "p.csproj"),
                $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup>{references}</ItemGroup></Project>""");

            var psi = new ProcessStartInfo("dotnet", "restore p.csproj --configfile nuget.config --no-cache -v q")
            {
                WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            psi.Environment["NUGET_PACKAGES"] = Path.Combine(dir, "pkgs");
            // NuGet also caches the service index per source URL across runs (--no-cache does not cover it). Two tenants share the URL
            // https://<proxy>/v3/index.json, so one run could reuse the other tenant's index. Keep every restore's HTTP cache private.
            psi.Environment["NUGET_HTTP_CACHE_PATH"] = Path.Combine(dir, "http-cache");
            psi.Environment["DOTNET_NOLOGO"] = "1";
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            psi.Environment.Remove("NUGET_PLUGIN_PATHS");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var process = Process.Start(psi)!;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await process.WaitForExitAsync(cts.Token);
            var output = await stdout + await stderr;

            string? resolved = null;
            var libraries = new Dictionary<string, string>();
            var assets = Path.Combine(dir, "obj", "project.assets.json");
            if (File.Exists(assets))
            {
                var libs = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(assets))!["libraries"]!.AsObject();
                resolved = libs.Select(l => l.Key).FirstOrDefault(k => k.StartsWith(packageId + "/", StringComparison.OrdinalIgnoreCase))?.Split('/')[1];
                foreach (var parts in libs.Select(l => l.Key.Split('/'))) libraries[parts[0].ToLowerInvariant()] = parts[1];
            }
            return new RestoreResult(process.ExitCode, output, resolved, sw.Elapsed) { Libraries = libraries };
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }
}
