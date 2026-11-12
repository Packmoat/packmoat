using System.Globalization;
using System.Text;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Admin;
using Packmoat.Host.Web;
using static Packmoat.Host.Customer.AppHtml;

namespace Packmoat.Host.Customer;

/// <summary>
/// The customer site's Waivers page: /waivers for every role (a Developer sees the active list read-only and no forms), and the
/// Admin-only writes: POST /waivers/create, GET and POST /waivers/revoke, POST /waivers/reauth. Thin handlers over
/// <see cref="CustomerWaiverCommands"/> and <see cref="CustomerWaiverQueries"/>, which hold every rule; the tenant comes only from the
/// session. Every write is a POST that has passed the antiforgery gate, with this site's Origin and a single-use nonce. The role and the
/// fresh sign-in are checked here first (so a stale form is not spent on a refusal) and again by the command. Everything a person or the
/// operator typed (package, version, reason, names) is HTML-encoded wherever it is shown.
/// </summary>
internal static class AppWaivers
{
    public const string Path = "/waivers";

    // The waiver wording, as visible text. The command is shown as code.
    internal const string PageText = "A waiver lets one exact version through the cooldown now. Use it for urgent fixes. Packmoat serves the version within seconds. " +
        "If a developer's restore still fails, clear their NuGet HTTP cache with dotnet nuget locals http-cache --clear and restore again. " +
        "Waivers end on their expiry date, at most 30 days.";

    internal const string CacheCommand = "dotnet nuget locals http-cache --clear";

    private static readonly string PageHtml =
        "<p class=\"lede\">" + E("A waiver lets one exact version through the cooldown now. Use it for urgent fixes. Packmoat serves the version within seconds.") + "</p>\n" +
        "<p>" + E("If a developer's restore still fails, clear their NuGet HTTP cache with") + $" <code>{E(CacheCommand)}</code> " +
        E("and restore again. Waivers end on their expiry date, at most 30 days.") + "</p>";

    /// <summary>What a Developer is told (and the token page's NU1102 help says the same).</summary>
    internal const string AskAnAdmin = "ask a Packmoat Admin in your organisation to waive that exact version";

    // ---------------------------------------------------------------- routing

    internal static async Task<bool> RouteAsync(HttpContext ctx, CustomerActor actor, AppSiteSettings s, string path, bool get)
    {
        Func<Task>? handler = (path, get) switch
        {
            (Path, true) => () => ListPage(ctx, actor),
            ("/waivers/create", false) => () => Create(ctx, actor),
            ("/waivers/revoke", true) => () => ConfirmRevoke(ctx, actor),
            ("/waivers/revoke", false) => () => Revoke(ctx, actor),
            ("/waivers/reauth", false) => () => AppSite.ReauthAsync(ctx, Path),
            _ => null,
        };
        if (handler is null) return false;
        if (!get && !SignedInSite.OriginOk(ctx, s.Host))
        {
            await SignedInSite.ErrorPage(ctx, 403, "Request refused", "The request did not come from this site.", next: "Go back to the page and try again.");
            return true;
        }
        try { await handler(); }
        catch (CustomerTokenRefusedException refused) { await Refused(ctx, refused.Reason, refused.Notice); }
        catch (WaiverRefusedException refused) { await NotAdded(ctx, refused); }
        return true;
    }

    private static string? Form(IFormCollection f, string name) => f.TryGetValue(name, out var v) && v.Count == 1 ? v[0] : null;
    private static string? QueryOne(HttpContext ctx, string name) => ctx.Request.Query.TryGetValue(name, out var v) && v.Count == 1 ? v[0] : null;
    private static string NonceKey(CustomerActor a) => $"{a.DirectoryId}/{a.ObjectId:D}";
    private static AppFormNonces Nonces(HttpContext ctx) => ctx.RequestServices.GetRequiredService<AppFormNonces>();
    private static CustomerWaiverCommands Commands(HttpContext ctx) => ctx.RequestServices.GetRequiredService<CustomerWaiverCommands>();
    private static CustomerWaiverQueries Queries(HttpContext ctx) => ctx.RequestServices.GetRequiredService<CustomerWaiverQueries>();
    private static DateTimeOffset Now(HttpContext ctx) => ctx.RequestServices.GetRequiredService<IClock>().UtcNow;
    private static string Af(HttpContext ctx) => Hidden(SignedInSite.AntiforgeryField, SignedInSite.AntiforgeryToken(ctx));
    private const string BackLink = "<a class=\"btn\" href=\"/waivers\">Back to waivers</a>";

    private static Task Show(HttpContext ctx, int status, string title, string body) =>
        SignedInSite.Write(ctx, status, Page(ctx, title, body, SignedInSite.SignOutForm(ctx)));

