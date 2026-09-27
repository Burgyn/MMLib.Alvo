using MMLib.Alvo.Schema;
using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>
/// The facets a field actually carries, as the Fields tab badges them — every one read off <see cref="FieldSchema"/>.
/// </summary>
/// <remarks>
/// Out of <c>Fields.razor</c> so it can be tested (the F-13 rule), and so an applied row and a staged row are badged
/// by one function: a staged row losing its <c>rollup</c> or <c>indexed</c> badge was the renderer's defect, not
/// the model's (docs/todo-admin.md §8d item 22).
/// </remarks>
internal static class FieldBadges
{
    private const int LiteralBadgeLimit = 24;

    /// <summary>The badges, ordered so the ones that change what a caller may send come first.</summary>
    /// <param name="field">The field, applied or staged.</param>
    /// <returns>Its badges, in the order the row draws them.</returns>
    /// <remarks>
    /// A reader scanning the column is looking for "what will this reject", not for the storage detail.
    /// </remarks>
    public static IEnumerable<string> Of(FieldSchema field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return [.. Constraints(field), .. Storage(field), .. Maintenance(field)];
    }

    /// <summary>What a caller may send: required, an explicit nullability, the default, unique, the format.</summary>
    private static IEnumerable<string> Constraints(FieldSchema field)
    {
        if (field.Required)
        {
            yield return "required";
        }

        /* Only a nullability that differs from the one `required` implies — anything else would badge every row. */
        if (field.Required && field.Nullable)
        {
            yield return "nullable";
        }

        if (!field.Required && !field.Nullable)
        {
            yield return "not null";
        }

        if (field.Default is { } literal)
        {
            yield return $"default {Literal(literal)}";
        }

        if (field.Unique)
        {
            yield return "unique";
        }

        if (field.Format is { Length: > 0 } format)
        {
            yield return format;
        }
    }

    /// <summary>How the column is stored: its length, precision, values and what a deleted target does.</summary>
    private static IEnumerable<string> Storage(FieldSchema field)
    {
        if (field.MaxLength is { } max)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"max {max}");
        }

        if (field.Precision is { } precision)
        {
            yield return field.Scale is { } scale
                ? string.Create(CultureInfo.InvariantCulture, $"{precision},{scale}")
                : string.Create(CultureInfo.InvariantCulture, $"precision {precision}");
        }

        if (field.EnumValues is { Count: > 0 } values)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"{values.Count} values");
        }

        if (field.Reference is { } reference)
        {
            yield return $"on delete {reference.OnDelete.ToString().ToLowerInvariant()}";
        }
    }

    /// <summary>What Alvo maintains for it: a computed value, a rollup, an index.</summary>
    private static IEnumerable<string> Maintenance(FieldSchema field)
    {
        if (field.ComputedExpression is { Length: > 0 })
        {
            yield return "computed";
        }

        if (field.Rollup is not null)
        {
            yield return "rollup";
        }

        if (field.Indexed)
        {
            yield return "indexed";
        }
    }

    /// <summary>
    /// The declared default as the descriptor holds it, short enough to sit in a badge.
    /// </summary>
    /// <remarks>
    /// The raw JSON rather than the .NET rendering, so a string default reads as <c>"unnamed"</c> and a
    /// number as <c>0</c> — the badge is telling the reader what is in the descriptor, and a quoted string
    /// that lost its quotes is a different declaration.
    /// </remarks>
    private static string Literal(JsonElement literal)
    {
        var text = literal.GetRawText();
        return text.Length <= LiteralBadgeLimit ? text : text[..LiteralBadgeLimit] + "…";
    }
}
