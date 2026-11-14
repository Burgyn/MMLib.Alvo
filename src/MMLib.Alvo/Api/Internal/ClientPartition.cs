using System.Net;
using System.Net.Sockets;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The partition a per-client budget is charged under: an IPv4 address as is, an IPv6 address by its /64, an
/// IPv4-mapped one as the IPv4 address.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the /64.</b> A single host is routinely handed a whole IPv6 /64, so a budget per address would let one client
/// claim as many budgets as it likes. A dual-stack client presenting its IPv4 address mapped into IPv6 has one budget,
/// not two. Only the connection's remote address is read — never a request header — so behind a proxy the partition is
/// honest only when the host's forwarded-headers handling sets that address.
/// </para>
/// <para>
/// <b>One copy, two budgets.</b> The core charges the token endpoint's budget with it (<c>AlvoRateLimits</c>), and the
/// standalone host its dashboard credential posts (<c>AlvoAdminCredentialLimit</c>), through an
/// <c>InternalsVisibleTo</c> granted for this type alone (see the core's <c>AssemblyInfo</c>), so the two budgets
/// cannot partition differently. Its facts are <c>ClientPartitionTests</c> in the Data API suite.
/// </para>
/// </remarks>
internal static class ClientPartition
{
    /// <summary>The partition <paramref name="address"/> is charged under; the empty string when it is unknown.</summary>
    /// <param name="address">The connection's remote address, or <see langword="null"/>.</param>
    internal static string Of(IPAddress? address)
    {
        if (address is null)
        {
            return string.Empty;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }
}
