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
    /// applies this <em>in addition to</em> the resolved policy predicate, so it can only narrow the
    /// set of rows the caller already sees, never widen it.
    /// </summary>
    /// <remarks>
    /// <b>"Can only narrow" is a statement about row visibility only — it is not true of field
    /// confidentiality.</b> A filter is a comparison whose <em>outcome</em> is observable from the
    /// result set, so a filter over a field the caller may not read leaks that field's value one
    /// comparison at a time (<c>salary.gt.&lt;x&gt;</c>, repeated, is a binary search) even though the
    /// value itself never appears in a response. An implementation must therefore reject a filter
    /// naming a field in <see cref="Rules.PolicyDecision.HiddenFields"/> rather than answer it — see
    /// <see cref="IAlvoData.QueryAsync"/>.
    /// </remarks>
    public AlvoFilter? Filter { get; init; }

    /// <summary>
    /// Gets the sort order to apply, outermost first; empty means <b>implementation-defined and unstable</b>
    /// order — two identical calls may return the same rows in a different sequence, because neither engine
    /// promises otherwise for a query with no <c>ORDER BY</c> (a PostgreSQL heap <c>UPDATE</c> relocates a row
    /// and changes the sequence). A caller that cares about order, or that pages, must name a key.
    /// A sort key is subject to the same confidentiality rule as <see cref="Filter"/>: ordering
    /// by a hidden field discloses that field's ordering across the whole page, so an implementation
    /// rejects it.
    /// </summary>
    /// <remarks>
    /// A key naming a <b>nullable</b> field is usable on a paged read like any other, and
    /// <see cref="AlvoSort.Nulls"/> is what makes it so: the ordering over nulls is total and known, so a
    /// keyset boundary can be expressed for it. That was not always true — until F4 a paged read over a
    /// nullable key was refused outright, because the boundary was a chain of comparisons with no
    /// <c>IS NULL</c> arm and answering would have silently lost rows. An implementation that cannot compare
    /// the pair <em>(where the null sorts, then the value)</em> must still refuse rather than answer; losing
    /// rows quietly is the one option this port has never allowed.
    /// </remarks>
    public IReadOnlyList<AlvoSort> Sort { get; init; } = [];

    /// <summary>Gets the maximum number of rows to return, or <see langword="null"/> for no explicit limit.</summary>
    public int? Limit { get; init; }

    /// <summary>
    /// Gets the opaque keyset-pagination cursor returned by a previous page, or <see langword="null"/>
    /// for the first page. Never a page number/offset: an offset shifts under concurrent writes,
    /// where a keyset cursor does not.
    /// </summary>
    public string? After { get; init; }

    /// <summary>
    /// Gets the number of leading rows to skip, or <see langword="null"/> for none. The opt-in second
    /// paging mode §2.1 requires beside the keyset default: simple for a UI that shows page numbers,
    /// and wrong for a large set — an offset shifts under concurrent writes and degenerates on a million
    /// rows, which is why <see cref="After"/> is the default and this is not.
    /// </summary>
    /// <remarks>
    /// Mutually exclusive with <see cref="After"/>: they anchor the same window two different ways, so a
    /// query carrying both is refused as malformed rather than served by whichever the implementation
    /// happens to check first.
    /// </remarks>
    public int? Offset { get; init; }

    /// <summary>
    /// Gets whether this read also asks for <see cref="AlvoPage.TotalCount"/> — how many rows the query
    /// matches in total, not how many this page carries. <see langword="false"/> by default, and that default
    /// is the whole point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Opt-in because an exact count is a second full scan of the filtered set, on every page.</b> §2.1
    /// requires it to be opt-in and the domain analysis names <c>count(*)</c> over a large table as the
    /// specific expense; as a default it would make every list roughly twice the work for a number most
    /// callers never read. An implementation that is not asked for one must not compute one.
    /// </para>
    /// <para>
    /// <b>The count is over the <em>policy-filtered</em> set, and over the caller's filter — never over the
    /// table, and never over the page.</b> It ignores <see cref="Limit"/>, <see cref="Offset"/> and
    /// <see cref="After"/> entirely: "how many rows are there" is a question about the set the caller can
    /// see, which is the same set the page is a window onto. A count composed any other way is an oracle
    /// about rows the caller cannot read.
    /// </para>
    /// <para>
    /// <b>A boolean rather than an <c>exact | planned | estimated</c> mode, deliberately.</b> A planner
    /// estimate is engine-specific — PostgreSQL has <c>EXPLAIN</c>, SQLite has no equivalent worth the name
    /// — and §0 principle 3 says the behaviour is identical on every engine, so a mode that is real on one
    /// driver and a lie on the other belongs on neither. The three RFC 7240 spellings are an HTTP
    /// vocabulary; the layer that reads the header degrades them and says so in
    /// <c>Preference-Applied</c>. When a driver can honestly estimate, this port grows a mode and
    /// <see cref="AlvoPage"/> grows the applied one — additively, at the point the distinction becomes true.
    /// </para>
    /// </remarks>
    public bool IncludeTotalCount { get; init; }

    /// <summary>
    /// Throws when <paramref name="query"/>'s paging window is self-contradictory or out of range —
    /// a negative <see cref="Limit"/> or <see cref="Offset"/>, or both <see cref="After"/> and
    /// <see cref="Offset"/> set at once.
    /// </summary>
    /// <remarks>
    /// Every <see cref="IAlvoData"/> implementation calls this before composing a page, in place of the
    /// private negative-<see cref="Limit"/> check PR2 wrote twice, once per implementation. A rule of the
    /// port belongs here, on the same <see cref="AlvoFilter.EnsureWithinLimits"/> precedent — so a third
    /// implementation inherits the rule instead of writing a fourth copy of it.
    /// </remarks>
    /// <param name="query">The query about to be served.</param>
    /// <exception cref="ArgumentException"><paramref name="query"/>'s paging window is malformed.</exception>
    public static void EnsurePagingWindowIsSane(AlvoQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        ArgumentOutOfRangeException.ThrowIfNegative(query.Limit ?? 0, nameof(Limit));

        if (query.Offset is { } offset)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset, nameof(Offset));
        }

        if (query.After is not null && query.Offset is not null)
        {
            throw new ArgumentException(
                "A query cannot combine a keyset cursor ('after') with an offset: they anchor the same "
                + "paging window two different ways, and answering with only one would silently resolve an "
                + "ambiguous request rather than refuse it. Send only one.",
                nameof(query));
        }
    }
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