    private static void SeeOther(HttpContext ctx, string location)
    {
        ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
        ctx.Response.Headers.Location = location;
    }

    /// <summary>The role and the fresh sign-in, before a form is read or its nonce spent. The command checks both again.</summary>
    private static void RequireFreshAdmin(HttpContext ctx, CustomerActor actor)
    {
        if (actor.Role != CustomerRole.Admin) throw new CustomerTokenRefusedException(CustomerTokenRefusal.AdminOnly);
        if (!CustomerTokenRules.IsFresh(actor.SignedInAt, Now(ctx))) throw new CustomerTokenRefusedException(CustomerTokenRefusal.FreshSignInRequired);
    }

    private static Task FormUsed(HttpContext ctx) =>
        Show(ctx, 409, "Open the form again", ErrorCard(ctx, "Open the form again", "This form was already used, or it is too old.",
            "Go back to waivers and try again from a freshly loaded page.", BackLink));

    // ---------------------------------------------------------------- refusals

    /// <summary>The waiver page's own wording for the refusals it can meet; the rest are the token pages' (not linked, suspended).</summary>
    private static async Task Refused(HttpContext ctx, CustomerTokenRefusal reason, ServingNotice? notice = null)
    {
        switch (reason)
        {
            case CustomerTokenRefusal.AdminOnly:
                await Show(ctx, 403, "Admins only", ErrorCard(ctx, "Admins only", "Only a Packmoat Admin in your organisation can add or revoke waivers.",
                    $"If you need a version waived, {AskAnAdmin}.", BackLink));
                return;
            case CustomerTokenRefusal.FreshSignInRequired:
                SignedInSite.UseLandingCsp(ctx);   // the form below posts, then redirects to Microsoft's sign-in
                await Show(ctx, 403, "Sign in again first", ErrorCard(ctx, "Sign in again first",
                    $"Adding and revoking waivers need a sign-in within the last {(int)CustomerTokenRules.FreshSignIn.TotalMinutes} minutes, confirmed by Microsoft.",
                    "Sign in again with Microsoft, then repeat the change. If it still says this after signing in again, " + Support(ctx).Phrase + ": Microsoft may not be sending the sign-in time.",
                    ReauthForm(ctx) + "<a class=\"btn ghost\" href=\"/waivers\">Back to waivers</a>"));
                return;
            case CustomerTokenRefusal.TooManyRequests:
                ctx.Response.Headers.RetryAfter = "600";
                await Show(ctx, 429, "Too many changes", ErrorCard(ctx, "Too many changes", "Too many waiver changes from your account in the last hour.",
                    "Wait a while, then try again.", BackLink));
                return;
            default:
                await AppTokens.Refused(ctx, reason, notice);
                return;
        }
    }

    /// <summary>A refused waiver: the refusal's fixed message (nothing typed is echoed). 409 for a duplicate or the cap, 400 otherwise.</summary>
    private static Task NotAdded(HttpContext ctx, WaiverRefusedException refused)
    {
        var status = refused.Reason is WaiverRefusal.Duplicate or WaiverRefusal.TooManyWaivers ? 409 : 400;
        var next = refused.Reason switch
        {
            WaiverRefusal.Duplicate => "The existing waiver already lets that version through. Revoke it first to change its expiry or reason.",
            WaiverRefusal.TooManyWaivers => "Revoke a waiver that is no longer needed, then add this one.",
            WaiverRefusal.InternalPrefix => "Packages under your internal prefixes always come from your own feed.",
            _ => "Go back, correct the entries and try again.",
        };
        return Show(ctx, status, "Waiver not added", ErrorCard(ctx, "Waiver not added", refused.Message, next, BackLink));
    }

    private static string ReauthForm(HttpContext ctx) =>
        $"<form method=\"post\" action=\"/waivers/reauth\" class=\"inline\">{Af(ctx)}<button type=\"submit\" class=\"btn\">Sign in again</button></form>";

    // ---------------------------------------------------------------- shared bits

