using Microsoft.Extensions.Options;

using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;

using System.Net;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The connect-time decision a webhook delivery makes about <em>where</em> it is about to open a socket: the
/// addresses a host name resolved to, judged before any byte leaves the process.
/// </summary>
/// <remarks>
/// The resolver is injected, so a name that "resolves to" a private or loopback address is a fact of the test
/// rather than of whatever DNS the machine running it has — the rebinding case is exactly the one real DNS
/// cannot be relied on to reproduce.
/// </remarks>
public sealed class WebhookEgressGuardTests
{
    /// <summary>A name that resolves only to public addresses is connected to, at those addresses.</summary>
    [Fact]
    public async Task A_name_resolving_to_public_addresses_is_permitted()
    {
        var guard = Guard(Resolving("93.184.215.14", "2606:2800:21f:cb07:6820:80da:af6b:8b2c"));

        var addresses = await guard.PermittedAddressesAsync("hooks.example.com", Cancellation);

        addresses.Select(address => address.ToString())
            .ShouldBe(["93.184.215.14", "2606:2800:21f:cb07:6820:80da:af6b:8b2c"]);
    }

    /// <summary>
    /// <b>A public-looking name that resolves to a private address is refused</b> — the SSRF case an apply-time
    /// URL check cannot see, because the URL is a perfectly good <c>https</c> name.
    /// </summary>
    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("169.254.169.254")]
    [InlineData("100.100.100.200")]
    [InlineData("fd00:ec2::254")]
    [InlineData("::ffff:192.168.0.10")]
    public async Task A_name_resolving_to_a_non_public_address_is_refused(string resolved)
    {
        var guard = Guard(Resolving(resolved));

        var refusal = await Should.ThrowAsync<HttpRequestException>(
            () => guard.PermittedAddressesAsync("hooks.example.com", Cancellation));

        refusal.Message.ShouldContain(IPAddress.Parse(resolved).ToString());
        refusal.Message.ShouldContain(AlvoEventOptionsConfiguration.WebhookAllowedNetworksKey);
    }

    /// <summary>
    /// <b>A name resolving to loopback is refused</b>: that is DNS rebinding, not the declared local receiver
    /// the loopback carve-out exists for.
    /// </summary>
    [Fact]
    public async Task A_name_resolving_to_loopback_is_refused_as_rebinding()
    {
        var guard = Guard(Resolving("127.0.0.1"));

        await Should.ThrowAsync<HttpRequestException>(
            () => guard.PermittedAddressesAsync("rebind.example.com", Cancellation));
    }

    /// <summary>
    /// One non-public address among public ones refuses the whole name, so a record set cannot mix a decoy
    /// public address with the internal one it is really aimed at.
    /// </summary>
    [Fact]
    public async Task One_non_public_address_among_public_ones_refuses_the_name()
    {
        var guard = Guard(Resolving("93.184.215.14", "10.0.0.5"));

        await Should.ThrowAsync<HttpRequestException>(
            () => guard.PermittedAddressesAsync("hooks.example.com", Cancellation));
    }

    /// <summary>
    /// A loopback <em>literal</em> is the declared development receiver and is permitted — without asking DNS,
    /// because there is nothing to resolve.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    public async Task A_loopback_literal_is_permitted_without_resolving(string host)
    {
        var guard = Guard(Unreachable);

        var addresses = await guard.PermittedAddressesAsync(host, Cancellation);

        IPAddress.IsLoopback(addresses.ShouldHaveSingleItem()).ShouldBeTrue();
    }

    /// <summary><c>localhost</c> is the declared loopback name, and is permitted while it resolves to loopback.</summary>
    [Fact]
    public async Task Localhost_resolving_to_loopback_is_permitted()
    {
        var guard = Guard(Resolving("::1", "127.0.0.1"));

        var addresses = await guard.PermittedAddressesAsync("localhost", Cancellation);

        addresses.Length.ShouldBe(2);
    }

    /// <summary>
    /// <c>localhost</c> earns the loopback carve-out only for loopback addresses; a hosts file pointing it at a
    /// private network is still refused.
    /// </summary>
    [Fact]
    public async Task Localhost_resolving_to_a_private_address_is_refused()
    {
        var guard = Guard(Resolving("10.0.0.5"));

        await Should.ThrowAsync<HttpRequestException>(
            () => guard.PermittedAddressesAsync("localhost", Cancellation));
    }

    /// <summary>A private literal is refused like a private resolution.</summary>
    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.5")]
    [InlineData("[fe80::1]")]
    public async Task A_non_public_literal_is_refused(string host)
    {
        var guard = Guard(Unreachable);

        await Should.ThrowAsync<HttpRequestException>(() => guard.PermittedAddressesAsync(host, Cancellation));
    }

    /// <summary>
    /// <b>The opt-in admits exactly the networks it lists</b>, and nothing else in the non-public space — an
    /// embedded host that delivers to one internal service does not thereby open the metadata endpoint.
    /// </summary>
    [Fact]
    public async Task An_allowed_network_admits_its_own_addresses_and_no_others()
    {
        var allowed = Guard(Resolving("10.1.2.3"), "10.0.0.0/8");
        var refused = Guard(Resolving("169.254.169.254"), "10.0.0.0/8");

        (await allowed.PermittedAddressesAsync("billing.internal", Cancellation)).ShouldHaveSingleItem();
        await Should.ThrowAsync<HttpRequestException>(
            () => refused.PermittedAddressesAsync("metadata.internal", Cancellation));
    }

    /// <summary>An allowed IPv4 network also admits the same address in its IPv4-mapped IPv6 form.</summary>
    [Fact]
    public async Task An_allowed_ipv4_network_admits_its_mapped_ipv6_form()
    {
        var guard = Guard(Resolving("::ffff:10.1.2.3"), "10.0.0.0/8");

        (await guard.PermittedAddressesAsync("billing.internal", Cancellation)).ShouldHaveSingleItem();
    }

    /// <summary>A name that resolves to nothing is a failed delivery, not a connection to nowhere.</summary>
    [Fact]
    public async Task A_name_resolving_to_nothing_is_refused()
    {
        var guard = Guard(Resolving());

        await Should.ThrowAsync<HttpRequestException>(
            () => guard.PermittedAddressesAsync("empty.example.com", Cancellation));
    }

    /// <summary>
    /// <b>The handler follows no redirect and uses no proxy</b>: a 3xx is a failed delivery rather than a second,
    /// unchecked destination, and a proxy would move the connect this guard judges out of the process.
    /// </summary>
    [Fact]
    public void The_handler_follows_no_redirect_uses_no_proxy_and_connects_through_the_guard()
    {
        using var handler = Guard(Unreachable).CreateHandler();

        handler.AllowAutoRedirect.ShouldBeFalse();
        handler.UseProxy.ShouldBeFalse();
        handler.ConnectCallback.ShouldNotBeNull();
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static WebhookEgressGuard Guard(WebhookHostResolver resolver, params string[] allowedNetworks)
    {
        var options = new AlvoEventOptions();
        foreach (var network in allowedNetworks)
        {
            options.WebhookAllowedNetworks.Add(network);
        }

        return new WebhookEgressGuard(Options.Create(options), resolver);
    }

    private static WebhookHostResolver Resolving(params string[] addresses) =>
        (_, _) => Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());

    private static Task<IPAddress[]> Unreachable(string host, CancellationToken cancellationToken) =>
        throw new InvalidOperationException($"'{host}' is a literal and must not be resolved.");
}
