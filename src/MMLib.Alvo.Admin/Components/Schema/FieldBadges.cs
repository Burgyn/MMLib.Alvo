using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Management;
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

    /// <summary>The two <c>boolOrCel</c> policies a field declares, in the order they are badged.</summary>
    private static readonly string[] _policies = ["hidden", "readOnly"];

    /// <summary>The badges, ordered so the ones that change what a caller may send come first.</summary>
    /// <param name="field">The field, applied or staged.</param>
    /// <returns>Its badges, in the order the row draws them.</returns>
    /// <remarks>
    /// A reader scanning the column is looking for "what will this reject", not for the storage detail.
    /// </remarks>
    public static IEnumerable<string> Of(FieldSchema field) => Of(field, declared: null);

    /// <summary>The badges, with the policy only the declaration carries.</summary>
    /// <param name="field">The field, applied or staged.</param>
    /// <param name="declared">Its declaration in the descriptor, when the screen has it.</param>
    /// <returns>Its badges, in the order the row draws them.</returns>
    /// <remarks>
    /// <c>hidden</c> and <c>readOnly</c> are policy the resolved <see cref="FieldSchema"/> does not carry (#267), so
    /// they are read off the declaration — the same reading <c>DescriptorLens.Masks</c>/<c>Locks</c> give the Data
    /// screen. They follow the constraints: they change what a caller may send and see, not how a column is stored.
    /// </remarks>
    public static IEnumerable<string> Of(FieldSchema field, JsonElement? declared)
    {
        ArgumentNullException.ThrowIfNull(field);
        return [.. Constraints(field), .. Policy(declared), .. Storage(field), .. Maintenance(field)];
    }

    /// <summary>
    /// The build's refusals this declaration carries, in the order the build published them.
    /// </summary>
    /// <param name="declared">The field's declaration, when the screen has it.</param>
    /// <param name="refused">Every refusal the build publishes, as <c>ManagementCapabilities.Refused</c> reports it.</param>
    /// <returns>The refusals the row names; empty for a field the apply accepts.</returns>
    /// <remarks>
    /// <para>
    /// <b>Only a staged field can carry one</b>: the apply refuses each, so an applied field never does. Without this a
    /// staged field with <c>validation</c> looked ordinary until it was opened (#269).
    /// </para>
    /// <para>
    /// <b>The build decides which slots exist</b> (<see cref="RefusalPlaces"/>, <see cref="RefusalScreen.FieldsList"/>);
    /// this only says how each is seen in a declaration, in the form <c>UnhonouredFeatures</c> refuses it: any
    /// <c>validation</c>, a <c>$cel</c> default (<c>ValueOrExpr.IsTaggedExpression</c>), a <c>rollup.where</c>
    /// (<c>RollupResolver.EnsureNoFilter</c>). A slot the build stops publishing stops being badged.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ManagementRefusedFeature> Refused(
        JsonElement? declared, IReadOnlyList<ManagementRefusedFeature> refused)
        => declared is { ValueKind: JsonValueKind.Object } field
            ? [.. RefusalPlaces.On(RefusalScreen.FieldsList, refused)
                .Where(refusal => _carries.TryGetValue(refusal.Slot, out var carries) && carries(field))]
            : [];

    /// <summary>What a row's refusal badge reads, for a slot <see cref="Refused"/> returned.</summary>
    /// <param name="slot">The build's slot.</param>
    public static string RefusedWord(string slot) => slot switch
    {
        "field.default" => "$cel default — refused at apply",
        "rollup.where" => "rollup where — refused at apply",
        _ => $"{slot.Replace("field.", string.Empty, StringComparison.Ordinal)} — refused at apply",
    };

    /// <summary>How each field slot is seen in a declaration.</summary>
    private static readonly Dictionary<string, Func<JsonElement, bool>> _carries = new(StringComparer.Ordinal)
    {
        ["field.validation"] = field => Present(field, "validation"),
        ["field.default"] = field => field.TryGetProperty("default", out var value) && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("$cel", out var cel) && cel.ValueKind == JsonValueKind.String,
        ["rollup.where"] = field => field.TryGetProperty("rollup", out var rollup) && rollup.ValueKind == JsonValueKind.Object
            && Present(rollup, "where"),
    };

    /// <summary>Whether the key is there with a value; <c>null</c> is the absence the mapper reads it as.</summary>
    private static bool Present(JsonElement owner, string key)
        => owner.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null;

    /// <summary><c>hidden</c>, then <c>readOnly</c>: <c>true</c> for every caller, a CEL string for some.</summary>
    private static IEnumerable<string> Policy(JsonElement? declared)
    {
        foreach (var key in _policies)
        {
            if (DescriptorLens.PolicyOf(declared, key) is { } form)
            {
                yield return form == JsonValueKind.True ? key : $"{key} (conditional)";
            }
        }
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
