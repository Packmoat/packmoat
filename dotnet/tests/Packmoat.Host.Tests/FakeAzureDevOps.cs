using System.Collections.Concurrent;
using System.Net;
using System.Text;
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

/// <summary>
/// Stands in for Azure DevOps' API hosts (feeds, vssps, dev.azure.com) for a delegated (bearer) token, in process: Get Feed by name or
/// id, feed settings, paged permissions and service principals, the identities API and Projects - Get. Shapes as seen live in #63 S0
/// (a test organisation). Anything but "Bearer <see cref="Token"/>" is 401. Every request is recorded with its Authorization header.
/// </summary>
public sealed class FakeAzureDevOps : HttpMessageHandler
{
    /// <summary>The Admin's delegated token (the settings check).</summary>
    public const string Token = "SENTINEL-ADO-TOKEN-a9c3f1e07b5d4e2f";
    /// <summary>A developer's delegated token (the developer proof, #64 I2).</summary>
    public const string DeveloperToken = "SENTINEL-DEV-TOKEN-5b1e9c04d7a2";
    /// <summary>Packmoat's own identity's token (the shared app in the customer's directory), sent as the Basic password like the proxy's.</summary>
    public const string PackmoatToken = "SENTINEL-PACKMOAT-TOKEN-0c4f2a";
    public static readonly string PackmoatAuthorization = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("packmoat:" + PackmoatToken));
    public const string SharedApp = "5a2ed000-0000-4000-8000-000000000001";
    public const string SpObjectId = "5e1fa000-1111-4222-8333-444455556666";
    public const string Org = "Contoso";
    public const string ProjectName = "Platform Team";
    public static readonly Guid ProjectId = Guid.Parse("7a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");
    public const string FeedName = "public-packages";
    public static readonly Guid FeedId = Guid.Parse("0f9e8d7c-6b5a-4c3d-8e2f-1a0b9c8d7e6f");
    public const string PcaDescriptor = "Microsoft.TeamFoundation.Identity;S-1-9-1551374245-3000000001-3000000002-3000000003-0-0-0-0-1";
    public const string AdaDescriptor = "Microsoft.IdentityModel.Claims.ClaimsIdentity;contoso.example\\ada@contoso.example";

    public FakeAzureDevOps(string directory) => Directory = directory;

    /// <summary>The directory Packmoat's service principal comes from.</summary>
    public string Directory { get; set; }
    public bool OrgScoped { get; set; }
    public string ReturnedFeedName { get; set; } = FeedName;
    public string ReturnedProjectName { get; set; } = ProjectName;
    public string ProjectVisibility { get; set; } = "private";
    /// <summary>When set, Get Feed by name answers this (203 with an HTML sign-in page, 302 to elsewhere, 404, 401...).</summary>
    public int? GetFeedStatus { get; set; }
    /// <summary>A named administrator (the feed's creator) on the feed, on the permissions list's second page.</summary>
    public bool NamedAdmin { get; set; }
    /// <summary>False: Packmoat's service principal is not in the organisation's list (#119).</summary>
    public bool SharedAppListed { get; set; } = true;
    /// <summary>When set, the feed's permissions list answers this status instead (#119: a check that errors).</summary>
    public int? PermissionsStatus { get; set; }
    public JsonArray Upstreams { get; set; } = new(new JsonObject { ["name"] = "nuget.org", ["protocol"] = "nuget", ["location"] = "https://api.nuget.org/v3/index.json" });
    /// <summary>More entries on the permissions list's first page (a broad group with a role, say).</summary>
    public JsonArray ExtraPermissions { get; } = new();
    public TimeSpan Delay { get; set; }
    /// <summary>A delay on the package host only (Packmoat's probes), so the settings calls stay fast.</summary>
    public TimeSpan PkgsDelay { get; set; }
    /// <summary>Every listed feed and view carries a "url" into this other organisation: Packmoat must never use it.</summary>
    public const string EvilFeedsUrl = "https://feeds.dev.azure.com/evilorg/_apis/packaging/Feeds";
    /// <summary>A request whose path contains this times out the way HttpClient's own Timeout does (TaskCanceledException, not the caller's token).</summary>
    public string? TimeoutOn { get; set; }
    /// <summary>A request whose path contains <see cref="CancelOnPath"/> cancels this source (the caller's token), then waits on its token.</summary>
    public CancellationTokenSource? CancelOn { get; set; }
    public string? CancelOnPath { get; set; }
    public ConcurrentQueue<(Uri Url, string? Authorization)> Seen { get; } = new();

    /// <summary>Another feed of the organisation (#64 I2): what Packmoat's identity gets on its index, its views and its upstreams.</summary>
    public sealed class OtherFeed
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public required string Name { get; init; }
        public Guid? ProjectId { get; init; } = FakeAzureDevOps.ProjectId;
        public int PackmoatStatus { get; set; } = 403;
        /// <summary>Null: Get Feeds leaves upstreamSources out for this feed.</summary>
        public JsonArray? Upstreams { get; set; } = new();
        /// <summary>A null visibility: the views list leaves the field out for that view.</summary>
        public List<(Guid Id, string Name, string? Visibility, int PackmoatStatus)> Views { get; } = new() { (Guid.NewGuid(), "Local", "private", 403) };
        public int ViewsStatus { get; set; } = 200;
    }

    /// <summary>The organisation's other feeds, as Get Feeds lists them (at organisation level, project-scoped ones too).</summary>
    public List<OtherFeed> OtherFeeds { get; } = new() { new OtherFeed { Name = "team-packages" } };
    /// <summary>What Packmoat's own identity gets on the public-only feed's index (the control).</summary>
    public int PackmoatIndexStatus { get; set; } = 200;
    /// <summary>What the developer's token gets on the public-only feed's index.</summary>
    public int DeveloperIndexStatus { get; set; } = 401;
    /// <summary>What the developer's token gets on the organisation's feed list (anything but 200: no list).</summary>
    public int DeveloperListStatus { get; set; } = 200;
    /// <summary>The developer's list includes the public-only feed (they can read it).</summary>
    public bool DeveloperSeesPublicFeed { get; set; }
    /// <summary>The developer's list includes the feeds' upstreamSources (I0 could not confirm a Reader sees them).</summary>
    public bool DeveloperSeesUpstreams { get; set; } = true;
    /// <summary>The Admin's organisation feed list answers this instead (anything but 200: no list).</summary>
    public int AdminListStatus { get; set; } = 200;

    private enum Caller { None, Admin, Developer, Packmoat }

    private static Caller Who(string? authorization) =>
        authorization == "Bearer " + Token ? Caller.Admin
        : authorization == "Bearer " + DeveloperToken ? Caller.Developer
        : authorization == PackmoatAuthorization ? Caller.Packmoat
        : Caller.None;

    private static HttpResponseMessage Json(JsonNode body, string? continuation = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (continuation is not null) r.Headers.Add("x-ms-continuationtoken", continuation);
        return r;
    }

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!;
        Seen.Enqueue((url, request.Headers.Authorization?.ToString()));
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (PkgsDelay > TimeSpan.Zero && url.Host == "pkgs.dev.azure.com") await Task.Delay(PkgsDelay, ct);
        if (CancelOn is { } cancel && CancelOnPath is { } at && url.AbsolutePath.Contains(at, StringComparison.Ordinal))
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
        }
        if (TimeoutOn is { } part && url.AbsolutePath.Contains(part, StringComparison.Ordinal))
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 20 seconds elapsing.", new TimeoutException());
        var who = Who(request.Headers.Authorization?.ToString());
        var seg = url.AbsolutePath.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        if (url.Host == "pkgs.dev.azure.com") return Pkgs(seg, who);
        if (who is not (Caller.Admin or Caller.Developer)) return Status(HttpStatusCode.Unauthorized);
        if (seg.Length < 2 || !seg[0].Equals(Org, StringComparison.OrdinalIgnoreCase)) return Status(HttpStatusCode.NotFound);
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        if (url.Host == "feeds.dev.azure.com" && seg.Length == 4 && seg[1] == "_apis" && seg[2] == "packaging" && seg[3] == "feeds") return FeedList(who);
        if (who == Caller.Developer) return Status(HttpStatusCode.Forbidden);   // a developer may only list feeds
        if (url.Host == "feeds.dev.azure.com" && OtherViews(seg) is { } views) return views;
        return url.Host switch
        {
            "feeds.dev.azure.com" => Feeds(seg, query),
            "vssps.dev.azure.com" => Vssps(seg, query),
            "dev.azure.com" => Core(seg),
            _ => Status(HttpStatusCode.NotFound),
        };
    }

    /// <summary>Get Feeds at organisation level: the public-only feed (unless a developer who cannot see it asks) and every other feed.</summary>
    private HttpResponseMessage FeedList(Caller who)
    {
        var status = who == Caller.Developer ? DeveloperListStatus : AdminListStatus;
        if (status != 200) return Status((HttpStatusCode)status);
        var withUpstreams = who == Caller.Admin || DeveloperSeesUpstreams;
        var list = new JsonArray();
        JsonObject Entry(Guid id, string name, Guid? project, JsonArray? upstreams)
        {
            var o = new JsonObject
            {
                ["id"] = id.ToString("D"), ["name"] = name, ["url"] = $"{EvilFeedsUrl}/{id:D}",   // never followed, never a source of the org
                ["project"] = project is { } p ? new JsonObject { ["id"] = p.ToString("D"), ["name"] = ProjectName, ["visibility"] = "private" } : null,
            };
            if (withUpstreams && upstreams is not null) o["upstreamSources"] = upstreams.DeepClone();
            return o;
        }
        if (who == Caller.Admin || DeveloperSeesPublicFeed)
            list.Add(Entry(FeedId, FeedName, OrgScoped ? null : ProjectId, Upstreams));
        foreach (var f in OtherFeeds) list.Add(Entry(f.Id, f.Name, f.ProjectId, f.Upstreams));
        return Json(new JsonObject { ["count"] = list.Count, ["value"] = list });
    }

    /// <summary>Another feed's views, by GUIDs: {org}[/{projectId}]/_apis/packaging/feeds/{feedId}/views.</summary>
    private HttpResponseMessage? OtherViews(string[] seg)
    {
        var a = Array.IndexOf(seg, "_apis");
        if (a is not (1 or 2) || seg.Length != a + 5 || seg[a + 4] != "views" || !Guid.TryParse(seg[a + 3], out var id)) return null;
        var feed = OtherFeeds.FirstOrDefault(f => f.Id == id);
        if (feed is null) return null;
        if (!(a == 2 ? Guid.TryParse(seg[1], out var p) && p == feed.ProjectId : feed.ProjectId is null)) return Status(HttpStatusCode.NotFound);
        if (feed.ViewsStatus != 200) return Status((HttpStatusCode)feed.ViewsStatus);
        return Json(new JsonObject { ["value"] = new JsonArray(feed.Views.Select(v =>
        {
            var o = new JsonObject { ["id"] = v.Id.ToString("D"), ["name"] = v.Name, ["url"] = $"{EvilFeedsUrl}/{feed.Id:D}/views/{v.Id:D}" };
            if (v.Visibility is not null) o["visibility"] = v.Visibility;
            return (JsonNode)o;
        }).ToArray()) });
    }

    /// <summary>
    /// The package host: {org}[/{projectId}]/_packaging/{feedId}[@{viewId}]/nuget/v3/index.json, by GUIDs. The public-only feed answers
    /// Packmoat's identity <see cref="PackmoatIndexStatus"/> and a developer <see cref="DeveloperIndexStatus"/>; another feed (or view)
    /// answers Packmoat's identity its own status. Everyone else: 401.
    /// </summary>
    private HttpResponseMessage Pkgs(string[] seg, Caller who)
    {
        var p = Array.IndexOf(seg, "_packaging");
        if (seg.Length < 2 || !seg[0].Equals(Org, StringComparison.OrdinalIgnoreCase) || p is not (1 or 2) || seg.Length != p + 5 || string.Join('/', seg[(p + 2)..]) != "nuget/v3/index.json")
            return Status(HttpStatusCode.NotFound);
        var feedPart = seg[p + 1].Split('@');
        if (!Guid.TryParse(feedPart[0], out var feedId)) return Status(HttpStatusCode.NotFound);
        Guid? project = p == 2 && Guid.TryParse(seg[1], out var pid) ? pid : null;
        if (feedId == FeedId && feedPart.Length == 1 && project == (OrgScoped ? null : ProjectId))
            return Status(who switch
            {
                Caller.Packmoat => (HttpStatusCode)PackmoatIndexStatus,
                Caller.Developer => (HttpStatusCode)DeveloperIndexStatus,
                Caller.Admin => HttpStatusCode.OK,
                _ => HttpStatusCode.Unauthorized,
            });
        var other = OtherFeeds.FirstOrDefault(f => f.Id == feedId && f.ProjectId == project);
        if (other is null || who != Caller.Packmoat) return Status(other is null ? HttpStatusCode.NotFound : HttpStatusCode.Unauthorized);
        if (feedPart.Length == 1) return Status((HttpStatusCode)other.PackmoatStatus);
        return Guid.TryParse(feedPart[1], out var viewId) && other.Views.FirstOrDefault(v => v.Id == viewId) is { Name: not null } view
            ? Status((HttpStatusCode)view.PackmoatStatus) : Status(HttpStatusCode.NotFound);
    }

    private bool IsProject(string? p) => OrgScoped ? p is null : p is not null && (p.Equals(ProjectName, StringComparison.OrdinalIgnoreCase) || (Guid.TryParse(p, out var g) && g == ProjectId));

    private HttpResponseMessage Feeds(string[] seg, System.Collections.Specialized.NameValueCollection query)
    {
        var a = Array.IndexOf(seg, "_apis");
        if (a is not (1 or 2) || seg.Length < a + 4 || seg[a + 1] != "packaging" || seg[a + 2] != "feeds") return Status(HttpStatusCode.NotFound);
        if (!IsProject(a == 2 ? seg[1] : null)) return Status(HttpStatusCode.NotFound);
        var feed = seg[a + 3];
        var byId = Guid.TryParse(feed, out var id) && id == FeedId;
        if (!byId && !feed.Equals(FeedName, StringComparison.OrdinalIgnoreCase)) return Status(HttpStatusCode.NotFound);
        var tail = seg.Length > a + 4 ? seg[a + 4] : null;
        if (tail is null)
        {
            if (!byId && GetFeedStatus is { } code)
            {
                var r = Status((HttpStatusCode)code);
                if (code == 203) r.Content = new StringContent("<html><body>Sign in to Azure DevOps</body></html>", Encoding.UTF8, "text/html");
                if (code is >= 300 and < 400) r.Headers.Location = new Uri("https://evil.example/collect");
                return r;
            }
            return Json(new JsonObject
            {
                ["id"] = FeedId.ToString("D"), ["name"] = ReturnedFeedName,
                ["url"] = "https://evil.example/_apis/packaging/feeds/never-used",
                ["project"] = OrgScoped ? null : new JsonObject { ["id"] = ProjectId.ToString("D"), ["name"] = ReturnedProjectName, ["visibility"] = ProjectVisibility },
                ["upstreamSources"] = Upstreams.DeepClone(),
            });
        }
        if (tail == "views") return Json(new JsonObject { ["value"] = new JsonArray(new JsonObject { ["name"] = "Local", ["visibility"] = "private" }) });
        if (tail != "permissions") return Status(HttpStatusCode.NotFound);
        if (PermissionsStatus is { } refused) return Status((HttpStatusCode)refused);
        // Page 1: Packmoat (collaborator) and the organisation's built-in admin group; page 2: the named admin, when there is one.
        static JsonObject Perm(string role, string name, string descriptor) => new() { ["role"] = role, ["displayName"] = name, ["identityDescriptor"] = descriptor, ["isInheritedRole"] = false };
        if (query["continuationToken"] == "p2")
            return Json(new JsonObject { ["value"] = NamedAdmin ? new JsonArray(Perm("administrator", "Ada Lovelace", AdaDescriptor)) : new JsonArray() });
        return Json(new JsonObject
        {
            ["value"] = new JsonArray(new JsonNode?[]
            {
                Perm("collaborator", "Packmoat feed access", $"Microsoft.VisualStudio.Services.Claims.AadServicePrincipal;{Directory}\\{SpObjectId}"),
                Perm("administrator", "[contoso]\\Project Collection Administrators", PcaDescriptor),
            }.Concat(ExtraPermissions.Select(e => e?.DeepClone())).ToArray()),
        }, continuation: "p2");
    }

    private HttpResponseMessage Vssps(string[] seg, System.Collections.Specialized.NameValueCollection query)
    {
        var path = string.Join('/', seg.Skip(1));
        if (path == "_apis/graph/serviceprincipals")
        {
            // Packmoat's app is on the second page, so the check must follow the continuation token to find it.
            if (query["continuationToken"] is null)
                return Json(new JsonObject { ["value"] = new JsonArray(Sp("aaaaaaaa-0000-4000-8000-000000000001", "aaaaaaaa-0000-4000-8000-0000000000aa")) }, continuation: "1");
            return Json(new JsonObject { ["value"] = SharedAppListed ? new JsonArray(Sp(SharedApp, SpObjectId)) : new JsonArray() });
        }
        if (path == "_apis/identities")
        {
            var found = new JsonArray();
            if (string.Equals(query["descriptors"], PcaDescriptor, StringComparison.Ordinal))
                found.Add(Identity(PcaDescriptor, "[contoso]\\Project Collection Administrators", "AdministrativeApplicationGroup", "contoso",
                    "vstfs:///Framework/IdentityDomain/5c1e0000-0000-4000-8000-000000000000", "Project Collection Administrators"));
            return Json(new JsonObject { ["count"] = found.Count, ["value"] = found });
        }
        return Status(HttpStatusCode.NotFound);
    }

    private JsonObject Sp(string appId, string originId) => new()
    {
        ["subjectKind"] = "servicePrincipal", ["applicationId"] = appId, ["origin"] = "aad", ["originId"] = originId, ["domain"] = Directory,
        ["displayName"] = "sp", ["descriptor"] = "aadsp." + originId[..8], ["isDeletedInOrigin"] = false,
    };

    private static JsonObject Identity(string descriptor, string displayName, string specialType, string scopeName, string domain, string account)
    {
        static JsonObject P(string v) => new() { ["$type"] = "System.String", ["$value"] = v };
        return new JsonObject
        {
            ["descriptor"] = descriptor, ["providerDisplayName"] = displayName, ["isContainer"] = true, ["isActive"] = true,
            ["properties"] = new JsonObject { ["SpecialType"] = P(specialType), ["ScopeName"] = P(scopeName), ["Domain"] = P(domain), ["Account"] = P(account) },
        };
    }

    private HttpResponseMessage Core(string[] seg)
    {
        if (seg.Length == 4 && seg[1] == "_apis" && seg[2] == "projects" && !OrgScoped && IsProject(seg[3]))
            return Json(new JsonObject { ["id"] = ProjectId.ToString("D"), ["name"] = ProjectName, ["visibility"] = ProjectVisibility });
        return Status(HttpStatusCode.NotFound);
    }
}

