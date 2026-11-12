using System.Globalization;
using System.Text;
using Packmoat.Application;
using Packmoat.Domain;
using Packmoat.Host.Admin;
using Packmoat.Host.Modules;
using Packmoat.Host.Web;
using static Packmoat.Host.Customer.AppHtml;

namespace Packmoat.Host.Customer;

/// <summary>
/// The customer site's token pages: /tokens (everyone: their own tokens) and /team (Admin: every token of the tenant, automation tokens).
/// Thin handlers over <see cref="CustomerTokenCommands"/> and <see cref="CustomerTokenQueries"/>, which hold every rule; the tenant comes
/// only from the actor. Every write is a POST that has passed the antiforgery gate; here the Origin is checked too, and the create forms
/// carry a single-use nonce (this site's own store) so a reload cannot quietly create a second token. A new token's value appears in exactly one place: the
/// body of the POST response that created it (Cache-Control: no-store, as every site response). Never in a URL, a redirect, a cookie,
/// a log line or admin_actions.
/// </summary>
/// <summary>
/// The customer site's own single-use form nonces, keyed by person, in the app scope of the store. Separate from the admin site's, so
/// neither can evict or use the other's.
/// </summary>
public sealed class AppFormNonces : FormNonces
{
    public AppFormNonces(IFormNonceStore store, ILoggerFactory? logs = null) : base(store, FormNonceScopes.App, logs) { }
}

internal static class AppTokens
{
    // ---------------------------------------------------------------- routing

