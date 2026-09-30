using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// Renders a <c>webhook.payload</c> template as JSON <em>by construction</em>: every placeholder value is
/// encoded for the position it sits in, and the result is parsed before it is allowed out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each value is encoded for its sink, which is OWASP's output-encoding rule.</b> A placeholder inside a
/// JSON string renders as that string's escaped content, through <see cref="JsonEncodedText"/> with the same
/// default encoder the envelope is written with — so a quote, a backslash or a newline in a value is data
/// and can never close the string. A placeholder outside every string renders as exactly <em>one</em> JSON
/// value, written by <see cref="AlvoEventJson.TryWriteValue"/>: a number stays a number, a boolean is
/// <c>true</c>/<c>false</c>, a null is <c>null</c>, and a text value is a quoted string — so <c>1, 2</c> or
/// <c>[1]</c> in a text field is still one string and cannot forge an element or a member.
/// </para>
/// <para>
/// <b>So the author's literal text alone fixes the document's shape.</b> A value is one token or string
/// content, never structure, which is what makes <see cref="TryValidate"/> a proof rather than a sample: it
/// fills every placeholder with a stand-in and parses the result, and a template that parses then parses for
/// every row. The one position that breaks this — straight after a backslash inside a string, where the
/// backslash would pair with the value's first character and let the value decide where the string ends — is
/// refused at apply by name.
/// </para>
/// <para>
/// <b>The delivery-time parse is defence in depth, and a body that fails it is never sent.</b> After
/// <see cref="TryValidate"/> no row can reach it — which is the point of by-construction rendering — so it
/// guards the renderer against itself, for the price of one parse per delivery. A value JSON has no spelling
/// for (a non-finite <see cref="double"/>) is refused one step earlier, by <see cref="Utf8JsonWriter"/>
/// throwing. Either way the executor throws, which is the existing failure path — released, retried to the
/// attempt ceiling, logged as a poison event. Retrying a deterministic failure spends a bounded number of
/// attempts; a separate "permanent" verdict for one failure class would need the dead-letter queue the event
/// design defers to 7.1, and would be the only such verdict in the dispatcher.
/// </para>
/// </remarks>
internal static class JsonPayload
{
    /// <summary>Whether <paramref name="template"/> renders a JSON document for every row, and why not.</summary>
    /// <param name="template">A parsed <c>webhook.payload</c> template.</param>
    /// <param name="refusal">Why it does not; <see langword="null"/> when it does.</param>
    internal static bool TryValidate(AlvoTemplate template, [NotNullWhen(false)] out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(template);

        refusal = Positions(template).Contains(JsonPosition.AfterEscape)
            ? EscapedPlaceholder(template)
            : IsJson(Fill(template, StandIn)) ? null : NotJson(template);

        return refusal is null;
    }

    /// <summary>Renders <paramref name="template"/> against one event, encoding every value for its position.</summary>
    /// <param name="template">A <c>webhook.payload</c> template that <see cref="TryValidate"/> accepted.</param>
    /// <param name="event">The event the placeholders read.</param>
    /// <param name="body">The rendered JSON document; <see langword="null"/> when the result does not parse.</param>
    internal static bool TryRender(AlvoTemplate template, AlvoEvent @event, [NotNullWhen(true)] out string? body)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(@event);

        var rendered = Fill(
            template, (placeholder, position) => Encoded(placeholder, position, TemplatePlaceholder.ValueOf(placeholder, @event)));
        body = IsJson(rendered) ? rendered : null;

