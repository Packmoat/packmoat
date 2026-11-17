using System.Text.RegularExpressions;

namespace Packmoat.Architecture.Tests;

/// <summary>
/// CHANGELOG.md follows Keep a Changelog: an "Unreleased" heading, then one "## [X.Y.Z] - YYYY-MM-DD" heading per release, newest first,
/// with only the standard kinds of change under each. It names no issue number (the file is public), and the newest dated version is the
/// version the Helm chart belongs to (its appVersion), so a release cannot be written up without the chart moving, or the other way round.
/// </summary>
public class ChangelogTests
{
    private static readonly Regex Heading = new(@"^## \[(?<v>\d+\.\d+\.\d+)\] - (?<d>\d{4}-\d{2}-\d{2})$", RegexOptions.CultureInvariant);
    private static readonly Regex Kind = new(@"^### (Added|Changed|Deprecated|Removed|Fixed|Security)$", RegexOptions.CultureInvariant);
    private static readonly Regex IssueNumber = new(@"#\d+", RegexOptions.CultureInvariant);

    /// <summary>What is wrong with the changelog text, one problem per entry; empty when it is right.</summary>
    internal static string[] Problems(string text, string chartAppVersion)
    {
        var problems = new List<string>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines[0] != "# Changelog") problems.Add("the first line is not '# Changelog'");
        var headings = lines.Where(l => l.StartsWith("## ", StringComparison.Ordinal)).ToArray();
        if (headings.Length == 0 || headings[0] != "## [Unreleased]") problems.Add("the first '## ' heading is not '## [Unreleased]'");
        var versions = new List<Version>();
        foreach (var h in headings.Skip(1))
        {
            var m = Heading.Match(h);
            if (!m.Success) { problems.Add($"not '## [X.Y.Z] - YYYY-MM-DD': {h}"); continue; }
            versions.Add(Version.Parse(m.Groups["v"].Value));
            if (!DateOnly.TryParseExact(m.Groups["d"].Value, "yyyy-MM-dd", out _)) problems.Add($"not a date: {h}");
        }
        for (var i = 1; i < versions.Count; i++)
            if (versions[i] >= versions[i - 1]) problems.Add($"versions are not strictly descending: {versions[i - 1]} then {versions[i]}");
        foreach (var l in lines.Where(l => l.StartsWith("### ", StringComparison.Ordinal) && !Kind.IsMatch(l)))
            problems.Add($"not a Keep a Changelog kind of change: {l}");
        for (var i = 0; i < lines.Length; i++)
            foreach (Match m in IssueNumber.Matches(lines[i])) problems.Add($"line {i + 1} names an issue number: {m.Value}");
        if (versions.Count == 0) problems.Add("no released version");
        else if (versions[0].ToString() != chartAppVersion)
            problems.Add($"the newest version {versions[0]} is not the chart's appVersion {chartAppVersion}");
        return problems.ToArray();
    }

    private static string ChartAppVersion()
    {
        var m = Regex.Match(CoreRoot.Read("deploy", "helm", "packmoat", "Chart.yaml"), @"(?m)^appVersion:\s*""?([^""\s#]+)""?\s*$");
        Assert.True(m.Success, "Chart.yaml has no appVersion line");
        return m.Groups[1].Value;
    }

    [Fact]   // mutations: Chart appVersion 0.1.1; a #123 in CHANGELOG.md; two versions out of order
    public void The_changelog_is_Keep_a_Changelog_newest_first_with_no_issue_numbers_and_matches_the_chart()
    {
        var problems = Problems(CoreRoot.Read("CHANGELOG.md"), ChartAppVersion());
        Assert.True(problems.Length == 0, "CHANGELOG.md:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void The_check_sees_each_kind_of_mistake()
    {
        const string Good = "# Changelog\n\n## [Unreleased]\n\n## [0.2.0] - 2026-12-01\n\n### Changed\n\n- x\n\n## [0.1.0] - 2026-11-17\n\n### Added\n\n- y\n";
        Assert.Empty(Problems(Good, "0.2.0"));
        Assert.Contains(Problems(Good, "0.1.0"), p => p.Contains("appVersion"));
        Assert.Contains(Problems(Good.Replace("- y", "- y (#12)"), "0.2.0"), p => p.Contains("issue number"));
        Assert.Contains(Problems(Good.Replace("## [0.2.0] - 2026-12-01", "## [0.0.9] - 2026-12-01"), "0.0.9"), p => p.Contains("descending"));
        Assert.Contains(Problems(Good.Replace("## [Unreleased]\n\n", ""), "0.2.0"), p => p.Contains("Unreleased"));
        Assert.Contains(Problems(Good.Replace("2026-11-17", "17 Nov 2026"), "0.2.0"), p => p.Contains("YYYY"));
        Assert.Contains(Problems(Good.Replace("### Added", "### News"), "0.2.0"), p => p.Contains("kind of change"));
    }
}