    /// <summary>True when the path was one of these pages (answered here, whatever the outcome).</summary>
    internal static async Task<bool> RouteAsync(HttpContext ctx, CustomerActor actor, AppSiteSettings s, string path, bool get)
    {
        Func<Task>? handler = (path, get) switch
        {
            ("/tokens", true) => () => MinePage(ctx, actor),
            ("/tokens/create", false) => () => CreateMine(ctx, actor),
            ("/tokens/revoke", true) => () => ConfirmRevokeMine(ctx, actor),
            ("/tokens/revoke", false) => () => RevokeMine(ctx, actor),
            ("/team", true) => () => TeamPage(ctx, actor),
            ("/team/create", false) => () => CreateAutomation(ctx, actor),
            ("/team/revoke", true) => () => ConfirmRevokeTeam(ctx, actor),
            ("/team/revoke", false) => () => RevokeTeam(ctx, actor),
            ("/team/auto-join", false) => () => SetAutoJoin(ctx, actor),
            ("/reauth", false) => () => AppSite.ReauthAsync(ctx, "/team"),
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
        return true;
    }

    private static string? Form(IFormCollection f, string name) => f.TryGetValue(name, out var v) && v.Count == 1 ? v[0] : null;

    /// <summary>The nonce key for one person (directory and object id). The store is this site's own (<see cref="AppFormNonces"/>).</summary>
    private static string NonceKey(CustomerActor a) => $"{a.DirectoryId}/{a.ObjectId:D}";

    /// <summary>A fresh single-use nonce for a create form: issued only when the form is shown.</summary>
    private static Task<string> NonceAsync(HttpContext ctx, CustomerActor actor) =>
        ctx.RequestServices.GetRequiredService<AppFormNonces>().IssueAsync(NonceKey(actor), ctx.RequestAborted);

    private static CustomerTokenCommands Commands(HttpContext ctx) => ctx.RequestServices.GetRequiredService<CustomerTokenCommands>();
    private static CustomerTokenQueries Queries(HttpContext ctx) => ctx.RequestServices.GetRequiredService<CustomerTokenQueries>();
    private static DateTimeOffset Now(HttpContext ctx) => ctx.RequestServices.GetRequiredService<IClock>().UtcNow;
    private static string Af(HttpContext ctx) => Hidden(SignedInSite.AntiforgeryField, SignedInSite.AntiforgeryToken(ctx));

    private static Task Show(HttpContext ctx, int status, string title, string body) =>
        SignedInSite.Write(ctx, status, Page(ctx, title, body, SignedInSite.SignOutForm(ctx)));

    private static void SeeOther(HttpContext ctx, string location)
    {
        ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
        ctx.Response.Headers.Location = location;
    }

    // ---------------------------------------------------------------- refusals

    /// <summary>
    /// A fixed, friendly page for each refusal. Nothing a person typed is echoed. When the serving gate refused, the customer site's module
    /// words the page from the gate's <paramref name="notice"/>; with nothing from it, the core's neutral words.
    /// </summary>
    internal static async Task Refused(HttpContext ctx, CustomerTokenRefusal reason, ServingNotice? notice = null)
    {
        var (status, title, message, next) = RefusalWords(ctx, reason, notice);
        string extra;
        if (reason == CustomerTokenRefusal.FreshSignInRequired)
        {
            SignedInSite.UseLandingCsp(ctx);   // the form below posts, then redirects to Microsoft's sign-in: the one kind of page that needs this CSP
            extra = ReauthForm(ctx) + "<a class=\"btn ghost\" href=\"/team\">Back to team tokens</a>";
        }
        else extra = "<a class=\"btn\" href=\"/tokens\">Back to your tokens</a><a class=\"btn ghost\" href=\"/home\">Home</a>";
        if (status == 429) ctx.Response.Headers.RetryAfter = "600";
        await SignedInSite.Write(ctx, status, Page(ctx, title, ErrorCard(ctx, title, message, next, extra), SignedInSite.SignOutForm(ctx)));
    }

    /// <summary>
    /// What a token page shows in place of its create form when issuing would be refused: the same words as <see cref="Refused"/>,
    /// so a person learns why before filling in a form the POST would refuse. The page still lists the tokens, so they can be revoked.
    /// </summary>
    private static string Blocked(HttpContext ctx, CustomerTokenBlocker blocker)
    {
        var (_, title, message, next) = RefusalWords(ctx, blocker.Reason, blocker.Notice);
        return $"<div class=\"blocked\" role=\"status\"><p><strong>{E(title)}</strong></p><p>{E(message)}</p>" +
               $"<p class=\"next\"><strong>What to do next</strong>{E(next)}</p></div>";
    }

    /// <summary>The status and the fixed words for each refusal: the page <see cref="Refused"/> writes, and <see cref="Blocked"/> reuses.</summary>
    private static (int Status, string Title, string Message, string Next) RefusalWords(HttpContext ctx, CustomerTokenRefusal reason, ServingNotice? notice)
    {
        var support = Support(ctx);
        var gateWords = reason == CustomerTokenRefusal.ServingRefused
            ? ctx.RequestServices.GetRequiredService<ICustomerSiteModule>().ServingRefused(notice, support)
            : null;
        return reason switch
        {
            CustomerTokenRefusal.TenantNotVerified => (409, "Not ready yet",
                "Packmoat hasn't finished checking the connection to your organisation's feed, so tokens can't be created yet.",
                support.OperatorSubject + " will let you know when your feed is ready. If you've been waiting more than a day, " + support.Phrase + "."),
            CustomerTokenRefusal.TenantNotSetUp => (409, "Not connected yet",
                "This organisation isn't connected to an Azure Artifacts feed yet.",
                "Tokens can be created once the feed is connected. If you need help, " + support.Phrase + "."),
            CustomerTokenRefusal.TooManyTokens => (409, "Too many tokens",
                $"You already have {CustomerTokenRules.MaxActiveTokensPerMember} active tokens, the most one person can hold.",
                "Revoke a token you no longer use, then create a new one."),
            CustomerTokenRefusal.AdminOnly => (403, "Admins only",
                "Only a Packmoat admin in your organisation can do this.",
                "Ask someone with the Packmoat.Admin role in your organisation."),
            CustomerTokenRefusal.TenantSuspended => (403, "Suspended",
                AppSite.SuspendedSentence(support),
                AppSite.SuspendedNext(support, ctx.RequestServices.GetRequiredService<ICustomerSiteModule>())),
            CustomerTokenRefusal.ServingRefused when gateWords is not null => (403, gateWords.Title, gateWords.Message, gateWords.Next),
            CustomerTokenRefusal.ServingRefused => (403, NotServingTitle, NotServingSentence,
                "See your home page for details. If you need help, " + support.Phrase + "."),
            CustomerTokenRefusal.NotLinked => (403, "Not linked",
                "Your organisation's directory is no longer linked to this Packmoat account.",
                "Sign in again. If it keeps happening, " + support.Phrase + "."),
            CustomerTokenRefusal.BadLabel => (400, "Check the name",
                $"Give the token a name of 1 to {CustomerTokenRules.MaxLabel} characters, with no line breaks or invisible characters.",
                "Go back and try again with a shorter, plain name, for example the machine's name."),
            CustomerTokenRefusal.BadExpiry => (400, "Check the expiry",
                $"An automation token lasts {CustomerTokenRules.MinAutomationDays} to {CustomerTokenRules.MaxAutomationDays} days.",
                "Go back and choose a whole number of days in that range."),
            CustomerTokenRefusal.TooManyRequests => (429, "Too many changes",
                "Too many token changes from your account in the last hour.",
                "Wait a while, then try again."),
            CustomerTokenRefusal.FreshSignInRequired => (403, "Sign in again first",
                $"Creating automation tokens and revoking other people's tokens need a sign-in within the last {(int)CustomerTokenRules.FreshSignIn.TotalMinutes} minutes, confirmed by Microsoft.",
                "Sign in again with Microsoft, then repeat the change. If it still says this after signing in again, " + support.Phrase + ": Microsoft may not be sending the sign-in time."),
            _ => (403, "Not allowed", "You are not allowed to do this.", "Go back to your home page."),
        };
    }

    private static string ReauthForm(HttpContext ctx) =>
        $"<form method=\"post\" action=\"/reauth\" class=\"inline\">{Af(ctx)}<button type=\"submit\" class=\"btn\">Sign in again</button></form>";

    // ---------------------------------------------------------------- shared bits

    private static string Day(DateTimeOffset t) => t.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private static (string Text, string Css) Status(TokenInfo t, DateTimeOffset now) =>
        t.RevokedAt is not null ? ("Revoked", "revoked") : t.ExpiresAt is { } x && x <= now ? ("Expired", "expired") : ("Active", "active");

    private static string StatusCell(TokenInfo t, DateTimeOffset now)
    {
        var (text, css) = Status(t, now);
        return $"<td><span class=\"status {css}\">{text}</span></td>";
    }

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

    private static string Message(HttpContext ctx, string? key) => key switch
    {
        "revoked" => "<p class=\"flash\" role=\"status\">Token revoked. Anything using it stops working within a few seconds.</p>",
        "gone" => "<p class=\"flash warn\" role=\"status\">That token was not found or was already revoked. Nothing changed.</p>",
        "autojoin-on" => "<p class=\"flash\" role=\"status\">" + E(AutoJoinOnFlash) + "</p>",
        "autojoin-off" => "<p class=\"flash\" role=\"status\">" + E(AutoJoinOffFlash) + "</p>",
        "reauth-unconfirmed" => "<p class=\"flash warn\" role=\"status\">Microsoft didn't confirm a recent sign-in, so admin changes stay locked. " +
            E(Support(ctx).CapitalPhrase) + " to check the sign-in setup: Packmoat needs Microsoft to send the sign-in time.</p>",
        _ => "",
    };

    private static string? QueryOne(HttpContext ctx, string name) => ctx.Request.Query.TryGetValue(name, out var v) && v.Count == 1 ? v[0] : null;

    // ---------------------------------------------------------------- /tokens: everyone, their own

    private static async Task MinePage(HttpContext ctx, CustomerActor actor)
    {
        var now = Now(ctx);
        var mine = await Queries(ctx).MineAsync(actor, ctx.RequestAborted);
        var active = mine.Count(t => t.IssuedVia == TokenIssuer.Self && t.IsActiveAt(now));
        var blocker = await Queries(ctx).IssueBlockerAsync(actor, ctx.RequestAborted);   // the POST's own rule: no form it would refuse
        var rows = mine.OrderByDescending(t => t.CreatedAt).Select(t =>
            $"<tr>{Cell(t.Label)}{Cell(Day(t.CreatedAt))}{Cell(t.LastUsedAt is { } u ? Day(u) : "Never")}{Cell(t.ExpiresAt is { } x ? Day(x) : "Never")}{StatusCell(t, now)}" +
            (Status(t, now).Css == "active" ? $"<td><a href=\"/tokens/revoke?token={E(Uri.EscapeDataString(t.TokenId))}\">Revoke</a></td>" : "<td></td>") + "</tr>");
        var full = active >= CustomerTokenRules.MaxActiveTokensPerMember;
        var nonce = blocker is null && !full ? await NonceAsync(ctx, actor) : null;   // issued only when the form is shown
        var body =
            "<section class=\"card wide\" aria-labelledby=\"tokens-h\"><p class=\"eyebrow\">Your feed tokens</p><h1 id=\"tokens-h\">Your feed tokens</h1>" +
            $"<p class=\"lede\">A token lets NuGet on your machine read your organisation's packages through Packmoat. Each one lasts {CustomerTokenRules.DeveloperTokenDays} days.</p>" +
            Message(ctx, QueryOne(ctx, "m")) +
            Table(new[] { "Name", "Created", "Last used", "Expires", "Status", "" }, rows, "You have no tokens yet.") +
            "<h2>Create my token</h2>" +
            (blocker is not null ? Blocked(ctx, blocker)
                : full
                ? $"<p class=\"next\"><strong>You have {CustomerTokenRules.MaxActiveTokensPerMember} active tokens</strong>Revoke one you no longer use to create another.</p>"
                : $"<form method=\"post\" action=\"/tokens/create\" class=\"create\">{Af(ctx)}{Hidden("nonce", nonce!)}" +
                  $"<label for=\"label\">Name</label><input id=\"label\" name=\"label\" type=\"text\" maxlength=\"{CustomerTokenRules.MaxLabel}\" required autocomplete=\"off\" placeholder=\"for example: work laptop\">" +
                  "<button type=\"submit\" class=\"btn\">Create my token</button></form>" +
                  $"<p class=\"fine\">Name it after the machine you'll use it on. You can hold up to {CustomerTokenRules.MaxActiveTokensPerMember} active tokens; the token is shown once.</p>") +
            "<div class=\"actions\"><a class=\"btn ghost\" href=\"/home\">Back to home</a></div></section>";
        await Show(ctx, 200, "Your feed tokens", body);
    }

    private static async Task CreateMine(HttpContext ctx, CustomerActor actor)
    {
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!await ctx.RequestServices.GetRequiredService<AppFormNonces>().ConsumeAsync(Form(form, "nonce"), NonceKey(actor), ctx.RequestAborted))
        {
            await Show(ctx, 409, "Open the form again", ErrorCard(ctx, "Open the form again", "This form was already used, or it is too old.",
                "Go back to your tokens and create the token from a freshly loaded page.", "<a class=\"btn\" href=\"/tokens\">Back to your tokens</a>"));
            return;
        }
        var label = Form(form, "label") ?? "";
        var issued = await Commands(ctx).IssueDeveloperTokenAsync(actor, label, ctx.RequestAborted);
        await ShowOnce(ctx, issued, CustomerTokenRules.CleanLabel(label)!, Now(ctx).AddDays(CustomerTokenRules.DeveloperTokenDays), developer: true);
    }

