using MMLib.Alvo.Descriptor;

using System.Text;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// One POST to a declared webhook endpoint. Nothing else: no signature, no retry, no queue.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is deliberately absent, so a reader does not assume it.</b> The delivery is <b>unsigned</b> —
/// <see cref="WebhookEndpoint.SecretRef"/> is never read, and no Standard Webhooks
/// <c>webhook-id</c>/<c>webhook-timestamp</c>/<c>webhook-signature</c> header is sent, so a receiver cannot
/// yet verify the sender. It is also <b>unprojected</b>: the body is the whole envelope or the whole rendered
/// template, with no per-endpoint field selection. Signing belongs to the webhook-management work and
/// projection is filed as its own issue; the descriptor's own <c>webhooks</c> warning names both absences at
/// apply time, and this type must not read as though either were handled.
/// </para>
/// <para>
/// <b>There is no retry here, and that is the design.</b> The outbox is the retry: a failed attempt throws,
/// the dispatcher releases the entry, and the next claim delivers it again with its attempt count
/// incremented. A retry loop inside this type would be a second, invisible multiplier on top of that — and it
/// would hold a claimed entry past its lease while sleeping.
/// </para>
/// <para>
/// <b>The network is allowed here because this runs after the commit.</b> The iron rule is that no external
/// call happens before the transaction commits; a delivery is driven from the outbox, which by construction
/// only holds committed events.
/// </para>
/// <para>
/// <b>Every failure is retried, and none is classified.</b> A 500, a 404, a connection refused, a DNS failure
/// and a timeout all become an exception and all get the same treatment, because nothing at delivery time can
/// tell a permanently wrong endpoint from one whose deployment is thirty seconds from finishing. The bound is
/// the attempt ceiling the dispatcher passes to the outbox claim, not a per-status rule here — a status-based
/// "permanent" verdict would need somewhere to put the abandoned event, and this build has no dead-letter
/// queue to put it in.
/// </para>
/// </remarks>
/// <param name="clients">The factory the named client is resolved from, so a host owns the handler and its timeout.</param>
internal sealed class WebhookDelivery(IHttpClientFactory clients)
{
    /// <summary>
    /// The named <see cref="HttpClient"/> every delivery goes through, so a host configures the handler,
    /// the timeout and any resilience once, by name, without this type owning any of them.
    /// </summary>
    internal const string HttpClientName = "MMLib.Alvo.Events.Webhook";

    /// <summary>POSTs <paramref name="body"/> to <paramref name="endpoint"/>, throwing unless it succeeded.</summary>
    /// <param name="endpoint">The endpoint as the descriptor declared it, resolved when the hook was compiled.</param>
    /// <param name="body">The request body — the canonical envelope, or the action's rendered <c>payload</c>.</param>
    /// <param name="cancellationToken">A token to cancel the delivery; cancelled when the host is shutting down.</param>
    /// <exception cref="HttpRequestException">The endpoint refused the delivery, or could not be reached.</exception>
    /// <exception cref="TimeoutException">The request did not complete inside the named client's timeout.</exception>
    /// <remarks>
    /// The content type is always <c>application/json</c>, which is the endpoint's contract and the envelope's
    /// own media type. A cost worth naming: a <c>payload</c> template renders to whatever the author wrote, and
    /// nothing re-checks that the result is JSON — the template engine renders text, so an author who writes
    /// <c>{{new.title}}</c> as a whole payload sends a bare string under a JSON content type.
    /// </remarks>
    internal async Task PostAsync(WebhookEndpoint endpoint, string body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(body);

        using var content = new StringContent(body, Encoding.UTF8, AlvoEvent.DataContentType);
        using var response = await PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Sends the request, and turns a timeout back into a failure rather than leaving it as a cancellation.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpClient"/> reports its own timeout as an <see cref="OperationCanceledException"/>, which
    /// is the same type the host's shutdown raises. The dispatcher treats a cancellation as "we are stopping"
    /// and a failure as "release and retry", so leaving the two indistinguishable would make a slow endpoint
    /// look like a shutdown and silently end the pump. The caller's own token still cancels as a cancellation.
    /// </remarks>
    private async Task<HttpResponseMessage> PostAsync(
        WebhookEndpoint endpoint, StringContent content, CancellationToken cancellationToken)
    {
        try
        {
            return await clients.CreateClient(HttpClientName)
                .PostAsync(new Uri(endpoint.Url), content, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException cancelled) when (!cancellationToken.IsCancellationRequested)
        {
            throw TimedOut(endpoint, cancelled);
        }
    }

    private static TimeoutException TimedOut(WebhookEndpoint endpoint, Exception cancelled) => new(
        $"Delivering to webhook endpoint '{endpoint.Url}' did not complete inside the "
        + $"'{HttpClientName}' client's timeout. The event is retried until it reaches the configured "
        + "attempt ceiling; raise the timeout by configuring that named client if the endpoint is "
        + "legitimately slow.",
        cancelled);
}
