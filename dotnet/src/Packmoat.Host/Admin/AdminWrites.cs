using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Modules;
using Packmoat.Host.Web;
using static Packmoat.Host.Admin.AdminHtml;

namespace Packmoat.Host.Admin;

/// <summary>Slows write POSTs from one address: at most <c>max</c> per window (a fixed window), then 429. Separate from the sign-in and proxy limiters.</summary>
public sealed class AdminWriteLimiter
{
    private readonly IClock _clock;
    private readonly int _max;
    private readonly TimeSpan _window;
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _byAddress = new();
    private readonly object _lock = new();

    public AdminWriteLimiter(IClock clock, int max = 30, TimeSpan? window = null)
    {
        _clock = clock;
        _max = max;
        _window = window ?? TimeSpan.FromMinutes(1);
    }

    /// <summary>Counts this attempt. Returns null when it may proceed, otherwise the seconds to wait.</summary>
    public int? TryAcquire(string address)
    {
        var now = _clock.UtcNow;
        lock (_lock)
        {
            if (!_byAddress.TryGetValue(address, out var e) || now - e.Start >= _window) e = (now, 0);
            if (e.Count >= _max) { _byAddress[address] = e; return Math.Max(1, (int)Math.Ceiling((e.Start + _window - now).TotalSeconds)); }
            _byAddress[address] = (e.Start, e.Count + 1);
            if (_byAddress.Count > 20_000)
                foreach (var k in _byAddress.Where(kv => now - kv.Value.Start >= _window).Select(kv => kv.Key).ToList()) _byAddress.Remove(k);
            return null;
        }
    }
}

/// <summary>
/// The admin site's own single-use nonces for "Issue token" forms, keyed by tenant id (see <see cref="Packmoat.Host.Web.FormNonces"/>).
/// Never shared with the customer site.
/// </summary>
public sealed class AdminFormNonces : Packmoat.Host.Web.FormNonces
{
    public AdminFormNonces(IClock clock) : base(clock) { }
}

/// <summary>
/// The write actions of the admin site: thin handlers over <see cref="AdminCommands"/>. Every one is a POST that has already passed the
/// antiforgery gate and the allowlist; here the Origin is checked, the rate limit applied, every field validated, and the action recorded.
/// Who acts is taken from the signed-in identity only (<see cref="ActorFor"/>), never from a form field, and always as the operator scope.
/// </summary>
public static class AdminWrites
{
    /// <summary>
    /// Canned messages, chosen by a short key in the redirect (<c>?r=key</c>). The key is looked up, never printed: nothing from the request
    /// is ever reflected into a page, so there is nothing to encode wrongly. An unknown key shows nothing.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (bool Ok, string Text)> Messages = new Dictionary<string, (bool, string)>
    {
        ["created"] = (true, "Tenant created. It has no upstream identity or certificate yet: those are set with the packmoat-admin command line only."),
        ["prefix-added"] = (true, "Internal prefix added."),
        ["prefix-removed"] = (true, "Internal prefix removed."),
        ["override-set"] = (true, "Package override saved."),
        ["override-removed"] = (true, "Package override removed."),
        ["waiver-added"] = (true, "Waiver added."),
        ["waiver-revoked"] = (true, "Waiver revoked."),
        ["suspended"] = (true, "Tenant suspended. Its tokens stop working within a few seconds."),
        ["activated"] = (true, "Tenant activated."),
        ["cooldown-set"] = (true, "Cooldown saved."),
        ["token-revoked"] = (true, "Token revoked. Running servers notice within 5 seconds."),
        ["bad-id"] = (false, "Tenant id must be 1 to 63 characters: lower-case letters, digits, hyphen or underscore, starting with a letter or digit."),
        ["bad-name"] = (false, "Name is required (up to 100 characters, no line breaks)."),
        ["bad-upstream"] = (false, "Upstream base must be an https URL on the standard port with no user name, query, fragment, % or backslash (up to 200 characters)."),
        ["bad-path-prefix"] = (false, "Path prefix may use letters, digits, dot, hyphen, underscore and slash only, with no \"..\" (up to 200 characters)."),
        ["upstream-host"] = (false, "Upstream host is not allowed. It must be a host name (not an IP address or localhost) on the allowed list (PACKMOAT_ADMIN_UPSTREAM_HOSTS, default pkgs.dev.azure.com), https on the standard port."),
        ["feed-outside"] = (false, "Feed URL must start with the upstream base followed by the path prefix."),
        ["reload"] = (false, "That form was already used or has expired, so nothing was done (this stops a reload from issuing a second token). Open the tenant page again and retry."),
        ["bad-feed"] = (false, "Feed URL must be an https URL on the standard port with no user name, query, fragment, % or backslash (up to 300 characters)."),
        ["bad-cooldown"] = (false, "Cooldown must be a whole number of days from 0 to 3650."),
        ["bad-days"] = (false, "Days must be a whole number from 1 to 3650."),
        ["bad-package"] = (false, "Package id may use letters, digits, dot, hyphen and underscore only (up to 100 characters)."),
        ["bad-prefix"] = (false, "Prefix needs at least 2 letters, digits, dot, hyphen or underscore, with one optional * at the end (up to 100 characters)."),
        ["bad-version"] = (false, "Version must be * or a version such as 1.2.3 or 1.2.3-beta.1."),
        ["bad-reason"] = (false, "A reason is required (up to 200 characters, no line breaks)."),
        ["bad-label"] = (false, "A label is required (up to 100 characters, no line breaks)."),
        ["bad-kind"] = (false, "Kind must be developer or automation."),
        ["bad-token-id"] = (false, "That is not a token id."),
        ["bad-waiver-id"] = (false, "That is not a waiver id."),
        ["waiver-duplicate"] = (false, "That package and version already has an active waiver. Revoke it first to change it. Nothing was changed."),
        ["bad-form"] = (false, "The form was not understood. Nothing was changed."),
        ["no-tenant"] = (false, "No such tenant. Nothing was changed."),
        ["exists"] = (false, "A tenant with that id already exists. Nothing was changed."),
        ["not-found"] = (false, "That item is not on this tenant (it may already be gone). Nothing was changed."),
        ["trail-failed"] = (false, "The action was NOT done: the admin action trail could not be written."),
        ["failed"] = (false, "The action failed. Check the attempt on the Actions page."),
    };

