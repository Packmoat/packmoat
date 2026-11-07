using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// Runs only when the live settings are present in the environment. Nothing identifying a real organisation lives in the repository:
///   PACKMOAT_LIVE_SECRETS_DIR  folder holding app-id.txt and sp.pem (outside the repository)
///   PACKMOAT_LIVE_ORG, PACKMOAT_LIVE_PROJECT, PACKMOAT_LIVE_FEED   the Azure DevOps organisation, project and public-only feed
///   PACKMOAT_LIVE_AZURE_TENANT the Entra tenant id of the service principal
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public static readonly string[] Required = { "PACKMOAT_LIVE_SECRETS_DIR", "PACKMOAT_LIVE_ORG", "PACKMOAT_LIVE_PROJECT", "PACKMOAT_LIVE_FEED", "PACKMOAT_LIVE_AZURE_TENANT" };

    public LiveFactAttribute()
    {
        var missing = Required.Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))).ToArray();
        var dir = Environment.GetEnvironmentVariable("PACKMOAT_LIVE_SECRETS_DIR");
        if (missing.Length > 0) Skip = "Live test skipped. Set: " + string.Join(", ", missing);
        else if (!File.Exists(Path.Combine(dir!, "sp.pem"))) Skip = "Live test skipped: sp.pem not found in PACKMOAT_LIVE_SECRETS_DIR.";
    }
}

[Collection("sqlite-pools")]
public class LiveSpikeFeedTests
{
    private const string Token = "pmt_live_test_token_0123456789abcdef";

    private static async Task<(Microsoft.AspNetCore.Builder.WebApplication App, string Url)> StartAsync(string dir)
    {
        static string Env(string name) => Environment.GetEnvironmentVariable(name)!;
        var org = Env("PACKMOAT_LIVE_ORG");
        var tenant = new Tenant("live", "Live spike", "https://pkgs.dev.azure.com/", org + "/",
            $"https://pkgs.dev.azure.com/{org}/{Env("PACKMOAT_LIVE_PROJECT")}/_packaging/{Env("PACKMOAT_LIVE_FEED")}/nuget/v3/index.json", new TenantPolicy(14));
        var tenants = new FakeTenants();
        tenants.Add(Token, tenant);
        var identity = new Packmoat.Application.UpstreamIdentity(Env("PACKMOAT_LIVE_AZURE_TENANT"), File.ReadAllText(Path.Combine(dir, "app-id.txt")).Trim(), File.ReadAllText(Path.Combine(dir, "sp.pem")));
        var store = new InMemoryIdentityStore(identity);
        var app = PackmoatApp.Build(new[] { "--urls", "http://127.0.0.1:0" }, TestModules.Server, s =>
        {
            s.AddSingleton<ITenantAuthenticator>(tenants);
            s.AddSingleton<IAuditLog>(new RecordingAuditLog());
            s.AddSingleton<IUpstreamCredentialSource>(new EntraCertificateCredentialSource(store, new SystemClock()));
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("localhost", "127.0.0.1");
        return (app, url.TrimEnd('/') + "/v3/index.json");
    }

    [LiveFact]
    public async Task Real_feed_real_dates_floating_restore_picks_the_newest_version_older_than_the_cooldown()
    {
        var (app, url) = await StartAsync(Environment.GetEnvironmentVariable("PACKMOAT_LIVE_SECRETS_DIR")!);
        await using var _ = app;

        // Expected answer, computed independently from nuget.org's own dates.
        using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });
        var dates = await new NuGetOrgPublishDateSource(http, new SystemClock()).GetPublishDatesAsync(Ecosystem.NuGet, "Moq", default);
        Assert.NotNull(dates);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-14);
        var expected = dates!.Where(d => !d.Key.Contains('-') && d.Key.StartsWith("4.") && d.Value <= cutoff)
            .Select(d => Version.Parse(d.Key)).Max()!.ToString(3);

