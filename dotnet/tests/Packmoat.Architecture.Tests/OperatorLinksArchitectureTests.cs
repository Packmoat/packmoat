using System.Text.RegularExpressions;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// Self-hosting (#186): the hosted service's website and support address are settings (PACKMOAT_OPERATOR_NAME, PACKMOAT_SUPPORT_EMAIL,
/// PACKMOAT_SITE_URL), never code. No string in src/ names packmoat.com, and only the composition root reads the three settings: everything
/// else is handed them. The root reads them through Settings.Get (#233).
/// </summary>
public class OperatorLinksArchitectureTests
{
    private static readonly string[] Variables = ["PACKMOAT_OPERATOR_NAME", "PACKMOAT_SUPPORT_EMAIL", "PACKMOAT_SITE_URL"];

    // A string literal (regular, verbatim or interpolated, on one line) that names the hosted domain. Comments are prose, not output.
    private static readonly Regex HostedDomain = new(@"""[^""\r\n]*packmoat\.com[^""\r\n]*""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Packmoat.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Packmoat.sln not found.");
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>The code part of a line: a whole-line comment is dropped. (A trailing comment holding a quoted packmoat.com would be flagged: say it without quotes.)</summary>
    internal static string[] HostedDomainStrings(string source) => source.Split('\n')
        .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
        .SelectMany(line => HostedDomain.Matches(line).Select(m => m.Value))
        .ToArray();

    [Fact]
    public void No_string_in_the_product_names_the_hosted_domain()
    {
        var files = SourceFiles().ToArray();
        Assert.Contains(files, f => Path.GetFileName(f) == "AppHtml.cs");
        var offenders = files.SelectMany(f => HostedDomainStrings(File.ReadAllText(f)).Select(s => $"{Path.GetRelativePath(RepoRoot(), f)}: {s}")).ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_scanner_sees_a_hosted_string_and_ignores_comments()
    {
        Assert.Equal(["\"https://packmoat.com/privacy\""], HostedDomainStrings("    public const string PrivacyUrl = \"https://packmoat.com/privacy\";"));
        Assert.Equal(["\"Email support@packmoat.com.\""], HostedDomainStrings("x = \"Email support@packmoat.com.\";"));
        Assert.NotEmpty(HostedDomainStrings("extra = \"<a class=\\\"btn\\\" href=\\\"https://packmoat.com/\\\">Go</a>\";"));   // inside escaped quotes too
        Assert.Empty(HostedDomainStrings("/// <summary>The mark, as on packmoat.com.</summary>"));
        Assert.Empty(HostedDomainStrings("    // packmoat.com/privacy changes only after that"));
        Assert.Empty(HostedDomainStrings("x = \"https://contoso.example\";"));
    }

    [Fact]
    public void Only_the_composition_root_reads_the_operator_settings()
    {
        var readers = SourceFiles()
            .Where(f => File.ReadAllText(f) is var s && (Variables.Any(v => s.Contains(v, StringComparison.Ordinal)) || s.Contains("OperatorLinks.NameVariable", StringComparison.Ordinal))
                && s.Contains("Settings.Get(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(Path.Combine(RepoRoot(), "src"), f).Replace('\\', '/'))
            .ToArray();
        Assert.Equal(["Packmoat.Host/PackmoatApp.cs"], readers);
        // Everything else is handed them: the customer pages through the site's settings, the checks through their options.
        var parse = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Packmoat.Host", "Web", "OperatorLinks.cs"));
        Assert.DoesNotContain("GetEnvironmentVariable", parse);
        Assert.DoesNotContain("Settings.Get(", parse);
    }
}
