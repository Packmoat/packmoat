using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>A stand-in for Azure DevOps: the feed settings API, plus what package endpoints answer to different callers.</summary>
public sealed class FakeDevOps : IAsyncDisposable
{
    public const string AdminSecret = "ADMIN-SECRET-xyz";
    public const string DeveloperSecret = "DEV-SECRET-abc";

    private readonly WebApplication _app;
    public string Base { get; private set; } = "";
    public JsonArray Upstreams { get; set; } = new(U("https://api.nuget.org/v3/index.json"), U("https://registry.npmjs.org/"));
    public JsonArray Permissions { get; set; } = new();
    public JsonArray Views { get; set; } = new(V("Local", "private"), V("Release", "private"));
    public int DeveloperIndexStatus { get; set; } = 403;
    public int PrivateFeedStatus { get; set; } = 403;
    /// <summary>What Packmoat's own credential gets on the public-only feed index (the positive control).</summary>
    public int PackmoatIndexStatus { get; set; } = 200;
    /// <summary>False simulates an expired or mistyped developer credential.</summary>
    public bool DeveloperTokenValid { get; set; } = true;
    /// <summary>When set, the permissions list is served in two pages; this is the second.</summary>
    public JsonArray? PermissionsPage2 { get; set; }
    /// <summary>The first N permissions requests are answered 429 with Retry-After: 0.</summary>
    public int ThrottleFirstPermissionRequests { get; set; }
    private int _permissionRequests;
    /// <summary>The identities API (vssps), by legacy descriptor. Unknown descriptors are answered with an empty list.</summary>
    public Dictionary<string, JsonObject> Identities { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Anything but 200 makes the identities API refuse (as for a credential without Identity read).</summary>
    public int IdentitiesStatus { get; set; } = 200;
    public ConcurrentQueue<string> Seen { get; } = new();

    /// <summary>An identity in the shape the live identities API returned (a test organisation): properties are {"$type","$value"} pairs.</summary>
    public static JsonObject Identity(string descriptor, string displayName, string specialType, string scopeName, string domain, string account, bool isContainer = true)
    {
        static JsonObject P(string v) => new() { ["$type"] = "System.String", ["$value"] = v };
        return new JsonObject
        {
            ["descriptor"] = descriptor, ["providerDisplayName"] = displayName, ["isContainer"] = isContainer, ["isActive"] = true,
            ["properties"] = new JsonObject
            {
                ["SchemaClassName"] = P("Group"), ["SpecialType"] = P(specialType), ["ScopeName"] = P(scopeName), ["Domain"] = P(domain), ["Account"] = P(account),
            },
        };
    }

    private FakeDevOps(WebApplication app) => _app = app;

    public static JsonObject U(string location) => new() { ["name"] = "u", ["protocol"] = "nuget", ["location"] = location };
    public static JsonObject V(string name, string visibility) => new() { ["name"] = name, ["visibility"] = visibility };
    public static JsonObject Perm(string role, string name, string descriptor, bool inherited = false) =>
        new() { ["role"] = role, ["displayName"] = name, ["identityDescriptor"] = descriptor, ["isInheritedRole"] = inherited };

    public static async Task<FakeDevOps> StartAsync()
    {
        var b = WebApplication.CreateBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        var app = b.Build();
        var f = new FakeDevOps(app);
        app.Run(f.Handle);
        await app.StartAsync();
        f.Base = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("localhost", "127.0.0.1").TrimEnd('/');
        return f;
    }

    private async Task Handle(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value!;
        var auth = ctx.Request.Headers.Authorization.ToString();
        Seen.Enqueue(path);
        if (path.EndsWith("/_apis/packaging/feeds"))   // listing feeds: any valid credential may
        {
            var dev = auth.Contains(Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(":" + DeveloperSecret)));
            ctx.Response.StatusCode = dev && DeveloperTokenValid ? 200 : 401;
            return;
        }
        if (path.Contains("/_apis/packaging/feeds/"))
        {
            if (!(auth.Contains(Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(":" + AdminSecret))) || auth == "Bearer " + AdminSecret)) { ctx.Response.StatusCode = 401; return; }
            if (path.EndsWith("/permissions") && Interlocked.Increment(ref _permissionRequests) <= ThrottleFirstPermissionRequests)
            {
                ctx.Response.StatusCode = 429;
                ctx.Response.Headers["Retry-After"] = "0";
                return;
            }
            var second = ctx.Request.Query["continuationToken"] == "p2";
            if (path.EndsWith("/permissions") && PermissionsPage2 is not null && !second) ctx.Response.Headers["x-ms-continuationtoken"] = "p2";
            JsonNode body = path.EndsWith("/permissions") ? new JsonObject { ["value"] = (second ? PermissionsPage2! : Permissions).DeepClone() }
                : path.EndsWith("/views") ? new JsonObject { ["value"] = Views.DeepClone() }
                : new JsonObject { ["name"] = "pub", ["upstreamSources"] = Upstreams.DeepClone() };
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(body.ToJsonString());
            return;
        }
        if (path.EndsWith("/_apis/identities"))
        {
            if (!(auth.Contains(Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(":" + AdminSecret))) || auth == "Bearer " + AdminSecret)) { ctx.Response.StatusCode = 401; return; }
            if (IdentitiesStatus != 200) { ctx.Response.StatusCode = IdentitiesStatus; return; }
            var found = new JsonArray();
            if (Identities.TryGetValue(ctx.Request.Query["descriptors"].ToString(), out var identity)) found.Add(identity.DeepClone());
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(new JsonObject { ["count"] = found.Count, ["value"] = found }.ToJsonString());
            return;
        }
        if (path.Contains("/private/")) { ctx.Response.StatusCode = PrivateFeedStatus; return; }
        if (path.Contains("_packaging/pub/"))
        {
            var packmoat = auth == "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("packmoat:SP-SECRET-TOKEN"));
            ctx.Response.StatusCode = packmoat ? PackmoatIndexStatus : auth.Length > 0 ? DeveloperIndexStatus : 401;
            return;
        }
        ctx.Response.StatusCode = 404;
    }

    public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
}

