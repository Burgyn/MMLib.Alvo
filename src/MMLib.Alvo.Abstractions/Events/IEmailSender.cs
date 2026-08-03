namespace MMLib.Alvo.Events;

/// <summary>
/// One outbound message an <c>email</c> after-hook action produced: the rendered recipient, subject and body.
/// </summary>
/// <remarks>
/// <para>
/// Three plain strings, because every one of them comes out of the <c>{{…}}</c> template engine already
/// rendered. There is no attachment, no CC/BCC, no HTML/plain-text alternative and no reply-to: the frozen
/// <c>$defs/action</c> declares <c>template</c>, <c>to</c> and <c>data</c> and nothing else, so a richer
/// message type would be a shape no descriptor can express.
/// </para>
/// <para>
/// <b>Every field can carry row data, including a <c>hidden</c> one.</b> A recipient rendered from
/// <c>{{new.owner_email}}</c> and a body rendered from <c>{{new.commission_note}}</c> are ordinary uses of
/// this type, so an implementation is handling row content: it is the delivery channel, not a log sink, and
/// what it does with the message is a disclosure decision belonging to whoever registers it.
/// </para>
/// <para>
/// <b><see cref="To"/> is unvalidated rendered row text, and an implementation must validate it.</b>
/// <c>email.to</c> takes the same <c>{{…}}</c> placeholders as the body, and the recommended shape is a field
/// on the record — so <em>anyone who can write a row chooses this recipient</em>, and Alvo checks only that
/// the placeholder resolves, never that the result is an address. Two consequences an implementation owns:
/// a value carrying <c>CR</c> or <c>LF</c> is <b>SMTP header injection</b> in any sender that concatenates it
/// into a header, and an empty string is reachable — a template that renders to nothing, or a NULL column —
/// which surfaces as a mail failure that reads like a broken mail server. Both are named here rather than
/// discovered by the PR that adds a real sender, because a caller-controlled recipient is inert only for as
/// long as the shipped provider delivers nowhere. Tracked in issue #155.
/// </para>
/// </remarks>
/// <param name="To">The recipient address, already rendered.</param>
/// <param name="Subject">The subject line, already rendered; empty when the template declares none.</param>
/// <param name="Body">The body, already rendered; empty when the template declares none.</param>
public sealed record AlvoMailMessage(string To, string Subject, string Body);

/// <summary>
/// The mail provider port an <c>email</c> after-hook action delivers through.
/// </summary>
/// <remarks>
/// <para>
/// <b>A console development provider ships; SMTP does not.</b> Alvo's registration binds this port to a
/// provider that writes the whole message to the log and says so in the line it writes — there is no SMTP
/// sender in this build and no mail service in the compose file, so "email works end to end" is provable
/// against the console provider and against nothing else. A host that wants mail delivered registers its own
/// <see cref="IEmailSender"/> and takes the port over, exactly as it would any other provider.
/// </para>
/// <para>
/// <b>Delivery is at-least-once, so an implementation must be idempotent or deduplicate.</b> The dispatcher
/// claims an outbox entry, delivers, and marks it dispatched afterwards; a process that dies in between
/// re-delivers the same event after its lease expires. The dedup key is the event's own <c>id</c> — that is
/// what CloudEvents defines it for — and it is available to a sender that needs it because
/// <c>{{event.id}}</c> renders into any slot of the message.
/// </para>
/// <para>
/// <b>A failure must throw.</b> The executor does not catch, so the dispatcher sees the failure, releases the
/// outbox entry and retries it up to the configured ceiling. An implementation that logged and returned
/// would report every permanently undelivered message as delivered.
/// </para>
/// </remarks>
public interface IEmailSender
{
    /// <summary>Sends one message.</summary>
    /// <param name="message">The rendered message.</param>
    /// <param name="cancellationToken">A token to cancel the send; cancelled when the host is shutting down.</param>
    /// <exception cref="Exception">
    /// The message could not be sent. Any exception type is a delivery failure: the dispatcher retries the
    /// event rather than interpreting the failure, because nothing at delivery time can tell a permanent
    /// refusal from an outage that ends before the attempt ceiling does.
    /// </exception>
    Task SendAsync(AlvoMailMessage message, CancellationToken cancellationToken = default);
}