        var r = await Restore.RunAsync(url, Token, "Moq", "4.*");

        Assert.True(r.Succeeded, r.Output);
        Assert.Equal(expected, r.ResolvedVersion);
    }

    [LiveFact]
    public async Task Real_feed_the_youngest_version_inside_the_cooldown_is_refused_when_one_exists()
    {
        var (app, url) = await StartAsync(Environment.GetEnvironmentVariable("PACKMOAT_LIVE_SECRETS_DIR")!);
        await using var _ = app;
        using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });
        var dates = await new NuGetOrgPublishDateSource(http, new SystemClock()).GetPublishDatesAsync(Ecosystem.NuGet, "Moq", default);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-14);
        var young = dates!.Where(d => !d.Key.Contains('-') && d.Value > cutoff).OrderByDescending(d => d.Value).Select(d => d.Key).FirstOrDefault();
        if (young is null) return; // nothing young today: nothing to refuse

        var r = await Restore.RunAsync(url, Token, "Moq", young);

        Assert.False(r.Succeeded);
        Assert.Contains("NU1102", r.Output);
    }
}

/// <summary>A tenant store that only knows one upstream identity. Everything else is unused by the credential source.</summary>
internal sealed class InMemoryIdentityStore : ITenantStore
{
    private readonly Packmoat.Application.UpstreamIdentity _identity;
    public InMemoryIdentityStore(Packmoat.Application.UpstreamIdentity identity) => _identity = identity;
    public Task<Packmoat.Application.UpstreamIdentity?> GetUpstreamIdentityAsync(string tenantId, CancellationToken ct) => Task.FromResult<Packmoat.Application.UpstreamIdentity?>(_identity);
    public Task<Tenant?> GetAsync(string tenantId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task CreateAsync(NewTenant tenant, CancellationToken ct) => throw new NotSupportedException();
    public Task SetActiveAsync(string tenantId, bool active, CancellationToken ct) => throw new NotSupportedException();
    public Task SetCooldownAsync(string tenantId, int days, CancellationToken ct) => throw new NotSupportedException();
    public Task AddInternalPrefixAsync(string tenantId, string prefix, CancellationToken ct) => throw new NotSupportedException();
    public Task RemoveInternalPrefixAsync(string tenantId, string prefix, CancellationToken ct) => throw new NotSupportedException();
    public Task SetOverrideAsync(string tenantId, string packageId, int days, CancellationToken ct) => throw new NotSupportedException();
    public Task RemoveOverrideAsync(string tenantId, string packageId, CancellationToken ct) => throw new NotSupportedException();
    public Task<Waiver> AddWaiverAsync(string tenantId, string packageId, string version, DateTimeOffset expiresAt, string reason, string createdBy, CancellationToken ct) => throw new NotSupportedException();
    public Task<CustomerWaiverInsert> AddCustomerWaiverAsync(string tenantId, string packageId, string version, DateTimeOffset expiresAt, string reason, string createdBy, int maxActive, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> RevokeWaiverAsync(string? tenantId, string waiverId, string revokedBy, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<WaiverRecord>> ListWaiversAsync(string tenantId, int limit, CancellationToken ct) => throw new NotSupportedException();
    public Task SetUpstreamIdentityAsync(string tenantId, Packmoat.Application.UpstreamIdentity identity, CancellationToken ct) => throw new NotSupportedException();
    public Task BindSharedIdentityAsync(string tenantId, string azureTenantId, CancellationToken ct) => throw new NotSupportedException();
    public Task<Packmoat.Application.UpstreamBinding?> GetUpstreamBindingAsync(string tenantId, CancellationToken ct) =>
        Task.FromResult<Packmoat.Application.UpstreamBinding?>(new(Packmoat.Application.IdentityKind.Own, _identity.AzureTenantId, _identity.ClientId));
    public Task<TenantSetup?> GetSetupAsync(string tenantId, CancellationToken ct) => throw new NotSupportedException();
}