    /// <summary>The one and only place a new token's value appears: this response body (no-store).</summary>
    private static Task ShowOnce(HttpContext ctx, IssuedToken issued, string label, DateTimeOffset expires, bool developer)
    {
        var feed = ctx.RequestServices.GetRequiredService<AppSiteSettings>().FeedIndexUrl;
        var snippet = $"dotnet nuget add source {feed} -n packmoat -u packmoat -p {issued.Plaintext} --store-password-in-clear-text";
        var back = developer ? "/tokens" : "/team";
        var body =
            $"<section class=\"card wide\" aria-labelledby=\"once-h\"><p class=\"eyebrow\">{(developer ? "Your feed tokens" : "Team tokens")}</p><h1 id=\"once-h\">Your token is ready</h1>" +
            $"<p>Token <strong>{E(label)}</strong>{(developer ? "" : " (automation)")}, expires {E(Day(expires))}.</p>" +
            "<p class=\"next\"><strong>Copy it now</strong>This is the only time Packmoat shows it. It isn't stored anywhere and can't be shown again. If you lose it, revoke it and create a new one.</p>" +
            $"<h2>The token</h2><pre class=\"secret\"><code>{E(issued.Plaintext)}</code></pre>" +
            "<h2>Add Packmoat as a NuGet source</h2>" +
            (developer ? "<p>Run this once on your machine:</p>" : "<p>On the build agent, store the token as a secret and add the source with it, for example:</p>") +
            $"<pre class=\"snippet\"><code>{E(snippet)}</code></pre>" +
            "<p class=\"fine\">The password option is needed on macOS and Linux, where NuGet cannot encrypt stored passwords. The token only reads packages your organisation's feed already serves.</p>" +
            "<h2>Three things that may surprise you</h2><ul class=\"surprises\">" +
            "<li><strong>Floating versions pick the newest old-enough version.</strong> A range such as 13.* resolves to the newest version that has passed your cooldown, not the newest published.</li>" +
            "<li><strong>An exact pin to a too-new version fails with NU1102.</strong> NuGet reports it as \"unable to find package version\". Wait until it's old enough, or " + E(AppWaivers.AskAnAdmin) + " on the <a href=\"/waivers\">Waivers</a> page.</li>" +
            "<li><strong>Package search in Visual Studio isn't supported.</strong> Add packages by id and version (dotnet add package, or edit the project file) instead.</li></ul>" +
            $"<div class=\"actions\"><a class=\"btn\" href=\"{back}\">Done</a></div></section>";
        return Show(ctx, 200, "Your token is ready", body);
    }