/// <summary>Stands in for Entra: hands out <see cref="FakeAzureDevOps.PackmoatToken"/> and records which directory and app asked.</summary>
public sealed class FakePackmoatCredential : Azure.Core.TokenCredential
{
    public ConcurrentQueue<AppSignIn> SignIns { get; } = new();
    public bool Fail { get; set; }

    public Func<AppSignIn, Azure.Core.TokenCredential> Factory => signIn => { SignIns.Enqueue(signIn); return this; };

    public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        Fail ? throw new InvalidOperationException("sign-in failed") : new(FakeAzureDevOps.PackmoatToken, DateTimeOffset.UtcNow.AddHours(1));

    public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}

/// <summary>One active shared app credential, in memory.</summary>
public sealed class OneSharedCredential : ISharedAppCredentialStore
{
    public SharedAppCredential? Active { get; set; } = new(FakeAzureDevOps.SharedApp, "-----BEGIN PRIVATE KEY-----\nTEST\n-----END PRIVATE KEY-----");
    public Task SetAsync(SharedAppSlot slot, SharedAppCredential credential, CancellationToken ct) => throw new NotSupportedException();
    public Task<SharedAppCredential?> GetAsync(SharedAppSlot slot, CancellationToken ct) => Task.FromResult(slot == SharedAppSlot.Active ? Active : null);
    public Task<bool> PromoteAsync(Action<SharedAppCredential> validate, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> RecordVerificationAsync(SharedAppVerification v, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> DeleteVerificationAsync(SharedAppVerification v, CancellationToken ct) => throw new NotSupportedException();
    public Task<SharedAppVerification?> GetVerificationAsync(string tenantId, CancellationToken ct) => Task.FromResult<SharedAppVerification?>(null);
}
