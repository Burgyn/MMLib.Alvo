using Microsoft.Extensions.Options;

using System.Net;
using System.Net.Sockets;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The webhook client's network egress policy: default-deny every non-public destination, judged on the
/// address the socket is about to connect to rather than on the URL an author wrote.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why at connect time.</b> The apply-time check in <c>AfterHookCompiler</c> sees a URL, and a URL says
/// nothing about where its name resolves: <c>https://hooks.example.com</c> can resolve to <c>10.0.0.5</c> or
/// to <c>169.254.169.254</c> today, and to something else between the check and the request (DNS rebinding).
/// So — as the OWASP SSRF Prevention Cheat Sheet prescribes, and as hardened webhook senders do — this
/// resolves the name itself inside <see cref="SocketsHttpHandler.ConnectCallback"/>, refuses the connection if
/// <em>any</em> resolved address is non-public, and connects to exactly the addresses it judged. There is no
/// second lookup for an attacker's resolver to answer differently, so no window between check and use.
/// </para>
/// <para>
/// <b>Loopback is permitted only when the endpoint <em>declares</em> it</b> — a loopback literal
/// (<c>127.0.0.1</c>, <c>[::1]</c>) or the name <c>localhost</c> — which is exactly the set
/// <see cref="Uri.IsLoopback"/> admits for the apply-time <c>http</c> carve-out, and is what a local receiver
/// and this repository's own end-to-end suites use. Any other name resolving to loopback is rebinding and is
/// refused like any other non-public destination; <c>localhost</c> resolving anywhere but loopback is refused
/// too.
/// </para>
/// <para>
/// <b>The opt-in is a list of networks, not a switch</b> (<see cref="AlvoEventOptions.WebhookAllowedNetworks"/>).
/// An embedded host that delivers to one internal service names that service's network and nothing else, so
/// admitting <c>10.20.0.0/16</c> does not also admit the metadata endpoint — the least-privilege shape of an
/// egress proxy's allow-ranges. An allowed network wins over every deny, loopback and link-local included,
/// because naming it is the explicit policy default-deny asks for.
/// </para>
/// <para>
/// <b>The handler follows no redirect and uses no proxy.</b> A followed <c>3xx</c> would be a second
/// destination — an internal host, or the same host over cleartext — that never passed this check, so a
/// redirect is a failed delivery, as it is for the hardened webhook senders that follow none. A proxy
/// moves the connect out of this process, so the address judged would be the proxy's, not the destination's;
/// the documented deviation is that a host needing an egress proxy replaces the named client's primary handler
/// and thereby owns the egress policy itself — ideally with an SSRF-aware proxy such as Smokescreen.
/// </para>
/// <para>
/// <b>A deliberate deviation from the analysis, toward refusing.</b> <c>baas-analyza.md</c> lists SSRF
/// protection among the sender's safeguards as <em>optional</em>; here it is on by default and the opt-in is
/// the exception, because §0's default-deny principle outranks a component note, and because the descriptor
/// author who writes the URL is not necessarily the operator whose network it can reach.
/// </para>
/// <para>
/// <b>A refusal is an ordinary failed delivery.</b> It throws, the handler wraps it into the
/// <see cref="HttpRequestException"/> every connection failure is, and the dispatcher releases, backs off and
/// eventually gives the event up exactly as it would for an unreachable host. The message names the host and
/// the refused address — never the URL, whose path and query are the one credential an unsigned endpoint has
/// (see <see cref="WebhookTarget"/>).
/// </para>
/// </remarks>
/// <param name="options">The event options whose <see cref="AlvoEventOptions.WebhookAllowedNetworks"/> is the opt-in.</param>
/// <param name="resolve">Resolves a host name; <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/> in production.</param>
internal sealed class WebhookEgressGuard(IOptions<AlvoEventOptions> options, WebhookHostResolver resolve)
{
    /// <summary>
    /// A handler that connects only through this guard, follows no redirect and uses no proxy.
    /// </summary>
    /// <returns>The primary handler the named webhook client is built on.</returns>
    internal SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = ConnectAsync,
    };

    /// <summary>
    /// The addresses <paramref name="host"/> may be connected to, or a refusal when any of them is forbidden.
    /// </summary>
    /// <param name="host">The endpoint's host: a name, or an address literal (bracketed or not).</param>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    /// <returns>Every address the host resolved to, all of them permitted.</returns>
    /// <exception cref="HttpRequestException">The host resolved to nothing, or to a forbidden address.</exception>
    internal async Task<IPAddress[]> PermittedAddressesAsync(string host, CancellationToken cancellationToken)
    {
        var literal = Literal(host);
        var addresses = literal is null
            ? await resolve(host, cancellationToken).ConfigureAwait(false)
            : [literal];
        if (addresses.Length == 0)
        {
            throw Unresolved(host);
        }

        var declaresLoopback = literal is not null || IsLocalhost(host);
        var forbidden = Array.Find(addresses, address => !IsPermitted(address, declaresLoopback));

        return forbidden is null ? addresses : throw Refused(host, forbidden);
    }

    private async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = await PermittedAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);

        return await ConnectAsync(addresses, endpoint.Port, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<Stream> ConnectAsync(
        IPAddress[] addresses, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, port, cancellationToken).ConfigureAwait(false);

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private bool IsPermitted(IPAddress address, bool declaresLoopback) =>
        WebhookAddressClassifier.IsPublic(address)
        || (declaresLoopback && WebhookAddressClassifier.IsLoopback(address))
        || IsAllowed(address);

    private bool IsAllowed(IPAddress address)
    {
        var judged = WebhookAddressClassifier.Unwrap(address);

        return Array.Exists(AllowedNetworks, network => network.Contains(judged) || network.Contains(address));
    }

    private IPNetwork[] AllowedNetworks => _allowedNetworks ??=
        [.. options.Value.WebhookAllowedNetworks.Select(network => IPNetwork.Parse(network))];

    private IPNetwork[]? _allowedNetworks;

    /// <summary>The address a host literal spells, which a literal loopback declares rather than resolves to.</summary>
    private static IPAddress? Literal(string host) =>
        IPAddress.TryParse(host.AsSpan().Trim("[]"), out var address) ? address : null;

    private static bool IsLocalhost(string host) =>
        string.Equals(host, LocalhostName, StringComparison.OrdinalIgnoreCase);

    private const string LocalhostName = "localhost";

    private static HttpRequestException Unresolved(string host) => new(
        HttpRequestError.NameResolutionError,
        $"The webhook host '{host}' resolved to no address, so there is nothing to deliver to.");

    private static HttpRequestException Refused(string host, IPAddress address) => new(
        HttpRequestError.ConnectionError,
        $"The webhook host '{host}' resolved to {address}, which is not a public address, so the delivery was "
        + "refused before connecting: webhooks are default-deny for private, loopback, link-local (cloud "
        + "metadata), CGNAT, multicast and reserved networks. If this endpoint is an internal service the host "
        + $"trusts, add its network to {AlvoEventOptionsConfiguration.WebhookAllowedNetworksKey} (as an "
        + $"environment variable, {AlvoEventOptionsConfiguration.WebhookAllowedNetworksVariable}__0), such as "
        + "'10.20.0.0/16'.");
}
