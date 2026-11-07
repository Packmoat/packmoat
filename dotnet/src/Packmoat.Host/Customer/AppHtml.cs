using System.Text;
using System.Text.Encodings.Web;
using Packmoat.Application;
using Packmoat.Host.Modules;
using Packmoat.Host.Web;

namespace Packmoat.Host.Customer;

/// <summary>
/// Hand-built HTML for the customer site. Every dynamic value goes through <see cref="E"/> (HtmlEncoder.Default). Tenant names and display
/// names are untrusted. No scripts, no inline styles and no style attributes (the CSP allows neither): the page links one stylesheet from
/// this origin, and the logo is inline SVG drawn with presentation attributes only. Nothing is loaded from another origin; the only
/// other-origin references are plain links to the operator's website (<see cref="OperatorLinks"/>: home, privacy, and the terms on the
/// sign-up page), the customer-site module's footer links and the support address, each only when configured (#186).
/// </summary>
public static class AppHtml
{
    public static string E(string? value) => HtmlEncoder.Default.Encode(value ?? "");

    /// <summary>Who runs this server (#186), as the customer site was configured with it.</summary>
    public static OperatorLinks Op(HttpContext ctx) => ctx.RequestServices.GetRequiredService<AppSiteSettings>().Operator;

    /// <summary>Whom this server's sentences tell a person to ask (#186): "email support@example.com", or neutral words.</summary>
    public static SupportContact Support(HttpContext ctx) => Op(ctx).Support;

    /// <summary>The Packmoat mark (the shared chrome's).</summary>
    public const string LogoSvg = SiteChrome.LogoSvg;

    /// <summary>The same mark as a standalone file, served at /favicon.svg.</summary>
    public const string FaviconSvg = SiteChrome.FaviconSvg;

