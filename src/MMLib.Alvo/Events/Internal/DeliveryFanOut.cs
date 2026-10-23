using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// Records, for one committed event, one delivery per bound handler whose condition holds — before the event's declarative
/// actions run, so their failure cannot keep a handler from its work (spec 2026-10-14 §4.8, M1).
/// </summary>
/// <remarks>
/// The store's uniqueness on (event, subscriber) makes a second fan-out of one event a no-op, which is what lets the outbox
/// retry an entry whose actions failed. Only a storage failure escapes; the dispatcher then holds the record's later events
/// back.
/// </remarks>
internal sealed class DeliveryFanOut(
    IDeliveryStore store,
    ExtensionBinder binder,
    IPredicateEvaluator evaluator,
    AlvoChainDepthLimit depth,
    ILogger<DeliveryFanOut> logger)
{
    internal async Task<int> FanOutAsync(AlvoEvent @event, PolicyCatalog catalog, CancellationToken cancellationToken)
    {
        var requests = RequestsFor(@event, binder.For(catalog));
        if (requests.Count > 0)
        {
            await store.FanOutAsync(requests, cancellationToken).ConfigureAwait(false);
        }

        return requests.Count;
    }

    private List<DeliveryRequest> RequestsFor(AlvoEvent @event, ExtensionBindings bindings)
    {
        var caller = EnvelopeReading.CallerOf(@event);
        var requests = new List<DeliveryRequest>();
        foreach (var (handler, condition) in bindings.Selecting(@event))
        {
            if (Request(@event, handler, HandlerCondition.Judge(condition, @event, evaluator, caller)) is { } request)
            {
                requests.Add(request);
            }
        }

        requests.AddRange(bindings.UnboundSelecting(@event).Select(handler => Unbound(@event, handler)));

        return requests;
    }

    private DeliveryRequest? Request(AlvoEvent @event, BoundHandler handler, HandlerVerdict verdict) => verdict switch
    {
        HandlerVerdict.NoMatch => null,
        HandlerVerdict.Failed => WhenFailed(@event, handler),
        _ when @event.ChainDepth + 1 > depth.MaxDepth => TooDeep(@event, handler),
        _ => Pending(@event, handler),
    };

    private DeliveryRequest WhenFailed(AlvoEvent @event, BoundHandler handler)
    {
        DeliveryLog.HandlerConditionFailed(logger, handler.Id, @event.Id);
        AlvoDeliveryMetrics.DeadRecorded(handler.Id, DeliveryErrors.WhenFailed);

        return Pending(@event, handler) with { DeadReason = DeliveryErrors.WhenFailed };
    }

    private DeliveryRequest TooDeep(AlvoEvent @event, BoundHandler handler)
    {
        DeliveryLog.ChainDepthExceeded(logger, handler.Id, @event.Id, @event.ChainDepth, @event.ChainDepth + 1, depth.MaxDepth);
        AlvoDeliveryMetrics.DeadRecorded(handler.Id, DeliveryErrors.ChainDepthExceeded);

        return Pending(@event, handler) with { DeadReason = DeliveryErrors.ChainDepthExceeded };
    }

    private static DeliveryRequest Pending(AlvoEvent @event, BoundHandler handler) =>
        Request(@event, handler.Id);

    private static DeliveryRequest Request(AlvoEvent @event, string subscriber) => new()
    {
        EventId = @event.Id,
        Subscriber = subscriber,
        PartitionKey = @event.PartitionKey,
        TenantId = @event.TenantId,
        Kind = DeliveryKind.Handler,
    };

    private static DeliveryRequest Unbound(AlvoEvent @event, HandlerRegistration handler)
    {
        AlvoDeliveryMetrics.DeadRecorded(handler.Id, DeliveryRequest.UnboundReason);

        return Request(@event, handler.Id) with { DeadReason = DeliveryRequest.UnboundReason };
    }
}
