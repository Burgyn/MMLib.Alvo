using Microsoft.Extensions.Logging;

using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// Runs one compiled after-hook's action against one event: a <c>webhook</c> POST, or an <c>email</c> through
/// the mail port. The other three action types the frozen schema declares never reach here.
/// </summary>
/// <remarks>
/// <para>
/// <b>It does not decide <em>whether</em> to run.</b> The condition is part of the subscription, evaluated
/// before an execution entry exists, so everything that reaches this type is an action that is going to run
/// and be logged. That split is why a filtered-out event produces one counter increment and no log entry.
/// </para>
/// <para>
/// <b>It does not catch anything either, and that is deliberate.</b> A delivery failure has to reach the
/// dispatcher, which releases the outbox entry so the next claim retries it — that release is the only thing
/// that makes delivery at-least-once. An executor that logged and returned would turn every transient 503
/// into a silently dropped event, and every downstream chaos assertion would pass straight over it.
/// </para>
/// <para>
/// <b>Nothing is resolved for the first time here.</b> Every template was parsed and checked against the
/// entity's schema when the descriptor was applied, and the endpoint was resolved in the same pass, so this
/// type renders and posts. A refusal at delivery time has nobody to report to.
/// </para>
/// <para>
/// <b>Delivery is at-least-once, so an action must be idempotent or deduplicated by event id.</b> The
/// envelope's <c>id</c> is the consumer's dedup key — that is what CloudEvents defines it for, and what the
/// Standard Webhooks guidance and the large payment APIs both tell receivers to use. Alvo does not deduplicate
/// on the receiver's behalf.
/// </para>
/// </remarks>
/// <param name="webhooks">The POST-to-a-declared-endpoint delivery.</param>
/// <param name="email">The mail port; a console development provider unless a host replaces it.</param>
/// <param name="logger">The logger the one execution-log entry per action is written through.</param>
internal sealed class EventActionExecutor(
    WebhookDelivery webhooks,
    IEmailSender email,
    ILogger<EventActionExecutor> logger)
{
    /// <summary>Runs <paramref name="hook"/>'s action for <paramref name="event"/>.</summary>
    /// <param name="hook">The compiled hook, whose condition has already selected it.</param>
    /// <param name="event">The event the action's templates render against.</param>
    /// <param name="cancellationToken">A token to cancel the action; cancelled when the host is shutting down.</param>
    /// <exception cref="InvalidOperationException">
    /// The hook carries an action type this build refuses at apply time, or a <c>webhook</c> action with no
    /// resolved endpoint — both unreachable from a descriptor, and both an invariant rather than an
    /// author's mistake.
    /// </exception>
    internal Task ExecuteAsync(CompiledAfterHook hook, AlvoEvent @event, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hook);
        ArgumentNullException.ThrowIfNull(@event);

        return hook.Action.Action switch
        {
            WebhookAction => DeliverAsync(hook, @event, cancellationToken),
            EmailAction => SendAsync(hook, @event, cancellationToken),
            _ => throw UnreachableAction(hook),
        };
    }

    private async Task DeliverAsync(CompiledAfterHook hook, AlvoEvent @event, CancellationToken cancellationToken)
    {
        await webhooks
            .PostAsync(EndpointOf(hook), BodyOf(hook.Action, @event), cancellationToken)
            .ConfigureAwait(false);

        Executed(hook, @event);
    }

    private async Task SendAsync(CompiledAfterHook hook, AlvoEvent @event, CancellationToken cancellationToken)
    {
        await email.SendAsync(MessageOf(hook.Action, @event), cancellationToken).ConfigureAwait(false);

        Executed(hook, @event);
    }

    /// <summary>
    /// The request body: the action's rendered <c>payload</c> when it declares one, and otherwise the
    /// canonical envelope exactly as the outbox stored it.
    /// </summary>
    /// <remarks>
    /// The canonical envelope goes through <see cref="AlvoEventJson.Write"/> rather than being forwarded as
    /// the stored payload text, because the stored text is not in scope here and a second serializer for the
    /// same shape is how a delivered body and a stored one come to differ.
    /// </remarks>
    private static string BodyOf(CompiledAction action, AlvoEvent @event) =>
        action.Templates.TryGetValue(ActionSlot.Payload, out var payload)
            ? payload.Render(@event)
            : AlvoEventJson.Write(@event);

    /// <summary>
    /// The message, entirely from compiled templates — a literal recipient is a template with no placeholder,
    /// so nothing here reads a raw descriptor string.
    /// </summary>
    private static AlvoMailMessage MessageOf(CompiledAction action, AlvoEvent @event) => new(
        Render(action, ActionSlot.To, @event),
        Render(action, ActionSlot.Subject, @event),
        Render(action, ActionSlot.Body, @event));

    private static string Render(CompiledAction action, string slot, AlvoEvent @event) =>
        action.Templates.TryGetValue(slot, out var template) ? template.Render(@event) : string.Empty;

    private void Executed(CompiledAfterHook hook, AlvoEvent @event) => EventLog.ActionExecuted(
        logger, hook.Path, ActionType.NameOf(hook.Action.Action), @event.Id, @event.Type);

    private static WebhookTarget EndpointOf(CompiledAfterHook hook) =>
        hook.Action.Endpoint ?? throw UnresolvedEndpoint(hook);

    /// <summary>
    /// The arm no descriptor can reach: the three remaining action types are refused when a descriptor is
    /// applied, so this exists only so a hand-built catalog fails loudly instead of silently doing nothing.
    /// </summary>
    private static InvalidOperationException UnreachableAction(CompiledAfterHook hook) => new(
        $"After-hook '{hook.Path}' carries a "
        + $"'{ActionType.NameOf(hook.Action.Action)}' action, which this build refuses when a "
        + "descriptor is applied and therefore never runs. Reaching this point means the policy catalog was "
        + "built by hand rather than from a descriptor.");

    private static InvalidOperationException UnresolvedEndpoint(CompiledAfterHook hook) => new(
        $"After-hook '{hook.Path}' is a webhook action with no resolved endpoint. An endpoint is resolved "
        + "from 'webhooks.endpoints' when the descriptor is applied — where an unknown name, a relative URL "
        + "and a non-HTTPS one are all refused — so reaching this point means the policy catalog was built by "
        + "hand rather than from a descriptor.");
}