    private static async Task ConfirmRevokeMine(HttpContext ctx, CustomerActor actor)
    {
        var id = AdminInput.TokenId(QueryOne(ctx, "token"), out _);
        var now = Now(ctx);
        var token = id is null ? null : (await Queries(ctx).MineAsync(actor, ctx.RequestAborted)).FirstOrDefault(t => t.TokenId == id && Status(t, now).Css == "active");
        if (token is null) { SeeOther(ctx, "/tokens?m=gone"); return; }
        await Show(ctx, 200, "Revoke token", ConfirmCard("/tokens/revoke", "/tokens", token, ctx,
            "Anything using it, such as NuGet on your machine, stops working within a few seconds. This can't be undone."));
    }

    private static string ConfirmCard(string action, string cancel, TokenInfo token, HttpContext ctx, string consequence) =>
        $"<section class=\"card notice\" aria-labelledby=\"confirm-h\"><h1 id=\"confirm-h\">Revoke {E(token.Label)}?</h1>" +
        $"<p>{E(consequence)}</p>" +
        $"<div class=\"actions\"><form method=\"post\" action=\"{action}\" class=\"inline\">{Af(ctx)}{Hidden("token", token.TokenId)}" +
        "<button type=\"submit\" class=\"btn danger\">Revoke token</button></form>" +
        $"<a class=\"btn ghost\" href=\"{cancel}\">Cancel</a></div></section>";

