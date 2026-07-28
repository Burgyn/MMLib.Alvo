using System.Collections.Frozen;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The query-string keys that mean something to the API itself rather than naming a field. In PostgREST's
/// grammar every other key <em>is</em> a field name, so this set is the whole boundary between "a paging
/// instruction" and "a filter".
/// </summary>
/// <remarks>
/// <para>
/// <b>A reserved key wins, and a descriptor that would shadow one is refused at mapping.</b> The field-name
/// grammar (<c>^[a-z][a-z0-9_]{0,62}$</c>) admits every name here, so a field called <c>limit</c> is a legal
/// descriptor and <c>?limit=10</c> would be genuinely ambiguous. Resolving that per request — either silently
/// preferring one reading, or refusing the request — would make a descriptor problem look like a caller
/// problem, which is the opposite of Alvo's rule that a bad descriptor fails once, at apply/startup.
/// <see cref="EnsureNoneIsShadowed(Schema.EntitySchema)"/> is that refusal, and <c>DescriptorValidator</c> raises
/// the same collision at apply time.
/// </para>
/// <para>
/// <c>not</c> is reserved even though it is only ever a <em>prefix</em>. As a top-level key it would be
/// unambiguous (<c>?not=eq.x</c> is a filter on a field called <c>not</c>, <c>?not.not=eq.x</c> negates it),
/// but the member form a group uses is not: inside <c>or=(…)</c> the member <c>not.eq.x</c> is either a
/// negated term or a filter on <c>not</c>, and nothing in the grammar distinguishes them. A field that can be
/// filtered at the top level and not inside a group is worse than a field name refused outright, so the
/// ambiguity is closed here instead of half-supported.
/// </para>
/// </remarks>
internal static class ReservedQueryKeys
{
    /// <summary>The sort-order parameter.</summary>
    internal const string Order = "order";

    /// <summary>The page-size parameter.</summary>
    internal const string Limit = "limit";

    /// <summary>The row-skip parameter.</summary>
    internal const string Offset = "offset";

    /// <summary>The opaque keyset-cursor parameter.</summary>
    internal const string After = "after";

    /// <summary>The projection parameter.</summary>
    internal const string Select = "select";

    /// <summary>The disjunction group keyword.</summary>
    internal const string Or = "or";

    /// <summary>The conjunction group keyword.</summary>
    internal const string And = "and";

    /// <summary>The negation keyword, only ever written as a prefix.</summary>
    internal const string Not = "not";

    /// <summary>The prefix that negates the term or group it precedes.</summary>
    internal const string NotPrefix = Not + ".";

    /// <summary>Every reserved key, in the order the fix suggestions list them.</summary>
    internal static IReadOnlyList<string> All { get; } = [Order, Limit, Offset, After, Select, Or, And, Not];

    /// <summary>Every reserved key as a comma-separated list, for a fix suggestion.</summary>
    internal static string AsList { get; } = string.Join(", ", All);

    private static readonly FrozenSet<string> _reserved = All.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="key"/> is a reserved key rather than a field name.</summary>
    /// <param name="key">The query-string key.</param>
    internal static bool IsReserved(string key) => _reserved.Contains(key);

    /// <summary>
    /// Throws when any of <paramref name="entities"/> declares a field whose name a query-string key reserves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The whole applied schema is checked before a single route is mapped.</b> Per-entity it would map every
    /// route up to the offending entity and then throw, leaving a host half-mapped on a startup failure — a state
    /// nobody should have to reason about, and one whose symptom (some entities reachable, some not) says nothing
    /// about its cause.
    /// </para>
    /// <para>
    /// <b>This is the belt, not the primary guard.</b> A descriptor declaring such a field is refused at
    /// <em>apply</em> time by <c>DescriptorValidator</c>, which is where a bad descriptor belongs — it is wrong
    /// whether or not the API is mounted. This still exists because an applied schema can reach route mapping
    /// without ever having passed that validation: a descriptor applied by an earlier build, or F7's
    /// dynamic-entity registry, which never goes through the descriptor validator at all.
    /// </para>
    /// </remarks>
    /// <param name="entities">Every entity about to get routes.</param>
    /// <exception cref="InvalidOperationException">A declared field shadows a reserved query-string key.</exception>
    internal static void EnsureNoneIsShadowed(IEnumerable<Schema.EntitySchema> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        foreach (var entity in entities)
        {
            EnsureNoneIsShadowed(entity);
        }
    }

    /// <summary>
    /// Throws when <paramref name="entity"/> declares a field whose name a query-string key reserves, so the
    /// ambiguity is a startup failure naming the entity, the field and the fix — not a route that silently
    /// cannot filter by that field.
    /// </summary>
    /// <param name="entity">The entity to check.</param>
    /// <exception cref="InvalidOperationException">A declared field shadows a reserved query-string key.</exception>
    internal static void EnsureNoneIsShadowed(Schema.EntitySchema entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        foreach (var field in entity.Fields.Where(field => IsReserved(field.Name)))
        {
            throw new InvalidOperationException(
                $"Entity '{entity.Name}' declares a field named '{field.Name}', which the Data API's query "
                + $"string reserves ({AsList}). A request could not tell a filter on that field from the "
                + "reserved parameter, so rename the field in the descriptor.");
        }
    }
}
