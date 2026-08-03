using Microsoft.Extensions.Logging;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The development <see cref="IEmailSender"/>: it writes the whole message to the log and sends nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is registered by default because the alternative is worse.</b> An <c>email</c> action with no
/// provider registered would fail at delivery and be retried to the attempt ceiling — an authoring-time
/// question ("is mail configured?") answered as a runtime outage. A provider that visibly does nothing turns
/// that into one readable line.
/// </para>
/// <para>
/// <b>The line names itself a development provider, and that word is pinned by a fact.</b> This provider's
/// one failure mode is an operator believing mail is going out; there is no SMTP sender in this build and no
/// mail service in the compose file, so nothing else in the system would tell them otherwise.
/// </para>
/// <para>
/// <b>It is the one place in this subsystem that logs a rendered value</b> — see <see cref="EventLog"/> for
/// why nothing else does. Here the log is the mailbox: a console provider that redacted the body would
/// deliver nowhere and report nothing.
/// </para>
/// </remarks>
/// <param name="logger">The logger the message is written to.</param>
internal sealed class ConsoleEmailSender(ILogger<ConsoleEmailSender> logger) : IEmailSender
{
    /// <inheritdoc/>
    /// <remarks>Idempotent by construction: a re-delivered event writes the same line again and sends nothing.</remarks>
    public Task SendAsync(AlvoMailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        EventLog.EmailSentToConsole(logger, message.To, message.Subject, message.Body);

        return Task.CompletedTask;
    }
}
