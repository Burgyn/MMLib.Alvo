using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MMLib.Alvo.Admin.Internal;
using System.Globalization;
using System.Text;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// Reads the Import box's text from the stream alvo.js made over it on submit, within <see cref="ImportLimit"/> (#316).
/// </summary>
/// <remarks>
/// The stream is the browser's word, so both ceilings are judged here again: its declared length before anything is read
/// (the framework's own stream then refuses more bytes than were declared), and the character count of what was read.
/// A read that fails for any other reason than the page or the circuit going is refused in place too, so the circuit
/// survives it and the paste can be tried again.
/// </remarks>
internal static partial class ImportStream
{
    /// <summary>The sentence for a paste the stream could not deliver.</summary>
    public const string UnreadableRefusal = "That paste could not be read. Try again, or import a smaller descriptor.";

    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>The box's text, or the refusal for a paste over a ceiling.</summary>
    /// <param name="stream">The stream over the box's text.</param>
    /// <param name="logger">Where a failed read is reported — never with the text.</param>
    /// <param name="ct">Cancels the read, when the page goes.</param>
    /// <returns>The text read, or the sentence that says why it was not.</returns>
    /// <remarks>A cancellation or a circuit that has gone is not caught: there is no page left to refuse it on.</remarks>
    public static async Task<ImportRead> ReadAsync(IJSStreamReference stream, ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Length > ImportLimit.MaxStreamBytes)
        {
            return ImportRead.Refused(ImportLimit.Refusal(chars: null, stream.Length));
        }

        string text;
        try
        {
            text = await ReadTextAsync(stream, ct);
        }
        catch (Exception exception) when (!AdminInterop.IsDisconnect(exception))
        {
            ReadFailed(logger, stream.Length, exception);
            return ImportRead.Failed();
        }

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

    [LoggerMessage(EventId = 23, Level = LogLevel.Warning,
        Message = "The Import box's text could not be read from its stream ({Bytes} bytes declared); the paste was refused in place.")]
    private static partial void ReadFailed(ILogger logger, long bytes, Exception exception);
}

/// <summary>What <see cref="ImportStream.ReadAsync"/> found: the text, or why it was not read.</summary>
/// <param name="Text">The box's text, when it was read.</param>
/// <param name="Refusal">The sentence for a paste over a ceiling, or one the stream could not deliver, when it was not.</param>
/// <param name="Unreadable">Whether the stream failed, rather than the paste being over a ceiling.</param>
internal sealed record ImportRead(string? Text, string? Refusal, bool Unreadable = false)
{
    /// <summary>A text read whole.</summary>
    public static ImportRead Read(string text) => new(text, null);

    /// <summary>A paste over a ceiling.</summary>
    public static ImportRead Refused(string refusal) => new(null, refusal);

    /// <summary>A paste the stream could not deliver.</summary>
    public static ImportRead Failed() => new(null, ImportStream.UnreadableRefusal, Unreadable: true);
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
