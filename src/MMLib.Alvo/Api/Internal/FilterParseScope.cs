using MMLib.Alvo.Data;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// What one request's whole filter is parsed against: the caller's resolvable fields, and the node budget
/// <see cref="AlvoFilter.MaxTerms"/> sets for the request as a whole.
/// </summary>
/// <remarks>
/// <para>
/// <b>The budget is spent while descending, not measured afterwards.</b> Measuring a finished tree means
/// building it first, so a query string carrying a hundred thousand terms is allocated in full and only then
/// refused — and a deeply nested one is walked recursively on the way in, which is a
/// <see cref="StackOverflowException"/> no <c>catch</c> can contain. The port's own
/// <see cref="AlvoFilter.EnsureWithinLimits"/> still runs on what this produces: it is the belt that keeps a
/// filter from being <em>served</em>, and this is the one that keeps it from being <em>built</em>.
/// </para>
/// <para>
/// The budget is per <b>request</b>, not per parameter. Every top-level key contributes to one tree, so
/// counting per key would let a caller multiply the limit by sending a hundred filters.
/// </para>
/// </remarks>
/// <param name="fields">The caller's resolvable fields.</param>
internal sealed class FilterParseScope(QueryFieldResolver fields)
{
    private int _nodes;
    private int _candidates;

    /// <summary>The caller's resolvable fields.</summary>
    internal QueryFieldResolver Fields => fields;

    /// <summary>
    /// Charges one node against this request's budget, answering <see langword="false"/> once it is spent.
    /// Every node a parser is about to construct — comparison, connective and negation alike — is charged,
    /// because every one of them is a node a backend then has to walk.
    /// </summary>
    internal bool TryChargeNode() => ++_nodes <= AlvoFilter.MaxTerms;

    /// <summary>
    /// Charges <paramref name="count"/> <c>in</c> candidates against this request's <b>total</b>, answering
    /// <see langword="false"/> once the allowance is spent.
    /// </summary>
    /// <remarks>
    /// <see cref="AlvoFilter.MaxInCandidates"/> is a per-list bound and the port measures only the longest list,
    /// so nothing below this caps the <em>sum</em>: the maximum number of terms each carrying a maximum list is
    /// <c>MaxTerms × MaxInCandidates</c> = 256 000 bind parameters in one statement, past the 32 766 ceiling that
    /// bound was measured against. This is a limit on the statement rather than on the tree, which is why it lives
    /// beside the node budget instead of being folded into it.
    /// </remarks>
    /// <param name="count">How many candidates one list carries.</param>
    internal bool TryChargeCandidates(int count) =>
        (_candidates += count) <= AlvoFilter.MaxInCandidates;

    /// <summary>
    /// How many <c>in</c> candidates this request may still spend, plus one — the largest list a splitter
    /// should bother producing before the charge below would refuse it anyway.
    /// </summary>
    /// <remarks>
    /// <b>A splitter that used the per-list bound alone would materialise the whole list and only then be
    /// refused by the running total.</b> That is the defect making the split lazy exists to close, one level
    /// up: 256 terms each carrying a full list is 256 000 substrings built and thrown away, and a request
    /// body — unlike a request line — is large enough to send them. Passing this instead makes the split stop
    /// at the first list the total cannot afford.
    /// <para>
    /// <b>Plus one, so the caller still refuses rather than accepts.</b> A splitter given exactly the
    /// remaining allowance would produce a list the charge accepts; given one more, it either produces a list
    /// the charge refuses or refuses the split itself. Both answer <c>too-many-in-candidates</c>, which is
    /// why the extra element is free.
    /// </para>
    /// <para>
    /// <b>Never below one, and that is a correctness guard rather than tidiness.</b> A charge that fails
    /// still spends — <see cref="TryChargeCandidates"/> adds before it compares — so a request carrying a
    /// second over-long list arrives here with the allowance already negative. A splitter is entitled to
    /// refuse a maximum of zero, so passing one through would turn a caller's over-wide filter into a 500
    /// instead of the 422 it earns. One is the smallest bound that still refuses.
    /// </para>
    /// </remarks>
    internal int AffordableCandidates => Math.Max(1, AlvoFilter.MaxInCandidates - _candidates + 1);

    /// <summary>
    /// How many filter nodes this request may still spend, plus one — the largest group a splitter should
    /// bother producing.
    /// </summary>
    /// <remarks>
    /// The node counterpart of <see cref="AffordableCandidates"/>, and needed for the same reason: a group's
    /// members are split before any of them is charged, so a request that has already spent its budget could
    /// still make the splitter build a maximum-width group. The <c>+ 1</c> and the floor of one serve the
    /// same purposes they serve there.
    /// </remarks>
    internal int AffordableNodes => Math.Max(1, AlvoFilter.MaxTerms - _nodes + 1);
}
