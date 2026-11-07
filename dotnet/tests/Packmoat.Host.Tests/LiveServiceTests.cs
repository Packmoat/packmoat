using System.Net;
using Packmoat.Domain;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// Runs only when pointed at a deployed service. It restores real packages through the live proxy with the `e2e-test` tenant's token,
/// so it proves what the in-process tests cannot: the server's certificate login to Azure, the cooldown on live data, and delivery.
///   PACKMOAT_E2E_URL          the service index, e.g. https://feeds.contoso.example/v3/index.json
///   PACKMOAT_E2E_TOKEN_FILE   a file (outside the repository, mode 600) holding the e2e-test tenant's token
///   PACKMOAT_E2E_COOLDOWN     optional, the tenant's cooldown in days (default 14)
/// See docs/runbooks/integration-test.md.
/// </summary>
public sealed class LiveServiceFactAttribute : FactAttribute
{
    public LiveServiceFactAttribute()
    {
        var url = Environment.GetEnvironmentVariable("PACKMOAT_E2E_URL");
        var file = Environment.GetEnvironmentVariable("PACKMOAT_E2E_TOKEN_FILE");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(file)) Skip = "Live service test skipped. Set: PACKMOAT_E2E_URL, PACKMOAT_E2E_TOKEN_FILE";
        else if (!File.Exists(file)) Skip = "Live service test skipped: PACKMOAT_E2E_TOKEN_FILE not found.";
    }
}

public class LiveServiceTests
{
    // Packages that publish stable versions several times a week, so one of them almost always has a version inside the cooldown.
    // A quiet package (Moq went 17 days without a release) would let the cooldown tests pass without hiding anything.
    private static readonly string[] Candidates = { "AWSSDK.Core", "AWSSDK.S3" };

    private static string Url => Environment.GetEnvironmentVariable("PACKMOAT_E2E_URL")!;
    private static string Token => File.ReadAllText(Environment.GetEnvironmentVariable("PACKMOAT_E2E_TOKEN_FILE")!).Trim();
    private static int CooldownDays => int.TryParse(Environment.GetEnvironmentVariable("PACKMOAT_E2E_COOLDOWN"), out var d) ? d : 14;

    private sealed record Pick(string Package, string Major, string Allowed, string Young);

    /// <summary>
    /// From nuget.org's own publish dates (read directly, not through the proxy, so the expected answer is independent of it): the first
    /// candidate whose newest stable version is inside the cooldown, with the newest stable version of that major that is old enough.
    /// Fails rather than skips when none has a young version: then the cooldown cannot be shown working today.
    /// </summary>
    private static async Task<Pick> PickAsync()
    {
        using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });
        var source = new NuGetOrgPublishDateSource(http, new SystemClock());
        var cutoff = DateTimeOffset.UtcNow.AddDays(-CooldownDays);
        foreach (var package in Candidates)
        {
            var dates = await source.GetPublishDatesAsync(Ecosystem.NuGet, package, default);
            Assert.NotNull(dates);
            var stable = dates!.Where(d => !d.Key.Contains('-')).Select(d => (Version: Version.Parse(d.Key), Published: d.Value)).ToList();
            var newest = stable.MaxBy(v => v.Version);
            if (newest.Published <= cutoff) continue;
            var allowed = stable.Where(v => v.Version.Major == newest.Version.Major && v.Published <= cutoff).Select(v => v.Version).Max();
            if (allowed is null) continue;
            return new Pick(package, newest.Version.Major + ".*", allowed.ToString(), newest.Version.ToString());
        }
        Assert.Fail($"None of {string.Join(", ", Candidates)} has a stable version younger than {CooldownDays} days, so the cooldown cannot be shown working today. Add a busier candidate.");
        return null!;
    }

    [LiveServiceFact]
    public async Task Without_a_token_the_service_refuses()
    {
        using var http = new HttpClient();
        var r = await http.GetAsync(Url);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [LiveServiceFact]
    public async Task A_floating_restore_skips_the_young_version_and_gets_the_newest_old_enough()
    {
        var pick = await PickAsync();

        var r = await Restore.RunAsync(Url, Token, pick.Package, pick.Major);

        Assert.True(r.Succeeded, r.Output);
        Assert.Equal(pick.Allowed, r.ResolvedVersion);
        Assert.NotEqual(pick.Young, r.ResolvedVersion);
    }

    [LiveServiceFact]
    public async Task An_exact_pin_to_a_version_inside_the_cooldown_is_refused()
    {
        var pick = await PickAsync();

        var r = await Restore.RunAsync(Url, Token, pick.Package, pick.Young);

        Assert.False(r.Succeeded, $"{pick.Package} {pick.Young} restored although it is inside the cooldown");
        Assert.Contains("NU1102", r.Output);
    }

    [LiveServiceFact]
    public async Task An_exact_pin_to_a_version_older_than_the_cooldown_restores()
    {
        var pick = await PickAsync();

        var r = await Restore.RunAsync(Url, Token, pick.Package, pick.Allowed);

        Assert.True(r.Succeeded, r.Output);
        Assert.Equal(pick.Allowed, r.ResolvedVersion);
    }
}
