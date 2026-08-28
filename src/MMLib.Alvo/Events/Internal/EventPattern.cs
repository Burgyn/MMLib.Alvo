namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// <b>The one authority on the frozen <c>$defs/eventPattern</c> grammar</b>: which namespaces it reserves,
/// and whether one pattern subscribes with a wildcard.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two unrelated-looking rules read the same vocabulary, which is why it is a type rather than two
/// literals.</b> <see cref="HasWildcard"/> is what refuses <c>entity.orders.*</c> at apply
/// (<c>DescriptorToSchemaMapper</c>); <see cref="ReservedNamespaces"/> is what refuses a host publishing
/// <c>entity.orders.updated</c> as a custom event (<see cref="AlvoEventName"/>). They were a hand-copied
/// alternation in two files in the first draft, which is the defect <c>UnhonouredFeatures</c>' own remarks
/// describe: two copies of one list with nothing tying them, so a namespace added to one side is silently
/// unreserved on the other. <c>EventPatternTests.The_reserved_namespaces_are_the_schema_s_own</c> ties this
/// set to <c>schema/project.schema.json</c> itself, so the schema stays the authority over even this one.
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
    /// <summary>The segment separator, as the grammar spells it.</summary>
    internal const char Separator = '.';

    /// <summary>A segment that subscribes to every value of its position.</summary>
    internal const string Wildcard = "*";

    /// <summary>
    /// The namespaces the frozen grammar's first segment admits — and therefore the ones a host may never
    /// mint an event into.
    /// </summary>
    /// <remarks>
    /// Ordinal, because the one reader that decides whether an event <em>is</em> a data event —
    /// <c>EventSubscriptions.TryReadSubscription</c> — compares its first segment with
    /// <see cref="StringComparison.Ordinal"/>. A case-insensitive set here would reserve names that reader
    /// does not recognise, which is a different rule than "indistinguishable from a real data change".
    /// </remarks>
    internal static IReadOnlySet<string> ReservedNamespaces { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "entity", "auth", "storage" };

    /// <summary>Whether <paramref name="segment"/> is one of the framework's own namespaces.</summary>
    /// <param name="segment">One event-name segment, normally the first.</param>
    internal static bool IsReservedNamespace(string segment) => ReservedNamespaces.Contains(segment);

    /// <summary>Whether <paramref name="pattern"/> subscribes to more than one exact event name.</summary>
    /// <param name="pattern">A <c>$defs/eventPattern</c>-typed value, as the descriptor declares it.</param>
    internal static bool HasWildcard(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        foreach (var segment in pattern.Split(Separator))
        {
            if (string.Equals(segment, Wildcard, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
