using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Infrastructure;

namespace Packmoat.Host.Tests;

// The fake Azure DevOps and the runs the bad-answer tests use. Shared with the hosted tests (#197), which compile this file in: their
// customer pages read the same wording.
public partial class OnboardingBadResponseTests
{
    private const string AppId = "99999999-8888-4777-8666-555555555555";
    private const string Dir = "11111111-2222-4333-8444-555555555555";
    private const string ObjectId = "0b1ec700-0000-4000-8000-00000000a001";
    private const string AdminSecret = "ADMIN-SECRET-bad-response";
    private const string SignInPage = "<!DOCTYPE html><html><head><title>Azure DevOps Services | Sign In</title></head><body>sign in</body></html>";

    /// <summary>Every settings and Graph call gets <see cref="Bad"/> when set, and a valid JSON answer otherwise.</summary>
    private sealed class FakeAzureDevOps : HttpMessageHandler
    {
        public Func<HttpResponseMessage>? Bad { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!;
            if (url.Host is "feeds.example" or "vssps.example" && Bad is not null) return Task.FromResult(Bad());
            if (url.Host == "vssps.example" && url.AbsolutePath.EndsWith("/_apis/graph/serviceprincipals"))
                return Task.FromResult(Json(new JsonObject { ["value"] = new JsonArray(new JsonObject
                {
                    ["applicationId"] = AppId, ["originId"] = ObjectId, ["domain"] = Dir, ["descriptor"] = "aadsp.x", ["isDeletedInOrigin"] = false,
                }) }));
            if (url.Host == "feeds.example")
            {
                if (url.AbsolutePath.EndsWith("/permissions"))
                    return Task.FromResult(Json(new JsonObject { ["value"] = new JsonArray(new JsonObject
                    {
                        ["role"] = "collaborator", ["displayName"] = "", ["identityDescriptor"] = $"Microsoft.VisualStudio.Services.Claims.AadServicePrincipal;{Dir}\\{ObjectId}",
                    }) }));
                if (url.AbsolutePath.EndsWith("/views")) return Task.FromResult(Json(new JsonObject { ["value"] = new JsonArray() }));
                return Task.FromResult(Json(new JsonObject { ["upstreamSources"] = new JsonArray(new JsonObject { ["location"] = "https://api.nuget.org/v3/index.json" }) }));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(JsonNode body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private static Task<IReadOnlyList<CheckResult>> Run(FakeAzureDevOps fake, bool customer = false) =>
        Run(fake, customer, TestOperator.Support);

    /// <summary>The customer site's wording against an Azure DevOps that answers <paramref name="kind"/>, telling people to ask <paramref name="support"/>.</summary>
    internal static Task<IReadOnlyList<CheckResult>> RunCustomerAsync(string kind, SupportContact support) =>
        Run(new FakeAzureDevOps { Bad = () => Make(kind) }, customer: true, support);

    private static Task<IReadOnlyList<CheckResult>> Run(FakeAzureDevOps fake, bool customer, SupportContact support)
    {
        var o = new OnboardingOptions
        {
            Organization = "org", Project = "proj", Feed = "pub",
            FeedsApiBase = "https://feeds.example", PackagesBase = "https://pkgs.example", GraphApiBase = "https://vssps.example",
            AdminToken = AdminSecret, AdminTokenIsBearer = true, SharedAppClientId = AppId, SharedAppDirectoryId = Dir, RetryWaitCap = TimeSpan.Zero,
        };
        if (customer) { o.CustomerWording = true; o.Support = support; }   // the CLI runs with the default (#119)
        var tenant = new Tenant("acme", "Acme", "https://pkgs.example/", "org/", "https://pkgs.example/org/proj/_packaging/pub/nuget/v3/index.json", new TenantPolicy(14));
        return new OnboardingChecks(new HttpClient(fake), new StaticBasicCredentialSource("packmoat", "SP"), tenant, o).RunAsync(default);
    }

    private static CheckResult Get(IReadOnlyList<CheckResult> r, string id) => r.Single(x => x.Id == id);

    public static TheoryData<string> BadAnswers => new() { "203-html", "203-json", "302", "200-html", "200-not-an-object", "200-empty" };

    private static HttpResponseMessage Make(string kind) => kind switch
    {
        "203-html" => new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation) { Content = new StringContent(SignInPage, Encoding.UTF8, "text/html") },
        // A 203 whose body happens to be a valid JSON object: only the 200-only status rule refuses it.
        "203-json" => new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation)
        {
            Content = new StringContent(new JsonObject { ["value"] = new JsonArray(), ["upstreamSources"] = new JsonArray(new JsonObject { ["location"] = "https://api.nuget.org/v3/index.json" }) }.ToJsonString(), Encoding.UTF8, "application/json"),
        },
        "302" => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://app.vssps.visualstudio.com/_signin?realm=dev.azure.com") } },
        "200-html" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SignInPage, Encoding.UTF8, "text/html") },
        "200-not-an-object" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[1,2,3]", Encoding.UTF8, "application/json") },
        "200-empty" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("", Encoding.UTF8, "application/json") },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
