using System.Net;
using System.Net.Sockets;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// Whether a resolved address is one a webhook may reach without the host's explicit opt-in: a globally
/// reachable unicast address, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ranges are the IANA IPv4 and IPv6 Special-Purpose Address Registries' "not globally reachable"
/// entries</b> — private (RFC 1918, RFC 4193 ULA), loopback, link-local (which is where
/// <c>169.254.169.254</c>, every major cloud's instance-metadata endpoint, lives), CGNAT <c>100.64/10</c>
/// (Alibaba's metadata endpoint), the documentation (including RFC 9637's <c>3fff::/20</c>) and benchmarking
/// blocks, SRv6 SIDs (<c>5f00::/16</c>, RFC 9602), multicast, reserved and
/// unspecified — the list the OWASP SSRF Prevention Cheat Sheet points at rather than restating.
/// </para>
/// <para>
/// <b>An IPv4 address carried inside an IPv6 one is judged as the IPv4 address it carries.</b> IPv4-mapped
/// (<c>::ffff:0:0/96</c>), NAT64 (<c>64:ff9b::/96</c>) and 6to4 (<c>2002::/16</c>) all route to an embedded
/// IPv4 address, so <c>::ffff:169.254.169.254</c> is the metadata endpoint however it is spelled — the classic
/// way past a check that reads only one family.
/// </para>
/// <para>
/// <b>Three deliberate deviations from the registry, all toward refusing.</b> The deprecated IPv4-compatible
/// block <c>::/96</c> and the deprecated SIIT IPv4-translated block <c>::ffff:0:0:0/96</c> (RFC 2765) are
/// refused whole rather than unwrapped, and <c>2001::/23</c> (IETF protocol assignments, Teredo included) is
/// refused whole although a few anycast entries inside it are globally reachable — no webhook receiver lives
/// there, and Teredo obfuscates the IPv4 address it tunnels to. The deprecated 6to4 relay anycast
/// <c>192.88.99.0/24</c> (RFC 7526) is refused for the same reason.
/// </para>
/// </remarks>
internal static class WebhookAddressClassifier
{
    /// <summary>Whether <paramref name="address"/> is globally reachable, judged through any IPv4 it embeds.</summary>
    /// <param name="address">A resolved or literal address.</param>
    /// <returns><see langword="true"/> unless the address falls in a non-public range.</returns>
    internal static bool IsPublic(IPAddress address)
    {
        var judged = Unwrap(address);

        return !Array.Exists(_nonPublicNetworks, network => network.Contains(judged));
    }

    /// <summary>Whether <paramref name="address"/> is loopback, in either family or IPv4-mapped.</summary>
    /// <param name="address">A resolved or literal address.</param>
    /// <returns><see langword="true"/> for <c>127.0.0.0/8</c>, <c>::1</c> and their mapped forms.</returns>
    internal static bool IsLoopback(IPAddress address) => IPAddress.IsLoopback(Unwrap(address));

    /// <summary>The IPv4 address an IPv6 transition form routes to, or the address itself.</summary>
    /// <param name="address">A resolved or literal address.</param>
    /// <returns>The address every allow and deny decision is made against.</returns>
    internal static IPAddress Unwrap(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }

        return EmbeddedIPv4(address) ?? address;
    }

    private static IPAddress? EmbeddedIPv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (_nat64.Contains(address))
        {
            return new IPAddress(bytes.AsSpan(12, 4));
        }

        return _sixToFour.Contains(address) ? new IPAddress(bytes.AsSpan(2, 4)) : null;
    }

    private static readonly IPNetwork _nat64 = IPNetwork.Parse("64:ff9b::/96");

    private static readonly IPNetwork _sixToFour = IPNetwork.Parse("2002::/16");

    private static readonly IPNetwork[] _nonPublicNetworks =
    [
        .. new[]
        {
            "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
            "192.0.0.0/24", "192.0.2.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24",
            "192.88.99.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4",
            "::/96", "::ffff:0:0:0/96", "64:ff9b:1::/48", "100::/64", "2001::/23", "2001:db8::/32", "3fff::/20",
            "5f00::/16", "fc00::/7", "fe80::/10", "fec0::/10", "ff00::/8",
        }.Select(network => IPNetwork.Parse(network)),
    ];
}