    /// <summary>
    /// The site's one stylesheet: the shared chrome rules (palette, header, footer), then this site's own rules. There is no sign-in page
    /// (a signed-out visit goes straight to Microsoft), so Microsoft's sign-in button rules are the admin site's only.
    /// </summary>
    public const string Stylesheet = SiteChrome.BaseStylesheet + "\n" + """
        main{flex:1;padding:40px 0 48px;display:flex;flex-direction:column;align-items:center;justify-content:center}
        h1{font-family:var(--display);font-size:clamp(1.7rem,4.4vw,2.4rem);line-height:1.1;letter-spacing:-.02em;margin:0 0 14px;text-wrap:balance;overflow-wrap:anywhere}
        h2{font-family:var(--display);font-size:1.05rem;margin:22px 0 6px}
        p{margin:0 0 14px;max-width:62ch}
        .card{background:var(--panel);border:1px solid var(--rule);border-radius:14px;padding:32px;width:100%;max-width:560px}
        .lede{font-size:1.1rem;color:var(--muted)}
        .fine{font-size:14px;color:var(--muted);margin:16px 0 0}
        .eyebrow{font-family:var(--mono);font-size:12px;letter-spacing:.08em;text-transform:uppercase;color:var(--muted);margin:0 0 6px}
        .btn,header.top button,.actions button{display:inline-block;background:var(--moat);color:var(--moat-ink);font:600 1rem/1.2 var(--sans);text-decoration:none;border:0;border-radius:8px;padding:12px 20px;cursor:pointer}
        header.top button,.btn.ghost,.actions button{background:transparent;color:var(--ink);border:1px solid var(--rule);padding:9px 16px;font-size:.95rem}
        .btn:hover{filter:brightness(1.08)}
        header.top button:hover,.actions button:hover,.btn.ghost:hover{background:var(--panel-2);filter:none}
        .actions{display:flex;flex-wrap:wrap;gap:12px;align-items:center;margin-top:24px}
        .actions form{margin:0}
        .facts{display:flex;flex-wrap:wrap;justify-content:space-between;gap:12px 24px;margin:20px 0 0;padding:16px 0;border-top:1px solid var(--rule);border-bottom:1px solid var(--rule)}
        .facts div{min-width:0}
        .facts dt{font-size:14px;color:var(--muted)}
        .facts dd{margin:0;font-weight:600;font-size:1.05rem;overflow-wrap:anywhere}
        .role{display:inline-block;font-family:var(--mono);font-size:12px;font-weight:600;letter-spacing:.06em;text-transform:uppercase;border-radius:999px;padding:4px 12px;background:var(--ok-bg);color:var(--moat)}
        .role.admin{background:var(--moat);color:var(--moat-ink)}
        .stat{display:flex;flex-direction:column-reverse;margin:20px 0 0;padding:20px;background:var(--panel-2);border-radius:12px}
        .stat dt{color:var(--muted);font-size:15px}
        .stat dd{margin:0;font-family:var(--display);font-size:3rem;font-weight:700;line-height:1;letter-spacing:-.02em;color:var(--moat)}
        .stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px;margin:20px 0 0}
        .stats .stat{margin:0}
        .stat dd .unit{font-family:var(--sans);font-size:1rem;font-weight:600;letter-spacing:0;color:var(--muted);margin-left:6px}
        .muted{color:var(--muted)}
        .notice{border-top:4px solid var(--warn)}
        .next{background:var(--warn-bg);border-radius:8px;padding:12px 14px;font-size:15px}
        .next strong{display:block}
        .card.wide{max-width:900px}
        .links{display:flex;flex-wrap:wrap;gap:12px;margin:24px 0 0}
        .actions button.btn,.create button.btn,.reauth button.btn{background:var(--moat);color:var(--moat-ink);border:0;padding:12px 20px;font-size:1rem}
        .actions button.btn:hover,.create button.btn:hover,.reauth button.btn:hover{background:var(--moat);filter:brightness(1.08)}
        .actions button.btn.danger{background:var(--danger)}
        form.inline{display:inline;margin:0}
        .table-wrap{overflow-x:auto;margin:20px 0 0;border:1px solid var(--rule);border-radius:10px}
        table.tokens{width:100%;border-collapse:collapse;font-size:15px}
        .tokens th{text-align:left;font-size:13px;font-weight:600;color:var(--muted);background:var(--panel-2);padding:10px 12px;white-space:nowrap}
        .tokens td{padding:10px 12px;border-top:1px solid var(--rule);vertical-align:top;overflow-wrap:anywhere}
        .status{display:inline-block;font-family:var(--mono);font-size:12px;font-weight:600;letter-spacing:.04em;text-transform:uppercase;border-radius:999px;padding:2px 10px;background:var(--panel-2);color:var(--muted)}
        .status.active{background:var(--ok-bg);color:var(--moat)}
        .status.expired{background:var(--warn-bg);color:var(--warn)}
        .sr{position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap}
        .empty{color:var(--muted);margin:16px 0 0}
        .flash{background:var(--ok-bg);border-radius:8px;padding:10px 14px;font-size:15px;margin:16px 0 0}
        .flash.warn{background:var(--warn-bg)}
        .callout{border-radius:8px;padding:12px 14px;font-size:15px;margin:0 0 20px;background:var(--ok-bg)}
        .callout strong{display:block}
        .callout.warn{background:var(--warn-bg)}
        .callout.stop{background:var(--warn-bg);border-left:4px solid var(--danger)}
        .create{display:flex;flex-wrap:wrap;align-items:flex-end;gap:10px 12px;margin:12px 0 0}
        .create label{flex-basis:100%;font-size:14px;font-weight:600;margin:0 0 -6px}
        .create input{font:inherit;padding:10px 12px;border:1px solid var(--rule);border-radius:8px;background:#fff;color:var(--ink);min-width:0;flex:1 1 240px}
        .create input[type=number]{flex:0 1 120px}
        .create input:focus-visible{outline:3px solid var(--moat);outline-offset:1px}
        .create label.terms{display:flex;gap:10px;align-items:flex-start;font-size:15px;font-weight:400;margin:4px 0 0}
        .create input[type=checkbox]{flex:0 0 auto;width:18px;height:18px;min-width:0;margin:2px 0 0;padding:0}
        pre.secret,pre.snippet{margin:8px 0 14px;padding:14px 16px;background:var(--ink);color:#e9efed;border-radius:10px;font:14px/1.5 var(--mono);white-space:pre-wrap;overflow-wrap:anywhere}
        pre.secret{font-size:16px;font-weight:600}
        .surprises{margin:0;padding-left:20px;display:grid;gap:8px}
        .surprises strong{display:block}
        .reauth{background:var(--warn-bg);border-radius:10px;padding:14px 16px;margin:12px 0 0}
        .reauth p{margin:0 0 10px}
        .checkout{margin:16px 0 0}
        .checkout fieldset{border:0;margin:0;padding:0;display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:12px}
        .checkout legend{font-size:14px;font-weight:600;margin:0 0 8px;padding:0}
        .checkout .plan{display:flex;gap:10px;align-items:flex-start;padding:16px;border:1px solid var(--rule);border-radius:10px;background:var(--panel-2)}
        .checkout label.plan{cursor:pointer}
        .checkout .plan strong{display:block;font-size:1.1rem}
        .checkout .price{display:block;font-family:var(--display);font-size:1.6rem;font-weight:700;color:var(--moat)}
        .checkout input[type=radio],.checkout input[type=checkbox]{flex:0 0 auto;width:18px;height:18px;margin:3px 0 0}
        .checkout input:focus-visible{outline:3px solid var(--moat);outline-offset:1px}
        .checkout label.terms{display:flex;gap:10px;align-items:flex-start;font-size:15px;margin:16px 0 0}
        .checkout button.btn{margin-top:16px}
        ul.disclose{margin:16px 0 0;padding-left:20px;display:grid;gap:6px;font-size:15px}
        .callout .btn{display:table;margin-top:10px;padding:9px 16px;font-size:.95rem}
        ol.steps{margin:20px 0 0;padding-left:22px;display:grid;gap:8px}
        ol.steps h2{margin-top:12px}
        h3{font-size:1rem;margin:16px 0 4px}
        .status.fail{background:var(--warn-bg);color:var(--danger)}
        ul.checks{list-style:none;margin:12px 0 0;padding:0;display:grid;gap:8px;font-size:15px}
        ul.checks details{margin:4px 0 0;color:var(--muted)}
        ul.checks summary{cursor:pointer}
        .results{margin:16px 0 0;padding:16px 0 0;border-top:1px solid var(--rule)}
        .results form{margin:12px 0 0}
        .sr-only{position:absolute;width:1px;height:1px;padding:0;margin:-1px;overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap;border:0}
        """ + "\n" + SiteChrome.FooterStylesheet + "\n" + """
        @media (max-width:520px){body{padding:0 12px}header.top{padding:14px 0}.card{padding:22px 18px;border-radius:12px}main{padding:20px 0 32px;justify-content:flex-start}.lede{font-size:1.02rem}.stat{padding:16px}.stat dd{font-size:2.4rem}footer nav{gap:6px 16px}}
        @media (prefers-reduced-motion:reduce){*{transition:none!important;animation:none!important}}
        """;

