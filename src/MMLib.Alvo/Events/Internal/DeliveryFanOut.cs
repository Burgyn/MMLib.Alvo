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
/// <para>
/// The store's uniqueness on (event, subscriber) makes a second fan-out of one event a no-op, which is what lets the outbox
/// retry an entry whose actions failed. Only a storage failure escapes; the dispatcher then holds the record's later events
/// back.
/// </para>
/// <para>
/// <b>A dead delivery is announced once, when its row is written.</b> Its log line and its <c>alvo.delivery.dead</c>
/// increment wait for the insert and are emitted only when the store reports new rows — so a failed insert announces
/// nothing, and a retried entry (whose rows all exist) announces nothing again. The store writes a fan-out all together
/// or not at all, so "new rows" means this call's rows; the one residue is a catalog change between two attempts that
/// adds a handler, which makes the retry's insert partial and re-announces the earlier dead rows.
/// </para>
/// <para>
/// <b>The depth limit outranks a failed condition.</b> A handler whose condition holds — or threw, so it cannot be said
/// not to — on an event already at the limit is recorded <c>chain-depth-exceeded</c>, the loop signal, rather than
/// <c>when-failed</c>; a condition that does not hold records nothing at any depth.
/// </para>
/// </remarks>
internal sealed class DeliveryFanOut(
    IDeliveryStore store,
    ExtensionBinder binder,
    IPredicateEvaluator evaluator,
    AlvoChainDepthLimit depth,
    ILogger<DeliveryFanOut> logger)
{
    /// <summary>Records the event's deliveries.</summary>
    /// <returns>How many deliveries the event fans out to, recorded now or by an earlier attempt — dead ones included.</returns>
    internal async Task<int> FanOutAsync(AlvoEvent @event, PolicyCatalog catalog, CancellationToken cancellationToken)
    {
        var plan = Plan(@event, binder.For(catalog));
        if (plan.Requests.Count > 0
            && await store.FanOutAsync(plan.Requests, cancellationToken).ConfigureAwait(false) > 0)
        {
            plan.Announcements.ForEach(announce => announce());
        }

        return plan.Requests.Count;
    }

    private FanOutPlan Plan(AlvoEvent @event, ExtensionBindings bindings)
    {
        var caller = EnvelopeReading.CallerOf(@event);
        var plan = new FanOutPlan();
        foreach (var (handler, condition) in bindings.Selecting(@event))
        {
            Add(plan, @event, handler, HandlerCondition.Judge(condition, @event, evaluator, caller));
        }

        foreach (var handler in bindings.UnboundSelecting(@event))
        {
            plan.Dead(Request(@event, handler.Id), DeliveryRequest.UnboundReason, () => { });
        }

        return plan;
    }

    private void Add(FanOutPlan plan, AlvoEvent @event, BoundHandler handler, HandlerJudgement judgement)
    {
        var request = Request(@event, handler.Id);
        if (judgement.Verdict is HandlerVerdict.NoMatch)
        {
            Unread(@event, handler, judgement);
        }
        else if (@event.ChainDepth + 1 > depth.MaxDepth)
        {
            plan.Dead(request, DeliveryErrors.ChainDepthExceeded, () => DeliveryLog.ChainDepthExceeded(
                logger, handler.Id, @event.Id, @event.ChainDepth, @event.ChainDepth + 1, depth.MaxDepth));
        }
        else if (judgement.Verdict is HandlerVerdict.Failed)
        {
            plan.Dead(request, DeliveryErrors.WhenFailed, () => DeliveryLog.HandlerConditionFailed(
                logger, handler.Id, @event.Id, judgement.ErrorType ?? nameof(Exception)));
        }
        else
        {
            plan.Requests.Add(request);
        }
    }

    private void Unread(AlvoEvent @event, BoundHandler handler, HandlerJudgement judgement)
    {
        if (judgement.Unread is { } reference)
        {
            DeliveryLog.HandlerConditionUnread(logger, handler.Id, @event.Id, reference);
        }
    }

    private static DeliveryRequest Request(AlvoEvent @event, string subscriber) => new()
    {
        EventId = @event.Id,
        Subscriber = subscriber,
        PartitionKey = @event.PartitionKey,
        TenantId = @event.TenantId,
        Kind = DeliveryKind.Handler,
    };

    /// <summary>One event's requests, and what to announce once they are written.</summary>
    private sealed class FanOutPlan
    {
        internal List<DeliveryRequest> Requests { get; } = [];

        internal List<Action> Announcements { get; } = [];

        internal void Dead(DeliveryRequest request, string reason, Action log)
        {
            Requests.Add(request with { DeadReason = reason });
            Announcements.Add(() =>
            {
                log();
                AlvoDeliveryMetrics.DeadRecorded(request.Subscriber, reason);
            });
        }
    }
}
