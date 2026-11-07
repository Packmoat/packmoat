namespace Packmoat.Domain;

/// <summary>
/// The internal-prefix rule, in one place: the policy engine hides a matching package (dependency confusion), and a customer cannot waive
/// one (#157). A prefix may end with '*' (ignored); matching is case-insensitive; a prefix that is empty once trimmed is ignored rather
/// than read as "every package".
/// </summary>
public static class InternalPrefixes
{
    public static bool Matches(IEnumerable<string> prefixes, string packageId)
    {
        var id = packageId.Trim().ToLowerInvariant();
        foreach (var raw in prefixes)
        {
            var prefix = raw.Trim().TrimEnd('*').ToLowerInvariant();
            if (prefix.Length == 0) continue;
            if (id.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
