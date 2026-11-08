using System.Text;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Auth;
using Packmoat.Protocols.NuGet;

namespace Packmoat.Host.Proxy;

/// <summary>The request lifecycle from docs/architecture.md section 2 ("One NuGet request"). Fails closed: anything unrecognised is a 404.</summary>
public sealed class NuGetProxy
{
    private const string ProxyPrefix = "/v3/u/";

    private readonly IUpstreamClient _upstream;
    private readonly IPackageGate _gate;
    private readonly PackmoatOptions _options;
    private readonly IAuditLog _audit;
    private readonly IClock _clock;
    private readonly ILogger<NuGetProxy> _log;

    public NuGetProxy(IUpstreamClient upstream, IPackageGate gate, PackmoatOptions options, IAuditLog audit, IClock clock, ILogger<NuGetProxy> log)
    {
        _upstream = upstream;
        _gate = gate;
        _options = options;
        _audit = audit;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// Records a download (#136: downloads only; listings and metadata are in the decision record). In production the log is a
    /// <see cref="QueuedAuditLog"/>: this queues and returns, so the audit store can neither slow nor fail the download.
    /// </summary>
    private ValueTask AuditDownload(HttpContext ctx, Tenant tenant, string package, string version, Verdict verdict) =>
        _audit.AppendAsync(new AuditEvent(_clock.UtcNow, ctx.TraceIdentifier, tenant.Id, TokenAuthMiddleware.GetTokenId(ctx), AuditKind.Download,
            package, version, verdict.Decision, verdict.Reason, verdict.WaiverId), ctx.RequestAborted);

    public static void Map(WebApplication app, Readiness readiness)
    {
        // "ok <12-char commit>" or "ok unknown" (#166). The health checks only look at the status code, never at this body.
        app.MapGet("/healthz", () => Results.Text("ok " + BuildInfo.Commit));
        // "ready" or "not ready" (#233): the database, its migrations and the master key, read only. Unauthenticated, like /healthz.
        Readiness.Map(app, readiness);
        // Authenticated liveness for the deploy health check: proves the token store answers and a token verifies. No upstream is contacted.
        app.MapGet("/v3/ping", () => Results.NoContent());
        app.MapGet("/v3/index.json", (HttpContext c, NuGetProxy p) => p.HandleAsync(c, null));
        app.MapGet(ProxyPrefix + "{**rest}", (HttpContext c, NuGetProxy p) => p.HandleAsync(c, (c.Request.Path.ToUriComponent() + c.Request.QueryString.ToUriComponent())[ProxyPrefix.Length..]));
    }

    private async Task HandleAsync(HttpContext ctx, string? rest)
    {
        var tenant = TokenAuthMiddleware.GetTenant(ctx);
        var ct = ctx.RequestAborted;

        Uri url;
        if (rest is null)
        {
            if (!TryFeedIndexUrl(tenant, out url)) { await Reply(ctx, 404, "Not found."); return; }
        }
        else if (!TryBuildUpstreamUrl(tenant, rest, out url)) { await Reply(ctx, 404, "Not found."); return; }

        var publicBase = (_options.PublicBaseUrl ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}").TrimEnd('/');
        var map = new UrlMap(tenant.UpstreamBase, publicBase + ProxyPrefix);
        var route = rest is null ? new NuGetRoute(NuGetResourceKind.ServiceIndex) : NuGetRouteParser.Parse(url.AbsolutePath);

        try
        {
            switch (route.Kind)
            {
                case NuGetResourceKind.ServiceIndex:
                    await ServeJson(ctx, tenant, url, map, json => ServiceIndexRewriter.Rewrite(json, map));
                    break;

                case NuGetResourceKind.FlatContainerContent:
                {
                    var verdict = await _gate.DecideAsync(tenant, route.PackageId!, route.Version!, ct);
                    await AuditDownload(ctx, tenant, route.PackageId!, route.Version!, verdict);
                    if (!verdict.IsAllowed) { await Hidden(ctx, tenant, route, verdict); break; }
                    await Stream(ctx, tenant, url);
                    break;
                }

                case NuGetResourceKind.FlatContainerVersions:
                    await ServeJson(ctx, tenant, url, map, async json =>
                    {
                        var verdicts = await _gate.DecideManyAsync(tenant, route.PackageId!, VersionFilters.FlatVersions(json), ct);
                        return VersionFilters.FilterFlat(json, v => IsHidden(verdicts, v), map);
                    });
                    break;

                case NuGetResourceKind.RegistrationIndex:
                case NuGetResourceKind.RegistrationPage:
                    await ServeJson(ctx, tenant, url, map, async json =>
                    {
                        var verdicts = await _gate.DecideManyAsync(tenant, route.PackageId!,
                            VersionFilters.RegistrationVersions(json).Where(v => v.Length > 0).Distinct().ToArray(), ct);
                        return VersionFilters.FilterRegistration(json, v => IsHidden(verdicts, v), map);
                    });
                    break;

                case NuGetResourceKind.RegistrationLeaf:
                {
                    var verdict = await _gate.DecideAsync(tenant, route.PackageId!, route.Version!, ct);
                    if (!verdict.IsAllowed) { await Hidden(ctx, tenant, route, verdict); break; }
                    await ServeJson(ctx, tenant, url, map, json => map.Rewrite(json));
                    break;
                }

                case NuGetResourceKind.CatalogEntry:
                    await ServeJson(ctx, tenant, url, map, json => map.Rewrite(json));
                    break;

                default:
                    _log.LogWarning("Refused unrecognised upstream path for tenant {Tenant}: {Path}", tenant.Id, url.AbsolutePath);
                    await Reply(ctx, 404, "Not found.");
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* client went away */ }
        catch (TemporarilyUnavailableException)
        {
            // Publish dates are unavailable. We still hide (fail closed), but say "try again" (503), not "does not exist" (404).
            _log.LogWarning("Publish dates unavailable for tenant {Tenant} on {Path}", tenant.Id, url.AbsolutePath);
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.Headers.RetryAfter = "30";
                await Reply(ctx, 503, "Publish dates are temporarily unavailable. Try again shortly.");
            }
        }
        catch (Exception ex)
        {
            // Includes a missing upstream identity, an unreachable feed and malformed upstream JSON. Fail closed, say nothing sensitive.
            _log.LogError(ex, "Upstream failure for tenant {Tenant} on {Path}", tenant.Id, url.AbsolutePath);
            if (!ctx.Response.HasStarted) await Reply(ctx, 502, "Upstream feed unavailable.");
        }
    }

    private static bool IsHidden(IReadOnlyDictionary<string, Verdict> verdicts, string version) =>
        VerdictFilter.IsHidden(verdicts, version);

    private async Task Hidden(HttpContext ctx, Tenant tenant, NuGetRoute route, Verdict verdict)
    {
        // An outage is not "hidden by policy": say "try again" (503), still without serving anything.
        if (verdict.Reason == VerdictReason.DateSourceUnavailable) throw new TemporarilyUnavailableException();
        _log.LogInformation("Hid {Package} {Version} for tenant {Tenant}: {Reason}", route.PackageId, route.Version, tenant.Id, verdict.Reason);
        ctx.Response.Headers["X-Packmoat-Reason"] = verdict.Reason.ToString();
        await Reply(ctx, 404, $"Hidden by Packmoat policy ({verdict.Reason}).");
    }

    private async Task ServeJson(HttpContext ctx, Tenant tenant, Uri url, UrlMap map, Func<string, string> transform) =>
        await ServeJson(ctx, tenant, url, map, json => Task.FromResult(transform(json)));

    private async Task ServeJson(HttpContext ctx, Tenant tenant, Uri url, UrlMap map, Func<string, Task<string>> transform)
    {
        await using var upstream = await _upstream.GetAsync(tenant, url, "application/json", ctx.RequestAborted);
        if (!upstream.IsSuccess) { await PassStatus(ctx, tenant, upstream.StatusCode); return; }
        if (upstream.ContentLength > _options.MaxJsonBytes) throw new ResponseTooLargeException(_options.MaxJsonBytes);

        // The limit is enforced while reading: chunked and compressed responses have no trustworthy Content-Length.
        var json = await BoundedText.ReadAsync(upstream.Body, _options.MaxJsonBytes, ctx.RequestAborted);
        var output = await transform(json);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(output, ctx.RequestAborted);
    }

    private async Task Stream(HttpContext ctx, Tenant tenant, Uri url)
    {
        await using var upstream = await _upstream.GetAsync(tenant, url, null, ctx.RequestAborted);
        if (!upstream.IsSuccess) { await PassStatus(ctx, tenant, upstream.StatusCode); return; }
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = upstream.ContentType ?? "application/octet-stream";
        if (upstream.ContentLength is { } length) ctx.Response.ContentLength = length;
        await upstream.Body.CopyToAsync(ctx.Response.Body, ctx.RequestAborted); // streamed, never buffered
    }

    private async Task PassStatus(HttpContext ctx, Tenant tenant, int upstreamStatus)
    {
        // 404 stays 404. Anything else (including 401/403 from our own credential being refused) is a 502 so clients retry.
        if (upstreamStatus == 404) { await Reply(ctx, 404, "Not found."); return; }
        _log.LogError("Upstream answered {Status} for tenant {Tenant}", upstreamStatus, tenant.Id);
        await Reply(ctx, 502, "Upstream feed unavailable.");
    }

    private static Task Reply(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain";
        return ctx.Response.WriteAsync(message);
    }

    /// <summary>
    /// The tenant's feed index (the /v3/index.json request), or false when the tenant has no upstream yet (a self-made organisation
    /// before setup: empty base and feed URL) or its feed URL is not absolute. Nothing is fetched then.
    /// </summary>
    internal static bool TryFeedIndexUrl(Tenant tenant, out Uri url)
    {
        url = null!;
        if (string.IsNullOrEmpty(tenant.UpstreamBase) || !Uri.TryCreate(tenant.UpstreamBase, UriKind.Absolute, out _)) return false;
        return Uri.TryCreate(tenant.UpstreamFeedIndexUrl, UriKind.Absolute, out url!);
    }

    /// <summary>
    /// Only paths under the tenant's own organisation are allowed, with no traversal or encoded separators. A tenant with no upstream yet
    /// (a self-made organisation before setup) gets nothing: an empty base with an empty prefix would otherwise admit any path, including an
    /// absolute URL to another host.
    /// </summary>
    internal static bool TryBuildUpstreamUrl(Tenant tenant, string rest, out Uri url)
    {
        url = null!;
        if (string.IsNullOrEmpty(tenant.UpstreamBase) || !Uri.TryCreate(tenant.UpstreamBase, UriKind.Absolute, out _)) return false;
        var path = rest.Split('?', 2)[0];
        if (path.Length == 0 || path.Contains('\\') || path.Contains('\0') || path.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || path.Contains("%5c", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Split('/').Any(s => s is ".." or "." || s.Equals("%2e%2e", StringComparison.OrdinalIgnoreCase))) return false;
        if (!TenantUrlRules.PathIsUnderPrefix(path, tenant.AllowedPathPrefix)) return false;   // whole segments: "acme" never admits "acme-secret/"
        return Uri.TryCreate(tenant.UpstreamBase + rest, UriKind.Absolute, out url!)
            && url.AbsoluteUri.StartsWith(tenant.UpstreamBase, StringComparison.OrdinalIgnoreCase);
    }
}