    private static async Task RevokeMine(HttpContext ctx, CustomerActor actor)
    {
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var id = AdminInput.TokenId(Form(form, "token"), out _);
        var done = id is not null && await Commands(ctx).RevokeOwnTokenAsync(actor, id, ctx.RequestAborted);
        SeeOther(ctx, done ? "/tokens?m=revoked" : "/tokens?m=gone");
    }

    // ---------------------------------------------------------------- /team: Admin

    private static async Task TeamPage(HttpContext ctx, CustomerActor actor)
    {
        var now = Now(ctx);
        var all = await Queries(ctx).TenantAsync(actor, ctx.RequestAborted);   // refuses a Developer
        var fresh = CustomerTokenRules.IsFresh(actor.SignedInAt, now);
        var rows = all.OrderByDescending(t => t.CreatedAt).Select(t =>
            $"<tr>{Cell(t.OwnerOid is null ? "Issued by Packmoat" : t.OwnerName ?? "Unknown person")}{Cell(t.OwnerLastSeenAt is { } seen ? Day(seen) : "")}" +
            $"{Cell(t.Label)}{Cell(t.Kind == TokenKind.Automation ? "Automation" : "Developer")}{Cell(t.LastUsedAt is { } u ? Day(u) : "Never")}" +
            $"{Cell(t.ExpiresAt is { } x ? Day(x) : "Never")}{StatusCell(t, now)}" +
            (Status(t, now).Css != "revoked" ? $"<td><a href=\"/team/revoke?token={E(Uri.EscapeDataString(t.TokenId))}\">Revoke</a></td>" : "<td></td>") + "</tr>");
        string create;
        if (fresh && await Queries(ctx).IssueBlockerAsync(actor, ctx.RequestAborted) is { } blocker)   // the POST's own rule: no form it would refuse
            create = Blocked(ctx, blocker);
        else if (fresh)
        {
            var nonce = await NonceAsync(ctx, actor);
            create = $"<form method=\"post\" action=\"/team/create\" class=\"create\">{Af(ctx)}{Hidden("nonce", nonce)}" +
                     $"<label for=\"label\">Name</label><input id=\"label\" name=\"label\" type=\"text\" maxlength=\"{CustomerTokenRules.MaxLabel}\" required autocomplete=\"off\" placeholder=\"for example: main build agents\">" +
                     $"<label for=\"days\">Expires after (days)</label><input id=\"days\" name=\"days\" type=\"number\" min=\"{CustomerTokenRules.MinAutomationDays}\" max=\"{CustomerTokenRules.MaxAutomationDays}\" value=\"90\" required>" +
                     "<button type=\"submit\" class=\"btn\">Create automation token</button></form>" +
                     $"<p class=\"fine\">For build agents and CI. Automation tokens don't count as developers and always expire, after {CustomerTokenRules.MinAutomationDays} to {CustomerTokenRules.MaxAutomationDays} days. The token is shown once.</p>";
        }
        else
        {
            SignedInSite.UseLandingCsp(ctx);   // the form below posts, then redirects to Microsoft's sign-in: the one kind of page that needs this CSP
            create = "<div class=\"reauth\"><p><strong>Sign in again to make changes.</strong> " +
                     $"Creating automation tokens and revoking tokens here need a sign-in within the last {(int)CustomerTokenRules.FreshSignIn.TotalMinutes} minutes.</p>" +
                     ReauthForm(ctx) + "</div>";
        }
        var body =
            "<section class=\"card wide\" aria-labelledby=\"team-h\"><p class=\"eyebrow\">Team tokens</p><h1 id=\"team-h\">Your organisation's tokens</h1>" +
            "<p class=\"lede\">Every token for your organisation: who it belongs to, when it was last used and when it expires.</p>" +
            Message(ctx, QueryOne(ctx, "m") is "reauth" ? (fresh ? null : "reauth-unconfirmed") : QueryOne(ctx, "m")) +
            Table(new[] { "Owner", "Owner last seen", "Name", "Kind", "Last used", "Expires", "Status", "" }, rows, "There are no tokens yet.") +
            "<h2>Create an automation token</h2>" + create +
            WhoCanJoin(ctx, await ctx.RequestServices.GetRequiredService<AutoJoinCommands>().IsOnAsync(actor, ctx.RequestAborted)) +
            "<div class=\"actions\"><a class=\"btn ghost\" href=\"/home\">Back to home</a></div></section>";
        await Show(ctx, 200, "Team tokens", body);
    }

