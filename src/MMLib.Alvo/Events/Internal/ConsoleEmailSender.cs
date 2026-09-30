using Microsoft.Extensions.Logging;

using System.Text.Encodings.Web;
using System.Text.Json;

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
/// deliver nowhere and report nothing. The body goes to Debug and the envelope to Information, so a pipeline
/// shipping Information carries the recipient and the subject but not the part most likely to hold row data.
/// </para>
/// <para>
/// <b>Every value is escaped before it is logged</b>, because a structured-logging parameter is still
/// substituted verbatim by a text formatter, and a line break in row text would then write a line that looks
/// like the log's own. The executor has already refused a recipient or subject carrying one, but this port
/// is also a host's, and a body legitimately spans lines. The encoding is <see cref="JsonEncodedText"/>'s
/// with the relaxed encoder: control characters, quotes and backslashes are escaped, and non-ASCII text stays
/// readable — this is a log line, not an HTML context.
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

        EventLog.EmailSentToConsole(logger, Escaped(message.To), Escaped(message.Subject), message.Body.Length);
        EventLog.EmailBodyToConsole(logger, Escaped(message.Body));

        return Task.CompletedTask;
    }

    private static string Escaped(string value) =>
        JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();
}
