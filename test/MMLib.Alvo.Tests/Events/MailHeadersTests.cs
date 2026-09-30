using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// An email's <c>to</c> and <c>subject</c> are mail headers built from row text, so a recipient is exactly one
/// mailbox and neither carries a line break (CWE-93).
/// </summary>
public class MailHeadersTests
{
    [Theory]
    [InlineData("o@x.z")]
    [InlineData("ops@example.com")]
    [InlineData("first.last+tag@sub.example.co.uk")]
    [InlineData("žluť@příklad.cz")]
    public void A_single_plain_address_is_one_mailbox(string recipient)
        => MailHeaders.IsSingleMailbox(recipient).ShouldBeTrue();

    /// <summary>
    /// <see cref="System.Net.Mail.MailAddress"/> accepts every one of these — a list parses as a display name
    /// plus the last address — which is why equality with the parsed address decides, not the parse.
    /// </summary>
    [Theory]
    [InlineData("a@x.com\r\nBcc: victim@y.com")]
    [InlineData("a@x.com\nBcc: victim@y.com")]
    [InlineData("a@x.com, b@y.com")]
    [InlineData("a@x.com;b@y.com")]
    [InlineData("Boss <a@x.com>")]
    [InlineData("<a@x.com>")]
    [InlineData("(comment)a@x.com")]
    [InlineData(" a@x.com")]
    [InlineData("a@x.com ")]
    [InlineData("a@x.com\u0000")]
    [InlineData("a@x.com\u2028")]
    [InlineData("not-an-address")]
    [InlineData("")]
    public void A_list_a_display_name_a_line_break_or_a_non_address_is_not_one_mailbox(string recipient)
        => MailHeaders.IsSingleMailbox(recipient).ShouldBeFalse();

    [Theory]
    [InlineData("Deal won\r\nBcc: victim@y.com")]
    [InlineData("Deal\nwon")]
    [InlineData("Deal\u0085won")]
    [InlineData("Deal\u2029won")]
    [InlineData("Deal\u001bwon")]
    public void A_subject_with_a_line_break_or_control_character_is_refused(string subject)
        => MailHeaders.RefusedSlot(new AlvoMailMessage("o@x.z", subject, "body")).ShouldBe(ActionSlot.Subject);

    [Fact]
    public void A_recipient_is_checked_before_the_subject()
        => MailHeaders.RefusedSlot(new AlvoMailMessage("a@x.com, b@y.com", "a\nb", "body")).ShouldBe(ActionSlot.To);

    [Fact]
    public void A_normal_message_passes_with_a_multi_line_body()
        => MailHeaders.RefusedSlot(new AlvoMailMessage("o@x.z", "Deal won: Big deal — 1 200 €", "line one\nline two"))
            .ShouldBeNull();

    [Fact]
    public void A_literal_recipient_is_checked_completely_at_apply()
        => MailHeaders.LiteralRefusal(ActionSlot.To, AlvoTemplate.Parse("ops@firma.sk, boss@firma.sk"))
            .ShouldNotBeNull().ShouldContain("not exactly one mailbox");

    [Fact]
    public void A_recipient_with_a_placeholder_is_left_to_delivery()
        => MailHeaders.LiteralRefusal(ActionSlot.To, AlvoTemplate.Parse("{{new.owner_email}}")).ShouldBeNull();

    [Theory]
    [InlineData("to", "{{new.owner_email}}\r\nBcc: x@y.z")]
    [InlineData("subject", "Deal won\n{{new.title}}")]
    public void A_line_break_in_the_authors_own_header_text_is_refused_at_apply(string slot, string source)
        => MailHeaders.LiteralRefusal(slot, AlvoTemplate.Parse(source)).ShouldNotBeNull().ShouldContain("line break");

    [Fact]
    public void A_single_line_subject_template_passes_at_apply()
        => MailHeaders.LiteralRefusal(ActionSlot.Subject, AlvoTemplate.Parse("Deal won: {{new.title}}")).ShouldBeNull();
}
