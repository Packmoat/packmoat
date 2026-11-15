using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Packmoat.Host;

/// <summary>
/// Which requests may skip the host-name filter: a GET of /healthz or /readyz whose Host is an IP address, as a load balancer or an
/// orchestrator sends it. Nothing else skips the filter. The Host is read as sent, before any forwarded header.
/// </summary>
internal static partial class HealthHost
{
    public static bool Allows(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method)) return false;
        if (request.PathBase.HasValue) return false;
        var path = request.Path.Value;
        if (!string.Equals(path, "/healthz", StringComparison.Ordinal) && !string.Equals(path, Readiness.Route, StringComparison.Ordinal)) return false;
        // The header as sent: Request.Host.Value decodes an internationalised name and throws on a malformed one.
        return IsIpLiteral(request.Headers.Host.ToString());
    }

    /// <summary>An IPv4 dotted quad (each part 0 to 255, no leading zero) or a bracketed IPv6 address with no zone, with an optional port of 1 to 65535.</summary>
    public static bool IsIpLiteral(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (IPv4.Match(host) is { Success: true } four) return PortOk(four.Groups["port"]);
        if (IPv6.Match(host) is { Success: true } six)
        {
            if (!PortOk(six.Groups["port"])) return false;
            return IPAddress.TryParse(six.Groups["addr"].Value, out var address)
                && address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0;
        }
        return false;
    }

    private static bool PortOk(Group port) => !port.Success || (int.TryParse(port.Value, out var p) && p is >= 1 and <= 65535);

    [GeneratedRegex(@"^(?:(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])(?::(?<port>[1-9][0-9]{0,4}))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex IPv4Pattern();
    [GeneratedRegex(@"^\[(?<addr>[0-9A-Fa-f:.]+)\](?::(?<port>[1-9][0-9]{0,4}))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex IPv6Pattern();

    private static readonly Regex IPv4 = IPv4Pattern();
    private static readonly Regex IPv6 = IPv6Pattern();
}

/// <summary>Options that never change, for a middleware that wants an options monitor.</summary>
internal sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
