using System.Globalization;
using System.Net.Mail;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The checks an email's <c>to</c> and <c>subject</c> pass before a message reaches <see cref="IEmailSender"/>:
/// a recipient is exactly one mailbox address, and neither value carries a line break or another control
/// character.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both are header values, and both can be row text.</b> <c>email.to</c> takes the same placeholders as
/// the body and the recommended shape is <c>{{new.owner_email}}</c>, so anyone who can write a row chooses
/// the recipient. A <c>CR</c>/<c>LF</c> in a header value is header injection (CWE-93) in any sender that
/// concatenates it, and <c>a@x.com, b@y.com</c> is a second recipient in any sender that parses a list. The
/// check lives on Alvo's side of the port so a host's sender inherits it rather than having to know it.
/// </para>
/// <para>
/// <b><see cref="MailAddress"/> parses, and equality decides.</b> <see cref="MailAddress.TryCreate(string?, out MailAddress?)"/>
/// is permissive: it reads <c>a@x.com, b@y.com</c> as the display name <c>a@x.com,</c> and the address
/// <c>b@y.com</c>, and <c>Boss &lt;a@x.com&gt;</c> as a display name too. So a value is one mailbox only when
/// it parses with no display name <em>and</em> its parsed address is the whole value, character for
/// character — which refuses a list, a display name, a comment and surrounding whitespace with one rule.
/// </para>
/// <para>
/// <b>Checked twice, for the reason every template is.</b> <see cref="LiteralRefusal"/> is the apply-time
/// half and refuses what the author wrote; <see cref="RefusedSlot"/> is the delivery-time half and refuses
/// what a row rendered, where the executor's throw takes the existing failure path.
/// </para>
/// </remarks>
internal static class MailHeaders
{
    /// <summary>
    /// Why the author's own text in a <c>to</c> or <c>subject</c> template can never render a safe header,
    /// or <see langword="null"/> when it can.
    /// </summary>
    /// <param name="slot"><see cref="ActionSlot.To"/> or <see cref="ActionSlot.Subject"/>.</param>
    /// <param name="template">The slot's parsed template.</param>
    internal static string? LiteralRefusal(string slot, AlvoTemplate template)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(template);

        if (template.Segments.Any(segment => !segment.IsPlaceholder && !IsSingleLine(segment.Text)))
        {
            return LiteralLineBreak(slot);
        }

        var literal = LiteralRecipient(slot, template);

        return literal is not null && !IsSingleMailbox(literal) ? NotOneMailbox(literal) : null;
    }

    /// <summary>The slot whose rendered value is not a safe header, or <see langword="null"/> when both are.</summary>
    /// <param name="message">The rendered message.</param>
    internal static string? RefusedSlot(AlvoMailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return !IsSingleMailbox(message.To) ? ActionSlot.To
            : !IsSingleLine(message.Subject) ? ActionSlot.Subject
            : null;
    }

    /// <summary>Whether <paramref name="value"/> is exactly one mailbox address and nothing else.</summary>
    /// <param name="value">A rendered or literal recipient.</param>
    internal static bool IsSingleMailbox(string value) =>
        IsSingleLine(value)
        && MailAddress.TryCreate(value, out var address)
        && address.DisplayName.Length == 0
        && string.Equals(address.Address, value, StringComparison.Ordinal);

    /// <summary>Whether <paramref name="value"/> carries no control character and no Unicode line or paragraph separator.</summary>
    /// <param name="value">A header value.</param>
    internal static bool IsSingleLine(string value) => !value.Any(BreaksALine);

    private static bool BreaksALine(char character) =>
        char.IsControl(character)
        || char.GetUnicodeCategory(character) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    /// <summary>A placeholder-free recipient's whole text, which apply can check completely.</summary>
    private static string? LiteralRecipient(string slot, AlvoTemplate template) =>
        string.Equals(slot, ActionSlot.To, StringComparison.Ordinal) && template.Placeholders.Count == 0
            ? string.Concat(template.Segments.Select(segment => segment.Text))
            : null;

    private static string LiteralLineBreak(string slot) =>
        $"The '{slot}' template's own text carries a line break or another control character. '{slot}' is a "
        + "single-line mail header, and a line break in a header value is header injection: whatever follows it "
        + "is read as a header of its own.";

    private static string NotOneMailbox(string recipient) =>
        $"'{recipient}' is not exactly one mailbox address, so no message could ever be sent to it. A recipient "
        + "is one address with no display name, no list and no surrounding whitespace.";
}