    // ---------------------------------------------------------------- /team: who can join (auto-join)

    // The wording of the auto-join pages.
    /// <summary>
    /// The core's words when the serving gate refuses a tenant and the customer site's module has none for its notice. A module whose
    /// gate refuses with its own notice words it (<see cref="ICustomerSiteModule.ServingRefused"/>); these are for any other gate.
    /// </summary>
    internal const string NotServingTitle = "Not serving";
    internal const string NotServingSentence = "This Packmoat account isn't being served at the moment, so new feed tokens can't be created.";

    internal const string AutoJoinOnText = "Anyone in your organisation can sign in as a Developer without being given a role, and can create feed tokens that read your packages. Guests still need a Packmoat role.";
    internal const string AutoJoinOffText = "Only people with a Packmoat role can sign in. Give people the Packmoat Developer role in Microsoft Entra.";
    internal const string AutoJoinNoteText = "Turning this off signs out people who joined this way. Tokens they made keep working until you revoke them below.";
    internal const string AutoJoinOnFlash = "Automatic joining is on.";
    internal const string AutoJoinOffFlash = "Automatic joining is off.";

    /// <summary>The "Who can join" section: what the setting means now, one button that asks for the other value explicitly, and the note.</summary>
    private static string WhoCanJoin(HttpContext ctx, bool on) =>
        "<h2>Who can join</h2>" +
        $"<p>{E(on ? AutoJoinOnText : AutoJoinOffText)}</p>" +
        $"<form method=\"post\" action=\"/team/auto-join\" class=\"inline\">{Af(ctx)}{Hidden("enabled", on ? "off" : "on")}" +
        $"<button type=\"submit\" class=\"btn\">{(on ? "Turn off" : "Turn on")}</button></form>" +
        $"<p class=\"fine\">{E(AutoJoinNoteText)}</p>";

