using System.Globalization;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// How much text the Import box takes, and how many bytes the stream that carries it to the server may hold (#316).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> A server-interactive box sends its whole text to the circuit on every input event, and SignalR
/// closes a connection whose message is over <c>MaximumReceiveMessageSize</c>: 32 KB by default, under the full
/// bike-workshop example (31.6 KB raw, more once escaped). The circuit then closed without a word, the box came back
/// empty and the form posted natively.
/// </para>
/// <para>
/// <b>The text never travels in a circuit message.</b> The box is a native textarea with no handler, since Blazor sends
/// an input's whole value with every event dispatched from it; alvo.js stops its input events, and tells the circuit only
/// its line count and whether it holds anything (<see cref="BoxMeasure"/>). On submit the page asks for a stream over the
/// box's text (a <c>Blob</c>, which the framework wraps with <c>DotNet.createJSStreamReference</c>) and reads it through
/// <c>IJSStreamReference</c> (<see cref="ImportStream"/>), which Blazor carries in chunks under the hub's limit. So the circuit hub keeps SignalR's
/// own 32 KB, for an embedding host's circuits and for a <c>/_blazor</c> connection before sign-in alike:
/// <c>AddAlvoAdmin</c> raises nothing. This replaced a raise of that limit to 2 MiB (Ruling N's mechanism, superseded by
/// Ruling U-B), which let any client make the server buffer 2 MiB per message before signing in.
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
/// <b>The stream's ceiling is sized from it.</b> A <c>Blob</c> encodes the box's text as UTF-8, and one UTF-16 unit is at
/// most three UTF-8 bytes (a surrogate pair is four bytes for two units, a lone surrogate becomes U+FFFD's three), so a
/// text within <see cref="MaxChars"/> is always within <see cref="MaxStreamBytes"/>. The byte ceiling therefore never
/// refuses what the box let through: it is the server's own bound on what it reads from a client that did not go through
/// the box, checked against the stream's declared length before a byte is read.
/// </para>
/// <para>
/// <b>Refused at the box first.</b> alvo.js stops an input over the character ceiling, gives the box back its last text
/// within it, and raises <c>alvo:oversized</c>; it refuses a submit over it the same way, and streams nothing. The
/// Import screen draws <see cref="Refusal(string?)"/> in its place. The server re-checks both ceilings on what it reads.
/// </para>
/// </remarks>
internal static class ImportLimit
{
    /// <summary>The most characters the box takes: the expression check's descriptor ceiling.</summary>
    public const int MaxChars = 1_000_000;

    /// <summary>The most UTF-8 bytes one UTF-16 unit of the box's text encodes to.</summary>
    public const int MaxUtf8BytesPerChar = 3;

    /// <summary>Room above the exact bound, so the ceiling is never the thing a correct paste meets.</summary>
    public const long StreamSlackBytes = 4 * 1024;

    /// <summary>The most bytes the server reads from the stream that carries the box's text.</summary>
    public const long MaxStreamBytes = ((long)MaxChars * MaxUtf8BytesPerChar) + StreamSlackBytes;

    /// <summary>The refusal for a paste alvo.js stopped, with what it measured.</summary>
    /// <param name="measured">The paste's character count, as <c>alvo:oversized</c> carries it; anything else is not repeated.</param>
    /// <returns>A sentence that names the paste's size, the ceiling, and what to do instead.</returns>
    public static string Refusal(string? measured)
        => Refusal(
            long.TryParse(measured, NumberStyles.None, CultureInfo.InvariantCulture, out var chars) ? chars : null,
            bytes: null);

    /// <summary>The refusal for a paste over a ceiling, naming whatever of its size is known.</summary>
    /// <param name="chars">Its characters, when they were counted.</param>
    /// <param name="bytes">Its bytes as streamed, when they were known.</param>
    /// <returns>A sentence that names the paste's size, the ceiling, and what to do instead.</returns>
    public static string Refusal(long? chars, long? bytes)
        => Size(chars, bytes)
           + $"The import box takes up to {MaxChars.ToString("N0", CultureInfo.InvariantCulture)} characters — the most the "
           + "schema editors' live check reads. Apply a larger descriptor through the Management API or the CLI.";

    private static string Size(long? chars, long? bytes) => (chars, bytes) switch
    {
        ({ } c, { } b) => $"That paste is {Characters(c)}, {Kilobytes(b)}, and was not loaded. ",
        ({ } c, null) => $"That paste is {Characters(c)} and was not loaded. ",
        (null, { } b) => $"That paste is {Kilobytes(b)} and was not loaded. ",
        _ => "That paste was not loaded. ",
    };

    private static string Characters(long chars) => $"{chars.ToString("N0", CultureInfo.InvariantCulture)} characters";

    private static string Kilobytes(long bytes)
        => $"{Math.Round(bytes / 1024d, MidpointRounding.AwayFromZero).ToString("N0", CultureInfo.InvariantCulture)} KB";
}
