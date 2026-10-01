using System.Net;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// Resolves a webhook endpoint's host name to the addresses the egress guard judges and then connects to.
/// </summary>
/// <remarks>
/// A seam, registered as <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>, so a test can make
/// a name resolve to a private or loopback address without depending on the DNS of the machine it runs on.
/// </remarks>
/// <param name="host">The host name, never an address literal.</param>
/// <param name="cancellationToken">A token to cancel the lookup.</param>
/// <returns>Every address the name resolved to.</returns>
internal delegate Task<IPAddress[]> WebhookHostResolver(string host, CancellationToken cancellationToken);
