using System.Text.RegularExpressions;

namespace Packmoat.Host.Admin;

/// <summary>
/// Server-side checks for every field the admin forms accept. Each returns the cleaned value, or null with a fixed error key
/// (never the offending text: the key picks one of the canned messages, so nothing a person typed is ever echoed back).
/// Lengths are capped so a form always fits the 4 KB body limit of the admin branch.
/// </summary>
public static class AdminInput
{
    public const int MaxDays = 3650;
    public const int MaxLabel = 100;
    public const int MaxReason = 200;
    public const int MaxName = 100;

    private static readonly Regex TenantId = new(@"^[a-z0-9][a-z0-9_-]{0,62}$", RegexOptions.Compiled);
    private static readonly Regex PackageId = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.Compiled);
    private static readonly Regex Prefix = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{1,98}\*?$", RegexOptions.Compiled);
    private static readonly Regex Version = new(@"^(\*|[0-9A-Za-z][0-9A-Za-z.+*-]{0,63})$", RegexOptions.Compiled);
    // Long enough for the wizard's tight prefix {org}/{projectId}/_packaging/{feedId}/ (at most 50 + 1 + 36 + 12 + 36 + 1 = 136
    // characters), with room to spare: at most 199 characters in all.
    private static readonly Regex PathPrefix = new(@"^[A-Za-z0-9][A-Za-z0-9._/-]{0,198}$", RegexOptions.Compiled);
    private static readonly Regex TokenIdShape = new(@"^tok_[0-9a-f]{12}$", RegexOptions.Compiled);
    private static readonly Regex WaiverIdShape = new(@"^wvr_[0-9a-f]{12}$", RegexOptions.Compiled);

    public static string? Id(string? v, out string? error) => Match(v, TenantId, "bad-id", out error);
    public static string? Package(string? v, out string? error) => Match(v, PackageId, "bad-package", out error);
    public static string? InternalPrefix(string? v, out string? error) => Match(v, Prefix, "bad-prefix", out error);
    public static string? WaiverVersion(string? v, out string? error) => Match(v, Version, "bad-version", out error);
    public static string? TokenId(string? v, out string? error) => Match(v, TokenIdShape, "bad-token-id", out error);
    public static string? WaiverId(string? v, out string? error) => Match(v, WaiverIdShape, "bad-waiver-id", out error);

    public static string? Name(string? v, out string? error) => Text(v, MaxName, "bad-name", out error);
    public static string? Reason(string? v, out string? error) => Text(v, MaxReason, "bad-reason", out error);
    public static string? Label(string? v, out string? error) => Text(v, MaxLabel, "bad-label", out error);

    /// <summary>Required, trimmed, no control characters (no line breaks), at most <paramref name="max"/> characters.</summary>
    private static string? Text(string? v, int max, string key, out string? error)
    {
        var t = (v ?? "").Trim();
        if (t.Length == 0 || t.Length > max || t.Any(Unsafe)) { error = key; return null; }
        error = null;
        return t;
    }

    /// <summary>Control characters and invisible or direction-changing ones (Unicode categories Cf, Zl, Zp: bidi overrides, zero-width characters, line separators).</summary>
    private static bool Unsafe(char c) => char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format
        or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator;

    private static string? Match(string? v, Regex re, string key, out string? error)
    {
        var t = (v ?? "").Trim();
        if (!re.IsMatch(t)) { error = key; return null; }
        error = null;
        return t;
    }

    /// <summary>A whole number written in digits only (no sign, no spaces, no exponent) within the range.</summary>
    public static int? Days(string? v, int min, int max, string key, out string? error)
    {
        var t = (v ?? "").Trim();
        if (t.Length is 0 or > 5 || !t.All(char.IsAsciiDigit) || !int.TryParse(t, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) || n < min || n > max)
        { error = key; return null; }
        error = null;
        return n;
    }

    /// <summary>
    /// The upstream a tenant's credential will be sent to. Strict, because the proxy attaches the customer's credential to any URL under it:
    /// https on port 443 only, no user info, query or fragment, no percent-encoding or backslash (nothing to canonicalise differently), a DNS name
    /// (never an IP literal or localhost), and a host on the operator's allowlist. Returns the canonical absolute form, ending in a slash.
    /// </summary>
    public static string? Upstream(string? v, IReadOnlySet<string> allowedHosts, out string? error)
    {
        var u = Shape(v, 200, "bad-upstream", out error);
        if (u is null) return null;
        if (u.HostNameType != UriHostNameType.Dns || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || !allowedHosts.Contains(u.Host.ToLowerInvariant()))
        { error = "upstream-host"; return null; }
        error = null;
        var abs = u.AbsoluteUri;
        return abs.EndsWith('/') ? abs : abs + "/";
    }

    /// <summary>The feed's index URL: the same checks, and it must lie under <paramref name="prefix"/> (the canonical upstream base plus the path prefix).</summary>
    public static string? Feed(string? v, string prefix, out string? error)
    {
        var u = Shape(v, 300, "bad-feed", out error);
        if (u is null) return null;
        if (!u.AbsoluteUri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { error = "feed-outside"; return null; }
        error = null;
        return u.AbsoluteUri;
    }

    private static Uri? Shape(string? v, int max, string key, out string? error)
    {
        var t = (v ?? "").Trim();
        error = key;
        if (t.Length == 0 || t.Length > max || t.Any(Unsafe) || t.Any(char.IsWhiteSpace) || t.Contains('\\') || t.Contains('%') || t.Contains('?') || t.Contains('#') || t.Contains('@')) return null;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(u.Host) || !u.IsDefaultPort) return null;
        if (!string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return null;
        error = null;
        return u;
    }

    /// <summary>The path inside the upstream host, for example "my-org/". Optional; when given it must end with a slash (one is added) and holds no "..".</summary>
    public static string? AllowedPath(string? v, out string? error)
    {
        var t = (v ?? "").Trim();
        error = null;
        if (t.Length == 0) return "";
        if (!PathPrefix.IsMatch(t) || t.Contains("..", StringComparison.Ordinal) || t.Contains("//", StringComparison.Ordinal)) { error = "bad-path-prefix"; return null; }
        return t.EndsWith('/') ? t : t + "/";
    }
}
