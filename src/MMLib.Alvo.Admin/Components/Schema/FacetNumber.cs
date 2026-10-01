using System.Globalization;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// Reads a field editor's number box — max length, precision, scale — as the text the operator typed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read here, because the browser's reading lost the text.</b> A <c>type=number</c> box reports text it cannot
/// parse (<c>1e</c>, <c>--3</c>) as an empty value, and its <c>min</c> is only a constraint the form's
/// <c>novalidate</c> no longer checks. So "no limit" was staged for <c>1e</c> and the defaults 10 and 2 for an
/// unreadable precision or scale, with nothing on screen saying so.
/// </para>
/// <para>
/// <b>The bounds are the frozen schema's</b> (<c>$defs/field</c>: <c>maxLength</c> ≥ 1, <c>precision</c> 1–38,
/// <c>scale</c> ≥ 0), restated so the refusal lands at the box the operator is looking at rather than at the plan.
/// The schema stays the authority; the apply refuses the same values.
/// </para>
/// </remarks>
internal static class FacetNumber
{
    /// <summary>A max length: empty is no limit, otherwise a whole number of 1 or more.</summary>
    /// <param name="text">What the box holds.</param>
    public static FacetRead MaxLength(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? new FacetRead(null, null)
            : Whole(text, 1, null, "A whole number of 1 or more, or empty for no limit.");

    /// <summary>A decimal's precision: a whole number from 1 to 38.</summary>
    /// <param name="text">What the box holds.</param>
    public static FacetRead Precision(string? text) => Whole(text, 1, 38, "A whole number from 1 to 38.");

    /// <summary>A decimal's scale: a whole number of 0 or more.</summary>
    /// <param name="text">What the box holds.</param>
    public static FacetRead Scale(string? text) => Whole(text, 0, null, "A whole number of 0 or more.");

    private static FacetRead Whole(string? text, int minimum, int? maximum, string problem)
        => int.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            && number >= minimum && (maximum is null || number <= maximum)
            ? new FacetRead(number, null)
            : new FacetRead(null, problem);
}

/// <summary>What a number box reads as.</summary>
/// <param name="Value">The number, or <see langword="null"/> for an empty optional box or a refusal.</param>
/// <param name="Problem">What the box should hold instead, when it could not be read.</param>
internal readonly record struct FacetRead(int? Value, string? Problem);
