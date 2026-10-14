using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Events.Internal;

/// <summary>Which row images the event behind an after-point carries.</summary>
/// <param name="Pre">Whether <c>old.</c> has a row to read.</param>
/// <param name="Post">Whether <c>new.</c> and a bare field have a row to read.</param>
internal readonly record struct AfterImages(bool Pre, bool Post)
{
    /// <summary>The images of a descriptor after-point: a create has no row before it, a delete none after it.</summary>
    /// <param name="point">The descriptor's after-point name, such as <c>afterCreate</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="point"/> is not an after-point.</exception>
    internal static AfterImages At(string point) => point switch
    {
        "afterCreate" => new(Pre: false, Post: true),
        "afterUpdate" => new(Pre: true, Post: true),
        "afterDelete" => new(Pre: true, Post: false),
        _ => throw new ArgumentOutOfRangeException(
            nameof(point), point, "Not an after-point: only afterCreate, afterUpdate and afterDelete carry event images."),
    };

    /// <summary>Whether the event carries the row a reference in <paramref name="state"/> reads.</summary>
    /// <param name="state"><see cref="CelRecordState.Old"/> reads the pre-image; any other state the post-image.</param>
    internal bool Carries(CelRecordState state) => state == CelRecordState.Old ? Pre : Post;
}

/// <summary>
/// <b>#325</b>: refuses a post-commit condition or template that reads a row image its event does not carry. Such a
/// reference reads null, every comparison against it collapses to false — <c>!=</c> included — and a
/// <c>changed()</c> on a create or delete is true for every field that held a value, so a filter meant to narrow which
/// events reach a webhook passes them all; a template placeholder renders empty on every delivery.
/// </summary>
/// <remarks>
/// <para>
/// One rule for every post-commit condition and template: <c>AfterHookCompiler</c> now, the extension binding
/// compiler (HX-04) next. <c>BeforeHookCompiler.HonoursThePhase</c> stays separate because a before-delete's bare
/// field reads the row being removed, while an after-delete's reads the event's absent post-image.
/// </para>
/// <para>
/// Every wording is derived from the point and its <see cref="AfterImages"/>, so no message names an operation the
/// point is not about, and every offending reference is reported on its own.
/// </para>
/// </remarks>
internal static class AfterImagePhase
{
    /// <summary>The refusals for every reference <paramref name="root"/> makes to an image <paramref name="images"/> lacks.</summary>
    /// <param name="root">The compiled condition's tree.</param>
    /// <param name="images">The images the event behind <paramref name="point"/> carries.</param>
    /// <param name="point">The point's name, quoted in each refusal.</param>
    /// <param name="path">The condition's JSON pointer, where each refusal is reported.</param>
    /// <returns>One refusal per distinct offending reference and per distinct <c>changed()</c> call, in source order.</returns>
    internal static IReadOnlyList<DescriptorValidationError> Refusals(
        CelNode root, AfterImages images, string point, string path) =>
        [.. MissingImageReads(root, images, point, path), .. UnmeasurableChanges(root, images, point, path)];

    /// <summary>The refusals for every placeholder in <paramref name="template"/> that reads an image <paramref name="images"/> lacks.</summary>
    /// <param name="template">The parsed template.</param>
    /// <param name="images">The images the event behind <paramref name="point"/> carries.</param>
    /// <param name="point">The point's name, quoted in each refusal.</param>
    /// <param name="path">The template slot's JSON pointer, where each refusal is reported.</param>
    /// <returns>One refusal per distinct offending placeholder, in source order.</returns>
    internal static IReadOnlyList<DescriptorValidationError> Refusals(
        AlvoTemplate template, AfterImages images, string point, string path) =>
        [
            .. template.Placeholders
                .Select(TemplatePlaceholder.RowImageRead)
                .OfType<(CelRecordState State, string Field)>()
                .Where(read => !images.Carries(read.State))
                .Distinct()
                .Select(read => Error(
                    path,
                    MissingPlaceholderImage(read.State, read.Field, point),
                    PlaceholderInstead(read.Field, read.State, images, point))),
        ];

