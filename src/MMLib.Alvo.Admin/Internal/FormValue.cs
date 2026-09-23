using MMLib.Alvo.Schema;
using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// A field's value as the text a form control holds, and that text back as the value the data port takes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Invariant culture both ways (D-8).</b> The record form drew <c>1,0</c> and <c>21,6</c> beside an API
/// that says <c>1.0</c> and <c>21.6</c>. The server was not the cause: the text it sent was already
/// invariant. A <c>type="number"</c> input displays its value in the <em>browser's</em> locale, so a
/// Slovak Chromium showed a comma, and it dropped the declared scale because the value was never
/// formatted to it. A decimal is therefore a text input here, formatted and parsed invariant, at its
/// declared scale — <c>21.60</c>, as the grid shows it.
/// </para>
/// <para>
/// <b>Each value is sent as the type the port binds.</b> The HTTP API deserializes a body through
/// <see cref="FieldClrType"/>; the dashboard writes through the port directly, so it does the same, or a
/// decimal would reach the engine as the string <c>"21.60"</c>.
/// </para>
/// <para>
/// <b>An instant is shown and read in UTC</b>, because that is how it is stored and how the grid prints
/// it; a <c>datetime-local</c> control has no offset of its own to carry.
/// </para>
/// </remarks>
internal static class FormValue
{
    private const string DayFormat = "yyyy-MM-dd";

    private const string MinuteFormat = "yyyy-MM-dd'T'HH:mm";

    private static readonly string[] _instantFormats = [MinuteFormat, "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];

    private const NumberStyles DecimalStyle
        = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite
        | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    /// <summary>The text a control shows for <paramref name="value"/>; empty when there is none.</summary>
    public static string Format(FieldSchema field, object? value)
    {
        ArgumentNullException.ThrowIfNull(field);

        return value switch
        {
            null => string.Empty,
            JsonElement json => json.GetRawText(),
            _ => field.Type switch
            {
                FieldType.Decimal => Amount(value, field.Scale),
                FieldType.Boolean => Flag(value),
                FieldType.Date => Day(value),
                FieldType.DateTime => Instant(value),
                FieldType.Uuid or FieldType.Ref => RefLabels.IdOf(value)?.ToString() ?? Invariant(value),
                _ => Invariant(value),
            },
        };
    }

    /// <summary>A decimal at its declared scale, invariant; the value as text when it is not a number.</summary>
    public static string Amount(object value, int? scale)
    {
        ArgumentNullException.ThrowIfNull(value);

        var number = value switch
        {
            decimal exact => exact,
            double or float or int or long or short or byte => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            string text when decimal.TryParse(text, DecimalStyle, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => (decimal?)null,
        };

        return number switch
        {
            null => Invariant(value),
            { } known when scale is { } places and >= 0 and <= 28 => known.ToString($"F{places}", CultureInfo.InvariantCulture),
            { } known => known.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Reads <paramref name="text"/> as <paramref name="field"/>'s type.</summary>
    /// <returns>
    /// The value, <see langword="null"/> for an empty control, or a sentence saying what the text should
    /// look like when it cannot be read.
    /// </returns>
    public static FormParse Parse(FieldSchema field, string? text)
    {
        ArgumentNullException.ThrowIfNull(field);

        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return FormParse.Empty;
        }

        return field.Type switch
        {
            FieldType.Decimal => ParseDecimal(trimmed),
            FieldType.Integer => long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)
                ? FormParse.Of(whole)
                : FormParse.Refused("A whole number, such as 3."),
            FieldType.Boolean => bool.TryParse(trimmed, out var flag) ? FormParse.Of(flag) : FormParse.Refused("true or false."),
            FieldType.Uuid or FieldType.Ref => Guid.TryParse(trimmed, out var id)
                ? FormParse.Of(id)
                : FormParse.Refused("A full id, such as 0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b."),
            FieldType.Date => DateOnly.TryParseExact(trimmed, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? FormParse.Of(day)
                : FormParse.Refused("A day, written 2026-09-24."),
            FieldType.DateTime => ParseInstant(trimmed),
            _ => FormParse.Of(text),
        };
    }

    private static FormParse ParseDecimal(string text)
        => decimal.TryParse(text, DecimalStyle, CultureInfo.InvariantCulture, out var number)
            ? FormParse.Of(number)
            : FormParse.Refused("A number with a point before the decimals, such as 21.60.");

    private static FormParse ParseInstant(string text)
        => DateTime.TryParseExact(
            text, _instantFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment)
            ? FormParse.Of(new DateTimeOffset(moment, TimeSpan.Zero))
            : FormParse.Refused("A day and a time in UTC, written 2026-09-24T14:30.");

    private static string Flag(object value) => value switch
    {
        bool flag => flag ? "true" : "false",
        _ => Invariant(value),
    };

    private static string Day(object value) => value switch
    {
        DateOnly day => day.ToString(DayFormat, CultureInfo.InvariantCulture),
        DateTime moment => moment.ToString(DayFormat, CultureInfo.InvariantCulture),
        DateTimeOffset moment => moment.ToString(DayFormat, CultureInfo.InvariantCulture),
        _ => Invariant(value),
    };

    private static string Instant(object value) => value switch
    {
        DateTimeOffset moment => moment.ToUniversalTime().ToString(MinuteFormat, CultureInfo.InvariantCulture),
        DateTime moment => Utc(moment).ToString(MinuteFormat, CultureInfo.InvariantCulture),
        _ => Invariant(value),
    };

    /// <summary>A <see cref="DateTime"/> that says what zone it is in, in UTC; one that does not, as it is.</summary>
    private static DateTime Utc(DateTime moment)
        => moment.Kind == DateTimeKind.Unspecified ? moment : moment.ToUniversalTime();

    private static string Invariant(object value)
        => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>What one control's text reads as.</summary>
/// <param name="Value">The value to send; <see langword="null"/> for an empty control or a refusal.</param>
/// <param name="Problem">What the text should look like, when it could not be read.</param>
internal sealed record FormParse(object? Value, string? Problem)
{
    /// <summary>An empty control: the field is cleared.</summary>
    public static FormParse Empty { get; } = new(null, null);

    /// <summary>A value that was read.</summary>
    public static FormParse Of(object? value) => new(value, null);

    /// <summary>Text that could not be read, and what it should look like.</summary>
    public static FormParse Refused(string problem) => new(null, problem);
}
