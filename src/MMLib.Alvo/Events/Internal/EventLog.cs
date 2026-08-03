using Microsoft.Extensions.Logging;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// Every log line the event subsystem writes, as compile-time-generated <c>LoggerMessage</c> delegates.
/// </summary>
/// <remarks>
/// <para>
/// Source-generated because <c>CA1848</c> is an error in this repository, and gathered in one type because
/// the rule below is a property of the <em>set</em> of lines rather than of any one of them.
/// </para>
/// <para>
/// <b>No line in this subsystem carries a rendered value, and none carries an endpoint's URL.</b> An action
/// log entry names the hook's own JSON pointer, the action type, and the event's id and type — descriptor
/// coordinates and event identity, and nothing that came out of a row. The reason is
/// <see cref="AlvoEventData"/>'s: the envelope carries the <em>unmasked</em> post-image, so a rendered webhook
/// payload or email body can contain a <c>hidden</c> field. Logging the rendered value would take that field
/// out of the one place the design accepted it going — a descriptor-declared endpoint, chosen by the same
/// author as the <c>hidden</c> rule — and put it into whatever ships logs, which nobody declared and no author
/// chose. The event id is the join key: an operator who needs the payload reads the <c>alvo_outbox</c> row,
/// where it is stored once.
/// </para>
/// <para>
/// <b>That last sentence used to say "governed by that table's retention rather than by a log pipeline's",
/// and <c>alvo_outbox</c> has no retention.</b> Nothing deletes a row, and the payload holds the complete
/// unmasked post- and pre-image of every write for every entity and tenant — so the join key points at an
/// unbounded permanent store, not at a governed one. The rule above still holds, for the reason it always
/// did: a log pipeline's read set is wider still, and one more copy in it is strictly worse. But the
/// justification is now "one copy rather than two", not "one governed copy". Retention is filed as issue #154.
/// </para>
/// <para>
/// <b><see cref="ActionFailed"/> carries the exception, so anything a delivery interpolates into a message
/// reaches the pipeline.</b> That is why <c>WebhookTarget</c> exists and why nothing in the delivery path names
/// an endpoint's URL: <c>secretRef</c> is never read and no signature is sent, so a secret in the URL is the
/// only authentication an author has. Pinned by
/// <c>EventActionExecutorTests.No_log_line_carries_a_webhook_url_that_could_be_a_secret</c>.
/// </para>
/// <para>
/// <b><see cref="EmailSentToConsole"/> is the one deliberate exception, and it is not an exception to the
/// rule.</b> It writes the recipient, subject and body because for the console provider the log <em>is</em>
/// the mailbox — suppressing them would leave a mail provider that delivers nowhere and reports nothing. That
/// is exactly why the line has to name itself a development provider: an operator who sees message bodies in
/// production logs is looking at mail that was never sent.
/// </para>
/// </remarks>
internal static partial class EventLog
{
    /// <summary>The execution-log entry, written once per action that ran.</summary>
    /// <remarks>
    /// Written <b>after</b> the action succeeded, so the line means "this ran" rather than "this was
    /// attempted" — and an event that matched no hook produces none of these at all, which is the half of the
    /// execution-log criterion no counter can express.
    /// </remarks>
    /// <param name="logger">The logger the executor writes through.</param>
    /// <param name="hook">The hook's own JSON pointer, such as <c>/entities/deals/hooks/afterUpdate/0</c>.</param>
    /// <param name="action">The action's <c>type</c> discriminator, as the descriptor spells it.</param>
    /// <param name="eventId">The event's id — the join key to its <c>alvo_outbox</c> row and the consumer's dedup key.</param>
    /// <param name="eventType">The event type, such as <c>entity.deals.updated</c>.</param>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Alvo ran after-hook {Hook} ({Action}) for event {EventId} of type {EventType}.")]
    internal static partial void ActionExecuted(
        ILogger logger, string hook, string action, Guid eventId, string eventType);

