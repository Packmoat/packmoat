using System.Text.Encodings.Web;
using Packmoat.Host.Web;

namespace Packmoat.Host.Admin;

/// <summary>
/// Hand-built HTML for the admin site (the operator console). Every dynamic value goes through <see cref="E"/> (HtmlEncoder.Default: &lt; &gt;
/// &amp; and both quote characters are escaped, so it is safe in text and in attribute values). Package names, labels and reasons are untrusted input.
/// There are no inline scripts and no inline styles: the page links one stylesheet from this origin, and the mark is inline SVG drawn with
/// presentation attributes. Nothing on these pages names another origin, not even packmoat.com: the footer is fixed text.
/// The page wears the shared Packmoat chrome (<see cref="SiteChrome"/>) and says it is the operator console several independent ways: the
/// "Operator" pill, the role="note" banner, the ink header with the amber band, the title suffix, its own favicon and body class="operator".
/// </summary>
public static class AdminHtml
{
    public static string E(string? value) => HtmlEncoder.Default.Encode(value ?? "");

    /// <summary>The operator favicon, served at /favicon.svg: the Packmoat mark in white and amber on an ink tile, so the tab is never mistaken for the customer site.</summary>
    public const string OperatorFaviconSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 32 32\"><rect width=\"32\" height=\"32\" rx=\"8\" fill=\"#0e1a18\"/>" +
        "<rect x=\"4.5\" y=\"4.5\" width=\"23\" height=\"23\" rx=\"6\" fill=\"none\" stroke=\"#fff\" stroke-width=\"2\" stroke-dasharray=\"3.5 2.5\"/>" +
        "<rect x=\"10\" y=\"10\" width=\"12\" height=\"12\" rx=\"3\" fill=\"#8a4307\" stroke=\"#fff\" stroke-width=\"1\"/></svg>";

