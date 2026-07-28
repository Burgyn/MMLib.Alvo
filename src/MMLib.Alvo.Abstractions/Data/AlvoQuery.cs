using MMLib.Alvo.Schema;

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
    /// A key naming a <b>nullable</b> field is only usable on an <em>unpaged</em> read: a keyset cursor's
    /// boundary is a chain of comparisons that cannot express where nulls sort, so an implementation refuses a
    /// paged read (<see cref="Limit"/> or <see cref="After"/> set) sorted by one rather than silently losing
    /// the null-keyed rows. <see cref="EnsureSortKeysCanBePaged"/> is that refusal, and every implementation
    /// calls it rather than writing its own.
    /// </remarks>
    public IReadOnlyList<AlvoSort> Sort { get; init; } = [];

    /// <summary>
    /// Throws when <paramref name="query"/> is <b>paged</b> and sorts by a nullable field. Every
    /// <see cref="IAlvoData"/> implementation must call this before composing a page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A keyset boundary is a chain of comparisons with no <c>IS NULL</c> arm, so a <see langword="null"/> on
    /// either side makes the term <see langword="null"/> and a <c>WHERE</c> treats that as false: the page
    /// stops early and <b>silently</b>, losing every null-keyed row under <c>nullslast</c> and every row but
    /// the first under <c>nullsfirst</c>. The design's ruling is that a nullable sort column must declare its
    /// null placement <em>or be rejected</em>; the third option — accept the query and lose rows — is what this
    /// refuses.
    /// </para>
    /// <para>
    /// <b>It lives here because it is a rule of the port, not of one backend</b>, and it was written twice —
    /// verbatim, message included — in two shipped assemblies before it lived anywhere. This codebase's own
    /// precedent is <see cref="AlvoFilter.EnsureWithinLimits"/>: a public static guard in the ports, called by
    /// every implementation, so a third one (F7's dynamic driver) inherits the rule instead of making a third
    /// copy of it. The reference implementation calls it too, although it compares rows in memory and could
    /// page over a null key correctly — a reference that answered where the shipped backends refuse would give
    /// this port two contracts.
    /// </para>
    /// <para>
    /// Scoped to a paged read deliberately: an <b>unpaged</b> sorted read has no boundary, so its ordering over
    /// nulls is already correct and refusing it would break whole-set reads for no gain.
    /// </para>
    /// </remarks>
    /// <param name="query">The query about to be served.</param>
    /// <param name="entity">
    /// The entity as the implementation's own applied schema declares it, or <see langword="null"/> when it
    /// declares none — in which case there is no nullability to read and the check does not apply. An entity
    /// the implementation does not know is refused elsewhere, before any row is touched.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="query"/> is paged and a sort key names a nullable field.</exception>
    public static void EnsureSortKeysCanBePaged(AlvoQuery query, EntitySchema? entity)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (entity is null || !query.IsPaged)
        {
            return;
        }

        foreach (var key in query.Sort.Where(key => IsNullable(entity, key.Field)))
        {
            throw new ArgumentException(
                $"Sorting a paged read by '{key.Field}' is not supported, because that field is nullable and a "
                + "keyset cursor cannot express where its null values sort. Page by a required field, or read the "
                + "whole set without a limit or a cursor.",
                nameof(query));
        }
    }

    /// <summary>
    /// Whether this query asks for a page rather than the whole visible set — any of the three paging
    /// signals is enough, because each makes the boundary observable.
    /// </summary>
    private bool IsPaged => Limit is not null || After is not null || Offset is not null;

    /// <summary>
    /// Whether <paramref name="entity"/> declares <paramref name="field"/> nullable. A field the entity does
    /// not declare is not this check's business: an undeclared filter or sort key is refused, by name, before
    /// this runs.
    /// </summary>
    private static bool IsNullable(EntitySchema entity, string field) =>
        entity.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, field, StringComparison.Ordinal))
            is { Nullable: true };

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
    /// Throws when <paramref name="query"/>'s paging window is self-contradictory or out of range —
    /// a negative <see cref="Limit"/> or <see cref="Offset"/>, or both <see cref="After"/> and
    /// <see cref="Offset"/> set at once.
    /// </summary>
    /// <remarks>
    /// Every <see cref="IAlvoData"/> implementation calls this before composing a page, in place of the
    /// private negative-<see cref="Limit"/> check PR2 wrote twice, once per implementation. A rule of the
    /// port belongs here, beside <see cref="EnsureSortKeysCanBePaged"/>, for the same reason that one
    /// does — so a third implementation inherits the rule instead of writing a fourth copy of it.
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
