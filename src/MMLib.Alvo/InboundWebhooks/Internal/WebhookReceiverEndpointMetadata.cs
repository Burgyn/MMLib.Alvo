namespace MMLib.Alvo.InboundWebhooks.Internal;

/// <summary>
/// Marks the endpoint <c>MapAlvoWebhookReceiver</c> mapped, so <c>AlvoExceptionHandler</c> answers its failures as Alvo's
/// problem document — as it does a command's — rather than leaving them to the host's own handler.
/// </summary>
/// <param name="Pattern">The receiver's route pattern, its dedup scope.</param>
internal sealed record WebhookReceiverEndpointMetadata(string Pattern);
