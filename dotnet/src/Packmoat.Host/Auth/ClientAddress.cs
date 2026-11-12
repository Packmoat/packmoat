using System.Net;
using System.Net.Sockets;

namespace Packmoat.Host.Auth;

/// <summary>
/// The key the per-address limiters count by. An IPv4 address is itself, and an IPv4 address mapped into IPv6 is the plain IPv4 address,
/// so a dual-stack socket cannot give one client two keys. Any other IPv6 address is cut to its /64 network: one home or server is usually
/// given a whole /64, so keying by the full address would let one client rotate through 2^64 keys. A missing address is <c>"unknown"</c>.
/// </summary>
/// <remarks>
/// This works on the address the request already carries. X-Forwarded-For is applied earlier, by the forwarded-headers middleware, and only
/// from loopback, so this never decides which header to trust.
/// </remarks>
public static class ClientAddress
{
    public static string Key(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);   // keep the /64 network; this also drops any scope id
        return new IPAddress(bytes) + "/64";
    }

    public static string Key(HttpContext context) => Key(context.Connection.RemoteIpAddress);

    /// <summary>
    /// The wider network an address belongs to, which <see cref="TokenGuessLimiter"/> counts by only once its address map is full.
    /// An IPv4 address, mapped or not, becomes its /24; any other IPv6 address its /48, the usual size of one site's allocation, with any
    /// scope id dropped. A missing address is <c>"unknown"</c>.
    /// </summary>
    public static string NetworkKey(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            bytes[3] = 0;   // keep the /24 network
            return new IPAddress(bytes) + "/24";
        }
        Array.Clear(bytes, 6, 10);   // keep the /48 network; this also drops any scope id
        return new IPAddress(bytes) + "/48";
    }
}

/// <summary>
/// What <see cref="TokenGuessLimiter"/> counts a client by: its <see cref="ClientAddress.Key(IPAddress?)"/>, and the
/// <see cref="ClientAddress.NetworkKey"/> it falls back to when the address map is full.
/// </summary>
public readonly record struct ClientKey(string Address, string Network)
{
    public static ClientKey Of(IPAddress? address) => new(ClientAddress.Key(address), ClientAddress.NetworkKey(address));
}
