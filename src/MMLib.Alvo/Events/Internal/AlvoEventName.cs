using System.Text.RegularExpressions;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// <b>The guard that keeps a host from minting an event indistinguishable from a real data change.</b> Every
/// name reaching <see cref="IAlvoEvents.PublishAsync"/> passes through here first.
/// </summary>
/// <remarks>
/// <para>
/// <b>The reserved-namespace rule is the whole reason this type exists.</b> Without it a host could publish
/// <c>entity.orders.updated</c>, and every descriptor rule and after-hook subscribing to that name would fire
/// on a forged event — carrying a <c>partitionkey</c>, an <c>authid</c> and a <c>time</c> for a row nobody
/// wrote. The three namespaces are read off <see cref="EventPattern.ReservedNamespaces"/> rather than spelled
/// here, so the set that is <em>refused</em> and the set the frozen <c>$defs/eventPattern</c> grammar
/// <em>admits</em> cannot drift apart.
/// </para>
/// <para>
/// <b>Ordinal, not case-insensitive, and that is the tight rule rather than the loose one.</b>
/// "Indistinguishable from a real data change" has exactly one arbiter:
/// <see cref="EventSubscriptions"/>'s type reader, which compares the first segment with
/// <see cref="StringComparison.Ordinal"/>. <c>Entity.orders.updated</c> selects no hook there, so it is not
/// the forgery this rule is about — it is refused one rule later, by <see cref="CustomName"/>, for being
/// malformed. Matching the arbiter exactly is what keeps this guard a statement about a real reader instead
/// of a superstition about strings.
/// </para>
/// <para>
/// <b><see cref="CustomName"/> is well-formedness, not a namespace design.</b>
/// <c>docs/architecture/events.md</c> is explicit that the real answer is a <em>designed</em> namespace,
/// once — not a prefix bolted on under one PR's schedule. This only keeps the outbox's <c>event_type</c>
/// column to the shape everything downstream already reads: two or more dot-separated lower-case segments.
/// </para>
/// </remarks>
internal static partial class AlvoEventName
{
    /// <summary>
    /// Refuses <paramref name="type"/> unless a host may publish it as a custom application event.
    /// </summary>
    /// <param name="type">The event name the host asked to publish.</param>
    /// <param name="parameterName">The caller's own parameter name, so the refusal names the argument.</param>
    /// <exception cref="ArgumentException">
    /// It is blank, sits in one of the framework's reserved namespaces, or is not a well-formed event name.
    /// </exception>
    internal static void EnsureCustom(string? type, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type, parameterName);
        EnsureNotReserved(type, parameterName);
        EnsureWellFormed(type, parameterName);
    }

    /// <summary>The guarantee itself: the framework's own namespaces are not a host's to publish into.</summary>
    /// <param name="type">The event name the host asked to publish.</param>
    /// <param name="parameterName">The caller's own parameter name.</param>
    private static void EnsureNotReserved(string type, string parameterName)
    {
        var first = type.Split(EventPattern.Separator)[0];
        if (!EventPattern.IsReservedNamespace(first))
        {
            return;
        }

        throw new ArgumentException(
            $"Event name '{type}' is in the framework's reserved '{first}' namespace, which only Alvo itself "
            + $"may publish into. A published '{first}.' event would be indistinguishable from a real data "
            + "change: every descriptor rule and after-hook subscribing to that name would fire on it, with a "
            + "partition key and provenance for a record nobody wrote. Publish it under a name of your own — "
            + $"'orders.approved' rather than '{type}' — and note that no descriptor rule can subscribe to "
            + "such a name yet, because $defs/eventPattern is frozen to the reserved namespaces.",
            parameterName);
    }

    /// <summary>The name is at least two lower-case dot-separated segments, and nothing else.</summary>
    /// <param name="type">The event name the host asked to publish.</param>
    /// <param name="parameterName">The caller's own parameter name.</param>
    private static void EnsureWellFormed(string type, string parameterName)
    {
        if (CustomName().IsMatch(type))
        {
            return;
        }

        throw new ArgumentException(
            $"Event name '{type}' is not a well-formed event name. Use two or more dot-separated segments of "
            + "lower-case letters, digits and underscores, each starting with a letter — 'orders.approved' or "
            + "'billing.invoice.settled'. The shape mirrors the reserved names Alvo itself emits, so one "
            + "reader can parse both.",
            parameterName);
    }

    /// <summary>
    /// The shape a custom event name must have: two or more <c>[a-z][a-z0-9_]*</c> segments.
    /// </summary>
    /// <remarks>
    /// Deliberately <em>not</em> <c>$defs/eventPattern</c>: that grammar is a <b>subscription</b> pattern —
    /// it admits <c>*</c> and the <c>.batch</c> suffix, and it requires one of the reserved namespaces this
    /// guard refuses. The two would be one regular expression only if publishing and subscribing were the
    /// same act, which is exactly the confusion the designed namespace has to resolve.
    /// </remarks>
    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex CustomName();
}