    private static string When(DateTimeOffset t) => t.UtcDateTime.ToString("d MMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);

    // CreatedBy and RevokedBy arrive ready to show (CustomerWaiverQueries): a member's display name, or "Packmoat" for the operator,
    // never the operator's own name. Still untrusted text: always encoded.

    private static string Cell(string? text) => $"<td>{E(text)}</td>";

    private static string Table(string[] headings, IEnumerable<string> rows, string empty)
    {
        var list = rows.ToList();
        if (list.Count == 0) return $"<p class=\"empty\">{E(empty)}</p>";
        var sb = new StringBuilder("<div class=\"table-wrap\"><table class=\"tokens\"><thead><tr>");
        foreach (var h in headings) sb.Append("<th scope=\"col\">").Append(h.Length == 0 ? "<span class=\"sr\">Actions</span>" : E(h)).Append("</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var r in list) sb.Append(r);
        return sb.Append("</tbody></table></div>").ToString();
    }

    private static string Message(HttpContext ctx, string? key, bool fresh) => key switch
    {
        "added" => "<p class=\"flash\" role=\"status\">Waiver added. Packmoat serves the version within seconds.</p>",
        "revoked" => "<p class=\"flash\" role=\"status\">Waiver revoked. Packmoat stops serving the version within seconds, unless it has cleared the cooldown.</p>",
        "gone" => "<p class=\"flash warn\" role=\"status\">That waiver was not found or has already ended. Nothing changed.</p>",
        "reauth" when !fresh => "<p class=\"flash warn\" role=\"status\">Microsoft didn't confirm a recent sign-in, so waiver changes stay locked. " +
            E(Support(ctx).CapitalPhrase) + " to check the sign-in setup: Packmoat needs Microsoft to send the sign-in time.</p>",
        _ => "",
    };

    // ---------------------------------------------------------------- GET /waivers

    private static async Task ListPage(HttpContext ctx, CustomerActor actor)
    {
        var list = await Queries(ctx).ListAsync(actor, ctx.RequestAborted);
        var admin = list.IsAdmin;
        var fresh = admin && CustomerTokenRules.IsFresh(actor.SignedInAt, Now(ctx));

        var activeHeadings = admin
            ? new[] { "Package", "Version", "Reason", "Created by", "Created", "Expires", "" }
            : new[] { "Package", "Version", "Reason", "Created by", "Created", "Expires" };
        var active = list.Active.Select(w =>
            $"<tr>{Cell(w.PackageId)}{Cell(w.Version)}{Cell(w.Reason)}{Cell(w.CreatedBy)}{Cell(When(w.CreatedAt))}{Cell(When(w.ExpiresAt))}" +
            (admin ? $"<td><a href=\"/waivers/revoke?waiver={E(Uri.EscapeDataString(w.Id))}\">Revoke</a></td>" : "") + "</tr>");

        string changes;
        if (!admin)
            changes = $"<p class=\"next\"><strong>Need a version waived?</strong>Only a Packmoat Admin can add or revoke waivers: {E(AskAnAdmin)}.</p>";
        else if (fresh)
        {
            var nonce = await Nonces(ctx).IssueAsync(NonceKey(actor), ctx.RequestAborted);
            changes = "<h2>Add a waiver</h2>" +
                $"<form method=\"post\" action=\"/waivers/create\" class=\"create\">{Af(ctx)}{Hidden("nonce", nonce)}" +
                $"<label for=\"package\">Package</label><input id=\"package\" name=\"package\" type=\"text\" maxlength=\"{WaiverRules.MaxPackageId}\" required autocomplete=\"off\" placeholder=\"for example: Newtonsoft.Json\">" +
                $"<label for=\"version\">Exact version</label><input id=\"version\" name=\"version\" type=\"text\" maxlength=\"{WaiverRules.MaxVersion}\" required autocomplete=\"off\" placeholder=\"for example: 13.0.4\">" +
                $"<label for=\"days\">Expires after (days)</label><input id=\"days\" name=\"days\" type=\"number\" min=\"{WaiverRules.MinCustomerDays}\" max=\"{WaiverRules.MaxCustomerDays}\" value=\"{WaiverRules.DefaultCustomerDays}\" required>" +
                $"<label for=\"reason\">Reason</label><input id=\"reason\" name=\"reason\" type=\"text\" minlength=\"{WaiverRules.MinReason}\" maxlength=\"{WaiverRules.MaxReason}\" required autocomplete=\"off\" placeholder=\"for example: fix for CVE-2026-1234\">" +
                "<button type=\"submit\" class=\"btn\">Add waiver</button></form>" +
                $"<p class=\"fine\">One exact version, no wildcards. A reason of {WaiverRules.MinReason} to {WaiverRules.MaxReason} characters, " +
                $"{WaiverRules.MinCustomerDays} to {WaiverRules.MaxCustomerDays} days. Your organisation can hold up to {WaiverRules.MaxActiveCustomerWaivers} active waivers. " +
                "Every waiver is recorded with your name. Packages under your internal prefixes can't be waived.</p>";
        }
        else
        {
            SignedInSite.UseLandingCsp(ctx);   // the form below posts, then redirects to Microsoft's sign-in
            changes = "<h2>Add a waiver</h2><div class=\"reauth\"><p><strong>Sign in again to make changes.</strong> " +
                $"Adding and revoking waivers need a sign-in within the last {(int)CustomerTokenRules.FreshSignIn.TotalMinutes} minutes.</p>" +
                ReauthForm(ctx) + "</div>";
        }

        var history = "";
        if (admin)
        {
            var rows = list.History.Select(w =>
                $"<tr>{Cell(w.PackageId)}{Cell(w.Version)}{Cell(w.Reason)}{Cell(w.CreatedBy)}{Cell(When(w.CreatedAt))}" +
                Cell(w.RevokedAt is { } at ? $"Revoked {When(at)}" + (w.RevokedBy is { } by ? " by " + by : "") : $"Expired {When(w.ExpiresAt)}") + "</tr>");
            history = "<h2>History</h2>" + Table(new[] { "Package", "Version", "Reason", "Created by", "Created", "Ended" }, rows, "No waivers have ended yet.");
        }

        var body =
            "<section class=\"card wide\" aria-labelledby=\"waivers-h\"><p class=\"eyebrow\">Waivers</p><h1 id=\"waivers-h\">Waivers</h1>" +
            PageHtml +
            Message(ctx, QueryOne(ctx, "m"), fresh) +
            "<h2>Active waivers</h2>" +
            Table(activeHeadings, active, "There are no active waivers.") +
            changes + history +
            "<div class=\"actions\"><a class=\"btn ghost\" href=\"/home\">Back to home</a></div></section>";
        await Show(ctx, 200, "Waivers", body);
    }

    // ---------------------------------------------------------------- POST /waivers/create

    private static async Task Create(HttpContext ctx, CustomerActor actor)
    {
        RequireFreshAdmin(ctx, actor);
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!await Nonces(ctx).ConsumeAsync(Form(form, "nonce"), NonceKey(actor), ctx.RequestAborted)) { await FormUsed(ctx); return; }
        var days = AdminInput.Days(Form(form, "days"), WaiverRules.MinCustomerDays, WaiverRules.MaxCustomerDays, "bad-days", out _)
                   ?? throw new WaiverRefusedException(WaiverRefusal.BadExpiry);
        await Commands(ctx).AddAsync(actor, Form(form, "package"), Form(form, "version"), days, Form(form, "reason"), ctx.RequestAborted);
        SeeOther(ctx, "/waivers?m=added");
    }

    // ---------------------------------------------------------------- revoke

    /// <summary>This tenant's active waiver with that id, or null for anything else (a bad shape, unknown, ended, another tenant's).</summary>
    private static async Task<WaiverRecord?> FindActive(HttpContext ctx, CustomerActor actor, string? raw)
    {
        var id = AdminInput.WaiverId(raw, out _);
        if (id is null || !string.Equals(id, raw, StringComparison.Ordinal)) return null;
        return (await Queries(ctx).ListAsync(actor, ctx.RequestAborted)).Active.FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.Ordinal));
    }

    private static async Task ConfirmRevoke(HttpContext ctx, CustomerActor actor)
    {
        RequireFreshAdmin(ctx, actor);
        var w = await FindActive(ctx, actor, QueryOne(ctx, "waiver"));
        if (w is null) { SeeOther(ctx, "/waivers?m=gone"); return; }
        var nonce = await Nonces(ctx).IssueAsync(NonceKey(actor), ctx.RequestAborted);
        await Show(ctx, 200, "Revoke waiver",
            $"<section class=\"card notice\" aria-labelledby=\"confirm-h\"><h1 id=\"confirm-h\">Revoke the waiver for {E(w.PackageId)} {E(w.Version)}?</h1>" +
            $"<p>{E($"Made by {w.CreatedBy}, expires {When(w.ExpiresAt)}. Reason: {w.Reason}")}</p>" +
            "<p>Within seconds, Packmoat stops serving this version until it has cleared the cooldown. Builds that pin it fail to restore. This can't be undone, but you can add a new waiver.</p>" +
            $"<div class=\"actions\"><form method=\"post\" action=\"/waivers/revoke\" class=\"inline\">{Af(ctx)}{Hidden("nonce", nonce)}{Hidden("waiver", w.Id)}" +
            "<button type=\"submit\" class=\"btn danger\">Revoke waiver</button></form>" +
            "<a class=\"btn ghost\" href=\"/waivers\">Cancel</a></div></section>");
    }

    private static async Task Revoke(HttpContext ctx, CustomerActor actor)
    {
        RequireFreshAdmin(ctx, actor);
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!await Nonces(ctx).ConsumeAsync(Form(form, "nonce"), NonceKey(actor), ctx.RequestAborted)) { await FormUsed(ctx); return; }
        var raw = Form(form, "waiver");
        var id = AdminInput.WaiverId(raw, out _);
        var done = id is not null && string.Equals(id, raw, StringComparison.Ordinal) && await Commands(ctx).RevokeAsync(actor, id, ctx.RequestAborted);
        SeeOther(ctx, done ? "/waivers?m=revoked" : "/waivers?m=gone");
    }
}