public partial class OnboardingChecksTests
{
    private const string Packmoat = "AadServicePrincipal;tenant\\11111111-spid";

    /// <summary>The tenant whose stored feed is the one the options below name, so the "feed" check passes.</summary>
    internal static Tenant TenantFor(FakeDevOps d, string feedUrl = "/org/proj/_packaging/pub/nuget/v3/index.json") =>
        new("acme", "Acme", d.Base + "/", "org/", d.Base + feedUrl, new TenantPolicy(14));

    private static OnboardingChecks Build(FakeDevOps d, OnboardingOptions o, HttpClient? http = null, Tenant? tenant = null)
    {
        o.Organization = "org"; o.Project = "proj"; o.Feed = "pub";
        o.FeedsApiBase = d.Base; o.PackagesBase = d.Base; o.GraphApiBase = d.Base; o.PackmoatPrincipal = "11111111-spid";
        o.RetryWaitCap = TimeSpan.Zero;
        return new OnboardingChecks(http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, new StaticBasicCredentialSource("packmoat", "SP-SECRET-TOKEN"), tenant ?? TenantFor(d), o);
    }

    private static CheckResult Get(IReadOnlyList<CheckResult> r, string id) => r.Single(x => x.Id == id);

    // Descriptors and identities in the shape the live identities API returned them. The organisation's and project's numbers are made up
    // (#187); the structure is kept, and pinned by the test below. Both built-in admin groups have SIDs ending -0-0-0-0-1, so only the
    // identities API tells them apart.
    internal const string PcaDescriptor = "Microsoft.TeamFoundation.Identity;S-1-9-1551374245-1000000001-1000000002-1000000003-1000000004-0-0-0-0-1";
    internal const string ProjectAdminsDescriptor = "Microsoft.TeamFoundation.Identity;S-1-9-1551374245-2000000001-2000000002-2000000003-2000000004-0-0-0-0-1";
    internal const string OtherProjectAdminsDescriptor = "Microsoft.TeamFoundation.Identity;S-1-9-1551374245-111111111-222222222-333333333-444444444-0-0-0-0-1";
    internal const string ValidUsersDescriptor = "Microsoft.TeamFoundation.Identity;S-1-9-1551374245-1000000001-1000000002-1000000003-1000000004-0-0-0-0-3";
    internal const string CreatorDescriptor = "Microsoft.IdentityModel.Claims.ClaimsIdentity;c0de0000-0000-0000-0000-000000000000\\casey@contoso.example";
    internal const string CollectionDomain = "vstfs:///Framework/IdentityDomain/c011ec70-0000-4000-8000-000000000001";
    internal const string ProjectId = "a1a1a1a1-0000-4000-8000-000000000001";
    internal const string OtherProjectId = "0d1c2b3a-4f5e-4a6b-8c7d-9e0f1a2b3c4d";