    /// <summary>One delivery attempt that failed, written once per attempt rather than once per event.</summary>
    /// <remarks>
    /// The entry means "this attempt failed and the entry was handed back", so a retried event writes one of
    /// these per attempt — which is what makes the count comparable with <c>alvo.events.failed</c>. Nothing
    /// classifies the failure: a 500, a 404, a DNS failure and a timeout are indistinguishable at delivery from
    /// an endpoint whose deploy is thirty seconds out, so the exception is carried as itself and the ceiling
    /// decides when to stop.
    /// </remarks>
    /// <param name="logger">The logger the dispatcher writes through.</param>
    /// <param name="eventId">The event's id — the join key to its <c>alvo_outbox</c> row.</param>
    /// <param name="eventType">The event type, such as <c>entity.deals.updated</c>.</param>
    /// <param name="attempts">How many times the entry has been claimed, including this attempt.</param>
    /// <param name="failure">The failure exactly as it was thrown, stack trace and all.</param>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Alvo failed to deliver event {EventId} of type {EventType} on attempt {Attempts}. The entry "
            + "was handed back and is claimed again on a later tick.")]
    internal static partial void ActionFailed(
        ILogger logger, Guid eventId, string eventType, int attempts, Exception failure);

    /// <summary>The one loud line an abandoned event gets, because this build has no dead-letter queue.</summary>
    /// <remarks>
    /// At the ceiling the entry stops being claimed and is neither deleted nor moved: it stays in
    /// <c>alvo_outbox</c> with <c>dispatched_at</c> unset, so it is countable and inspectable. That is the whole
    /// stand-in for a queue (7.1 owns the real one), which is why abandonment has to be <em>loud</em> — an
    /// operator who never sees this line has no other signal that an event was given up on.
    /// </remarks>
    /// <param name="logger">The logger the dispatcher writes through.</param>
    /// <param name="eventId">The event's id — the join key to the row that is still there to inspect.</param>
    /// <param name="eventType">The event type, such as <c>entity.deals.updated</c>.</param>
    /// <param name="attempts">The attempt count that reached the ceiling.</param>
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Alvo gave up on event {EventId} of type {EventType} after {Attempts} attempts. It is no "
            + "longer claimed and was not deleted: the alvo_outbox row is still there, with dispatched_at "
            + "unset, so it can be inspected and — once the cause is fixed — released by hand.")]
    internal static partial void PoisonEvent(ILogger logger, Guid eventId, string eventType, int attempts);

    /// <summary>The pump ended on something other than a shutdown, and the host was left running.</summary>
    /// <remarks>
    /// <c>HostOptions.BackgroundServiceExceptionBehavior</c> defaults to <c>StopHost</c> and, from .NET 11,
    /// <c>RunAsync</c>/<c>StopAsync</c> also throw and the process exits non-zero — so a failure escaping
    /// <c>ExecuteAsync</c> would take down a host serving HTTP over one queue entry. The failure is contained
    /// instead, and this line is the whole notification: nothing restarts the pump, so a process that logs this
    /// serves requests and delivers no events until it is restarted.
    /// </remarks>
    /// <param name="logger">The logger the dispatcher writes through.</param>
    /// <param name="failure">The failure exactly as it was thrown, stack trace and all.</param>
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Alvo's outbox dispatcher stopped and will not restart in this process. The host keeps "
            + "serving requests, and no event is delivered until it is restarted.")]
    internal static partial void DispatcherStopped(ILogger logger, Exception failure);

    /// <summary>A hook's condition threw, so the hook was not selected.</summary>
    /// <remarks>
    /// Debug, because the loud version of this is one line per event and per hook — exactly the noise the
    /// execution-log criterion exists to prevent — and because a condition compiled in the
    /// <c>Condition</c> profile when the descriptor was applied cannot fail on an author's mistake. It is an
    /// internal invariant, recorded so it is diagnosable rather than invisible.
    /// </remarks>
    /// <param name="logger">The logger the dispatcher writes through.</param>
    /// <param name="hook">The hook's own JSON pointer.</param>
    /// <param name="eventId">The event whose subscription was being decided.</param>
    /// <param name="failure">The failure the evaluator threw.</param>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Alvo could not evaluate after-hook {Hook}'s condition for event {EventId}, so the hook was "
            + "not selected.")]
    internal static partial void ConditionRefusedTheHook(
        ILogger logger, string hook, Guid eventId, Exception failure);

    /// <summary>A hook's condition reads <c>@user.id</c> and the event records no actor, so it was not selected.</summary>
    /// <remarks>
    /// Debug for <see cref="ConditionRefusedTheHook"/>'s reasons, and separate from it because the cause is
    /// different and actionable: an anonymous write carries no <c>authid</c>, so a hook comparing a row against
    /// <c>@user.id</c> has nothing to compare it with. Refusing rather than comparing against the reserved
    /// all-zero id is the same direction the policy engine's required-context gate takes for a rule.
    /// </remarks>
    /// <param name="logger">The logger the dispatcher writes through.</param>
    /// <param name="hook">The hook's own JSON pointer.</param>
    /// <param name="eventId">The event whose subscription was being decided.</param>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Alvo did not select after-hook {Hook} for event {EventId}: its condition reads '@user.id' and "
            + "the event records no actor, so the comparison has no caller to resolve against.")]
    internal static partial void ConditionHasNoActorToRead(ILogger logger, string hook, Guid eventId);

    /// <summary>The development mail provider's one line, which is the whole message.</summary>
    /// <remarks>
    /// The word <em>development</em> is load-bearing and pinned by a fact: the failure mode this provider has
    /// is an operator believing mail is going out. Nothing in this build sends mail — there is no SMTP sender
    /// and no mail service in the compose file — so the line has to say so where it is read.
    /// </remarks>
    /// <param name="logger">The logger the console sender writes through.</param>
    /// <param name="to">The rendered recipient.</param>
    /// <param name="subject">The rendered subject.</param>
    /// <param name="body">The rendered body.</param>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Alvo's development email provider did not send this message — it has no SMTP sender and "
            + "writes mail to the log instead. To: {To} | Subject: {Subject} | Body: {Body}")]
    internal static partial void EmailSentToConsole(
        ILogger logger, string to, string subject, string body);
}