    /// <summary>
    /// POST /team/auto-join with enabled=on or enabled=off (exactly one, exactly that text). Antiforgery and Origin were checked before
    /// this runs; the role and the fresh sign-in are checked here first (so a stale admin sees "Sign in again"), and again by the command.
    /// Post/Redirect/Get back to /team with a fixed message.
    /// </summary>
    private static async Task SetAutoJoin(HttpContext ctx, CustomerActor actor)
    {
        if (actor.Role != CustomerRole.Admin) throw new CustomerTokenRefusedException(CustomerTokenRefusal.AdminOnly);
        if (!CustomerTokenRules.IsFresh(actor.SignedInAt, Now(ctx))) throw new CustomerTokenRefusedException(CustomerTokenRefusal.FreshSignInRequired);
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        bool? enabled = Form(form, "enabled") switch { "on" => true, "off" => false, _ => null };
        if (enabled is not { } value)
        {
            await Show(ctx, 400, "Request refused", ErrorCard(ctx, "Request refused", "The request was not understood.", "Go back to team tokens and try again.",
                "<a class=\"btn\" href=\"/team\">Back to team tokens</a>"));
            return;
        }
        await ctx.RequestServices.GetRequiredService<AutoJoinCommands>().SetAutoJoinAsync(actor, value, ctx.RequestAborted);
        SeeOther(ctx, value ? "/team?m=autojoin-on" : "/team?m=autojoin-off");
    }

    private static async Task CreateAutomation(HttpContext ctx, CustomerActor actor)
    {
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        // The role and the fresh sign-in are checked by the command; checked here first too, so a stale form is not spent on a refusal.
        if (actor.Role != CustomerRole.Admin) throw new CustomerTokenRefusedException(CustomerTokenRefusal.AdminOnly);
        if (!CustomerTokenRules.IsFresh(actor.SignedInAt, Now(ctx))) throw new CustomerTokenRefusedException(CustomerTokenRefusal.FreshSignInRequired);
        if (!await ctx.RequestServices.GetRequiredService<AppFormNonces>().ConsumeAsync(Form(form, "nonce"), NonceKey(actor), ctx.RequestAborted))
        {
            await Show(ctx, 409, "Open the form again", ErrorCard(ctx, "Open the form again", "This form was already used, or it is too old.",
                "Go back to team tokens and create the token from a freshly loaded page.", "<a class=\"btn\" href=\"/team\">Back to team tokens</a>"));
            return;
        }
        var days = AdminInput.Days(Form(form, "days"), CustomerTokenRules.MinAutomationDays, CustomerTokenRules.MaxAutomationDays, "bad-days", out _)
                   ?? throw new CustomerTokenRefusedException(CustomerTokenRefusal.BadExpiry);
        var label = Form(form, "label") ?? "";
        var issued = await Commands(ctx).IssueAutomationTokenAsync(actor, label, days, ctx.RequestAborted);
        await ShowOnce(ctx, issued, CustomerTokenRules.CleanLabel(label)!, Now(ctx).AddDays(days), developer: false);
    }

    private static async Task ConfirmRevokeTeam(HttpContext ctx, CustomerActor actor)
    {
        if (actor.Role != CustomerRole.Admin) throw new CustomerTokenRefusedException(CustomerTokenRefusal.AdminOnly);
        if (!CustomerTokenRules.IsFresh(actor.SignedInAt, Now(ctx))) throw new CustomerTokenRefusedException(CustomerTokenRefusal.FreshSignInRequired);
        var id = AdminInput.TokenId(QueryOne(ctx, "token"), out _);
        var token = id is null ? null : (await Queries(ctx).TenantAsync(actor, ctx.RequestAborted)).FirstOrDefault(t => t.TokenId == id && t.RevokedAt is null);
        if (token is null) { SeeOther(ctx, "/team?m=gone"); return; }
        var whose = token.OwnerOid is null ? "This token was issued by Packmoat." : $"This token belongs to {token.OwnerName ?? "an unknown person"}.";
        await Show(ctx, 200, "Revoke token", ConfirmCard("/team/revoke", "/team", token, ctx,
            $"{whose} Anything using it stops working within a few seconds. This can't be undone."));
    }

    private static async Task RevokeTeam(HttpContext ctx, CustomerActor actor)
    {
        if (actor.Role != CustomerRole.Admin) throw new CustomerTokenRefusedException(CustomerTokenRefusal.AdminOnly);
        if (!CustomerTokenRules.IsFresh(actor.SignedInAt, Now(ctx))) throw new CustomerTokenRefusedException(CustomerTokenRefusal.FreshSignInRequired);
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var id = AdminInput.TokenId(Form(form, "token"), out _);
        var done = id is not null && await Commands(ctx).RevokeTenantTokenAsync(actor, id, ctx.RequestAborted);   // role and fresh sign-in checked there
        SeeOther(ctx, done ? "/team?m=revoked" : "/team?m=gone");
    }
}
