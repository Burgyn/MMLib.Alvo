using Microsoft.Extensions.Logging;

namespace MMLib.Alvo.Events.Internal;

/// <summary>What deliveries log — ids, counts and type names; never a URL, a body or an exception message.</summary>
internal static partial class DeliveryLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Alvo recorded a dead delivery of event {EventId} for {Subscriber}: its when condition failed while it "
            + "was judged ({ErrorType}). Fix the condition, then redeliver it.")]
    internal static partial void HandlerConditionFailed(ILogger logger, string subscriber, Guid eventId, string errorType);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Alvo recorded a dead delivery of event {EventId} for {Subscriber}: the event is {ChainDepth} hops deep, "
            + "running the handler would make it {Depth}, past the limit of {MaxDepth}. Something reacts to its own effects.")]
    internal static partial void ChainDepthExceeded(ILogger logger, string subscriber, Guid eventId, int chainDepth, int depth, int maxDepth);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Alvo recorded no delivery of event {EventId} for {Subscriber}: its when condition reads {Reference}, "
            + "which the event does not carry, so it is not judged against an absent value.")]
    internal static partial void HandlerConditionUnread(ILogger logger, string subscriber, Guid eventId, string reference);
}