    private static IEnumerable<DescriptorValidationError> MissingImageReads(
        CelNode root, AfterImages images, string point, string path) =>
        Nodes(root).OfType<CelFieldRef>()
            .Where(reference => !images.Carries(reference.State))
            .DistinctBy(reference => (reference.State, reference.FieldName))
            .Select(reference => Error(
                path, MissingImage(reference, point), ReadInstead(reference.FieldName, reference.State, images, point)));

    private static IEnumerable<DescriptorValidationError> UnmeasurableChanges(
        CelNode root, AfterImages images, string point, string path) =>
        images is { Pre: true, Post: true }
            ? []
            : Nodes(root).OfType<CelChanged>()
                .DistinctBy(call => call.FieldName, StringComparer.Ordinal)
                .Select(call => Error(path, NoComparison(call.FieldName, images, point), CompareInstead(images, point)));

    private static string MissingImage(CelFieldRef reference, string point) =>
        $"This '{point}' condition reads '{Spelling(reference)}' ({Kind(reference.State)}), which its event cannot "
        + $"answer: the event behind '{point}' carries no row from {Side(reference.State)} the write, so the "
        + "reference reads null and every comparison against it — including '!=' — collapses to false, silently and "
        + "not always in the denying direction.";

    private static string MissingPlaceholderImage(CelRecordState state, string field, string point) =>
        $"This template reads '{Placeholder(state, field)}', which its event cannot answer: the event behind "
        + $"'{point}' carries no row from {Side(state)} the write, so the placeholder renders empty on every delivery.";

    private static string NoComparison(string field, AfterImages images, string point) =>
        $"This '{point}' condition calls 'changed({field})' (a 'changed(<field>)' call), which compares the row "
        + $"before the write with the row after it. The event behind '{point}' carries {Carried(images)}, so every "
        + "field that held a value reads as changed.";

    private static string ReadInstead(string field, CelRecordState missing, AfterImages images, string point) =>
        images.Carries(Other(missing))
            ? $"Read '{Root(Other(missing))}.{field}', the row from {Side(Other(missing))} the write, which the event "
                + $"behind '{point}' carries."
            : $"Remove the reference: the event behind '{point}' carries no row to read it from.";

    private static string PlaceholderInstead(string field, CelRecordState missing, AfterImages images, string point) =>
        images.Carries(Other(missing))
            ? $"Use '{Placeholder(Other(missing), field)}', the row from {Side(Other(missing))} the write, which the "
                + $"event behind '{point}' carries."
            : $"Remove the placeholder: the event behind '{point}' carries no row to render it from.";

    private static string CompareInstead(AfterImages images, string point) =>
        $"Compare the image '{point}' carries — {CarriedReads(images)} — or move the hook to 'afterUpdate', where "
        + "both rows exist and a change can be measured.";

    private static string Carried(AfterImages images) => images switch
    {
        { Pre: true, Post: false } => "only the row from before the write",
        { Pre: false, Post: true } => "only the row from after the write",
        _ => "neither row",
    };

    private static string CarriedReads(AfterImages images) => images switch
    {
        { Pre: true, Post: false } => "'old.<field>'",
        { Pre: false, Post: true } => "'new.<field>'",
        _ => "none",
    };

    private static CelRecordState Other(CelRecordState state) =>
        state == CelRecordState.Old ? CelRecordState.New : CelRecordState.Old;

    private static string Side(CelRecordState state) => state == CelRecordState.Old ? "before" : "after";

    private static string Root(CelRecordState state) => state == CelRecordState.Old ? "old" : "new";

    private static string Kind(CelRecordState state) => state switch
    {
        CelRecordState.Old => "an 'old.<field>' reference",
        CelRecordState.New => "a 'new.<field>' reference",
        _ => "a bare field, which reads as 'new.<field>' does",
    };

    private static string Spelling(CelFieldRef reference) =>
        reference.State == CelRecordState.Current
            ? reference.FieldName
            : $"{Root(reference.State)}.{reference.FieldName}";

    private static string Placeholder(CelRecordState state, string field) =>
        $"{AlvoTemplate.PlaceholderOpen}{Root(state)}.{field}{AlvoTemplate.PlaceholderClose}";

    private static IEnumerable<CelNode> Nodes(CelNode node) =>
        CelTree.Children(node).SelectMany(Nodes).Prepend(node);

    private static DescriptorValidationError Error(string path, string message, string fix) =>
        new(path, message, fix, DescriptorValidationSeverity.Error);
}
