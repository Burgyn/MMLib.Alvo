using MMLib.Alvo.Events.Internal;

using System.Net;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// Which resolved addresses a webhook may be delivered to without an explicit opt-in: the globally reachable
/// ones, and nothing from the IANA special-purpose registries a server-side request could be aimed at.
/// </summary>
public sealed class WebhookAddressClassifierTests
{
    /// <summary>Every non-public IPv4 range the registry marks as not globally reachable is refused.</summary>
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.0.9")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.0.0.170")]
    [InlineData("192.0.2.1")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("192.88.99.1")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    public void A_non_public_ipv4_address_is_not_public(string address) =>
        WebhookAddressClassifier.IsPublic(IPAddress.Parse(address)).ShouldBeFalse();

    /// <summary>
    /// Every non-public IPv6 range is refused, including the forms that smuggle an IPv4 address inside an IPv6
    /// one — mapped, compatible, NAT64 and 6to4 — which is the classic way past a check that only reads IPv4.
    /// </summary>
    [Theory]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::127.0.0.1")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("64:ff9b:1::1")]
    [InlineData("2002:a9fe:a9fe::1")]
    [InlineData("2001::1")]
    [InlineData("2001:db8::1")]
    [InlineData("100::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("fe80::1")]
    [InlineData("fec0::1")]
    [InlineData("ff02::1")]
    [InlineData("3fff::1")]
    [InlineData("3fff:fff:ffff::1")]
    [InlineData("5f00::1")]
    [InlineData("::ffff:0:8.8.8.8")]
    [InlineData("::ffff:0:a9fe:a9fe")]
    public void A_non_public_ipv6_address_is_not_public(string address) =>
        WebhookAddressClassifier.IsPublic(IPAddress.Parse(address)).ShouldBeFalse();

    /// <summary>A globally reachable address — in either family, or mapped — is public.</summary>
    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("192.169.0.1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:808:808::1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2a00:1450:4001::200e")]
    [InlineData("192.88.100.1")]
    [InlineData("3fff:1000::1")]
    [InlineData("5f01::1")]
    public void A_globally_reachable_address_is_public(string address) =>
        WebhookAddressClassifier.IsPublic(IPAddress.Parse(address)).ShouldBeTrue();

    /// <summary>
    /// Loopback is recognised through the IPv6 wrappers too, so a descriptor's <c>http://[::ffff:127.0.0.1]</c>
    /// is the same declared loopback as <c>http://127.0.0.1</c>.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData("8.8.8.8", false)]
    public void Loopback_is_recognised_in_every_family(string address, bool loopback) =>
        WebhookAddressClassifier.IsLoopback(IPAddress.Parse(address)).ShouldBe(loopback);
}
