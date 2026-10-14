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
    internal static AfterImages At(string point) => point switch
    {
        "afterCreate" => new(Pre: false, Post: true),
        "afterDelete" => new(Pre: true, Post: false),
        _ => new(Pre: true, Post: true),
    };
}

/// <summary>
/// <b>#325</b>: refuses a post-commit condition that reads a row image its event does not carry. Such a reference reads
/// null, every comparison against it collapses to false — <c>!=</c> included — and a <c>changed()</c> on a delete is
/// true for every field that held a value, so a filter meant to narrow which events reach a webhook passes them all.
/// </summary>
/// <remarks>
/// One rule for every post-commit condition: <c>AfterHookCompiler</c> now, the extension binding compiler (HX-04) next.
/// <c>BeforeHookCompiler.HonoursThePhase</c> stays separate because a before-delete's bare field reads the row being
/// removed, while an after-delete's reads the event's absent post-image.
/// </remarks>
internal static class AfterImagePhase
{
    /// <summary>The refusals for every image <paramref name="root"/> reads that <paramref name="images"/> lacks.</summary>
    /// <param name="root">The compiled condition's tree.</param>
    /// <param name="images">The images the event behind <paramref name="point"/> carries.</param>
    /// <param name="point">The point's name, quoted in each refusal.</param>
    /// <param name="path">The condition's JSON pointer, where each refusal is reported.</param>
    internal static IReadOnlyList<DescriptorValidationError> Refusals(
        CelNode root, AfterImages images, string point, string path) =>
        [.. Unanswerable(root, images, point).Select(refusal =>
            new DescriptorValidationError(path, refusal.Message, refusal.Fix, DescriptorValidationSeverity.Error))];

    private static IEnumerable<(string Message, string Fix)> Unanswerable(CelNode root, AfterImages images, string point)
    {
        if (!images.Pre && Reads(root, CelRecordState.Old))
        {
            yield return NoPreImage(point);
        }

        if (!images.Post && (Reads(root, CelRecordState.New) || Reads(root, CelRecordState.Current)))
        {
            yield return NoPostImage(point);
        }

        if (!(images.Pre && images.Post) && ReadsChanged(root))
        {
            yield return NoComparison(point);
        }
    }

    private static (string Message, string Fix) NoPreImage(string point) => (
        $"This '{point}' condition reads 'old.<field>', which its event cannot answer: a create has no row before it, so "
        + "the reference reads null and every comparison against it — including '!=' — collapses to false, silently and "
        + "not always in the denying direction.",
        "Read 'new.<field>', which after a create is the row that was written.");

    private static (string Message, string Fix) NoPostImage(string point) => (
        $"This '{point}' condition reads 'new.<field>' or a bare field name, which its event cannot answer: a delete "
        + "leaves no row, so the reference reads null and every comparison against it — including '!=' — collapses to "
        + "false, silently and not always in the denying direction.",
        "Read 'old.<field>', which after a delete is the row that was removed.");

    private static (string Message, string Fix) NoComparison(string point) => (
        $"This '{point}' condition calls 'changed(<field>)', which compares the row before the write with the row after "
        + "it. This event carries only one of the two, so after a delete every field that held a value reads as changed.",
        "Compare the image this point has — 'new.<field>' after a create, 'old.<field>' after a delete — or move the "
        + "hook to 'afterUpdate', where a change exists.");

    private static bool Reads(CelNode node, CelRecordState state) =>
        (node is CelFieldRef reference && reference.State == state)
        || CelTree.Children(node).Any(child => Reads(child, state));

    private static bool ReadsChanged(CelNode node) =>
        node is CelChanged || CelTree.Children(node).Any(ReadsChanged);
}
