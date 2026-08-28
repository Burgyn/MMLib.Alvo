using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Events;

/// <summary>
/// <b>The one authority on which event names a host may mint, and the guard that keeps it from minting one
/// indistinguishable from a real data change.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>The reserved-namespace rule is the whole reason this type exists.</b> Without it a host could publish
/// <c>entity.orders.updated</c>, and every descriptor rule and after-hook subscribing to that name would fire
/// on a forged event — carrying a <c>partitionkey</c>, an <c>authid</c> and a <c>time</c> for a row nobody
/// wrote.
/// </para>
/// <para>
/// <b>It lives in <c>Abstractions</c>, beside <see cref="AlvoEvent"/>, because the reserved namespaces are
/// part of the <em>wire</em> contract rather than of the descriptor.</b> That is also what lets
/// <see cref="AlvoCustomEvent.Create"/> — the only door to <see cref="IOutboxStore.AppendAsync"/> — enforce
/// the rule for every caller, including a host building its own envelope and an external driver's contract
/// tests. The descriptor-side grammar (wildcards, the <c>.batch</c> suffix) is a different question and stays
/// in the core, next to the apply path that refuses it.
/// </para>
/// <para>
/// <b>Ordinal, not case-insensitive, and that is the tight rule rather than the loose one.</b>
/// "Indistinguishable from a real data change" has exactly one arbiter: the core's subscription reader, which
/// compares the first segment with <see cref="StringComparison.Ordinal"/>.
/// <c>Entity.orders.updated</c> selects no hook there, so it is not the forgery this rule is about — it is
/// refused one rule later, by <see cref="CustomName"/>, for being malformed. Matching the arbiter exactly is
/// what keeps this guard a statement about a real reader instead of a superstition about strings.
/// </para>
/// </remarks>
public static partial class AlvoEventName
{
    /// <summary>The segment separator every event name uses.</summary>
    public const char Separator = '.';

    /// <summary>
    /// The namespaces Alvo emits into, and therefore the ones a host may never mint an event into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are exactly the namespaces the frozen <c>$defs/eventPattern</c> grammar admits as a
    /// subscription's first segment — asserted against <c>schema/project.schema.json</c> itself by
    /// <c>EventPatternTests.The_reserved_namespaces_are_the_schema_s_own</c>, because a hand-copied
    /// alternation that drifts from the schema does not fail, it silently reserves the wrong names.
    /// </para>
    /// <para>
    /// <b>A <see cref="FrozenSet{T}"/>, and the type is load-bearing rather than a micro-optimisation.</b>
    /// Handed out as <c>IReadOnlySet&lt;string&gt;</c> over a live <see cref="HashSet{T}"/> — which is what
    /// this was — a host could downcast it once at startup and call <c>Clear()</c>, disabling the
    /// reserved-namespace guard process-wide and making <see cref="AlvoCustomEvent.Create"/> accept
    /// <c>entity.orders.updated</c> again. That is the exact forgery <see cref="AlvoCustomEvent"/> exists to
    /// make structurally impossible, so the set that decides it must be immutable in fact and not only in the
    /// interface it is declared through. Found by review, after the first bypass had already been fixed.
    /// </para>
    /// </remarks>
    public static FrozenSet<string> ReservedNamespaces { get; } =
        new[] { "entity", "auth", "storage" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="segment"/> is one of the framework's own namespaces.</summary>
    /// <param name="segment">One event-name segment, normally the first.</param>
    public static bool IsReservedNamespace(string segment) => ReservedNamespaces.Contains(segment);

    /// <summary>
    /// Refuses <paramref name="type"/> unless a host may publish it as a custom application event.
    /// </summary>
    /// <param name="type">The event name the host asked to publish.</param>
    /// <param name="parameterName">The caller's own parameter name, so the refusal names the argument.</param>
    /// <exception cref="ArgumentException">
    /// It is blank, sits in one of the framework's reserved namespaces, or is not a well-formed event name.
    /// </exception>
    public static void EnsureCustom(string? type, string parameterName)
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
        var first = type.Split(Separator)[0];
        if (!IsReservedNamespace(first))
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
    /// <para>
    /// Deliberately <em>not</em> <c>$defs/eventPattern</c>: that grammar is a <b>subscription</b> pattern —
    /// it admits <c>*</c> and the <c>.batch</c> suffix, and it requires one of the reserved namespaces this
    /// guard refuses. The two would be one regular expression only if publishing and subscribing were the
    /// same act, which is exactly the confusion the designed namespace has to resolve.
    /// </para>
    /// <para>
    /// It is also what makes a custom event's partition key provably disjoint from a data event's: an entity
    /// name carries no dot (<c>schema/project.schema.json</c>, <c>entities</c>' <c>propertyNames</c>) and this
    /// requires at least one.
    /// </para>
    /// <para>
    /// <b>Anchored <c>\A…\z</c> and not <c>^…$</c>, which is a correctness fix rather than a style choice.</b>
    /// In .NET, <c>$</c> matches at the end of the string <em>or immediately before a trailing</em> <c>\n</c> —
    /// so <c>^…$</c> admitted <c>"orders.approved\n"</c>, and a name carrying a trailing newline would have
    /// reached the <c>event_type</c> and <c>partition_key</c> columns: two event types that render
    /// identically wherever they are printed, and a control character in any log line that ever names one.
    /// <c>\z</c> is the absolute end of input and admits nothing after it. Measured, not reasoned: the
    /// <c>\n</c> case was a failing fact before this change and a passing one after.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"\A[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex CustomName();
}
