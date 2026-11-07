using System.Globalization;

namespace Packmoat.Domain;

/// <summary>
/// Normalises NuGet version strings so the same version compares equal however it is written:
/// "1.0" and "1.0.0.0" become "1.0.0", build metadata after '+' is dropped, case is lowered.
/// </summary>
public static class NuGetVersionKey
{
    public static string Normalize(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var v = version.Trim().ToLowerInvariant();
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];

        var dash = v.IndexOf('-');
        var release = dash >= 0 ? v[..dash] : v;
        var prerelease = dash >= 0 ? v[dash..] : string.Empty;

        var parts = release.Split('.');
        var numbers = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (!ulong.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                return v; // not a numeric version: leave as is, the caller treats it literally
            numbers.Add(n.ToString(CultureInfo.InvariantCulture));
        }

        while (numbers.Count < 3) numbers.Add("0");
        if (numbers.Count == 4 && numbers[3] == "0") numbers.RemoveAt(3);
        return string.Join('.', numbers) + prerelease;
    }
}
