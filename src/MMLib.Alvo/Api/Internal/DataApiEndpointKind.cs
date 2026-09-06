using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The generated Data API's own vocabulary for "which endpoint is this" — one member per mapped route,
/// which is <b>not</b> the same thing as one member per <see cref="DataOperation"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists because <see cref="DataOperation"/> is the policy vocabulary.</b> A descriptor's
/// <c>rules</c> name those operations and <c>PolicyCatalog</c> is keyed by them, so a member added there
/// would let a descriptor configure a rule for a <em>transport</em> — and would make "<c>list</c> is
/// unconfigured" stop answering for a route that is a list. <see cref="Query"/> is a second way to reach
/// the same read, not a sixth thing a caller may be permitted to do.
/// </para>
/// <para>
/// <b>Everything the published document keys on keys on this</b> — the <c>operationId</c>, the summary,
/// the description, the parameter list, the request body and the response catalogue — because two routes
/// gated as one operation would otherwise mint one <c>operationId</c> twice and publish one route's prose
/// for the other.
/// </para>
/// </remarks>
internal enum DataApiEndpointKind
{
    /// <summary>The collection read, with its parameters in the query string.</summary>
    List,

    /// <summary>The collection read, with its parameters in a JSON request body.</summary>
    Query,

    /// <summary>The single-row read.</summary>
    Get,

    /// <summary>The create.</summary>
    Create,

    /// <summary>The partial update.</summary>
    Update,

    /// <summary>The delete.</summary>
    Delete,

    /// <summary>The batch create, taking many rows in one transaction.</summary>
    BatchCreate,

    /// <summary>The batch update.</summary>
    BatchUpdate,

    /// <summary>The batch delete.</summary>
    BatchDelete,
}

/// <summary>What a <see cref="DataApiEndpointKind"/> means to the layers below and above it.</summary>
internal static class DataApiEndpointKinds
{
    /// <summary>The operation this endpoint's authorization filter gates it as.</summary>
    /// <remarks>
    /// The one place the <see cref="DataApiEndpointKind.Query"/>-is-a-<c>list</c> mapping is written. Every
    /// caller reads it from here rather than restating it, including the delegate that resolves the decision
    /// before its body read — two encodings of one mapping is how a route comes to be gated as one operation
    /// and refused as another.
    /// </remarks>
    /// <param name="kind">The endpoint kind.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not one of the named cases.</exception>
    internal static DataOperation ToDataOperation(this DataApiEndpointKind kind) => kind switch
    {
        DataApiEndpointKind.List or DataApiEndpointKind.Query => DataOperation.List,
        DataApiEndpointKind.Get => DataOperation.Get,
        DataApiEndpointKind.Create or DataApiEndpointKind.BatchCreate => DataOperation.Create,
        DataApiEndpointKind.Update or DataApiEndpointKind.BatchUpdate => DataOperation.Update,
        DataApiEndpointKind.Delete or DataApiEndpointKind.BatchDelete => DataOperation.Delete,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "Unmapped endpoint kind; state which operation gates it here."),
    };

    /// <summary>The spelling this endpoint's <c>operationId</c> is built from.</summary>
    /// <remarks>
    /// <para>
    /// The five that existed before <see cref="DataApiEndpointKind.Query"/> read their spelling from
    /// <see cref="DataOperation"/>'s own table rather than repeating it, so no published <c>operationId</c>
    /// can move; only a kind whose name is <em>not</em> an operation's needs a spelling of its own, and it is
    /// spelled here rather than in <c>Abstractions</c>, where a transport's name has no business being.
    /// </para>
    /// <para>
    /// <b>Each batch kind needs an arm of its own, and the default arm is why.</b> Falling through to
    /// <see cref="ToDataOperation"/> would spell <see cref="DataApiEndpointKind.BatchCreate"/> as
    /// <c>create</c> — colliding with its single-row sibling, so two routes would mint one
    /// <c>operationId</c> and one route's prose would be published for the other. The routing suite's
    /// distinctness counter is what catches an arm left off.
    /// </para>
    /// </remarks>
    /// <param name="kind">The endpoint kind.</param>
    internal static string ToWireName(this DataApiEndpointKind kind) => kind switch
    {
        DataApiEndpointKind.Query => "query",
        DataApiEndpointKind.BatchCreate => "batchCreate",
        DataApiEndpointKind.BatchUpdate => "batchUpdate",
        DataApiEndpointKind.BatchDelete => "batchDelete",
        _ => kind.ToDataOperation().ToWireName(),
    };
}
