using Packmoat.Application;
using Packmoat.Host.Web;

namespace Packmoat.Host.Tests;

/// <summary>
/// The operator settings the test compositions use (#186, #187): PACKMOAT_OPERATOR_NAME, PACKMOAT_SUPPORT_EMAIL and PACKMOAT_SITE_URL, set
/// to a made-up operator, Contoso. The test compositions that pin or read the operator's wording set these, so a test names the address a
/// person sees without naming any real service.
/// </summary>
internal static class TestOperator
{
    public const string Name = "Contoso Packages";
    public const string SupportEmail = "support@contoso.example";
    public const string SiteUrl = "https://packages.contoso.example";

    // What the pages derive from them, spelled out so a test names the address a person sees.
    public const string MarketingUrl = SiteUrl;
    public const string PrivacyUrl = SiteUrl + "/privacy";
    public const string TermsUrl = SiteUrl + "/terms";
    public const string EarlyAccessUrl = SiteUrl + "/#access";

    public static readonly OperatorLinks Links = OperatorLinks.Parse(Name, SupportEmail, SiteUrl);

    public static SupportContact Support => Links.Support;

    /// <summary>Sets the test operator on a test composition's options.</summary>
    public static void Apply(PackmoatOptions o) => o.Operator = Links;
}
