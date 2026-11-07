using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.HttpOverrides;

namespace Packmoat.Host;

/// <summary>
/// Which addresses may set X-Forwarded-For, -Proto and -Host (#233, PACKMOAT_TRUSTED_PROXIES). Loopback is always trusted (a reverse proxy
/// on the same machine, as in the deploy kit); the setting adds IP addresses or networks (CIDR, such as 10.0.0.0/8), separated by spaces,
/// commas or semicolons, for a proxy elsewhere. Unset or empty means loopback only, as before. Only the nearest proxy is believed
/// (ForwardLimit stays 1).
/// <para>It stops the start, naming the entry's position but never its value, on an entry that is not an address or a network, on a network
/// with bits set past its prefix, and on a network of everything (0.0.0.0/0 or ::/0), which would let any client choose its address.</para>
/// <para>Not registered in DI: the composition root builds the options once.</para>
/// </summary>
public static class TrustedProxies
{
    public const string Variable = "PACKMOAT_TRUSTED_PROXIES";

    // Four dotted decimal parts only, so a short form such as "10" or "10.1" (which the address parser would accept) is refused.
    private static readonly Regex DottedQuad = new(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.CultureInvariant);

    /// <summary>The networks the setting names, in order. Unset or empty: none.</summary>
    public static IReadOnlyList<System.Net.IPNetwork> Parse(string? value)
    {
        var networks = new List<System.Net.IPNetwork>();
        var entries = (value ?? "").Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            var slash = entry.IndexOf('/');
            var addressText = slash < 0 ? entry : entry[..slash];
            if (!TryAddress(addressText, out var address))
                throw new InvalidOperationException($"{Variable}: entry {i + 1} is not an IP address or a network such as 192.0.2.0/24.");
            var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            int prefix = max;
            if (slash >= 0)
            {
                var prefixText = entry[(slash + 1)..];
                if (prefixText.Length is 0 or > 3 || !prefixText.All(char.IsAsciiDigit) || (prefix = int.Parse(prefixText, System.Globalization.CultureInfo.InvariantCulture)) > max)
                    throw new InvalidOperationException($"{Variable}: entry {i + 1} is not an IP address or a network such as 192.0.2.0/24.");
            }
            if (prefix == 0)
                throw new InvalidOperationException($"{Variable}: entry {i + 1} trusts every address. Name the proxy's own address or network.");
            if (HasHostBits(address, prefix))
                throw new InvalidOperationException($"{Variable}: entry {i + 1} has bits set past its prefix. Use the network's first address, such as 192.0.2.0/24.");
            networks.Add(new System.Net.IPNetwork(address, prefix));
        }
        return networks;
    }

    // 10.0.0.5/8 is refused rather than read as 10.0.0.0/8: the operator meant one of the two, and guessing could trust far more than meant.
    private static bool HasHostBits(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var bit = prefix; bit < bytes.Length * 8; bit++)
            if ((bytes[bit / 8] & (0x80 >> (bit % 8))) != 0) return true;
        return false;
    }

    private static bool TryAddress(string text, out IPAddress address)
    {
        address = IPAddress.None;
        if (text.Contains('%', StringComparison.Ordinal)) return false;   // no zone (scope) ids
        if (!text.Contains(':', StringComparison.Ordinal) && !DottedQuad.IsMatch(text)) return false;
        if (!IPAddress.TryParse(text, out var parsed)) return false;
        address = parsed;
        return true;
    }

    /// <summary>
    /// The forwarded-headers options the server uses: X-Forwarded-For, -Proto and -Host, from loopback (127.0.0.0/8, 127.0.0.1 and ::1, as
    /// before) and from each network the setting names.
    /// <para>When the setting names any network, an X-Forwarded-Host is believed only when it is one of <paramref name="hosts"/> (the host
    /// names the server answers to): the host filter has already checked the request's own Host, so a forwarded one must not bring in a name
    /// it never saw. With no hosts, X-Forwarded-Host is then not applied at all. Unset, nothing changes: loopback's X-Forwarded-Host is
    /// believed as before.</para>
    /// </summary>
    public static ForwardedHeadersOptions Options(string? value, IEnumerable<string>? hosts = null)
    {
        var networks = Parse(value);
        var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost };
        forwarded.KnownProxies.Add(IPAddress.Loopback);
        forwarded.KnownProxies.Add(IPAddress.IPv6Loopback);
        foreach (var network in networks) forwarded.KnownIPNetworks.Add(network);
        if (networks.Count > 0)
        {
            // An empty AllowedHosts means any host, so an empty list turns X-Forwarded-Host off instead.
            var allowed = (hosts ?? []).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (allowed.Count == 0) forwarded.ForwardedHeaders &= ~ForwardedHeaders.XForwardedHost;
            foreach (var host in allowed) forwarded.AllowedHosts.Add(host);
        }
        return forwarded;
    }
}
