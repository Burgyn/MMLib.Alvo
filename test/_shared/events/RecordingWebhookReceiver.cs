using MMLib.Alvo.Testing.Events;

using System.Net;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The receiver every after-hook in the criteria suite posts to: it records the URL and the body of each
/// delivery and answers <c>200 OK</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Installed as the named client's primary handler, not as a replacement for the factory.</b> The delivery
/// resolves <c>WebhookDelivery.HttpClientName</c> from <see cref="IHttpClientFactory"/>, so substituting the
/// socket underneath that named client leaves the whole production path — the factory, the named
/// configuration, the timeout, the content type — in place, and only the network is stood in for. Replacing
/// <see cref="IHttpClientFactory"/> itself would stop the criteria proving that the delivery goes through the
/// client a host configures by name.
/// </para>
/// <para>
/// <b>No socket, on purpose.</b> A criterion that drives 102 events must not depend on a listening port, and
/// the real-socket behaviour of the delivery — a refused connection, a 500, a timeout — is already pinned by
/// its own suite. What is measured here is <em>which</em> deliveries happened and how many.
/// </para>
/// </remarks>
internal sealed class RecordingWebhookReceiver : HttpMessageHandler
{
    /// <summary>Every delivery this receiver accepted, in arrival order.</summary>
    internal IReadOnlyList<AlvoEventDelivery> Deliveries
    {
        get
        {
            lock (_gate)
            {
                return [.. _deliveries];
            }
        }
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _deliveries.Add(new AlvoEventDelivery(request.RequestUri!.ToString(), body));
        }

        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private readonly List<AlvoEventDelivery> _deliveries = [];

    private readonly object _gate = new();
}
