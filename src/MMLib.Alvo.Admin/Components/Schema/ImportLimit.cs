using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// How much text the Import box takes, and how much one circuit message may carry so that it can (#316).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> A server-interactive box sends its whole text to the circuit on every input event, and SignalR
/// closes a connection whose message is over <c>MaximumReceiveMessageSize</c>: 32 KB by default, under the full
/// bike-workshop example (31.6 KB raw, more once escaped). The circuit then closed without a word, the box came back
/// empty and the form posted natively.
/// </para>
/// <para>
/// <b>The number is the framework's own descriptor ceiling.</b> Neither the analysis nor the spec sets a size for a
/// descriptor; the one number the build holds for a whole one is the expression check's
/// (<c>MaxCheckedDescriptorChars</c>, 1,000,000 characters, docs/superpowers/specs/2026-10-01-f5-expression-check-design.md
/// "Size"), which the schema editors post the working copy to on every keystroke. A larger copy could be imported and
/// then never checked as it is edited, so the box stops at the same count; <c>ImportLimitAgreementTests</c> in Host.Tests
/// holds the two to one number. That is about thirty times the largest example.
/// </para>
/// <para>
/// <b>The circuit's limit is sized from it.</b> The text travels JSON-escaped and UTF-8 encoded, so a Latin-script
/// descriptor costs at most two bytes a character ('"' as <c>\"</c>, 'č' as two bytes): 2 MiB carries the character
/// ceiling with room for the event's own envelope. Text dearer than that (control characters, scripts at three bytes a
/// character) meets <see cref="MaxSentBytes"/> first, which alvo.js measures exactly as it would be sent.
/// </para>
/// <para>
/// <b>What the raise costs, and who pays it.</b> The limit belongs to the one circuit hub (<c>ComponentHub</c>) every
/// server-interactive circuit in the process shares, an embedding host's own included, and it applies to a
/// <c>/_blazor</c> connection before anyone has signed in: <c>[Authorize]</c> acts on a page inside the circuit, not on
/// the connection. So a client can make the server buffer up to 2 MiB per message instead of 32 KB, which Microsoft's
/// guidance names as a denial-of-service risk. Taken deliberately, as a recorded deviation for the maintainer to accept;
/// bounded by <see cref="Internal.CircuitReceiveLimit"/>, which raises it only while the dashboard is enabled and never
/// lowers a host's own larger limit (a lower one set before <c>AddAlvoAdmin</c> is raised; one set after it wins).
/// </para>
/// <para>
/// <b>Refused at the box, never by the connection.</b> alvo.js stops an input over either ceiling before the circuit
/// hears of it, gives the box back the text the circuit last heard, and raises <c>alvo:oversized</c>; the Import screen
/// draws <see cref="Refusal"/> in its place.
/// </para>
/// </remarks>
internal static class ImportLimit
{
    /// <summary>The most characters the box takes: the expression check's descriptor ceiling.</summary>
    public const int MaxChars = 1_000_000;

    /// <summary>What one circuit message may carry, set on the dashboard's hub by <c>AddAlvoAdmin</c>.</summary>
    public const long CircuitReceiveBytes = 2 * 1024 * 1024;

    /// <summary>What the circuit's message adds around the box's text: the event's descriptor and the call's framing.</summary>
    public const long EnvelopeBytes = 64 * 1024;

    /// <summary>The most bytes the box's text may take as sent — JSON-escaped and UTF-8 encoded.</summary>
    public const long MaxSentBytes = CircuitReceiveBytes - EnvelopeBytes;

    /* Close to JSON.stringify, which leaves non-ASCII as itself: the count is for a sentence, not a gate. */
    private static readonly JsonSerializerOptions _escaping = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The refusal for a paste alvo.js stopped, with what it measured.</summary>
    /// <param name="measured">"&lt;characters&gt; &lt;bytes as sent&gt;", as <c>alvo:oversized</c> carries it; anything else is not repeated.</param>
    /// <returns>A sentence that names the paste's size, both ceilings, and what to do instead.</returns>
    public static string Refusal(string? measured)
    {
        var parts = (measured ?? string.Empty).Split(' ');
        var size = parts.Length == 2
                   && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var chars)
                   && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
            ? $"That paste is {chars.ToString("N0", CultureInfo.InvariantCulture)} characters, {Kilobytes(bytes)} as sent, and was not loaded. "
            : "That paste was not loaded. ";

        return size
            + $"The import box takes up to {MaxChars.ToString("N0", CultureInfo.InvariantCulture)} characters and {Kilobytes(MaxSentBytes)} "
            + "as sent — the most the schema editors' live check reads. Apply a larger descriptor through the Management API or the CLI.";
    }

    /// <summary>What alvo.js would have measured for <paramref name="text"/>: its characters, then its bytes JSON-escaped and UTF-8 encoded.</summary>
    /// <param name="text">A text that reached the circuit.</param>
    /// <returns>"&lt;characters&gt; &lt;bytes&gt;", as <see cref="Refusal"/> reads it.</returns>
    public static string Measure(string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(text, _escaping));
        return string.Create(CultureInfo.InvariantCulture, $"{text.Length} {bytes}");
    }

    private static string Kilobytes(long bytes)
        => $"{Math.Round(bytes / 1024d, MidpointRounding.AwayFromZero).ToString("N0", CultureInfo.InvariantCulture)} KB";
}
