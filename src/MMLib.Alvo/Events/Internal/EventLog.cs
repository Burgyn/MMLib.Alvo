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
/// <b>No line in this subsystem carries a rendered value.</b> An action log entry names the hook's own JSON
/// pointer, the action type, and the event's id and type — descriptor coordinates and event identity, and
/// nothing that came out of a row. The reason is <see cref="AlvoEventData"/>'s: the envelope carries the
/// <em>unmasked</em> post-image, so a rendered webhook payload or email body can contain a <c>hidden</c>
/// field. Logging the rendered value would take that field out of the one place the design accepted it going
/// — a descriptor-declared endpoint, chosen by the same author as the <c>hidden</c> rule — and put it into
/// whatever ships logs, which nobody declared and no author chose. The event id is the join key: an operator
/// who needs the payload reads the <c>alvo_outbox</c> row, where it is stored once and governed by that
/// table's retention rather than by a log pipeline's.
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
