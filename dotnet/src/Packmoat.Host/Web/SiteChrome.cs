using System.Text;
using System.Text.Encodings.Web;

namespace Packmoat.Host.Web;

/// <summary>
/// The page chrome both signed-in sites share: the Packmoat mark, the Microsoft sign-in logo, the chrome part of the stylesheet, and one
/// function that lays out a page. Each site describes itself with one of these records (label, title suffix, stylesheet, favicon, body
/// class, banner, footer) and keeps its own page rules. This file knows neither site.
/// No scripts, no inline styles and no style attributes (the CSP allows neither); the logos are inline SVG drawn with presentation
/// attributes only, and nothing is loaded from another origin.
/// </summary>
/// <param name="Label">A pill after the wordmark ("" for none). Plain text, encoded here.</param>
/// <param name="TitleSuffix">Appended to every page title, for example " - Packmoat".</param>
/// <param name="StylesheetPath">The site's one same-origin stylesheet.</param>
/// <param name="FaviconPath">The site's same-origin SVG favicon.</param>
/// <param name="BodyClass">A class on the body element ("" for none): the site's CSS hook.</param>
/// <param name="Banner">A full-width role="note" banner under the header, or null for none. Plain text, encoded here.</param>
/// <param name="FooterHtml">The whole footer element. Trusted, fixed markup: never a value.</param>
internal sealed record SiteChrome(string Label, string TitleSuffix, string StylesheetPath, string FaviconPath, string BodyClass, string? Banner, string FooterHtml)
{
    /// <summary>The Packmoat mark, as on packmoat.com and its favicon: a dashed rounded square round a solid inner square.</summary>
    public const string LogoSvg =
        "<svg class=\"mark\" viewBox=\"0 0 32 32\" width=\"28\" height=\"28\" aria-hidden=\"true\" focusable=\"false\">" +
        "<rect x=\"2\" y=\"2\" width=\"28\" height=\"28\" rx=\"8\" fill=\"none\" stroke=\"#0a6a5b\" stroke-width=\"2.4\" stroke-dasharray=\"4 3\"/>" +
        "<rect x=\"9\" y=\"9\" width=\"14\" height=\"14\" rx=\"3.5\" fill=\"#0a6a5b\"/></svg>";

    /// <summary>The same mark as a standalone file, for a site's /favicon.svg.</summary>
    public const string FaviconSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 32 32\"><rect x=\"2\" y=\"2\" width=\"28\" height=\"28\" rx=\"8\" fill=\"none\" stroke=\"#0a6a5b\" stroke-width=\"2.4\" stroke-dasharray=\"4 3\"/><rect x=\"9\" y=\"9\" width=\"14\" height=\"14\" rx=\"3.5\" fill=\"#0a6a5b\"/></svg>";

    /// <summary>The four-colour Microsoft logo, unaltered (Microsoft's own 21 x 21 asset from the identity branding guidelines).</summary>
    public const string MicrosoftLogoSvg =
        "<svg class=\"ms-logo\" viewBox=\"0 0 21 21\" width=\"21\" height=\"21\" aria-hidden=\"true\" focusable=\"false\">" +
        "<rect x=\"1\" y=\"1\" width=\"9\" height=\"9\" fill=\"#f25022\"/><rect x=\"1\" y=\"11\" width=\"9\" height=\"9\" fill=\"#00a4ef\"/>" +
        "<rect x=\"11\" y=\"1\" width=\"9\" height=\"9\" fill=\"#7fba00\"/><rect x=\"11\" y=\"11\" width=\"9\" height=\"9\" fill=\"#ffb900\"/></svg>";

