using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// What a compiled tree reads beyond the record's own current fields — a row image, a comparison of two, or the caller —
/// asked by every compiler that must refuse a reference its phase cannot answer.
/// </summary>
internal static class CelImageReads
{
    /// <summary>Whether <paramref name="node"/> reads a field of the <paramref name="state"/> image.</summary>
    /// <param name="node">The tree to walk.</param>
    /// <param name="state">The image — <c>old.</c> or <c>new.</c>.</param>
    internal static bool Reads(CelNode node, CelRecordState state) =>
        (node is CelFieldRef fieldRef && fieldRef.State == state)
        || CelTree.Children(node).Any(child => Reads(child, state));

    /// <summary>Whether <paramref name="node"/> calls <c>changed(field)</c>.</summary>
    /// <param name="node">The tree to walk.</param>
    internal static bool ReadsChanged(CelNode node) =>
        node is CelChanged || CelTree.Children(node).Any(ReadsChanged);

    /// <summary>Whether <paramref name="node"/> reads <c>@user</c> or <c>@tenant</c>.</summary>
    /// <param name="node">The tree to walk.</param>
    internal static bool ReadsContext(CelNode node) =>
        node is CelContextRef || CelTree.Children(node).Any(ReadsContext);
}