    /// <summary>
    /// The site's one stylesheet: the shared chrome rules, then the operator rules. The operator accent (--accent) defaults to the brand
    /// teal and body.operator sets it to amber; the header is ink with an amber band. Tables scroll sideways on a narrow screen.
    /// </summary>
    public const string Stylesheet = SiteChrome.BaseStylesheet + "\n" + SiteChrome.SignInButtonStylesheet + "\n" + SiteChrome.FooterStylesheet + "\n" + """
        :root{--accent:var(--moat)}
        body.operator{--accent:#8a4307;padding:0;font-size:15px;line-height:1.5}
        .operator .wrap{max-width:none}
        .operator header.top{background:var(--ink);color:#fff;border-bottom:4px solid var(--accent);padding:12px 24px;flex-wrap:wrap;gap:10px 24px}
        .operator .brand{color:#fff}
        .operator .brand .mark{background:#fff;border-radius:8px;padding:2px}
        .pill{display:inline-block;font-family:var(--mono);font-size:11px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;line-height:1;background:var(--accent);color:#fff;border-radius:999px;padding:5px 10px}
        .operator .pill{border:1px solid #fff;font-size:12px}
        .operator header.top nav{display:flex;flex-wrap:wrap;gap:4px 20px;flex:1}
        .operator header.top nav a{color:#fff;text-decoration:none;font-weight:600}
        .operator header.top nav a:hover{text-decoration:underline}
        .operator header.top a:focus-visible,.operator header.top button:focus-visible{outline-color:#fff}
        .operator header.top button{background:transparent;color:#fff;border:1px solid #8a9b96;border-radius:8px;padding:7px 14px;font:600 .9rem/1.2 var(--sans);cursor:pointer}
        .operator header.top button:hover{background:#24332f}
        .banner{background:var(--warn-bg);color:var(--ink);border-bottom:1px solid var(--rule);padding:8px 24px;font-size:14px;font-weight:600}
        .operator main{flex:1;display:block;width:100%;max-width:1100px;margin:0 auto;padding:24px 24px 40px}
        .operator footer{padding:20px 24px 32px}
        .operator h1{font-family:var(--display);font-size:1.6rem;line-height:1.2;letter-spacing:-.01em;margin:0 0 12px;overflow-wrap:anywhere}
        .operator h2{font-family:var(--display);font-size:1.1rem;margin:28px 0 8px}
        .operator p{margin:0 0 12px}
        table{display:block;overflow-x:auto;max-width:100%;border-collapse:collapse;background:var(--panel);border:1px solid var(--rule);border-radius:10px;margin:8px 0 20px}
        th,td{border-top:1px solid var(--rule);padding:8px 10px;text-align:left;vertical-align:top;overflow-wrap:break-word;font-size:14px}
        tr:first-child th{border-top:0}
        th{background:var(--panel-2);color:var(--muted);font-size:13px;font-weight:600;white-space:nowrap}
        button{font:600 .9rem/1.2 var(--sans);background:var(--moat);color:var(--moat-ink);border:0;border-radius:8px;padding:8px 14px;cursor:pointer;white-space:nowrap}
        main button:not(.ms-signin):hover{filter:brightness(1.08)}
        input,select{font:inherit;padding:6px 8px;border:1px solid var(--rule);border-radius:6px;background:#fff;color:var(--ink);max-width:100%}
        input:focus-visible,select:focus-visible{outline:3px solid var(--moat);outline-offset:1px}
        form.stack{margin:8px 0 16px}
        form.stack label,form.inline label{margin-right:.8rem}
        form.inline{display:inline-block;margin:.2rem .8rem .2rem 0}
        td form.inline{margin:0}
        .muted{color:var(--muted)}
        .ok{background:var(--ok-bg);border:1px solid var(--moat);border-radius:8px;padding:10px 14px}
        .err{background:var(--warn-bg);border:1px solid var(--danger);border-radius:8px;padding:10px 14px}
        code.token{display:inline-block;font-family:var(--mono);font-size:1.1rem;word-break:break-all;user-select:all;background:var(--panel-2);border-radius:6px;padding:4px 8px}
        .box{background:var(--panel);border:1px solid var(--rule);border-radius:14px;padding:24px 28px;max-width:40rem}
        .signin-stage{min-height:60vh;display:flex;align-items:center;justify-content:center}
        .card{background:var(--panel);border:1px solid var(--rule);border-radius:14px;padding:32px;width:100%;max-width:560px;margin:0 auto}
        .lede{color:var(--muted)}
        .signin-form{margin:20px 0 0}
        @media (max-width:520px){.operator header.top{padding:10px 12px}.banner{padding:8px 12px}.operator main{padding:16px 12px 32px}.operator footer{padding:16px 12px 24px}.box,.card{padding:20px 16px;border-radius:12px}.ms-signin{width:100%;justify-content:center}}
        @media (prefers-reduced-motion:reduce){*{transition:none!important;animation:none!important}}
        """;

    /// <summary>The operator console's chrome. The footer is fixed text with no link to any other origin.</summary>
    private static readonly SiteChrome Chrome = new(
        Label: "Operator",
        TitleSuffix: " - Packmoat operator",
        StylesheetPath: "/admin.css",
        FaviconPath: "/favicon.svg",
        BodyClass: "operator",
        Banner: "Operator console: changes here affect customer tenants.",
        FooterHtml: "<footer><p>Packmoat operator console. Internal use only.</p></footer>");

    private const string Nav = "<nav aria-label=\"Operator\"><a href=\"/tenants\">Tenants</a><a href=\"/audit\">Audit</a><a href=\"/actions\">Actions</a></nav>";

    /// <summary>A full operator page. The navigation and the sign-out button appear only when <paramref name="signOutForm"/> is given.</summary>
    public static string Page(string title, string body, string? signOutForm = null) => SiteChrome.Page(Chrome, title, body, Nav, signOutForm);

    public static string Hidden(string name, string value) => $"<input type=\"hidden\" name=\"{E(name)}\" value=\"{E(value)}\">";

    public static string Cell(string? text) => "<td>" + E(text) + "</td>";
}