    [Fact]   // mutation: a descriptor's ending changed to -0-0-0-0-2
    public void The_made_up_descriptors_keep_the_live_structure()
    {
        // The live prefix, the organisation's (or a project's) four numbers, then the group's ending: both admin groups -0-0-0-0-1, Valid
        // Users -0-0-0-0-3. The organisation's two groups share the collection's numbers; each project's group has its own.
        static (string Numbers, string Ending) Parts(string descriptor)
        {
            var m = System.Text.RegularExpressions.Regex.Match(descriptor, @"^Microsoft\.TeamFoundation\.Identity;S-1-9-1551374245-(\d+-\d+-\d+-\d+)-(\d+-\d+-\d+-\d+-\d+)$");
            Assert.True(m.Success, descriptor);
            return (m.Groups[1].Value, m.Groups[2].Value);
        }
        var (pca, project, other, valid) = (Parts(PcaDescriptor), Parts(ProjectAdminsDescriptor), Parts(OtherProjectAdminsDescriptor), Parts(ValidUsersDescriptor));
        Assert.Equal(("0-0-0-0-1", "0-0-0-0-1", "0-0-0-0-1", "0-0-0-0-3"), (pca.Ending, project.Ending, other.Ending, valid.Ending));
        Assert.Equal(pca.Numbers, valid.Numbers);
        Assert.Equal(3, new[] { pca.Numbers, project.Numbers, other.Numbers }.Distinct().Count());
    }

    // In these tests the options are --org org --project proj, so ScopeName is "org" and "proj".
    internal static JsonObject PcaIdentity(string scope = "org") =>
        FakeDevOps.Identity(PcaDescriptor, $"[{scope}]\\Project Collection Administrators", "AdministrativeApplicationGroup", scope, CollectionDomain, "Project Collection Administrators");
    internal static JsonObject ProjectAdminsIdentity(string descriptor = ProjectAdminsDescriptor, string scope = "proj", string projectId = ProjectId, string specialType = "AdministrativeApplicationGroup") =>
        FakeDevOps.Identity(descriptor, $"[{scope}]\\Project Administrators", specialType, scope, "vstfs:///Classification/TeamProject/" + projectId, "Project Administrators");

    private static void GoodFeed(FakeDevOps d)
    {
        d.Permissions = new JsonArray(
            FakeDevOps.Perm("collaborator", "", Packmoat),
            FakeDevOps.Perm("administrator", "[org]\\Project Collection Administrators", PcaDescriptor, inherited: true),
            FakeDevOps.Perm("none", "[org]\\Project Collection Valid Users", ValidUsersDescriptor, inherited: true));
        d.Identities[PcaDescriptor] = PcaIdentity();
    }

    /// <summary>What Azure DevOps puts on every new feed: the creator, Project Collection Administrators and the project's Project Administrators.</summary>
    private static void DefaultAdmins(FakeDevOps d)
    {
        d.Permissions = new JsonArray(
            FakeDevOps.Perm("collaborator", "", Packmoat),
            FakeDevOps.Perm("administrator", "Casey Creator", CreatorDescriptor),
            FakeDevOps.Perm("administrator", "[org]\\Project Collection Administrators", PcaDescriptor, inherited: true),
            FakeDevOps.Perm("administrator", "[proj]\\Project Administrators", ProjectAdminsDescriptor),
            FakeDevOps.Perm("none", "[org]\\Project Collection Valid Users", ValidUsersDescriptor, inherited: true));
        d.Identities[PcaDescriptor] = PcaIdentity();
        d.Identities[ProjectAdminsDescriptor] = ProjectAdminsIdentity();
    }