    /// <summary>
    /// The chrome rules: palette and type (packmoat.com's site/assets/site.css, light theme only), base, links and focus, the skip link, the
    /// page column, the header and the wordmark.
    /// </summary>
    public const string BaseStylesheet = """
        :root{--bg:#f3f6f5;--ink:#0e1a18;--muted:#52625d;--rule:#d3dcd8;--panel:#fff;--panel-2:#e9efed;--moat:#0a6a5b;--moat-ink:#fff;--ok-bg:#d9efe8;--warn:#8a4307;--warn-bg:#fbe9d6;--danger:#a3271f;
          --display:"Avenir Next","Segoe UI",system-ui,-apple-system,"Helvetica Neue",Arial,sans-serif;
          --sans:system-ui,-apple-system,"Segoe UI",Roboto,"Helvetica Neue",Arial,sans-serif;
          --mono:ui-monospace,"SF Mono",SFMono-Regular,Menlo,Consolas,"Liberation Mono",monospace;color-scheme:light}
        *{box-sizing:border-box}
        html{background:var(--bg)}
        body{margin:0;background:var(--bg);color:var(--ink);font-family:var(--sans);font-size:16px;line-height:1.6;padding:0 16px}
        a{color:var(--moat)}
        a:focus-visible,button:focus-visible{outline:3px solid var(--moat);outline-offset:2px;border-radius:4px}
        .skip{position:absolute;left:8px;top:-60px;background:var(--moat);color:var(--moat-ink);padding:8px 14px;border-radius:8px;z-index:10}
        .skip:focus{top:8px}
        .wrap{max-width:960px;margin:0 auto;min-height:100vh;display:flex;flex-direction:column}
        header.top{display:flex;justify-content:space-between;align-items:center;gap:12px;padding:18px 0;border-bottom:1px solid var(--rule)}
        .brand{font-family:var(--display);font-weight:700;font-size:1.25rem;letter-spacing:-.01em;display:inline-flex;align-items:center;gap:10px;color:var(--ink);text-decoration:none}
        .brand .mark{width:28px;height:28px;flex:none}
        header.top form{margin:0}
        """;

    /// <summary>
    /// The Microsoft sign-in button, following Microsoft's light-theme redlines: white, 1px #8c8c8c border, #5e5e5e text, Segoe UI semibold
    /// 15px, 41px high, 12px padding and gap.
    /// </summary>
    public const string SignInButtonStylesheet = """
        .ms-signin{display:inline-flex;align-items:center;gap:12px;height:41px;padding:0 12px;background:#fff;color:#5e5e5e;border:1px solid #8c8c8c;border-radius:0;
          font:600 15px/1 "Segoe UI","Segoe UI Web (West European)",-apple-system,BlinkMacSystemFont,Roboto,"Helvetica Neue",sans-serif;cursor:pointer}
        .ms-signin:hover{background:#f3f3f3}
        .ms-signin:active{background:#e6e6e6}
        .ms-signin .ms-logo{width:21px;height:21px;flex:none}
        .ms-signin:focus-visible{outline:3px solid var(--moat);outline-offset:3px;border-radius:0}
        """;

    /// <summary>The footer under every page.</summary>
    public const string FooterStylesheet = """
        footer{border-top:1px solid var(--rule);padding:20px 0 32px;color:var(--muted);font-size:14px}
        footer nav{display:flex;flex-wrap:wrap;gap:8px 20px;margin:0 0 8px}
        footer p{margin:0}
        """;

    private static string E(string? value) => HtmlEncoder.Default.Encode(value ?? "");

    /// <summary>
    /// A full page: the header with the mark, the wordmark and the label (and, when <paramref name="signOutForm"/> is given, the
    /// <paramref name="navHtml"/> and the sign-out form), the banner, the body, and the footer. The title is encoded here;
    /// <paramref name="body"/>, <paramref name="navHtml"/> and <paramref name="signOutForm"/> are markup the caller built and encoded.
    /// </summary>
    public static string Page(SiteChrome chrome, string title, string body, string? navHtml, string? signOutForm)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<meta name=\"robots\" content=\"noindex,nofollow\"><title>").Append(E(title)).Append(E(chrome.TitleSuffix)).Append("</title>");
        sb.Append("<link rel=\"icon\" href=\"").Append(E(chrome.FaviconPath)).Append("\" type=\"image/svg+xml\"><link rel=\"stylesheet\" href=\"").Append(E(chrome.StylesheetPath)).Append("\"></head>");
        sb.Append(chrome.BodyClass.Length == 0 ? "<body>" : "<body class=\"" + E(chrome.BodyClass) + "\">");
        sb.Append("<a class=\"skip\" href=\"#main\">Skip to content</a><div class=\"wrap\">");
        sb.Append("<header class=\"top\"><a class=\"brand\" href=\"/\">").Append(LogoSvg).Append("<span>Packmoat</span>");
        if (chrome.Label.Length > 0) sb.Append("<span class=\"pill\">").Append(E(chrome.Label)).Append("</span>");
        sb.Append("</a>");
        if (signOutForm is not null) sb.Append(navHtml).Append(signOutForm);
        sb.Append("</header>");
        if (chrome.Banner is { } banner) sb.Append("<div class=\"banner\" role=\"note\">").Append(E(banner)).Append("</div>");
        sb.Append("<main id=\"main\">").Append(body).Append("</main>");
        sb.Append(chrome.FooterHtml);
        sb.Append("</div></body></html>");
        return sb.ToString();
    }
}
