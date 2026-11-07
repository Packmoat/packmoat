using Packmoat.Host.Web;

namespace Packmoat.Host.Tests;

/// <summary>The shared page chrome on its own: one record, one function, no knowledge of either site.</summary>
public class SiteChromeTests
{
    private static readonly SiteChrome Plain = new("", " - Test", "/test.css", "/favicon.svg", "", null, "<footer><p>Fixed.</p></footer>");
    private static readonly SiteChrome Labelled = Plain with { Label = "Operator", BodyClass = "operator", Banner = "Careful <now> & \"here\"" };
    private const string SignOut = "<form method=\"post\" action=\"/signout\"><button type=\"submit\">Sign out</button></form>";
    private const string Nav = "<nav aria-label=\"Test\"><a href=\"/a\">A</a></nav>";

    [Fact]
    public void A_hostile_title_is_encoded()
    {
        var html = SiteChrome.Page(Plain, "<script>alert(1)</script>\"&'", "<p>body</p>", null, null);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<title>&lt;script&gt;alert(1)&lt;/script&gt;&quot;&amp;&#x27; - Test</title>", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_chrome_has_no_inline_style_and_no_script(bool labelled)
    {
        var html = SiteChrome.Page(labelled ? Labelled : Plain, "T", "<p>body</p>", Nav, SignOut);
        Assert.DoesNotMatch(@"(?i)\sstyle\s*=", html);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", html);
        Assert.Contains("<link rel=\"stylesheet\" href=\"/test.css\">", html);
        Assert.Contains("<link rel=\"icon\" href=\"/favicon.svg\" type=\"image/svg+xml\">", html);
        Assert.Contains(SiteChrome.LogoSvg, html);
        Assert.Contains("<main id=\"main\"><p>body</p></main>", html);
        Assert.Contains("<footer><p>Fixed.</p></footer>", html);
    }

    [Fact]
    public void Nav_and_sign_out_appear_only_with_a_sign_out_form()
    {
        var signedIn = SiteChrome.Page(Plain, "T", "", Nav, SignOut);
        Assert.Contains("</a>" + Nav + SignOut + "</header>", signedIn);

        var anonymous = SiteChrome.Page(Plain, "T", "", Nav, null);
        Assert.DoesNotContain(Nav, anonymous);
        Assert.DoesNotContain("/signout", anonymous);
        Assert.Contains("</a></header>", anonymous);

        Assert.Contains("</a>" + SignOut + "</header>", SiteChrome.Page(Plain, "T", "", null, SignOut));
    }

    [Fact]
    public void The_banner_label_and_body_class_appear_only_when_set()
    {
        var plain = SiteChrome.Page(Plain, "T", "", null, null);
        Assert.DoesNotContain("role=\"note\"", plain);
        Assert.DoesNotContain("class=\"pill\"", plain);
        Assert.Contains("</head><body><a class=\"skip\"", plain);
        Assert.Contains("<span>Packmoat</span></a>", plain);

        var labelled = SiteChrome.Page(Labelled, "T", "", null, null);
        Assert.Contains("</header><div class=\"banner\" role=\"note\">Careful &lt;now&gt; &amp; &quot;here&quot;</div><main", labelled);
        Assert.Contains("<span>Packmoat</span><span class=\"pill\">Operator</span></a>", labelled);
        Assert.Contains("</head><body class=\"operator\"><a class=\"skip\"", labelled);
    }

    [Fact]
    public void The_base_stylesheet_is_chrome_only_and_loads_nothing()
    {
        var css = SiteChrome.BaseStylesheet + SiteChrome.SignInButtonStylesheet + SiteChrome.FooterStylesheet;
        Assert.Contains("--moat:#0a6a5b", css);
        Assert.DoesNotContain("--accent", css);   // the operator accent is /admin.css's own; /app.css stays as it was
        foreach (var banned in new[] { "url(", "@import", "//", "@font-face", "operator" }) Assert.DoesNotContain(banned, css, StringComparison.OrdinalIgnoreCase);
        foreach (var chrome in new[] { ".skip", ".wrap", "header.top", ".brand" }) Assert.Contains(chrome, SiteChrome.BaseStylesheet);
        Assert.StartsWith(".ms-signin{", SiteChrome.SignInButtonStylesheet);
        Assert.StartsWith("footer{", SiteChrome.FooterStylesheet);
    }
}