    private static async Task<CheckResult> Permissions(FakeDevOps d, params string[] confirm)
    {
        var o = new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret };
        o.ConfirmedAdmins.AddRange(confirm);
        return Get(await Build(d, o).RunAsync(default), "permissions");
    }

    private const string Unconfirmed = "administrators not confirmed (ask the customer who they are, then rerun with --confirm-admin \"<name>\" for each): ";

    [Fact]
    public async Task The_default_feed_admins_pass_once_the_creator_is_confirmed()
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);

        var r = await Permissions(d, "Casey Creator");

        Assert.True(r.Status == CheckStatus.Pass, r.Detail);
        Assert.Contains("Confirmed by the operator (--confirm-admin): Casey Creator", r.Detail);
        Assert.Contains("[org]\\Project Collection Administrators (the organisation's built-in Project Collection Administrators, confirmed by the identities API)", r.Detail);
        Assert.Contains("[proj]\\Project Administrators (the project's built-in Project Administrators, confirmed by the identities API)", r.Detail);
        Assert.DoesNotContain(FakeDevOps.AdminSecret, r.Detail);
    }

    [Fact]
    public async Task The_creator_can_be_confirmed_by_descriptor()
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);
        Assert.Equal(CheckStatus.Pass, (await Permissions(d, CreatorDescriptor)).Status);
    }

    [Fact]
    public async Task A_project_given_by_id_matches_the_project_id_in_the_domain()
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);
        d.Identities[ProjectAdminsDescriptor] = ProjectAdminsIdentity(scope: "Platform");   // ScopeName is the name, the option is the id
        var o = new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret };
        o.ConfirmedAdmins.Add("Casey Creator");
        var checks = Build(d, o);
        o.Project = ProjectId;
        var r = Get(await checks.RunAsync(default), "permissions");
        Assert.Contains("the project's built-in Project Administrators, confirmed by the identities API", r.Detail);
    }

    [Fact]
    public async Task Without_a_confirmation_the_creator_is_a_warning_and_the_org_groups_are_not()
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);

        var r = await Permissions(d);

        Assert.Equal(CheckStatus.Warn, r.Status);
        Assert.Contains(Unconfirmed + "Casey Creator.", r.Detail);   // only the creator: the two organisation groups are expected
        Assert.Contains("Expected organisation admin groups", r.Detail);
    }

    [Fact]
    public async Task An_extra_unconfirmed_admin_is_a_warning_naming_it()
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);
        d.Permissions.Add(FakeDevOps.Perm("administrator", "Mallory", "Microsoft.IdentityModel.Claims.ClaimsIdentity;c0de0000-0000-0000-0000-000000000000\\mallory@example.com"));

        var r = await Permissions(d, "Casey Creator");

        Assert.Equal(CheckStatus.Warn, r.Status);
        Assert.Contains(Unconfirmed + "Mallory.", r.Detail);
        Assert.Contains("Confirmed by the operator (--confirm-admin): Casey Creator", r.Detail);
    }

    [Fact]
    public async Task A_confirmed_admin_who_is_not_on_the_feed_is_a_warning()
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);

        var r = await Permissions(d, "Casey Creator", "Alice Admin");

        Assert.Equal(CheckStatus.Warn, r.Status);
        Assert.Contains("confirmed but not a named administrator on the feed", r.Detail);
        Assert.Contains("Alice Admin", r.Detail);
    }

    [Fact]
    public async Task One_name_that_matches_two_admins_is_a_warning()
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);
        d.Permissions.Add(FakeDevOps.Perm("administrator", "Casey Creator", "Microsoft.IdentityModel.Claims.ClaimsIdentity;other\\casey@elsewhere.example"));

        var r = await Permissions(d, "Casey Creator");

        Assert.Equal(CheckStatus.Warn, r.Status);
        Assert.Contains("matches more than one administrator", r.Detail);
    }

    [Theory]
    [InlineData("[proj]\\Contributors")]
    [InlineData("[org]\\Project Collection Valid Users")]
    [InlineData("[proj]\\Readers")]
    [InlineData("proj Build Service (org)")]
    public async Task A_broad_group_as_administrator_fails_even_when_confirmed(string group)
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);
        d.Permissions.Add(FakeDevOps.Perm("administrator", group, "Microsoft.TeamFoundation.Identity;S-1-9-1551374245-1-2-3-4-1-5-6-7-8"));

        var r = await Permissions(d, "Casey Creator", group);

        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Contains($"{group} has administrator", r.Detail);
    }

    [Theory]
    [InlineData("collaborator")]
    [InlineData("contributor")]
    [InlineData("reader")]
    public async Task The_org_admin_groups_with_a_role_other_than_administrator_fail_as_before(string role)
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);
        foreach (var (name, descriptor) in new[] { ("[org]\\Project Collection Administrators", PcaDescriptor), ("[proj]\\Project Administrators", ProjectAdminsDescriptor) })
        {
            d.Permissions = new JsonArray(FakeDevOps.Perm("collaborator", "", Packmoat), FakeDevOps.Perm(role, name, descriptor));

            var r = await Permissions(d);

            Assert.Equal(CheckStatus.Fail, r.Status);
            Assert.Contains($"{name} has {role}", r.Detail);
        }
    }

    [Fact]
    public async Task Another_projects_Project_Administrators_is_not_auto_passed()
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);
        d.Permissions.Add(FakeDevOps.Perm("administrator", "[other]\\Project Administrators", OtherProjectAdminsDescriptor));
        d.Identities[OtherProjectAdminsDescriptor] = ProjectAdminsIdentity(OtherProjectAdminsDescriptor, scope: "other", projectId: OtherProjectId);

        var r = await Permissions(d, "Casey Creator");

        Assert.Equal(CheckStatus.Warn, r.Status);
        Assert.Contains(Unconfirmed + "[other]\\Project Administrators (not a built-in admin group of this organisation or project", r.Detail);
        Assert.Equal(CheckStatus.Pass, (await Permissions(d, "Casey Creator", "[other]\\Project Administrators")).Status);   // confirmable
    }

    [Fact]
    public async Task A_group_whose_SID_ends_like_PCA_but_is_a_project_group_is_not_PCA()
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);
        // Named like PCA, SID ending -0-0-0-0-1, but the API says it is another project's admin group.
        d.Permissions.Add(FakeDevOps.Perm("administrator", "[org]\\Project Collection Administrators", OtherProjectAdminsDescriptor));
        d.Identities[OtherProjectAdminsDescriptor] = ProjectAdminsIdentity(OtherProjectAdminsDescriptor, scope: "other", projectId: OtherProjectId);

        var r = await Permissions(d, "Casey Creator");

        Assert.Equal(CheckStatus.Warn, r.Status);
        Assert.Contains("[org]\\Project Collection Administrators (not a built-in admin group", r.Detail);
    }

    [Fact]
    public void The_built_in_group_rules_follow_the_live_identity_shape()
    {
        OnboardingChecks.IdentityFacts Facts(string specialType = "AdministrativeApplicationGroup", string scope = "contosoorg", string domain = CollectionDomain,
            string account = "Project Collection Administrators", bool container = true) => new(container, specialType, scope, domain, account);

        Assert.NotNull(OnboardingChecks.OrgAdminGroup(Facts(), "ContosoOrg", "Platform"));
        Assert.Null(OnboardingChecks.OrgAdminGroup(Facts(), "otherorg", "Platform"));                                  // another organisation
        Assert.Null(OnboardingChecks.OrgAdminGroup(Facts(specialType: "Generic"), "ContosoOrg", "Platform"));
        Assert.Null(OnboardingChecks.OrgAdminGroup(Facts(account: "Project Collection Build Administrators"), "ContosoOrg", "Platform"));
        Assert.Null(OnboardingChecks.OrgAdminGroup(Facts(container: false), "ContosoOrg", "Platform"));
        Assert.Null(OnboardingChecks.OrgAdminGroup(Facts(domain: "vstfs:///Classification/TeamProject/" + ProjectId), "ContosoOrg", "Platform"));

        var projectAdmins = Facts(scope: "Platform", domain: "vstfs:///Classification/TeamProject/" + ProjectId, account: "Project Administrators");
        Assert.Equal("the project's built-in Project Administrators", OnboardingChecks.OrgAdminGroup(projectAdmins, "ContosoOrg", "Platform"));
        Assert.NotNull(OnboardingChecks.OrgAdminGroup(projectAdmins, "ContosoOrg", ProjectId));
        Assert.Null(OnboardingChecks.OrgAdminGroup(projectAdmins, "ContosoOrg", "Other"));
        Assert.Null(OnboardingChecks.OrgAdminGroup(projectAdmins, "ContosoOrg", OtherProjectId));
        Assert.Null(OnboardingChecks.OrgAdminGroup(projectAdmins, "ContosoOrg", null));                                // an organisation-scoped feed
        Assert.Null(OnboardingChecks.OrgAdminGroup(projectAdmins with { Domain = CollectionDomain }, "ContosoOrg", "Platform"));
    }

    [Fact]
    public async Task A_user_named_like_Project_Administrators_is_not_auto_passed()
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);
        d.Permissions.Add(FakeDevOps.Perm("administrator", "[proj]\\Project Administrators", "Microsoft.IdentityModel.Claims.ClaimsIdentity;c0de0000-0000-0000-0000-000000000000\\spoof@example.com"));

        var r = await Permissions(d, "Casey Creator");

        Assert.Equal(CheckStatus.Warn, r.Status);
        Assert.Contains(Unconfirmed + "[proj]\\Project Administrators.", r.Detail);
    }

    [Fact]
    public async Task A_non_built_in_group_named_like_Project_Administrators_is_not_auto_passed()
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);
        d.Identities[ProjectAdminsDescriptor] = ProjectAdminsIdentity(specialType: "Generic");

        var r = await Permissions(d, "Casey Creator");

        Assert.Equal(CheckStatus.Warn, r.Status);
        Assert.Contains("[proj]\\Project Administrators (not a built-in admin group of this organisation or project: the identities API describes 'Project Administrators' of type 'Generic'", r.Detail);
    }

    [Fact]
    public async Task Without_an_identities_lookup_neither_group_is_recognised_but_both_can_be_confirmed()
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);
        d.IdentitiesStatus = 403;   // the admin PAT has Packaging only, no Identity read

        var r = await Permissions(d, "Casey Creator");

        Assert.Equal(CheckStatus.Warn, r.Status);
        Assert.Contains("[org]\\Project Collection Administrators (not recognised as a built-in admin group: the identities lookup failed (HTTP 403", r.Detail);
        Assert.Contains("[proj]\\Project Administrators (not recognised", r.Detail);
        Assert.Contains("the admin PAT needs Identity (read) for automatic recognition", r.Detail);
        Assert.DoesNotContain("Expected organisation admin groups", r.Detail);

        var confirmed = await Permissions(d, "Casey Creator", "[org]\\Project Collection Administrators", "[proj]\\Project Administrators");
        Assert.True(confirmed.Status == CheckStatus.Pass, confirmed.Detail);
    }

    [Fact]
    public async Task An_identity_the_API_does_not_return_is_not_recognised()
    {
        await using var d = await FakeDevOps.StartAsync();
        DefaultAdmins(d);
        d.Identities.Remove(PcaDescriptor);

        var r = await Permissions(d, "Casey Creator");

        Assert.Equal(CheckStatus.Warn, r.Status);
        Assert.Contains("[org]\\Project Collection Administrators (not recognised as a built-in admin group: the identities API did not return it)", r.Detail);
    }

    [Fact]
    public async Task A_correctly_isolated_customer_passes_everything_it_can_check()
    {
        await using var d = await FakeDevOps.StartAsync();
        await using var p = await ProxyFixture.StartAsync();
        GoodFeed(d);
        var o = new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret, DeveloperToken = FakeDevOps.DeveloperSecret, ProxyUrl = p.SourceUrl, ProxyToken = ProxyFixture.AcmeToken };
        o.PrivateFeedProbeUrls.Add(d.Base + "/org/private/_packaging/hr/nuget/v3/index.json");

        var r = await Build(d, o).RunAsync(default);

        Assert.Equal(CheckStatus.Pass, Get(r, "feed").Status);
        Assert.Equal(CheckStatus.Pass, Get(r, "upstreams").Status);
        Assert.Equal(CheckStatus.Pass, Get(r, "permissions").Status);   // the organisation's own admin group is expected
        Assert.Contains("Project Collection Administrators", Get(r, "permissions").Detail);
        Assert.Equal(CheckStatus.Pass, Get(r, "views").Status);
        Assert.Equal(CheckStatus.Pass, Get(r, "developer").Status);
        Assert.Equal(CheckStatus.Pass, Get(r, "private").Status);
        Assert.Equal(CheckStatus.Pass, Get(r, "proxy").Status);
        Assert.DoesNotContain(r, x => x.Id == "egress");               // it measured the operator's network, which proves nothing about the customer's
        Assert.DoesNotContain(r, x => x.Status == CheckStatus.Fail);
    }

    [Fact]
    public async Task The_portal_default_setup_fails_the_checks_that_matter()
    {
        await using var d = await FakeDevOps.StartAsync();
        d.Upstreams = new JsonArray(Enumerable.Range(0, 9).Select(i => (JsonNode)FakeDevOps.U(i == 0 ? "https://registry.npmjs.org/" : $"https://other{i}.example/")).ToArray());
        d.Permissions = new JsonArray(
            FakeDevOps.Perm("collaborator", "Acme Build Service (org)", "ServiceIdentity;x"),
            FakeDevOps.Perm("collaborator", "[Acme]\\Contributors", "S-1-9-contrib"),
            FakeDevOps.Perm("collaborator", "", Packmoat),
            FakeDevOps.Perm("administrator", "[Acme]\\Project Administrators", "S-1-9-padmin"));
        d.Views = new JsonArray(FakeDevOps.V("Local", "organization"), FakeDevOps.V("Release", "private"));
        d.DeveloperIndexStatus = 200;
        var o = new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret, DeveloperToken = FakeDevOps.DeveloperSecret };

        var r = await Build(d, o).RunAsync(default);

        Assert.Equal(CheckStatus.Fail, Get(r, "upstreams").Status);
        Assert.Contains("8 extra", Get(r, "upstreams").Detail);
        var perm = Get(r, "permissions");
        Assert.Equal(CheckStatus.Fail, perm.Status);
        Assert.Contains("Contributors has collaborator", perm.Detail);
        Assert.Contains("Build Service", perm.Detail);
        Assert.Equal(CheckStatus.Fail, Get(r, "views").Status);
        Assert.Contains("Local is 'organization'", Get(r, "views").Detail);
        var dev = Get(r, "developer");
        Assert.Equal(CheckStatus.Fail, dev.Status);
        Assert.Contains("bypass", dev.Detail);
    }

    [Fact]
    public async Task Packmoat_must_hold_exactly_the_collaborator_role()
    {
        await using var d = await FakeDevOps.StartAsync();
        d.Permissions = new JsonArray(FakeDevOps.Perm("contributor", "", Packmoat));
        var o = new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret };
        Assert.Contains("needs exactly 'collaborator'", Get(await Build(d, o).RunAsync(default), "permissions").Detail);

        d.Permissions = new JsonArray();
        var missing = Get(await Build(d, new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret }).RunAsync(default), "permissions");
        Assert.Equal(CheckStatus.Fail, missing.Status);
    }

    [Fact]
    public async Task A_private_feed_that_Packmoat_can_read_fails_loudly()
    {
        await using var d = await FakeDevOps.StartAsync();
        d.PrivateFeedStatus = 200;
        var o = new OnboardingOptions();
        o.PrivateFeedProbeUrls.Add(d.Base + "/org/private/_packaging/hr/nuget/v3/index.json?secret=1");

        var r = Get(await Build(d, o).RunAsync(default), "private");

        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Contains("can read", r.Detail);
        Assert.DoesNotContain("secret=1", r.Detail); // query strings are not echoed
    }

    [Fact]
    public async Task Checks_that_could_not_run_are_reported_as_not_verified_rather_than_passed()
    {
        await using var d = await FakeDevOps.StartAsync();
        var r = await Build(d, new OnboardingOptions()).RunAsync(default);

        Assert.Equal(CheckStatus.Skip, Get(r, "upstreams").Status);
        Assert.Equal(CheckStatus.Skip, Get(r, "permissions").Status);
        Assert.Equal(CheckStatus.Skip, Get(r, "views").Status);
        var dev = Get(r, "developer");
        Assert.Equal(CheckStatus.Skip, dev.Status);
        Assert.Contains("NOT VERIFIED", dev.Detail);
        Assert.Equal(CheckStatus.Skip, Get(r, "private").Status);
        Assert.Equal(CheckStatus.Skip, Get(r, "proxy").Status);
    }

    [Fact]
    public async Task A_bad_admin_credential_is_a_clear_failure_not_a_pass()
    {
        await using var d = await FakeDevOps.StartAsync();
        var r = await Build(d, new OnboardingOptions { AdminToken = "wrong" }).RunAsync(default);
        Assert.Equal(CheckStatus.Fail, Get(r, "permissions").Status);
        Assert.Contains("HTTP 401", Get(r, "permissions").Detail);
    }

    [Fact]
    public async Task The_proxy_check_fails_when_the_proxy_does_not_demand_a_token()
    {
        await using var d = await FakeDevOps.StartAsync();
        var o = new OnboardingOptions { ProxyUrl = d.Base + "/org/private/open", ProxyToken = "pmt_x" }; // fake answers 403 to everything here
        Assert.Equal(CheckStatus.Fail, Get(await Build(d, o).RunAsync(default), "proxy").Status);
    }

    [Fact]
    public async Task No_result_ever_contains_a_credential()
    {
        await using var d = await FakeDevOps.StartAsync();
        GoodFeed(d);
        var o = new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret, DeveloperToken = FakeDevOps.DeveloperSecret, ProxyUrl = d.Base + "/x", ProxyToken = "pmt_PROXY-SECRET" };
        o.PrivateFeedProbeUrls.Add(d.Base + "/org/private/x");

        var text = string.Concat((await Build(d, o).RunAsync(default)).Select(x => x.ToString()));

        foreach (var secret in new[] { FakeDevOps.AdminSecret, FakeDevOps.DeveloperSecret, "SP-SECRET-TOKEN", "pmt_PROXY-SECRET" }) Assert.DoesNotContain(secret, text);
    }

    // ---- the checked feed must be the feed Packmoat serves

    [Theory]
    [InlineData("/org/proj/_packaging/pub/nuget/v3/index.json")]
    [InlineData("/ORG/Proj/_packaging/PUB/nuget/v3/index.json/")]   // Azure DevOps names ignore case; a trailing slash means nothing
    public async Task The_feed_check_passes_when_the_options_name_the_tenants_feed(string stored)
    {
        await using var d = await FakeDevOps.StartAsync();
        var r = Get(await Build(d, new OnboardingOptions(), tenant: TenantFor(d, stored)).RunAsync(default), "feed");
        Assert.Equal(CheckStatus.Pass, r.Status);
    }

    [Theory]
    [InlineData("/org/proj/_packaging/other/nuget/v3/index.json")]
    [InlineData("/org/otherproj/_packaging/pub/nuget/v3/index.json")]
    [InlineData("/org2/proj/_packaging/pub/nuget/v3/index.json")]
    public async Task The_feed_check_fails_when_the_options_name_a_different_feed(string stored)
    {
        await using var d = await FakeDevOps.StartAsync();
        await using var p = await ProxyFixture.StartAsync();
        GoodFeed(d);
        var o = new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret, DeveloperToken = FakeDevOps.DeveloperSecret, ProxyUrl = p.SourceUrl, ProxyToken = ProxyFixture.AcmeToken };

        var all = await Build(d, o, tenant: TenantFor(d, stored)).RunAsync(default);
        var r = Get(all, "feed");

        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Contains("Packmoat serves", r.Detail);
        Assert.Contains(stored, r.Detail);
        Assert.Equal(1, OnboardingSummary.Evaluate(all).ExitCode);   // everything else may pass, but about the wrong feed: never "verified"
    }

    [Fact]
    public async Task The_feed_check_never_prints_user_info_or_query_from_the_stored_url()
    {
        await using var d = await FakeDevOps.StartAsync();
        var stored = $"http://user:STORED-SECRET@{new Uri(d.Base).Authority}/org/other/_packaging/x/nuget/v3/index.json?sig=QUERY-SECRET";
        var tenant = new Tenant("acme", "Acme", d.Base + "/", "org/", stored, new TenantPolicy(14));

        var r = Get(await Build(d, new OnboardingOptions(), tenant: tenant).RunAsync(default), "feed");

        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.DoesNotContain("STORED-SECRET", r.Detail);
        Assert.DoesNotContain("QUERY-SECRET", r.Detail);
    }

    [Theory]
    [InlineData("/org/proj/_packaging/p%75b/nuget/v3/index.json")]        // an escape that decodes to the right name still does not match
    [InlineData("/org/proj%2Fx/_packaging/pub/nuget/v3/index.json")]       // an escaped separator
    [InlineData("/org/proj/_packaging/pub/nuget/v3/index.json?feed=other")]
    [InlineData("/org/proj/_packaging/pub/nuget/v3/index.json#frag")]
    public async Task A_stored_url_with_an_escape_query_or_fragment_never_matches(string stored)
    {
        await using var d = await FakeDevOps.StartAsync();
        var r = Get(await Build(d, new OnboardingOptions(), tenant: TenantFor(d, stored)).RunAsync(default), "feed");
        Assert.Equal(CheckStatus.Fail, r.Status);
    }

    [Fact]
    public async Task A_stored_url_with_user_info_never_matches_even_when_host_and_path_do()
    {
        await using var d = await FakeDevOps.StartAsync();
        var stored = $"http://u:p@{new Uri(d.Base).Authority}/org/proj/_packaging/pub/nuget/v3/index.json";
        var tenant = new Tenant("acme", "Acme", d.Base + "/", "org/", stored, new TenantPolicy(14));
        Assert.Equal(CheckStatus.Fail, Get(await Build(d, new OnboardingOptions(), tenant: tenant).RunAsync(default), "feed").Status);
    }

    [Theory]
    [InlineData("org", "")]
    [InlineData("org", "a/b")]
    [InlineData("org", "a\\b")]
    [InlineData("org", "p%75b")]
    [InlineData("org", "pub?x=1")]
    [InlineData("org", "pub#x")]
    [InlineData("org", "..")]
    [InlineData("org", "a..b")]
    [InlineData("org", ".")]
    [InlineData("org", "p b")]
    [InlineData("", "pub")]
    [InlineData("o/rg", "pub")]
    [InlineData("o%2Frg", "pub")]
    [InlineData("..", "pub")]
    public async Task A_name_that_is_not_plain_is_refused_before_any_request(string org, string feed)
    {
        await using var d = await FakeDevOps.StartAsync();
        var o = new OnboardingOptions { AdminToken = FakeDevOps.AdminSecret };
        var checks = Build(d, o);
        o.Organization = org; o.Feed = feed;

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => checks.RunAsync(default));

        Assert.Equal(OnboardingChecks.BadNameMessage, ex.Message);
        Assert.Empty(d.Seen);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("p%20")]
    [InlineData("p?")]
    [InlineData("p#")]
    [InlineData("..")]
    public async Task A_project_that_is_not_plain_is_refused(string project)
    {
        await using var d = await FakeDevOps.StartAsync();
        var o = new OnboardingOptions();
        var checks = Build(d, o);
        o.Project = project;
        Assert.Equal(OnboardingChecks.BadNameMessage, (await Assert.ThrowsAsync<ArgumentException>(() => checks.RunAsync(default))).Message);
    }

    [Fact]
    public async Task No_project_is_fine()
    {
        await using var d = await FakeDevOps.StartAsync();
        var o = new OnboardingOptions();
        var checks = Build(d, o, tenant: TenantFor(d, "/org/_packaging/pub/nuget/v3/index.json"));
        o.Project = null;
        Assert.Equal(CheckStatus.Pass, Get(await checks.RunAsync(default), "feed").Status);
    }

    [Fact]
    public void The_feed_check_is_required()
    {
        Assert.Contains("feed", OnboardingSummary.Required);
        var results = OnboardingSummary.Required.Where(id => id != "feed").Select(id => new CheckResult(id, id, CheckStatus.Pass, "")).ToArray();
        Assert.Equal(2, OnboardingSummary.Evaluate(results).ExitCode);
    }
}
