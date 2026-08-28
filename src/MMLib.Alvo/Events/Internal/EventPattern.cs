namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// <b>The authority on the wildcard half of the frozen <c>$defs/eventPattern</c> grammar</b>: whether one
/// declared pattern subscribes to more than a single exact event name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the wildcard half lives here, and the split is along the two contracts.</b> The reserved
/// namespaces are part of the <em>wire</em> contract — which names Alvo emits, and therefore which a host may
/// not mint — so they live in <see cref="AlvoEventName"/> in <c>Abstractions</c>, where
/// <see cref="AlvoCustomEvent.Create"/> can enforce them for every caller. The wildcard is part of the
/// <em>descriptor</em> contract — what a rule may subscribe to — so it lives here, next to the apply path
/// that refuses it. An earlier draft kept both in this type and had to be split when the guard moved to the
/// only layer that could enforce it structurally.
/// </para>
/// <para>
/// <b>A segment is a wildcard only when it is <em>entirely</em> <c>*</c>.</b> The frozen grammar admits
/// <c>*</c> as a whole segment and nowhere else — <c>([a-z][a-z0-9_]*|\*)</c>, never a prefix or an infix —
/// so scanning for the character anywhere in the string would be a different question than the grammar asks,
/// and one that has no false-negative but a real false-positive the day a segment may contain one.
/// </para>
/// </remarks>
internal static class EventPattern
{
    /// <summary>A segment that subscribes to every value of its position.</summary>
    internal const string Wildcard = "*";

    /// <summary>Whether <paramref name="pattern"/> subscribes to more than one exact event name.</summary>
    /// <param name="pattern">A <c>$defs/eventPattern</c>-typed value, as the descriptor declares it.</param>
    internal static bool HasWildcard(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        foreach (var segment in pattern.Split(AlvoEventName.Separator))
        {
            if (string.Equals(segment, Wildcard, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
