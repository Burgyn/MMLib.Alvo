namespace MMLib.Alvo.Data;

/// <summary>
/// A request to list an entity's rows through <see cref="IAlvoData.QueryAsync"/>: which entity,
/// an optional caller filter, sort order, and a page size/cursor. This models the whole PostgREST-
/// style query surface F3 through F-final will expose, but only implements the F3 subset —
/// filtering, sorting, and keyset paging. Projection (selecting a field subset), relation
/// embedding, aggregates, and bulk operations are deliberately <b>not</b> modelled here yet; they
/// land in PR3.
/// </summary>
/// <remarks>
/// Every member of <b>this record</b> beyond <see cref="Entity"/> is additive by construction — a
/// new optional member (e.g. a future <c>Select</c> projection list) can be added here without
/// breaking an existing caller or provider, because §2.1 of the domain analysis warns that a badly
/// designed query language cannot be fixed later without a breaking change. Do not narrow or
/// repurpose an existing member to smuggle in a PR3 feature; add a new one instead. This promise is
/// scoped to <see cref="AlvoQuery"/> itself — <see cref="AlvoSort"/> and <see cref="AlvoComparison"/>
/// are positional records and do not carry the same guarantee; see their own remarks.
/// </remarks>
public sealed record AlvoQuery
{
    /// <summary>Gets the entity being queried.</summary>
    public required string Entity { get; init; }

    /// <summary>
    /// Gets the caller-supplied filter, or <see langword="null"/> for none. An implementation
    /// applies this <em>in addition to</em> the resolved policy predicate — it can only narrow
    /// the caller's already-visible rows, never widen them.
    /// </summary>
    public AlvoFilter? Filter { get; init; }

    /// <summary>Gets the sort order to apply, outermost first; empty means implementation-defined (but stable) order.</summary>
    public IReadOnlyList<AlvoSort> Sort { get; init; } = [];

    /// <summary>Gets the maximum number of rows to return, or <see langword="null"/> for no explicit limit.</summary>
    public int? Limit { get; init; }

    /// <summary>
    /// Gets the opaque keyset-pagination cursor returned by a previous page, or <see langword="null"/>
    /// for the first page. Never a page number/offset: an offset shifts under concurrent writes,
    /// where a keyset cursor does not.
    /// </summary>
    public string? After { get; init; }
}

/// <summary>One sort key in an <see cref="AlvoQuery"/>'s <see cref="AlvoQuery.Sort"/> list.</summary>
/// <param name="Field">The field to sort by.</param>
/// <param name="Descending">Whether to sort descending; <see langword="false"/> sorts ascending.</param>
/// <param name="Nulls">
/// Where a <see langword="null"/> value for <paramref name="Field"/> sorts. Explicit rather than
/// left to each backend's own default, because SQLite and PostgreSQL disagree on the default
/// placement of <c>NULL</c> for a given sort direction — an explicit placement is the only way
/// the same <see cref="AlvoQuery"/> produces the same order on both engines.
/// </param>
/// <remarks>
/// A positional record with defaulted parameters: adding a parameter here, even a defaulted one,
/// changes the constructor's signature and is a binary break for any compiled caller. This is a
/// deliberate, narrower guarantee than <see cref="AlvoQuery"/>'s own additive-by-construction one.
/// </remarks>
public sealed record AlvoSort(string Field, bool Descending = false, AlvoNullPlacement Nulls = AlvoNullPlacement.Last);

/// <summary>Where a <see langword="null"/> value sorts relative to every non-null value.</summary>
public enum AlvoNullPlacement
{
    /// <summary>Nulls sort after every non-null value.</summary>
    Last,

    /// <summary>Nulls sort before every non-null value.</summary>
    First,
}
