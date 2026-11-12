using System.Reflection;

namespace Packmoat.Host;

/// <summary>
/// The commit this build came from, so anyone can tell from /healthz whether a merge has reached the server. The image build
/// passes the commit as -p:SourceRevisionId, which the SDK appends to the informational version: "1.0.0+&lt;40-hex sha&gt;".
/// </summary>
public static class BuildInfo
{
    public const string Unknown = "unknown";
    public const int ShortLength = 12;

    /// <summary>The first 12 hex characters of the build commit, or "unknown".</summary>
    public static string Commit { get; } = ParseCommit(
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>
    /// Pure: takes an informational version and returns the short commit, or "unknown" unless the part after the last '+' is a
    /// full 40-character hex SHA-1. Anything else (no '+', "unknown", too short, not hex) is "unknown"; nothing is guessed.
    /// </summary>
    public static string ParseCommit(string? informationalVersion)
    {
        if (string.IsNullOrEmpty(informationalVersion)) return Unknown;
        var plus = informationalVersion.LastIndexOf('+');
        if (plus < 0) return Unknown;
        var sha = informationalVersion[(plus + 1)..];
        if (sha.Length != 40 || !sha.All(char.IsAsciiHexDigit)) return Unknown;
        return sha[..ShortLength].ToLowerInvariant();
    }
}