    /// <summary>The notice for <paramref name="key"/>: the core's, else the admin-site module's (#193). An unknown key shows nothing.</summary>
    internal static string Notice(HttpContext ctx, string? key) =>
        key is not null && (Messages.TryGetValue(key, out var m) || ctx.RequestServices.GetRequiredService<IAdminSiteModule>().Notices.TryGetValue(key, out m))
            ? $"<p class=\"{(m.Ok ? "ok" : "err")}\" role=\"status\">{E(m.Text)}</p>" : "";

    /// <summary>
    /// The one place an <see cref="ActorScope"/> is made for the admin site: the operator, named after the signed-in person
    /// (display name, or the object id when there is none). Never a tenant scope, never a form field.
    /// </summary>
    public static ActorScope ActorFor(ClaimsPrincipal who)
    {
        var name = who.FindFirst("name")?.Value;
        var oid = who.FindFirst(AdminAllowlist.ObjectClaim)?.Value;
        return ActorScope.OperatorAs(string.IsNullOrWhiteSpace(name) ? oid ?? "unknown" : name);
    }

    // ---------------------------------------------------------------- views

    internal static string Field(string name, string label, int max, string type = "text", bool required = true, string? hint = null, string? value = null) =>
        $"<label>{E(label)} <input type=\"{type}\" name=\"{E(name)}\" maxlength=\"{max}\"{(required ? " required" : "")}{(value is null ? "" : $" value=\"{E(value)}\"")}></label>" +
        (hint is null ? "" : $"<span class=\"muted\"> {E(hint)}</span>");

    internal static string PostForm(string action, string af, string fields, string button, string css = "stack") =>
        $"<form method=\"post\" action=\"{E(action)}\" class=\"{css}\">{Hidden(AdminSite.AntiforgeryField, af)}{fields}<button type=\"submit\">{E(button)}</button></form>";

    internal static string TenantAction(string tenantId, string verb) => "/tenants/" + Uri.EscapeDataString(tenantId) + "/" + verb;

    internal static string CreateTenantForm(string af) =>
        "<h2>Create a tenant</h2><p class=\"muted\">Creates the tenant record only. The upstream identity and certificate are not handled here: use <code>packmoat-admin identity</code> on the server.</p>" +
        PostForm("/tenant-create", af,
            Field("id", "Id", 63, hint: "lower-case letters, digits, - and _") + "<br>" +
            Field("name", "Name", AdminInput.MaxName) + "<br>" +
            Field("upstream_base", "Upstream base", 200, "url", hint: "full address including the https scheme; only allowed hosts (default pkgs.dev.azure.com)") + "<br>" +
            Field("path_prefix", "Path prefix", 200, required: false, hint: "for example my-org/") + "<br>" +
            Field("feed", "Feed index URL", 300, "url", hint: "the feed's v3 index.json, under the upstream base and path prefix") + "<br>" +
            Field("cooldown", "Cooldown days", 4, value: "14"), "Create tenant");

    // ---------------------------------------------------------------- handling

    /// <summary>Origin must be this site (the rule every site shares, <see cref="SignedInSite.OriginOk"/>).</summary>
    internal static bool OriginOk(HttpContext ctx, AdminSettings s) => SignedInSite.OriginOk(ctx, s.Host);

    private sealed record Plan(string Action, string TenantId, string Detail, Func<ActorScope, Task<bool>> Run, string OkKey, string[][] Fields,
        string? ConfirmText = null);

    private static string? Form(IFormCollection f, string name) => f.TryGetValue(name, out var v) && v.Count == 1 ? v[0] : null;

    /// <summary>True when the request was a write route (answered here, whatever the outcome).</summary>
    internal static async Task<bool> HandleAsync(HttpContext ctx, AdminSettings s, ClaimsPrincipal who)
    {
        var path = ctx.Request.Path.Value ?? "";
        var module = ctx.RequestServices.GetRequiredService<IAdminSiteModule>();
        string? tenantId = null, verb = null;
        AdminTenantAction? moduleAction = null;
        if (path == "/tenant-create") verb = "create";
        else if (path.StartsWith("/tenants/", StringComparison.Ordinal))
        {
            var parts = path["/tenants/".Length..].Split('/');
            if (parts.Length == 2 && parts[0].Length is > 0 and <= 200)
            {
                if (Verbs.Contains(parts[1])) { tenantId = parts[0]; verb = parts[1]; }
                else if (module.Actions.FirstOrDefault(a => a.Verb == parts[1]) is { } action) { tenantId = parts[0]; verb = parts[1]; moduleAction = action; }
            }
        }
        if (verb is null) return false;

        if (!OriginOk(ctx, s)) { await AdminSite.ErrorPage(ctx, 403, "Request refused", "The request did not come from this site."); return true; }
        if (ctx.RequestServices.GetRequiredService<AdminWriteLimiter>().TryAcquire(Packmoat.Host.Auth.ClientAddress.Key(ctx)) is { } wait)
        {
            await AdminSite.ErrorPage(ctx, 429, "Too many requests", "Too many changes from this address. Wait a minute.", wait);
            return true;
        }

        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var queries = ctx.RequestServices.GetRequiredService<AdminQueries>();
        var actor = ActorFor(who);

        if (verb == "create")
        {
            var (plan, err) = await PlanCreateAsync(ctx, s, form, queries, actor);
            if (plan is null) { Redirect(ctx, null, err!); return true; }
            await ExecuteAsync(ctx, who, plan);
            return true;
        }

        var tenant = await queries.GetTenantAsync(actor, tenantId!, ctx.RequestAborted);
        if (tenant is null) { Redirect(ctx, null, "no-tenant"); return true; }

        if (moduleAction is not null) { await RunModuleActionAsync(ctx, who, form, tenant, module, moduleAction); return true; }
        if (verb == "token-issue") { await IssueTokenAsync(ctx, who, form, tenant); return true; }

        var tokens = verb == "token-revoke" ? await queries.ListTokensAsync(actor, tenant.Id, ctx.RequestAborted) : null;
        var p = BuildPlan(ctx, form, tenant, tokens, verb, out var error);
        if (p is null) { Redirect(ctx, tenant.Id, error!); return true; }
        if (p.ConfirmText is not null && Form(form, "confirm") != "yes")
        {
            await ConfirmPageAsync(ctx, p.TenantId, verb, p.ConfirmText, p.Fields.Select(x => (x[0], x[1])));
            return true;
        }
        await ExecuteAsync(ctx, who, p);
        return true;
    }

    /// <summary>Below this many days a cooldown (tenant or per package) is treated as switching protection off, and asks for confirmation.</summary>
    public const int MinSaneCooldown = 3;
    /// <summary>A waiver this long, or one for every version, asks for confirmation.</summary>
    public const int LongWaiverDays = 365;

    private static readonly HashSet<string> Verbs = new(StringComparer.Ordinal)
    {
        "prefix-add", "prefix-remove", "override-set", "override-remove", "waiver-add", "waiver-revoke", "suspend", "activate", "cooldown", "token-issue", "token-revoke",
    };

    /// <summary>
    /// Refuses the start when the admin-site module's verbs or notice keys collide with the core's or repeat (#193): a module may add to
    /// the site, never shadow what the core does.
    /// </summary>
    internal static void Validate(IAdminSiteModule module)
    {
        var verbs = module.Actions.Select(a => a.Verb).ToList();
        if (verbs.FirstOrDefault(v => v == "create" || Verbs.Contains(v)) is { } coreVerb)
            throw new InvalidOperationException($"The admin-site module's action \"{coreVerb}\" is one of the core's. Refusing to start.");
        if (verbs.GroupBy(v => v, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new InvalidOperationException($"The admin-site module lists the action \"{twice.Key}\" more than once. Refusing to start.");
        if (module.Notices.Keys.FirstOrDefault(Messages.ContainsKey) is { } coreKey)
            throw new InvalidOperationException($"The admin-site module's notice \"{coreKey}\" is one of the core's. Refusing to start.");
    }

    private static async Task<(Plan?, string?)> PlanCreateAsync(HttpContext ctx, AdminSettings settings, IFormCollection f, AdminQueries queries, ActorScope actor)
    {
        string? err;
        var id = AdminInput.Id(Form(f, "id"), out err); if (id is null) return (null, err);
        var name = AdminInput.Name(Form(f, "name"), out err); if (name is null) return (null, err);
        var upstream = AdminInput.Upstream(Form(f, "upstream_base"), settings.UpstreamHosts, out err); if (upstream is null) return (null, err);
        var path = AdminInput.AllowedPath(Form(f, "path_prefix"), out err); if (path is null) return (null, err);
        var feed = AdminInput.Feed(Form(f, "feed"), upstream + path, out err); if (feed is null) return (null, err);
        var cooldown = AdminInput.Days(Form(f, "cooldown"), 0, AdminInput.MaxDays, "bad-cooldown", out err); if (cooldown is null) return (null, err);
        if (await queries.GetTenantAsync(actor, id, ctx.RequestAborted) is not null) return (null, "exists");
        var tenant = new NewTenant(id, name, upstream, path, feed, cooldown.Value, TenantOrigin.Operator);
        return (new Plan("tenant.create", id, $"name={name}; upstream={upstream}; path_prefix={path}; feed={feed}; cooldown_days={cooldown}",
            a => Run(ctx, c => c.CreateTenantAsync(a, tenant, CancellationToken.None)), "created", Array.Empty<string[]>()), null);
    }

    private static Task<bool> Run(HttpContext ctx, Func<AdminCommands, Task> change) => RunCore(ctx, async c => { await change(c); return true; });
    private static async Task<bool> RunCore(HttpContext ctx, Func<AdminCommands, Task<bool>> change) => await change(ctx.RequestServices.GetRequiredService<AdminCommands>());

    private static Plan? BuildPlan(HttpContext ctx, IFormCollection f, Tenant t, IReadOnlyList<TokenInfo>? tokens, string verb, out string? err)
    {
        err = null;
        var id = t.Id;
        switch (verb)
        {
            case "prefix-add":
            {
                var prefix = AdminInput.InternalPrefix(Form(f, "prefix"), out err); if (prefix is null) return null;
                return new Plan("prefix.add", id, $"prefix={prefix}", a => Run(ctx, c => c.AddPrefixAsync(a, id, prefix, CancellationToken.None)), "prefix-added", new[] { new[] { "prefix", prefix } });
            }
            case "prefix-remove":
            {
                var prefix = AdminInput.InternalPrefix(Form(f, "prefix"), out err); if (prefix is null) return null;
                if (!t.Policy.InternalPrefixes.Contains(prefix)) { err = "not-found"; return null; }
                return new Plan("prefix.remove", id, $"prefix={prefix}", a => Run(ctx, c => c.RemovePrefixAsync(a, id, prefix, CancellationToken.None)), "prefix-removed",
                    new[] { new[] { "prefix", prefix } }, $"remove the internal prefix \"{prefix}\" from {t.Name}. Packages with that prefix will go through the cooldown again");
            }
            case "override-set":
            {
                var package = AdminInput.Package(Form(f, "package"), out err); if (package is null) return null;
                var days = AdminInput.Days(Form(f, "days"), 0, AdminInput.MaxDays, "bad-cooldown", out err); if (days is null) return null;
                return new Plan("override.set", id, $"package={package.ToLowerInvariant()}; days={days}", a => Run(ctx, c => c.AddOverrideAsync(a, id, package, days.Value, CancellationToken.None)), "override-set",
                    new[] { new[] { "package", package }, new[] { "days", days.Value.ToString() } },
                    days < MinSaneCooldown ? $"set the cooldown for {package} on {t.Name} to {days} days. New versions of that package will be served almost at once" : null);
            }
            case "override-remove":
            {
                var package = AdminInput.Package(Form(f, "package"), out err); if (package is null) return null;
                if (!t.Policy.PackageOverrides.Any(o => string.Equals(o.PackageId, package, StringComparison.OrdinalIgnoreCase))) { err = "not-found"; return null; }
                return new Plan("override.remove", id, $"package={package.ToLowerInvariant()}", a => Run(ctx, c => c.RemoveOverrideAsync(a, id, package, CancellationToken.None)), "override-removed",
                    new[] { new[] { "package", package } }, $"remove the cooldown override for {package} on {t.Name}. The tenant's default cooldown will apply to it");
            }
            case "waiver-add":
            {
                var package = AdminInput.Package(Form(f, "package"), out err); if (package is null) return null;
                var version = AdminInput.WaiverVersion(Form(f, "version"), out err); if (version is null) return null;
                var days = AdminInput.Days(Form(f, "days"), AdminCommands.MinWaiverDays, AdminCommands.MaxWaiverDays, "bad-days", out err); if (days is null) return null;
                var reason = AdminInput.Reason(Form(f, "reason"), out err); if (reason is null) return null;
                return new Plan("waiver.add", id, $"package={package.ToLowerInvariant()}; version={version}; days={days}; reason={reason}",
                    a => RunCore(ctx, async c => { await c.AddWaiverAsync(a, id, package, version, days.Value, reason, CancellationToken.None); return true; }), "waiver-added",
                    new[] { new[] { "package", package }, new[] { "version", version }, new[] { "days", days.Value.ToString() }, new[] { "reason", reason } },
                    version.Contains('*') || days >= LongWaiverDays
                        ? $"waive the cooldown for {package} {version} on {t.Name} for {days} days. {(version.Contains('*') ? "It covers every version" : "It is a long waiver")}" : null);
            }
            case "waiver-revoke":
            {
                var wid = AdminInput.WaiverId(Form(f, "waiver"), out err); if (wid is null) return null;
                var w = t.Policy.Waivers.FirstOrDefault(x => x.Id == wid);
                if (w is null) { err = "not-found"; return null; }
                return new Plan("waiver.revoke", id, $"waiver={wid}; package={w.PackageId}; version={w.Version}", a => RunCore(ctx, c => c.RevokeWaiverAsync(a, wid, CancellationToken.None)), "waiver-revoked",
                    new[] { new[] { "waiver", wid } }, $"revoke waiver {wid} ({w.PackageId} {w.Version}) on {t.Name}. That version is held back again");
            }
            case "suspend":
                return new Plan("tenant.suspend", id, "active=false", a => Run(ctx, c => c.SuspendTenantAsync(a, id, CancellationToken.None)), "suspended",
                    Array.Empty<string[]>(), $"suspend {t.Name}. Every token of this tenant stops working within a few seconds");
            case "activate":
                return new Plan("tenant.activate", id, "active=true", a => Run(ctx, c => c.ActivateTenantAsync(a, id, CancellationToken.None)), "activated", Array.Empty<string[]>(),
                    $"activate {t.Name}. Its tokens will work again");
            case "cooldown":
            {
                var days = AdminInput.Days(Form(f, "days"), 0, AdminInput.MaxDays, "bad-cooldown", out err); if (days is null) return null;
                return new Plan("tenant.cooldown", id, $"days={days}", a => Run(ctx, c => c.SetCooldownAsync(a, id, days.Value, CancellationToken.None)), "cooldown-set", new[] { new[] { "days", days.Value.ToString() } },
                    days < MinSaneCooldown ? $"set the cooldown of {t.Name} to {days} days. New versions will be served almost at once" : null);
            }
            case "token-revoke":
            {
                var tid = AdminInput.TokenId(Form(f, "token"), out err); if (tid is null) return null;
                // Only an active token of THIS tenant: the operator's revoke takes a bare id, so the tenant is checked here.
                var info = tokens?.FirstOrDefault(k => k.TokenId == tid && k.RevokedAt is null);
                if (info is null) { err = "not-found"; return null; }
                return new Plan("token.revoke", id, $"token={tid}; kind={info.Kind}; label={CustomerTokenRules.Quote(info.Label)}", a => RunCore(ctx, c => c.RevokeTokenAsync(a, tid, CancellationToken.None)), "token-revoked",
                    new[] { new[] { "token", tid } }, $"revoke token {tid} (\"{info.Label}\") of {t.Name}. Whoever uses it will be refused");
            }
        }
        err = "bad-form";
        return null;
    }

    private static async Task ConfirmPageAsync(HttpContext ctx, string tenantId, string verb, string confirmText, IEnumerable<(string Name, string Value)> repost)
    {
        var af = ctx.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(ctx).RequestToken!;
        var fields = string.Concat(repost.Select(x => Hidden(x.Name, x.Value))) + Hidden("confirm", "yes");
        var body = $"<div class=\"box\"><h1>Please confirm</h1><p>You are about to {E(confirmText)}.</p>" +
            PostForm(TenantAction(tenantId, verb), af, fields, "Yes, do it", "stack") +
            $"<p><a href=\"/tenants/{E(Uri.EscapeDataString(tenantId))}\">Cancel</a></p></div>";
        await AdminSite.Write(ctx, 200, Page("Confirm", body, AdminSite.SignOutForm(ctx)));
    }

    /// <summary>
    /// A module's action, after every check of the core (#193): the confirm page first when the action asks for one (re-posting the
    /// form's own single-valued fields), then its handler. A trail that could not be written is "trail-failed": nothing was done.
    /// </summary>
    private static async Task RunModuleActionAsync(HttpContext ctx, ClaimsPrincipal who, IFormCollection form, Tenant tenant, IAdminSiteModule module, AdminTenantAction action)
    {
        var page = new AdminTenantPage(ctx, who, tenant, form, module);
        if (action.ConfirmText is { } confirm && Form(form, "confirm") != "yes")
        {
            var repost = form.Where(kv => kv.Key != AdminSite.AntiforgeryField && kv.Key != "confirm" && kv.Value.Count == 1).Select(kv => (kv.Key, kv.Value[0] ?? ""));
            await ConfirmPageAsync(ctx, tenant.Id, action.Verb, confirm(page), repost);
            return;
        }
        try { await action.Handle(page); }
        catch (AdminTrailException) { Redirect(ctx, tenant.Id, "trail-failed"); }
    }

    /// <summary>Redirects to the tenant page (or the list) with a canned message key. No text from the request goes in the URL.</summary>
    internal static void Redirect(HttpContext ctx, string? tenantId, string key)
    {
        ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
        ctx.Response.Headers.Location = (tenantId is null ? "/tenants" : "/tenants/" + Uri.EscapeDataString(tenantId)) + "?r=" + Uri.EscapeDataString(key);
    }

    /// <summary>
    /// Trail first, then the change (write-ahead), so a failure to write the trail stops the action before anything changed (fails closed).
    /// If the change then fails, a ".failed" row is appended so the trail never claims more than was attempted.
    /// </summary>
    private static async Task ExecuteAsync(HttpContext ctx, ClaimsPrincipal who, Plan p)
    {
        var actor = ActorFor(who);
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Packmoat.Admin");
        var tenantForRedirect = p.TenantId;

        if (!await TryAppendAsync(ctx, who, p.Action, p.TenantId, p.Detail))
        {
            Redirect(ctx, p.Action == "tenant.create" ? null : tenantForRedirect, "trail-failed");
            return;
        }

        var failure = "failed";
        var done = false;
        try
        {
            done = await p.Run(actor);
            if (!done) failure = "not-found";
        }
        catch (KeyNotFoundException) { failure = "no-tenant"; }
        catch (WaiverRefusedException ex) when (ex.Reason == WaiverRefusal.Duplicate) { failure = "waiver-duplicate"; }
        catch (Exception ex)
        {
            logger.LogError("Admin action {Action} failed ({Kind})", p.Action, ex.GetType().Name);   // never the exception text
        }
        if (!done)
        {
            await TryAppendAsync(ctx, who, p.Action + ".failed", p.TenantId, p.Detail);
            Redirect(ctx, p.Action == "tenant.create" ? null : tenantForRedirect, failure);
            return;
        }
        Redirect(ctx, tenantForRedirect, p.OkKey);
    }

    private static async Task<bool> TryAppendAsync(HttpContext ctx, ClaimsPrincipal who, string action, string tenantId, string detail)
    {
        var actor = ActorFor(who);
        var tid = who.FindFirst(AdminAllowlist.TenantClaim)?.Value ?? "";
        var oid = who.FindFirst(AdminAllowlist.ObjectClaim)?.Value ?? "";
        try
        {
            var clock = ctx.RequestServices.GetRequiredService<IClock>();
            await ctx.RequestServices.GetRequiredService<IAdminActionLog>().AppendAsync(new AdminActionRecord(clock.UtcNow, tid, oid, actor.Name, action, tenantId, detail), CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Packmoat.Admin").LogError("Admin action trail write failed for {Action} ({Kind})", action, ex.GetType().Name);
            return false;
        }
    }

    // ---------------------------------------------------------------- issuing a token

    private static async Task IssueTokenAsync(HttpContext ctx, ClaimsPrincipal who, IFormCollection f, Tenant t)
    {
        var label = AdminInput.Label(Form(f, "label"), out var err);
        if (label is null) { Redirect(ctx, t.Id, err!); return; }
        var kindText = Form(f, "kind");
        TokenKind kind;
        if (kindText == "developer") kind = TokenKind.Developer;
        else if (kindText == "automation") kind = TokenKind.Automation;
        else { Redirect(ctx, t.Id, "bad-kind"); return; }

        if (!ctx.RequestServices.GetRequiredService<AdminFormNonces>().Consume(Form(f, "nonce"), t.Id)) { Redirect(ctx, t.Id, "reload"); return; }

        var detail = $"kind={kind}; label={CustomerTokenRules.Quote(label)}";   // the kind and label only (label last, quoted): the token itself is never recorded
        if (!await TryAppendAsync(ctx, who, "token.issue", t.Id, detail)) { Redirect(ctx, t.Id, "trail-failed"); return; }

        IssuedToken issued;
        try
        {
            issued = await ctx.RequestServices.GetRequiredService<AdminCommands>().IssueTokenAsync(ActorFor(who), t.Id, label, CancellationToken.None, kind);
        }
        catch (Exception ex)
        {
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Packmoat.Admin").LogError("Admin action token.issue failed ({Kind})", ex.GetType().Name);
            await TryAppendAsync(ctx, who, "token.issue.failed", t.Id, detail);
            Redirect(ctx, t.Id, "failed");
            return;
        }
        await TryAppendAsync(ctx, who, "token.issue.id", t.Id, "id=" + issued.TokenId);   // best effort: the first row already stands

        // The one and only place the plaintext appears: this response. Not in a redirect, URL, cookie, session, log or the trail.
        ctx.Response.Headers.CacheControl = "no-store, max-age=0";
        var body = "<h1>Token issued</h1>" +
            $"<p>Tenant <strong>{E(t.Name)}</strong>, label <strong>{E(label)}</strong>, kind {E(kind.ToString())}, id {E(issued.TokenId)}.</p>" +
            "<div class=\"box\"><p>Copy this token now and put it in the customer's secret store. <strong>It cannot be shown again</strong>: " +
            "reloading this page does not show it, and nobody, including you, can read it back later. Reloading or going back and resubmitting asks to issue ANOTHER live token; this form can be used only once, so that is refused, but if you do open the tenant page again and issue another, both tokens work until you revoke one.</p>" +
            $"<p><code class=\"token\">{E(issued.Plaintext)}</code></p></div>" +
            $"<p><a href=\"/tenants/{E(Uri.EscapeDataString(t.Id))}\">Back to the tenant</a></p>";
        await AdminSite.Write(ctx, 200, Page("Token issued", body, AdminSite.SignOutForm(ctx)));
    }
}
