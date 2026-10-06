using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>One function as the hook editor offers it: every overload's signature, its summary, and who wrote it.</summary>
/// <param name="Name">The function's CEL spelling, e.g. <c>math.round</c>.</param>
/// <param name="Signatures">Every overload, as the core's refusals quote it.</param>
/// <param name="Summary">The one-sentence summary — host-authored text for a host function, rendered as text.</param>
/// <param name="IsHost">Whether the embedding host registered it.</param>
/// <param name="Parameters">The parameters of the overload with the most, which the insert template spells.</param>
internal sealed record OfferedFunction(
    string Name, IReadOnlyList<string> Signatures, string Summary, bool IsHost, IReadOnlyList<CelFunctionParameter> Parameters);

/// <summary>A text to write and the range in it to select next.</summary>
/// <param name="Text">The text.</param>
/// <param name="SelectFrom">Where the selection starts, in UTF-16 code units (the browser's unit too).</param>
/// <param name="SelectLength">How long it is; 0 puts the caret at <paramref name="SelectFrom"/>.</param>
internal sealed record Insertion(string Text, int SelectFrom, int SelectLength);

/// <summary>
/// What the hook editor offers under a CEL box and what Insert writes (spec §9). It evaluates nothing: it writes text
/// the live check and apply judge.
/// </summary>
internal static class FunctionOffer
{
    /// <summary>The functions a profile admits, one per name, in name order (ordinal, as the catalog lists them).</summary>
    /// <param name="functions">Every overload, as <c>cel/functions</c> answers.</param>
    /// <param name="profile">The box's profile: <see cref="CelProfile.Mutate"/> or <see cref="CelProfile.Condition"/>.</param>
    /// <returns>The offered functions.</returns>
    public static IReadOnlyList<OfferedFunction> For(IReadOnlyList<CelFunctionInfo> functions, CelProfile profile) =>
        [.. functions
            .Where(function => function.Profiles.Contains(profile))
            .GroupBy(function => function.Name, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(Offer)];

    /// <summary>
    /// An overload as one line — <c>name(p: T, …) -> R</c>, <c>?</c> where null is accepted or may come back. The same text
    /// the core's <c>CelFunction.Signature()</c> writes (Host.Tests <c>FunctionOfferAgreementTests</c> pins it).
    /// </summary>
    /// <param name="function">One overload.</param>
    /// <returns>Its signature.</returns>
    public static string Signature(CelFunctionInfo function) =>
        $"{function.Name}({string.Join(", ", function.Parameters.Select(Parameter))}) -> {function.Result}{Nullable(function.ResultMayBeNull)}";

    /// <summary>
    /// The call Insert writes: the parameter names as placeholders, the first one selected — or, with
    /// <paramref name="firstArgument"/>, that in the first place and the next placeholder selected (spec §9.2).
    /// </summary>
    /// <param name="function">The function.</param>
    /// <param name="firstArgument">A ready first argument (<c>new.field</c>), or <see langword="null"/>.</param>
    /// <returns>The template and its selection.</returns>
    public static Insertion Template(OfferedFunction function, string? firstArgument)
    {
        var arguments = function.Parameters
            .Select((parameter, index) => index == 0 && firstArgument is not null ? firstArgument : parameter.Name)
            .ToList();
        var text = $"{function.Name}({string.Join(", ", arguments)})";
        var placeholder = firstArgument is null ? 0 : 1;
        if (placeholder >= arguments.Count)
        {
            return new Insertion(text, text.Length, 0);
        }

        var from = function.Name.Length + 1 + arguments.Take(placeholder).Sum(argument => argument.Length + 2);
        return new Insertion(text, from, arguments[placeholder].Length);
    }

    /// <summary>
    /// <c>new.{field}</c> when a mutate row's field can be the function's first argument: the point has a <c>new.</c>
    /// image and the first parameter takes the field's CEL type (an Int also fills a Decimal, C1 F7).
    /// </summary>
    /// <param name="function">The function.</param>
    /// <param name="point">The hook's point.</param>
    /// <param name="field">The row's field, or <see langword="null"/> when none is chosen.</param>
    /// <returns>The argument, or <see langword="null"/>.</returns>
    public static string? FirstArgument(OfferedFunction function, string point, FieldSchema? field) =>
        field is not null && ConditionTable.HasNew(point) && function.Parameters is [var first, ..] && Takes(first.Type, CelFieldType.Of(field))
            ? $"new.{field.Name}"
            : null;

    /// <summary>Writes <paramref name="template"/> over the selection <c>[start, end)</c> of <paramref name="text"/>.</summary>
    /// <param name="text">The box's text.</param>
    /// <param name="start">The selection's start (clamped into the text).</param>
    /// <param name="end">Its end (clamped to at least <paramref name="start"/>).</param>
    /// <param name="template">What to write.</param>
    /// <returns>The new text and the range to select in it.</returns>
    public static Insertion Insert(string text, int start, int end, Insertion template)
    {
        var from = Math.Clamp(start, 0, text.Length);
        var to = Math.Clamp(end, from, text.Length);
        var written = string.Concat(text.AsSpan(0, from), template.Text, text.AsSpan(to));
        return new Insertion(written, from + template.SelectFrom, template.SelectLength);
    }

    private static OfferedFunction Offer(IGrouping<string, CelFunctionInfo> overloads)
    {
        var first = overloads.First();
        var longest = overloads.MaxBy(overload => overload.Parameters.Count)!;
        return new OfferedFunction(
            overloads.Key, [.. overloads.Select(Signature)], first.Summary, first.Provenance == CelFunctionProvenance.Host, longest.Parameters);
    }

    private static string Parameter(CelFunctionParameter parameter) => $"{parameter.Name}: {parameter.Type}{Nullable(parameter.AcceptsNull)}";

    private static string Nullable(bool nullable) => nullable ? "?" : string.Empty;

    private static bool Takes(CelValueType parameter, CelValueType argument) =>
        parameter == argument || (parameter == CelValueType.Decimal && argument == CelValueType.Int);
}
