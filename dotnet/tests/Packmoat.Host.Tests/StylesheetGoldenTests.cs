using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Packmoat.Host.Admin;
using Packmoat.Host.Customer;
using static Packmoat.Host.Tests.AdminSiteFixture;

namespace Packmoat.Host.Tests;

/// <summary>
/// Golden hashes of what a person sees: both stylesheets and the signed-out operator page. Any change to them, even a single character,
/// fails here on purpose, so a look-and-feel change is never made by accident. When the change is meant, put the new hash below and attach
/// before and after screenshots (1780, 1280 and 390 px wide) to the pull request.
/// </summary>
[Collection("sqlite-pools")]
public class StylesheetGoldenTests
{
    private const string AdminCssSha256 = "23fd80b304871e8a494e9cadfebae8d6826a27220a61559f6c30d6e9ff1bb495";
    private const string AppCssSha256 = "720c9184d2e00aa0598328d293100b3d4634b87112f04b49c9c18283b48c7060";
    private const string SignedOutAdminPageSha256 = "4abd68ac7ee47f9335eea2dc2196aa98fc5824ac2f6cfdc93182868b8114c3fb";

    /// <summary>Line endings are normalised, so a checkout with CRLF line endings hashes the same.</summary>
    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n"))));

    private static void AssertGolden(string what, string expected, string actual) =>
        Assert.True(expected == actual,
            $"{what} changed: SHA-256 is now {actual}, the golden value is {expected}. If the change is meant, update the hash in " +
            $"{nameof(StylesheetGoldenTests)} and attach before and after screenshots (1780, 1280 and 390 px wide) to the pull request.");

    [Fact]
    public void The_admin_stylesheet_matches_its_golden_hash() => AssertGolden("/admin.css", AdminCssSha256, Sha256(AdminHtml.Stylesheet));

    [Fact]
    public void The_customer_stylesheet_matches_its_golden_hash() => AssertGolden("/app.css", AppCssSha256, Sha256(AppHtml.Stylesheet));

    [Fact]
    public async Task The_signed_out_admin_page_matches_its_golden_hash()
    {
        await using var f = await StartAsync();
        var (status, body, _) = await f.Browser().GetTextAsync("/");
        Assert.Equal(200, status);
        // The antiforgery token is new on every request; everything else on the page is fixed.
        var normalised = Regex.Replace(body, "(name=\"" + Regex.Escape(AdminSite.AntiforgeryField) + "\" value=\")[^\"]+\"", "$1TOKEN\"");
        Assert.NotEqual(body, normalised);
        AssertGolden("The signed-out admin page", SignedOutAdminPageSha256, Sha256(normalised));
    }
}
