using Microsoft.Extensions.Logging;

namespace MMLib.Alvo.InboundWebhooks.Internal;

/// <summary>The inbound webhook log records. They name the receiver's pattern and the secret's name — never an id, a body, a secret or a signature.</summary>
internal static partial class WebhookLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook receiver '{Receiver}' refused a delivery: {Reason}.")]
    internal static partial void Refused(ILogger logger, string receiver, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook receiver '{Receiver}' acknowledged a delivery it had already handled.")]
    internal static partial void Duplicate(ILogger logger, string receiver);

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook receiver '{Receiver}' cannot verify deliveries: the secret '{Secret}' is unusable. {Reason}")]
    internal static partial void SecretUnusable(ILogger logger, string receiver, string secret, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook receiver '{Receiver}' handled a delivery after its claim lapsed to another run; that run records the id.")]
    internal static partial void ClaimLost(ILogger logger, string receiver);

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook receiver '{Receiver}' could not give a delivery's id back; the sender's retry is answered 409 until its claim's lease runs out.")]
    internal static partial void NotReleased(ILogger logger, string receiver, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Swept {Webhooks} inbound webhook ids older than seven days and {Commands} command keys older than a day.")]
    internal static partial void Swept(ILogger logger, int webhooks, int commands);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The ledger sweep found no inbound webhook id or command key to forget.")]
    internal static partial void NothingSwept(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The ledger sweep failed; it runs again in an hour.")]
    internal static partial void SweepFailed(ILogger logger, Exception failure);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The ledger sweep failed while the host was stopping; the next start sweeps again.")]
    internal static partial void SweeperStopped(ILogger logger, Exception failure);
}