    /// <summary>
    /// A full page: header with the mark (and a sign-out button when <paramref name="signOutForm"/> is given), the body, and the footer.
    /// </summary>
    public static string Page(HttpContext ctx, string title, string body, string? signOutForm = null) =>
        Page(Op(ctx), ctx.RequestServices.GetRequiredService<ICustomerSiteModule>(), title, body, signOutForm);

    /// <summary><see cref="Page(HttpContext, string, string, string?)"/> with the operator and the module given.</summary>
    public static string Page(OperatorLinks op, ICustomerSiteModule module, string title, string body, string? signOutForm = null) =>
        SiteChrome.Page(Chrome(op, module), title, body, null, signOutForm);

    /// <summary>The customer site's chrome: no label, no banner, no body class, and the operator's links in the footer.</summary>
    private static SiteChrome Chrome(OperatorLinks op, ICustomerSiteModule module) => new(
        Label: "",
        TitleSuffix: " - Packmoat",
        StylesheetPath: "/app.css",
        FaviconPath: "/favicon.svg",
        BodyClass: "",
        Banner: null,
        FooterHtml: Footer(op, module.FooterLinks(op)));

    /// <summary>
    /// The footer (#186): when a website is set, it, its privacy policy and the module's links (trusted markup); then whom to ask for help:
    /// the support address as a mailto link, else the operator's name, else neutral words. Never a fixed address. The website and address
    /// were checked at start to characters that need no escaping; the name is encoded.
    /// </summary>
    internal static string Footer(OperatorLinks op, string moduleLinks)
    {
        var sb = new StringBuilder("<footer>");
        if (op.SiteUrl is { } site)
            sb.Append("<nav aria-label=\"").Append(E((op.Name ?? op.SiteName) + " links")).Append("\"><a href=\"").Append(site).Append("\">").Append(E(op.SiteName))
              .Append("</a><a href=\"").Append(op.PrivacyUrl).Append("\">Privacy</a>").Append(moduleLinks).Append("</nav>");
        sb.Append("<p>Need help? ");
        if (op.SupportEmail is { } email) sb.Append("Email <a href=\"mailto:").Append(email).Append("\">").Append(email).Append("</a>");
        else sb.Append(E(op.Support.CapitalPhrase));
        return sb.Append(".</p></footer>").ToString();
    }

