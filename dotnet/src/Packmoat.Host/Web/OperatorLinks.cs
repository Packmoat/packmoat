using System.Text.RegularExpressions;
using Packmoat.Application;

namespace Packmoat.Host.Web;

/// <summary>
/// Who runs this server, as the customer pages name them (#186): the operator's name (PACKMOAT_OPERATOR_NAME), support address
/// (PACKMOAT_SUPPORT_EMAIL) and website (PACKMOAT_SITE_URL). The privacy policy and the terms are derived from the website:
/// &lt;site&gt;/privacy and &lt;site&gt;/terms. Read and checked once at start; a bad value stops the start without being echoed. Each is
/// optional: unset, the link or address is left out, or the sentence uses neutral words. Never another operator's.
/// </summary>
public sealed partial class OperatorLinks
{
    public const string NameVariable = "PACKMOAT_OPERATOR_NAME";
    public const string EmailVariable = "PACKMOAT_SUPPORT_EMAIL";
    public const string SiteVariable = "PACKMOAT_SITE_URL";

    /// <summary>Nothing set: no links, no address, neutral words.</summary>
    public static readonly OperatorLinks None = new(SupportContact.None, null);

    private OperatorLinks(SupportContact support, string? siteUrl)
    {
        Support = support;
        SiteUrl = siteUrl;
        SiteName = siteUrl is null ? null : new Uri(siteUrl).Host;
    }

    /// <summary>The operator's name and support address, as every sentence that names them uses them.</summary>
    public SupportContact Support { get; }

    /// <summary>The operator's name, or null.</summary>
    public string? Name => Support.OperatorName;

    /// <summary>The support address, or null. Checked at start to letters, digits and . _ % + - @ only, so it is safe in markup as it is.</summary>
    public string? SupportEmail => Support.Email;

    /// <summary>The website with no trailing slash (https://example.com), or null. Checked at start to plain URL characters only.</summary>
    public string? SiteUrl { get; }

    /// <summary>The website's host name, the link text for <see cref="SiteUrl"/> (example.com), or null.</summary>
    public string? SiteName { get; }

    /// <summary>&lt;site&gt;/privacy, or null.</summary>
    public string? PrivacyUrl => SiteUrl is null ? null : SiteUrl + "/privacy";

    /// <summary>&lt;site&gt;/terms, or null.</summary>
    public string? TermsUrl => SiteUrl is null ? null : SiteUrl + "/terms";

    /// <summary>
    /// Checks the three values. Empty or unset is allowed for each. Anything else that is not a plain name (at most 80 characters, no
    /// control characters, angle brackets, braces, quotes or ampersands), an email address, or an absolute https URL with no path, query,
    /// fragment, user or port stops the start. The message names the setting, never the value.
    /// </summary>
    public static OperatorLinks Parse(string? name, string? email, string? siteUrl)
    {
        name = Blank(name);
        email = Blank(email);
        siteUrl = Blank(siteUrl);
        if (name is not null && (name.Length > 80 || name.Any(c => char.IsControl(c) || c is '<' or '>' or '{' or '}' or '"' or '\'' or '&')))
            throw new InvalidOperationException(NameVariable + " must be a plain name of at most 80 characters, such as Contoso IT.");
        if (email is not null && (email.Length > 254 || !EmailShape().IsMatch(email)))
            throw new InvalidOperationException(EmailVariable + " must be an email address such as support@example.com.");
        if (siteUrl is not null)
        {
            var bare = siteUrl.EndsWith('/') ? siteUrl[..^1] : siteUrl;
            if (!SiteShape().IsMatch(bare) || !Uri.TryCreate(bare, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new InvalidOperationException(SiteVariable + " must be an https address with no path, such as https://example.com.");
            siteUrl = "https://" + uri.Host;
        }
        return new OperatorLinks(new SupportContact(name, email), siteUrl);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^[A-Za-z0-9._%+-]{1,64}@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailShape();

    /// <summary>https:// and a DNS name only: nothing that needs escaping in an attribute, since the address goes into href as it is.</summary>
    [GeneratedRegex(@"^https://[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SiteShape();

    public override string ToString() => "OperatorLinks";
}
