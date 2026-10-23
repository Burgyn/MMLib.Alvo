namespace MMLib.Alvo.TestHost.Internal;

/// <summary>
/// Forwards to the caller's webhook handler without owning it: <c>IHttpClientFactory</c> disposes the primary handlers
/// it builds, and a test's stub must outlive the host it served.
/// </summary>
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