    /// <summary>
    /// The card every error page uses: a heading, the page's own message, then what to do next and the way back. All three are plain text and
    /// encoded here. <paramref name="extraHtml"/> is trusted markup (a fixed link or a form), never a value.
    /// </summary>
    public static string ErrorCard(HttpContext ctx, string title, string message, string? next, string? extraHtml = null) =>
        ErrorCard(Op(ctx), title, message, next, extraHtml);

    /// <summary><see cref="ErrorCard(HttpContext, string, string, string?, string?)"/> with the operator given.</summary>
    public static string ErrorCard(OperatorLinks op, string title, string message, string? next, string? extraHtml = null)
    {
        var sb = new StringBuilder();
        sb.Append("<section class=\"card notice\"><h1>").Append(E(title)).Append("</h1><p>").Append(E(message)).Append("</p>");
        sb.Append("<p class=\"next\"><strong>What to do next</strong>").Append(E(next ?? "Try again. If it keeps happening, " + op.Support.Phrase + ".")).Append("</p>");
        sb.Append("<div class=\"actions\">").Append(extraHtml ?? "<a class=\"btn\" href=\"/\">Go to Packmoat</a>").Append("</div></section>");
        return sb.ToString();
    }

    /// <summary>The usual action on a sign-in card: an explicit start, where Microsoft offers its account picker.</summary>
    public const string SignInAgainLink = "<a class=\"btn\" href=\"/signin\">Sign in again</a>";

    /// <summary>
    /// The card for every sign-in outcome (signed out, refused, cancelled, failed): a heading, one sentence, an optional detail line and one
    /// action, "Sign in again" unless given. Title, sentence and detail are plain text, encoded here; <paramref name="actionHtml"/> is
    /// trusted, fixed markup, never a value. <paramref name="notice"/> marks a failure with the warning rule on top. <paramref name="moreHtml"/>,
    /// shown after the detail, is trusted markup too, made only of fixed text and encoded values.
    /// </summary>
    public static string SignInCard(string title, string sentence, string? detail = null, string? actionHtml = null, bool notice = true, string? moreHtml = null)
    {
        var sb = new StringBuilder();
        sb.Append(notice ? "<section class=\"card notice\"><h1>" : "<section class=\"card\"><h1>").Append(E(title)).Append("</h1><p>").Append(E(sentence)).Append("</p>");
        if (detail is not null) sb.Append("<p class=\"fine\">").Append(E(detail)).Append("</p>");
        if (moreHtml is not null) sb.Append(moreHtml);
        sb.Append("<div class=\"actions\">").Append(actionHtml ?? SignInAgainLink).Append("</div></section>");
        return sb.ToString();
    }

    public static string Hidden(string name, string value) => $"<input type=\"hidden\" name=\"{E(name)}\" value=\"{E(value)}\">";
}
