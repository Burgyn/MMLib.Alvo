using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// A mutate literal as the target field holds it, or why it cannot — checked as the operator types (spec B3, §4.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>The same calls the apply makes.</b> <c>BeforeHookCompiler.Convert</c> reads the literal's JSON with
/// <c>TryGetInt64</c>, <c>TryGetDecimal</c>, <c>TryGetDateTimeOffset</c> and <c>TryGetGuid</c>
/// (src/MMLib.Alvo/Rules/Internal/BeforeHookCompiler.cs:435-445); this converts the typed text to the JSON it will write
/// and asks the same <c>System.Text.Json</c> questions, so a literal accepted here is accepted there
/// (<c>HooksEditorAgreementTests</c>).
/// </para>
/// <para>
/// <b>An enum literal must be a declared value.</b> Apply refuses a non-member too once #308 lands (it did not when this
/// was written, spec §11); the form refuses it as typed, so the operator learns it at the box rather than at apply.
/// </para>
/// </remarks>
internal static class MutateLiteral
{
    /// <summary>The JSON a literal row writes into a field, or why the field cannot hold it.</summary>
    /// <param name="row">The row.</param>
    /// <param name="field">The field it patches, as the working copy declares it.</param>
    /// <param name="value">The literal; <see langword="null"/> for empty.</param>
    /// <param name="refusal">Why it cannot be written, when it cannot.</param>
    /// <returns><see langword="true"/> when it can be written.</returns>
    public static bool TryValue(MutateRow row, FieldSchema field, out JsonNode? value, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(field);
        if (row.Empty)
        {
            value = null;
            refusal = EmptyRefusal(field);
            return refusal is null;
        }

        (value, refusal) = Convert(row.Text, field);
        return refusal is null;
    }

    /// <summary>A declared literal as the row that writes it back.</summary>
    /// <param name="field">The field it patches.</param>
    /// <param name="value">The declared literal.</param>
    public static MutateRow Row(string field, JsonNode? value) => value switch
    {
        null => new MutateRow(field, MutateMode.Literal, string.Empty) { Empty = true },
        JsonValue scalar when scalar.GetValueKind() == JsonValueKind.String => new MutateRow(field, MutateMode.Literal, scalar.GetValue<string>()),
        _ => new MutateRow(field, MutateMode.Literal, value.ToJsonString()),
    };

    /// <summary>What a literal box for this field takes, in one sentence.</summary>
    /// <param name="field">The field.</param>
    public static string Hint(FieldSchema field) => CelFieldType.Of(field.Type) switch
    {
        CelValueType.String => "Text, written as typed.",
        CelValueType.Int => "A whole number such as 3 or -2.",
        CelValueType.Decimal => "A number such as 12.5.",
        CelValueType.Bool => "true or false.",
        CelValueType.Timestamp => "A date and time such as 2026-10-05T12:00:00Z.",
        CelValueType.Uuid => "An id such as 3f2c1a9e-6b7d-4c8e-9f10-2a3b4c5d6e7f.",
        _ => "A json field takes no literal value here.",
    };

    private static string? EmptyRefusal(FieldSchema field) => field.Required
        ? $"'{field.Name}' is required, so a hook cannot set it to empty: every write it fires on would carry a null the engine refuses."
        : null;

    private static (JsonNode? Value, string? Refusal) Convert(string text, FieldSchema field) => CelFieldType.Of(field.Type) switch
    {
        CelValueType.String => Text(text, field),
        CelValueType.Int => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)
            ? (JsonValue.Create(whole), null)
            : (null, Shape(field, "a whole number such as 3 or -2")),
        CelValueType.Decimal => decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
            ? (JsonValue.Create(number), null)
            : (null, Shape(field, "a number such as 12.5")),
        CelValueType.Bool => text is "true" or "false" ? (JsonValue.Create(text == "true"), null) : (null, Shape(field, "true or false")),
        CelValueType.Timestamp => Reads(text, element => element.TryGetDateTimeOffset(out _))
            ? (JsonValue.Create(text), null)
            : (null, Shape(field, "a date and time such as 2026-10-05T12:00:00Z")),
        CelValueType.Uuid => Reads(text, element => element.TryGetGuid(out _))
            ? (JsonValue.Create(text), null)
            : (null, Shape(field, "an id such as 3f2c1a9e-6b7d-4c8e-9f10-2a3b4c5d6e7f")),
        _ => (null, $"'{field.Name}' is a json field, and this build converts no literal into one. Write an expression, or set it to empty."),
    };

    private static (JsonNode? Value, string? Refusal) Text(string text, FieldSchema field)
    {
        if (field.EnumValues is { Count: > 0 } values && !values.Contains(text, StringComparer.Ordinal))
        {
            return (null, $"'{text}' is not one of {field.Name}'s values: {string.Join(", ", values)}.");
        }

        return (JsonValue.Create(text), null);
    }

    /// <summary>Asks one <c>System.Text.Json</c> question of the JSON string the row would write — the apply's own question.</summary>
    private static bool Reads(string text, Func<JsonElement, bool> question)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(text));
        return question(document.RootElement);
    }

    private static string Shape(FieldSchema field, string takes)
        => $"'{field.Name}' is a {field.Type.ToString().ToLowerInvariant()} field: it takes {takes}.";
}
