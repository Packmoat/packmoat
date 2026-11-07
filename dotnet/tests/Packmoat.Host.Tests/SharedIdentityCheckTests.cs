using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

/// <summary>
/// Shared-app mode in the onboarding check: the 'identity' check finds Packmoat's app among the organisation's service principals
/// (Azure DevOps Graph API, shape as documented by Microsoft; not yet proven live) and hands it to the permissions check.
/// </summary>
public class SharedIdentityCheckTests
{
    private const string AppId = "99999999-8888-4777-8666-555555555555";
    private const string Dir = "11111111-2222-4333-8444-555555555555";
    private const string ObjectId = "0b1ec700-0000-4000-8000-00000000a001";
    private const string AdminSecret = "ADMIN-SECRET-shared";

    /// <summary>Answers like Azure DevOps: Graph service principals (paged by X-MS-ContinuationToken), feed settings, permissions, views.</summary>
    private sealed class FakeAzureDevOps : HttpMessageHandler
    {
        public List<JsonArray> ServicePrincipalPages { get; } = new();
        public JsonArray Permissions { get; set; } = new();
        public HttpStatusCode GraphStatus { get; set; } = HttpStatusCode.OK;
        public bool GraphUnreachable { get; set; }
        public List<string> Seen { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!;
            Seen.Add(url.ToString());
            var auth = request.Headers.Authorization?.ToString() ?? "";
            if (auth != "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + AdminSecret))) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

            if (url.Host == "vssps.example" && url.AbsolutePath == "/org/_apis/graph/serviceprincipals")
            {
                if (GraphUnreachable) throw new HttpRequestException("no route to host");
                if (GraphStatus != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(GraphStatus));
                Assert.Contains("api-version=7.1-preview.1", url.Query);
                var token = System.Web.HttpUtility.ParseQueryString(url.Query)["continuationToken"];
                var page = token is null ? 0 : int.Parse(token);
                var response = Json(new JsonObject { ["count"] = ServicePrincipalPages[page].Count, ["value"] = ServicePrincipalPages[page].DeepClone() });
                if (page + 1 < ServicePrincipalPages.Count) response.Headers.Add("X-MS-ContinuationToken", (page + 1).ToString());
                return Task.FromResult(response);
            }
            if (url.Host == "feeds.example")
            {
                if (url.AbsolutePath.EndsWith("/permissions")) return Task.FromResult(Json(new JsonObject { ["value"] = Permissions.DeepClone() }));
                if (url.AbsolutePath.EndsWith("/views")) return Task.FromResult(Json(new JsonObject { ["value"] = new JsonArray() }));
                return Task.FromResult(Json(new JsonObject { ["upstreamSources"] = new JsonArray(new JsonObject { ["location"] = "https://api.nuget.org/v3/index.json" }) }));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(JsonNode body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    /// <summary>A GraphServicePrincipal as in Microsoft's documented sample.</summary>
    private static JsonObject Sp(string appId, string originId, string domain = Dir, bool deleted = false) => new()
    {
        ["subjectKind"] = "servicePrincipal", ["applicationId"] = appId, ["origin"] = "aad", ["originId"] = originId, ["domain"] = domain,
        ["displayName"] = "sp " + originId[..4], ["descriptor"] = "aadsp." + Convert.ToBase64String(Encoding.ASCII.GetBytes(originId)), ["isDeletedInOrigin"] = deleted,
    };

    private static JsonObject Perm(string role, string descriptor) => new() { ["role"] = role, ["displayName"] = "", ["identityDescriptor"] = descriptor };

    private static async Task<IReadOnlyList<CheckResult>> Run(FakeAzureDevOps fake, string principal = "", bool admin = true)
    {
        var o = new OnboardingOptions
        {
            Organization = "org", Project = "proj", Feed = "pub",
            FeedsApiBase = "https://feeds.example", PackagesBase = "https://pkgs.example", GraphApiBase = "https://vssps.example",
            AdminToken = admin ? AdminSecret : null, PackmoatPrincipal = principal, SharedAppClientId = AppId, SharedAppDirectoryId = Dir,
            RetryWaitCap = TimeSpan.Zero,
        };
        var tenant = new Tenant("acme", "Acme", "https://pkgs.example/", "org/", "https://pkgs.example/org/proj/_packaging/pub/nuget/v3/index.json", new TenantPolicy(14));
        return await new OnboardingChecks(new HttpClient(fake), new StaticBasicCredentialSource("packmoat", "SP"), tenant, o).RunAsync(default);
    }

    private static CheckResult Get(IReadOnlyList<CheckResult> r, string id) => r.Single(x => x.Id == id);

    private static FakeAzureDevOps WithPackmoatCollaborator(params JsonArray[] pages)
    {
        var fake = new FakeAzureDevOps { Permissions = new JsonArray(Perm("collaborator", $"Microsoft.VisualStudio.Services.Claims.AadServicePrincipal;{Dir}\\{ObjectId}")) };
        fake.ServicePrincipalPages.AddRange(pages);
        return fake;
    }

    [Fact]
    public async Task Exactly_one_match_passes_and_becomes_Packmoats_principal_without_principal_option()
    {
        var fake = WithPackmoatCollaborator(new JsonArray(Sp("00000000-0000-4000-8000-000000000001", "aaaaaaaa-0000-4000-8000-000000000001"), Sp(AppId, ObjectId)));

        var r = await Run(fake);

        Assert.Equal(CheckStatus.Pass, Get(r, "identity").Status);
        Assert.Contains(ObjectId, Get(r, "identity").Detail);
        Assert.Equal(CheckStatus.Pass, Get(r, "permissions").Status);
    }

    [Fact]
    public async Task The_match_is_found_on_a_later_page()
    {
        var fake = WithPackmoatCollaborator(new JsonArray(Sp("00000000-0000-4000-8000-000000000001", "aaaaaaaa-0000-4000-8000-000000000001")), new JsonArray(), new JsonArray(Sp(AppId.ToUpperInvariant(), ObjectId)));
        var r = await Run(fake);
        Assert.Equal(CheckStatus.Pass, Get(r, "identity").Status);
        Assert.Equal(3, fake.Seen.Count(u => u.Contains("serviceprincipals")));
    }

    [Fact]
    public async Task No_match_fails_and_the_permissions_check_is_not_verified()
    {
        var fake = WithPackmoatCollaborator(new JsonArray(Sp("00000000-0000-4000-8000-000000000001", "aaaaaaaa-0000-4000-8000-000000000001")));
        var r = await Run(fake);
        Assert.Equal(CheckStatus.Fail, Get(r, "identity").Status);
        Assert.Contains("not a member of the organisation", Get(r, "identity").Detail);
        Assert.Equal(CheckStatus.Skip, Get(r, "permissions").Status);
        Assert.Equal(1, OnboardingSummary.Evaluate(r).ExitCode);
    }

    [Fact]
    public async Task A_deleted_service_principal_does_not_count()
    {
        var fake = WithPackmoatCollaborator(new JsonArray(Sp(AppId, ObjectId, deleted: true)));
        Assert.Equal(CheckStatus.Fail, Get(await Run(fake), "identity").Status);
    }

    [Fact]
    public async Task Several_matches_fail()
    {
        var fake = WithPackmoatCollaborator(new JsonArray(Sp(AppId, ObjectId)), new JsonArray(Sp(AppId, "cccccccc-0000-4000-8000-000000000003")));
        var r = Get(await Run(fake), "identity");
        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Contains("2 service principals", r.Detail);
    }

    [Fact]
    public async Task A_service_principal_from_another_directory_fails()
    {
        var fake = WithPackmoatCollaborator(new JsonArray(Sp(AppId, ObjectId, domain: "22222222-2222-4333-8444-555555555555")));
        var r = Get(await Run(fake), "identity");
        Assert.Equal(CheckStatus.Fail, r.Status);
        Assert.Contains("bound to directory", r.Detail);
    }

    [Fact]
    public async Task An_empty_or_missing_domain_is_not_verified()
    {
        var empty = WithPackmoatCollaborator(new JsonArray(Sp(AppId, ObjectId, domain: "")));
        var missing = WithPackmoatCollaborator(new JsonArray(Sp(AppId, ObjectId)));
        ((JsonObject)missing.ServicePrincipalPages[0][0]!).Remove("domain");

        foreach (var fake in new[] { empty, missing })
        {
            var r = await Run(fake);
            Assert.Equal(CheckStatus.Skip, Get(r, "identity").Status);
            Assert.Contains("NOT VERIFIED", Get(r, "identity").Detail);
            Assert.Equal(CheckStatus.Skip, Get(r, "permissions").Status);   // no principal handed on
            Assert.NotEqual(0, OnboardingSummary.Evaluate(r).ExitCode);
        }
    }

    [Fact]
    public async Task A_principal_option_that_disagrees_with_what_was_found_fails()
    {
        var fake = WithPackmoatCollaborator(new JsonArray(Sp(AppId, ObjectId)));
        Assert.Equal(CheckStatus.Fail, Get(await Run(fake, principal: "eeeeeeee-0000-4000-8000-000000000005"), "identity").Status);
        Assert.Equal(CheckStatus.Pass, Get(await Run(fake, principal: ObjectId), "identity").Status);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 401)]
    [InlineData(false, 500)]
    public async Task An_unreachable_or_refused_graph_api_is_not_verified(bool unreachable, int status)
    {
        var fake = WithPackmoatCollaborator(new JsonArray(Sp(AppId, ObjectId)));
        fake.GraphUnreachable = unreachable;
        if (!unreachable) fake.GraphStatus = (HttpStatusCode)status;

        var r = await Run(fake);

        Assert.Equal(CheckStatus.Skip, Get(r, "identity").Status);
        Assert.Contains("NOT VERIFIED", Get(r, "identity").Detail);
        Assert.Equal(CheckStatus.Skip, Get(r, "permissions").Status);
        Assert.DoesNotContain(AdminSecret, string.Concat(r.Select(x => x.Detail)));
    }

    [Fact]
    public async Task Without_an_admin_credential_the_identity_check_is_not_verified_and_blocks_the_verdict()
    {
        var r = await Run(WithPackmoatCollaborator(new JsonArray(Sp(AppId, ObjectId))), admin: false);
        Assert.Equal(CheckStatus.Skip, Get(r, "identity").Status);
        var passedOtherwise = OnboardingSummary.Required.Select(id => new CheckResult(id, id, CheckStatus.Pass, "")).Append(Get(r, "identity")).ToArray();
        Assert.Equal(2, OnboardingSummary.Evaluate(passedOtherwise).ExitCode);
    }

    // ---- the phase-1 gate that decides whether a pass is recorded

    private static List<CheckResult> PhaseOne(CheckStatus identity) =>
        OnboardingSummary.Required.Where(id => id != "proxy").Select(id => new CheckResult(id, id, CheckStatus.Pass, ""))
            .Append(new CheckResult("identity", "identity", identity, "")).ToList();

    [Fact]
    public void Phase_one_passes_only_when_every_required_check_but_proxy_passed_including_identity()
    {
        Assert.True(OnboardingSummary.PhaseOnePassed(PhaseOne(CheckStatus.Pass)));
        Assert.False(OnboardingSummary.PhaseOnePassed(PhaseOne(CheckStatus.Skip)));   // fails if 'identity' is dropped from the required checks
        Assert.False(OnboardingSummary.PhaseOnePassed(PhaseOne(CheckStatus.Warn)));
        Assert.False(OnboardingSummary.PhaseOnePassed(PhaseOne(CheckStatus.Pass).Where(r => r.Id != "identity").Append(new CheckResult("identity", "identity", CheckStatus.Fail, "")).ToList()));
    }

    [Theory]
    [InlineData("feed")]
    [InlineData("upstreams")]
    [InlineData("permissions")]
    [InlineData("views")]
    [InlineData("developer")]
    [InlineData("private")]
    public void Phase_one_needs_each_required_check_to_pass(string id)
    {
        foreach (var status in new[] { CheckStatus.Skip, CheckStatus.Warn, CheckStatus.Fail })
            Assert.False(OnboardingSummary.PhaseOnePassed(PhaseOne(CheckStatus.Pass).Select(r => r.Id == id ? r with { Status = status } : r).ToList()));
        Assert.False(OnboardingSummary.PhaseOnePassed(PhaseOne(CheckStatus.Pass).Where(r => r.Id != id).ToList()));   // missing counts as not passed
    }

    [Fact]
    public void Phase_one_ignores_the_proxy_check_and_any_extra_failure_still_blocks()
    {
        Assert.True(OnboardingSummary.PhaseOnePassed(PhaseOne(CheckStatus.Pass).Append(new CheckResult("proxy", "proxy", CheckStatus.Skip, "")).ToList()));
        Assert.False(OnboardingSummary.PhaseOnePassed(PhaseOne(CheckStatus.Pass).Append(new CheckResult("extra", "extra", CheckStatus.Fail, "")).ToList()));
    }

    [Fact]
    public async Task Own_mode_runs_no_identity_check_and_is_judged_as_before()
    {
        var fake = WithPackmoatCollaborator(new JsonArray(Sp(AppId, ObjectId)));
        var o = new OnboardingOptions { Organization = "org", Feed = "pub", FeedsApiBase = "https://feeds.example", GraphApiBase = "https://vssps.example", AdminToken = AdminSecret, PackmoatPrincipal = ObjectId };
        var tenant = new Tenant("acme", "Acme", "https://pkgs.example/", "org/", "https://pkgs.example/x", new TenantPolicy(14));
        var r = await new OnboardingChecks(new HttpClient(fake), new StaticBasicCredentialSource("p", "s"), tenant, o).RunAsync(default);
        Assert.DoesNotContain(r, x => x.Id == "identity");
        Assert.DoesNotContain(fake.Seen, u => u.Contains("serviceprincipals"));
        Assert.Equal(CheckStatus.Pass, Get(r, "permissions").Status);
    }
}