        return body is not null;
    }

    private const string BareStandIn = "null";

    private static string StandIn(string placeholder, JsonPosition position) =>
        position == JsonPosition.Bare ? BareStandIn : string.Empty;

    private static string Fill(AlvoTemplate template, Func<string, JsonPosition, string> value)
    {
        var body = new StringBuilder();
        var lexer = default(JsonStringLexer);
        foreach (var segment in template.Segments)
        {
            body.Append(segment.IsPlaceholder ? value(segment.Text, lexer.Position) : segment.Text);
            lexer = lexer.After(segment);
        }

        return body.ToString();
    }

    private static IEnumerable<JsonPosition> Positions(AlvoTemplate template)
    {
        var lexer = default(JsonStringLexer);
        foreach (var segment in template.Segments)
        {
            if (segment.IsPlaceholder)
            {
                yield return lexer.Position;
            }

            lexer = lexer.After(segment);
        }
    }

    private static string Encoded(string placeholder, JsonPosition position, object? value) => position switch
    {
        JsonPosition.Quoted => JsonEncodedText.Encode(AlvoTemplate.Format(value)).ToString(),
        JsonPosition.Bare => JsonValue(placeholder, value),
        _ => throw EscapedAtDelivery(placeholder),
    };

    private static string JsonValue(string placeholder, object? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (!AlvoEventJson.TryWriteValue(writer, value))
            {
                throw Unwritable(placeholder, value);
            }
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static bool IsJson(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string NotJson(AlvoTemplate template) =>
        $"The payload template '{Source(template)}' is not a JSON document once its placeholders are filled, and "
        + "a webhook body is posted as application/json. A placeholder inside a JSON string renders as that "
        + "string's escaped content; one outside a string renders as a single JSON value, so the literal text "
        + "around the placeholders has to be valid JSON on its own.";

    private static string EscapedPlaceholder(AlvoTemplate template) =>
        $"The payload template '{Source(template)}' puts a placeholder straight after a backslash inside a JSON "
        + "string. The backslash would pair with the value's first character, so the value would decide where "
        + "the string ends — which is the injection this renderer exists to prevent.";

    private static string Source(AlvoTemplate template) => string.Concat(template.Segments.Select(SourceOf));

    private static string SourceOf(AlvoTemplateSegment segment) =>
        segment.IsPlaceholder
            ? $"{AlvoTemplate.PlaceholderOpen}{segment.Text}{AlvoTemplate.PlaceholderClose}"
            : segment.Text;

    private static InvalidOperationException EscapedAtDelivery(string placeholder) => new(
        $"The payload placeholder '{placeholder}' follows a backslash inside a JSON string, which "
        + "JsonPayload.TryValidate refuses when a descriptor is applied — so reaching this point means a template "
        + "was compiled without that check.");

    private static NotSupportedException Unwritable(string placeholder, object? value) => new(
        $"The payload placeholder '{placeholder}' resolved to a {value?.GetType().Name}, which no field type maps "
        + "to and which therefore has no JSON spelling.");
}

/// <summary>Where a placeholder sits in a JSON payload template.</summary>
internal enum JsonPosition
{
    /// <summary>Outside every JSON string, where a whole JSON value goes.</summary>
    Bare,

    /// <summary>Inside a JSON string, where escaped string content goes.</summary>
    Quoted,

    /// <summary>Inside a JSON string, straight after an unpaired backslash.</summary>
    AfterEscape,
}

/// <summary>
/// The one piece of JSON's lexical grammar a payload template needs: whether a point in the text is inside a
/// string, and whether it follows an unpaired backslash there.
/// </summary>
/// <remarks>
/// Only literal segments move it. A placeholder's rendered value is string content or one whole value, and
/// neither changes whether the text after it is inside a string — which is the property the encoding exists
/// to guarantee.
/// </remarks>
/// <param name="InString">Whether the point is inside a JSON string.</param>
/// <param name="Escaped">Whether the point follows an unpaired backslash inside that string.</param>
internal readonly record struct JsonStringLexer(bool InString, bool Escaped)
{
    private const char Quote = '"';
    private const char Backslash = '\\';

    /// <summary>The position a placeholder at this point sits in.</summary>
    internal JsonPosition Position =>
        Escaped ? JsonPosition.AfterEscape : InString ? JsonPosition.Quoted : JsonPosition.Bare;

    /// <summary>The state after <paramref name="segment"/>; a placeholder leaves it unchanged.</summary>
    /// <param name="segment">The next segment of the template.</param>
    internal JsonStringLexer After(AlvoTemplateSegment segment)
    {
        if (segment.IsPlaceholder)
        {
            return this;
        }

        var state = this;
        foreach (var character in segment.Text)
        {
            state = state.After(character);
        }

        return state;
    }

    private JsonStringLexer After(char character) =>
        Escaped ? this with { Escaped = false }
        : InString && character == Backslash ? this with { Escaped = true }
        : character == Quote ? this with { InString = !InString }
        : this;
}
