namespace MMLib.Alvo.TestHost.Internal;

/// <summary>
/// Forwards to the caller's webhook handler without owning it: <c>IHttpClientFactory</c> disposes the primary handlers
/// it builds on handler expiry, and a test's stub must outlive the host it served.
/// </summary>
/// <remarks>
/// Disposing the host's container does not dispose a primary handler on this runtime — only the factory's expiry
/// cleanup does, after the handler lifetime has passed — so a fact that reuses one stub across two short-lived hosts
/// passes with or without this wrapper. It stays for the host that lives longer than the handler lifetime.
/// </remarks>
internal sealed class SharedReceiver(HttpMessageHandler receiver) : HttpMessageHandler
{
    private readonly HttpMessageInvoker _invoker = new(receiver, disposeHandler: false);

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        _invoker.SendAsync(request, cancellationToken);

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _invoker.Dispose();
        }

        base.Dispose(disposing);
    }
}
