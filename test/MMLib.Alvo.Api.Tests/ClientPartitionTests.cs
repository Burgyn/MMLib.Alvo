using MMLib.Alvo.Api.Internal;
using System.Net;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The partition a per-client budget is charged under — the token endpoint's, and the standalone host's credential
/// posts, which call this same type (moved from the host suite's set-password facts when the two copies became one).
/// </summary>
public sealed class ClientPartitionTests
{
    /// <summary>
    /// A client is its address: an IPv6 one by its /64, which a single host is routinely handed whole, and an
    /// IPv4-mapped one as the IPv4 address, so a dual-stack client has one budget.
    /// </summary>
    /// <param name="first">One address.</param>
    /// <param name="second">Another.</param>
    /// <param name="same">Whether they are one client.</param>
    [Theory]
    [InlineData("2001:db8:1:2:aaaa::1", "2001:db8:1:2:ffff:ffff:ffff:ffff", true)]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:3::1", false)]
    [InlineData("::ffff:10.42.0.7", "10.42.0.7", true)]
    [InlineData("10.42.0.7", "10.42.0.8", false)]
    public void A_client_is_its_ipv4_address_or_its_ipv6_slash_64(string first, string second, bool same)
        => (ClientPartition.Of(IPAddress.Parse(first)) == ClientPartition.Of(IPAddress.Parse(second))).ShouldBe(same);

    /// <summary>The partition keys themselves, so a mask that kept too much or too little shows as a value, not a count.</summary>
    /// <param name="address">The address, or <see langword="null"/> for an unknown one.</param>
    /// <param name="partition">The key it is charged under.</param>
    [Theory]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("::ffff:10.42.0.7", "10.42.0.7")]
    [InlineData("10.42.0.7", "10.42.0.7")]
    [InlineData(null, "")]
    public void The_partition_key_is_the_address_or_its_slash_64(string? address, string partition)
        => ClientPartition.Of(address is null ? null : IPAddress.Parse(address)).ShouldBe(partition);
}
