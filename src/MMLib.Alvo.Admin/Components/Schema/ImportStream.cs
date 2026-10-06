using Microsoft.JSInterop;
using System.Globalization;
using System.Text;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// Reads the Import box's text from the stream alvo.js made over it on submit, within <see cref="ImportLimit"/> (#316).
/// </summary>
/// <remarks>
/// The stream is the browser's word, so both ceilings are judged here again: its declared length before anything is read
/// (the framework's own stream then refuses more bytes than were declared), and the character count of what was read.
/// </remarks>
internal static class ImportStream
{
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>The box's text, or the refusal for a paste over a ceiling.</summary>
    /// <param name="stream">The stream over the box's text.</param>
    /// <param name="ct">Cancels the read, when the page goes.</param>
    /// <returns>The text read, or the sentence that says why it was not.</returns>
    public static async Task<ImportRead> ReadAsync(IJSStreamReference stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Length > ImportLimit.MaxStreamBytes)
        {
            return ImportRead.Refused(ImportLimit.Refusal(chars: null, stream.Length));
        }

        var text = await ReadTextAsync(stream, ct);
        return text.Length > ImportLimit.MaxChars
            ? ImportRead.Refused(ImportLimit.Refusal(text.Length, stream.Length))
            : ImportRead.Read(text);
    }

    private static async Task<string> ReadTextAsync(IJSStreamReference stream, CancellationToken ct)
    {
        await using var bytes = await stream.OpenReadStreamAsync(ImportLimit.MaxStreamBytes, ct);
        using var reader = new StreamReader(bytes, _utf8, detectEncodingFromByteOrderMarks: false);
        return await reader.ReadToEndAsync(ct);
    }
}

/// <summary>What <see cref="ImportStream.ReadAsync"/> found: the text, or why it was not read.</summary>
/// <param name="Text">The box's text, when it was read.</param>
/// <param name="Refusal">The sentence for a paste over a ceiling, when it was not.</param>
internal sealed record ImportRead(string? Text, string? Refusal)
{
    /// <summary>A text read whole.</summary>
    public static ImportRead Read(string text) => new(text, null);

    /// <summary>A paste over a ceiling.</summary>
    public static ImportRead Refused(string refusal) => new(null, refusal);
}

/// <summary>
/// What the Import box tells the circuit as it is typed into, in place of its text: how many lines it holds, and whether
/// it holds anything but whitespace (<c>alvo:measured</c>).
/// </summary>
/// <param name="Lines">Its line count; 0 for an empty box.</param>
/// <param name="Filled">Whether it holds more than whitespace.</param>
internal readonly record struct BoxMeasure(int Lines, bool Filled)
{
    /// <summary>An empty box.</summary>
    public static BoxMeasure Empty => default;

    /// <summary>Reads "&lt;lines&gt; &lt;1 or 0&gt;", as alvo.js sends it; anything else reads as an empty box.</summary>
    /// <param name="measured">The value <c>alvo:measured</c> carried.</param>
    /// <returns>The measure.</returns>
    public static BoxMeasure Parse(string? measured)
    {
        var parts = (measured ?? string.Empty).Split(' ');
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var lines)
               && parts[1] is "0" or "1"
            ? new BoxMeasure(lines, parts[1] == "1")
            : Empty;
    }
}
