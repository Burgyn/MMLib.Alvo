using MMLib.Alvo.Schema;
using System.Globalization;

namespace MMLib.Alvo.Admin.Components.Data;

/// <summary>How a grid cell is drawn.</summary>
internal enum CellKind
{
    /// <summary>No value: an em dash.</summary>
    Empty,

    /// <summary>Plain text.</summary>
    Text,

    /// <summary>An amount or a count: tabular and right-aligned.</summary>
    Number,

    /// <summary>One value of an enum: a neutral badge.</summary>
    Value,

    /// <summary>A boolean: a check or a dash.</summary>
    Flag,

    /// <summary>A date or an instant.</summary>
    Moment,

    /// <summary>A uuid: its first characters, the whole of it in the title.</summary>
    Identifier,
}

/// <summary>
/// One value of one field, formatted for the Data grid and its phone cards.
/// </summary>
/// <remarks>
/// <para>
/// <b>Invariant culture, always.</b> The API and every other screen are invariant, and a number shown
/// any other way reads <c>21,6</c> beside a request body that says <c>21.6</c> (D-8; the form's half
/// of it, and where the comma really came from, is in <see cref="FormValue"/>). A decimal also keeps
/// its declared scale, so <c>17.00</c> lines up under <c>12.90</c> in a <c>decimal(…,2)</c> column
/// instead of reading as a different kind of number.
/// </para>
/// <para>
/// <b>Short values do not wrap and long ones are clipped.</b> An identifier such as
/// <c>SO-2026-0163</c> broken over two lines is two things nobody typed; a paragraph in a cell
/// pushes the row to the height of the viewport. The line between them is
/// <see cref="ShortLength"/>, and a clipped value keeps its whole text in <see cref="Title"/>.
/// </para>
/// </remarks>
/// <param name="Kind">How the value is drawn.</param>
/// <param name="Text">What the cell shows.</param>
/// <param name="Title">The full value or its meaning, for the tooltip, when the text is not all of it.</param>
internal sealed record GridCell(CellKind Kind, string Text, string? Title = null)
{
    /// <summary>The longest text that is kept on one line whole rather than clipped.</summary>
    public const int ShortLength = 24;

    private const string Dash = "—";

    private static readonly GridCell _empty = new(CellKind.Empty, Dash);

    /// <summary>Whether the value is short enough to sit on one line unclipped.</summary>
    public bool Short => Kind != CellKind.Text || Text.Length <= ShortLength;

    /// <summary>Formats <paramref name="value"/> as <paramref name="field"/> declares it.</summary>
    public static GridCell Of(FieldSchema field, object? value)
    {
        ArgumentNullException.ThrowIfNull(field);

        return value switch
        {
            null => _empty,
            string { Length: 0 } => _empty,
            _ => field.Type switch
            {
                FieldType.Enum => new GridCell(CellKind.Value, Invariant(value)),
                FieldType.Boolean => Flag(value),
                FieldType.Decimal => new GridCell(CellKind.Number, FormValue.Amount(value, field.Scale)),
                FieldType.Integer => new GridCell(CellKind.Number, Invariant(value)),
                FieldType.Date or FieldType.DateTime => new GridCell(CellKind.Moment, Moment(value)),
                FieldType.Uuid or FieldType.Ref => Identifier(value),
                _ => Plain(Invariant(value)),
            },
        };
    }

    /// <summary>A uuid as its short form, or the value as text when it is not one.</summary>
    public static GridCell Identifier(object value)
        => RefLabels.IdOf(value) is { } id
            ? new GridCell(CellKind.Identifier, RefLabels.ShortId(id), id.ToString())
            : Plain(Invariant(value));

    /// <summary>Text, with its whole value as the title when it is long enough to be clipped.</summary>
    public static GridCell Plain(string text)
        => text.Length > ShortLength ? new GridCell(CellKind.Text, text, text) : new GridCell(CellKind.Text, text);

    private static GridCell Flag(object value)
        => value is true || (value is string text && bool.TryParse(text, out var parsed) && parsed)
            ? new GridCell(CellKind.Flag, "✓", "yes")
            : new GridCell(CellKind.Flag, Dash, "no");

    private static string Moment(object value) => value switch
    {
        DateTimeOffset moment => moment.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateTime moment => moment.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateOnly day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => Invariant(value),
    };

    private static string Invariant(object value)
        => Convert.ToString(value, CultureInfo.InvariantCulture) ?? Dash;
}
