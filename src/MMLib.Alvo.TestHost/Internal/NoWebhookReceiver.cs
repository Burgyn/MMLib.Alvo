namespace MMLib.Alvo.TestHost.Internal;

/// <summary>
/// The webhook handler of a host started without one: every delivery fails, and none reaches the network.
/// </summary>
internal sealed class NoWebhookReceiver : HttpMessageHandler
{
    internal const string Refusal =
        "This AlvoTestHost was started without a webhook receiver, so the delivery was refused instead of reaching the "
        + "network. Pass webhooks: to AlvoTestHost.StartAsync with a handler that answers for the endpoint.";

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException(Refusal));
}
