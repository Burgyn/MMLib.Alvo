using System.Collections.Frozen;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The query-string keys that mean something to the API itself rather than naming a field. In PostgREST's
/// grammar every other key <em>is</em> a field name, so this set is the whole boundary between "a paging
/// instruction" and "a filter".
/// </summary>
/// <remarks>
/// <para>
/// <b>A reserved key wins, and a descriptor that would shadow one is refused at startup.</b> The field-name
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
    /// <b>The whole schema is checked before a single route is built.</b> Per-entity it would build every route
    /// up to the offending entity and then throw, leaving a half-materialised table — a state nobody should have
    /// to reason about, and one whose symptom (some entities reachable, some not) says nothing about its cause.
    /// </para>
    /// <para>
    /// <b>This is the belt, not the primary guard, and the two run over different inputs at different times.</b>
    /// A descriptor declaring such a field is refused at <em>apply</em> time by <c>DescriptorValidator</c> and
    /// again by boot stage 0 (<c>DescriptorBootPlan</c>) over the descriptor's own mapped schema, both of which
    /// fail the start — that is where a bad descriptor belongs, since it is wrong whether or not the API is
    /// mounted. What this call adds is the belt for a schema that reaches route generation without ever having
    /// passed either: a schema applied by an earlier build, a substituted <c>ISchemaRegistry</c>, or F7's
    /// dynamic-entity registry. For that input the refusal can only be raised when
    /// <c>AlvoEndpointDataSource</c> first materialises the table, i.e. on the first request — recorded as a
    /// deviation rather than claimed to be a start-time guarantee.
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
    /// ambiguity is one refusal naming the entity, the field and the fix — not a route that silently cannot
    /// filter by that field.
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
