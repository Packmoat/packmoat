using Packmoat.Application;
using Packmoat.Host.Customer;
using Packmoat.Host.Web;

namespace Packmoat.Host.Tests;

/// <summary>
/// The operator's name, support address and website: parsed strictly at start, never echoed when refused, and rendered as links
/// only when set. With none set, the footer and the sentences use neutral words and never another operator's address.
/// </summary>
public class OperatorLinksTests
{
    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("  https://example.com  ")]
    [InlineData("https://EXAMPLE.com")]
    [InlineData("HTTPS://example.com")]
    public void A_website_is_kept_as_https_and_its_host_only(string value) =>
        Assert.Equal("https://example.com", OperatorLinks.Parse(null, null, value).SiteUrl);

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", "", "")]
    [InlineData("  ", "\t", " ")]
    public void Unset_or_blank_is_nothing_set(string? name, string? email, string? site)
    {
        var op = OperatorLinks.Parse(name, email, site);
        Assert.Equal((null, null, null, null, null), (op.Name, op.SupportEmail, op.SiteUrl, op.PrivacyUrl, op.TermsUrl));
        Assert.Equal(SupportContact.None, op.Support);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com/privacy")]
    [InlineData("https://example.com?q=1")]
    [InlineData("https://example.com/#access")]
    [InlineData("https://user@example.com")]
    [InlineData("https://example.com:8443")]
    [InlineData("https://localhost")]
    [InlineData("example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://exa\"mple.com")]
    [InlineData("https://exa<mple.com")]
    [InlineData("https://ex ample.com")]
    public void A_website_that_is_not_a_bare_https_address_stops_the_start_without_echoing_it(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => OperatorLinks.Parse(null, null, value));
        Assert.Equal(OperatorLinks.SiteVariable + " must be an https address with no path, such as https://example.com.", ex.Message);   // fixed text: nothing echoed
    }

    [Theory]
    [InlineData("support")]
    [InlineData("support@")]
    [InlineData("@example.com")]
    [InlineData("support@example")]
    [InlineData("sup port@example.com")]
    [InlineData("<support>@example.com")]
    [InlineData("support@example.com\"")]
    [InlineData("mailto:support@example.com")]
    [InlineData("a@b@example.com")]
    public void An_address_that_is_not_an_email_stops_the_start_without_echoing_it(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => OperatorLinks.Parse(null, value, null));
        Assert.Equal(OperatorLinks.EmailVariable + " must be an email address such as support@example.com.", ex.Message);   // fixed text: nothing echoed
    }

    [Theory]
    [InlineData("Contoso <IT>")]
    [InlineData("Contoso\nIT")]
    [InlineData("Contoso {0}")]
    [InlineData("Contoso \"IT\"")]
    [InlineData("R&D")]
    public void A_name_that_is_not_plain_text_stops_the_start_without_echoing_it(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => OperatorLinks.Parse(value, null, null));
        Assert.Equal(OperatorLinks.NameVariable + " must be a plain name of at most 80 characters, such as Contoso IT.", ex.Message);   // fixed text: nothing echoed
    }

    [Fact]
    public void A_name_longer_than_80_characters_stops_the_start() =>
        Assert.Throws<InvalidOperationException>(() => OperatorLinks.Parse(new string('a', 81), null, null));

    [Fact]
    public void With_nothing_set_the_words_are_neutral()
    {
        var s = SupportContact.None;
        Assert.Equal(("contact whoever runs Packmoat for you", "Contact whoever runs Packmoat for you", "Whoever runs Packmoat for you"),
            (s.Phrase, s.CapitalPhrase, s.OperatorSubject));
        var named = OperatorLinks.Parse("Contoso IT", null, null).Support;
        Assert.Equal(("contact Contoso IT", "Contoso IT"), (named.Phrase, named.OperatorSubject));
    }

    [Fact]
    public void A_self_hosted_footer_never_offers_early_access_even_with_a_website()
    {
        var site = OperatorLinks.Parse(null, null, "https://contoso.example");
        Assert.Equal("", ((Packmoat.Host.Modules.ICustomerSiteModule)NoCustomerSiteModule.Instance).FooterLinks(site));
        var page = AppHtml.Page(site, NoCustomerSiteModule.Instance, "Home", "<p>body</p>");
        Assert.Contains("<a href=\"https://contoso.example/privacy\">Privacy</a></nav>", page);
        Assert.DoesNotContain("early access", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#access", page);
    }

    [Fact]   // mutation: the footer drops the support link
    public void The_footer_shows_each_link_only_when_it_is_set()
    {
        Assert.Equal("<footer><p>Need help? Contact whoever runs Packmoat for you.</p></footer>", AppHtml.Footer(OperatorLinks.None, "<a href=\"x\">module</a>"));
        Assert.Equal("<footer><p>Need help? Contact Contoso IT.</p></footer>", AppHtml.Footer(OperatorLinks.Parse("Contoso IT", null, null), ""));
        Assert.Equal("<footer><p>Need help? Email <a href=\"mailto:it@contoso.example\">it@contoso.example</a>.</p></footer>",
            AppHtml.Footer(OperatorLinks.Parse(null, "it@contoso.example", null), ""));
        Assert.Equal("<footer><nav aria-label=\"contoso.example links\"><a href=\"https://contoso.example\">contoso.example</a><a href=\"https://contoso.example/privacy\">Privacy</a></nav>" +
            "<p>Need help? Contact whoever runs Packmoat for you.</p></footer>", AppHtml.Footer(OperatorLinks.Parse(null, null, "https://contoso.example"), ""));
        // A module's links follow the website's, and only when the website is set.
        Assert.Equal("<footer><nav aria-label=\"contoso.example links\"><a href=\"https://contoso.example\">contoso.example</a><a href=\"https://contoso.example/privacy\">Privacy</a>" +
            "<a href=\"x\">module</a></nav><p>Need help? Email <a href=\"mailto:it@contoso.example\">it@contoso.example</a>.</p></footer>",
            AppHtml.Footer(OperatorLinks.Parse(null, "it@contoso.example", "https://contoso.example"), "<a href=\"x\">module</a>"));
    }

    [Fact]
    public void With_no_support_address_there_is_no_support_button()
    {
        Assert.Null(AppSite.SupportButton(OperatorLinks.None));
        Assert.Null(AppSite.SupportButton(OperatorLinks.Parse("Contoso IT", null, "https://contoso.example")));
        Assert.Equal("<a class=\"btn\" href=\"mailto:it@contoso.example\">Email Contoso IT support</a>", AppSite.SupportButton(OperatorLinks.Parse("Contoso IT", "it@contoso.example", null)));
    }

    [Fact]
    public void The_operators_name_is_encoded_in_the_footer_and_the_support_button()
    {
        var op = OperatorLinks.Parse("Café IT", "it@contoso.example", "https://contoso.example");
        Assert.Contains("<nav aria-label=\"Caf&#xE9; IT links\">", AppHtml.Footer(op, ""));
        Assert.Equal("<a class=\"btn\" href=\"mailto:it@contoso.example\">Email Caf&#xE9; IT support</a>", AppSite.SupportButton(op));
    }

    [Theory]
    [InlineData("https://feeds.example.com", "feeds.other.example", "https://feeds.example.com/v3/index.json")]
    [InlineData("https://feeds.example.com/", "feeds.other.example", "https://feeds.example.com/v3/index.json")]
    [InlineData(null, "Feeds.Example.com", "https://feeds.example.com/v3/index.json")]
    [InlineData("", "feeds.example.com", "https://feeds.example.com/v3/index.json")]
    public void The_token_pages_name_this_servers_own_feed_address(string? publicBase, string firstHost, string expected) =>
        Assert.Equal(expected, AppSite.FeedIndexUrl(publicBase, [firstHost, "second.example"]));
}
