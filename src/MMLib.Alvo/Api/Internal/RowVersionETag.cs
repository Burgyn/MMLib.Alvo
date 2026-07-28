using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;
using System.Globalization;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The one place a row's <c>updated_at</c> becomes an HTTP entity tag and back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Strong, and over the row version rather than the response bytes.</b> RFC 9110 §13.1.1 compares
/// <c>If-Match</c> with the <em>strong</em> comparison function, so a weak tag would never match and
/// the header would silently never protect anything. The cost, stated: two callers whose policies mask
/// different fields share a tag for one row version even though their representations differ. That is
/// tolerable because these responses are private and uncacheable by design (<c>Cache-Control:
/// no-store</c>), and the tag exists for optimistic concurrency, not for a shared cache.
/// </para>
/// <para>
/// <b>Encoded from a value that came out of the database, never from a clock.</b> The tag is the
/// instant's <see cref="DateTimeOffset.UtcTicks"/> in invariant digits; PostgreSQL keeps microseconds
/// and SQLite keeps text, so a tag minted from an in-memory instant would not survive its own round
/// trip and every <c>If-Match</c> would fail with nothing to diagnose. Every write already re-reads
/// the row (PR2), so a stored value is always at hand.
/// </para>
/// <para>
/// <b><see cref="DateTimeOffset.UtcTicks"/> rather than the rendered timestamp</b> for the same reason:
/// <see cref="AlvoPrecondition"/>'s comparison is <see cref="DateTimeOffset"/> equality, which <em>is</em>
/// equality of <see cref="DateTimeOffset.UtcTicks"/>, so encoding that one integer is the only spelling
/// whose round trip cannot lose or gain a bit the comparison then reads. A rendered <c>"O"</c> timestamp
/// would have to be re-parsed, and a parse is a place a precision or an offset can move.
/// </para>
/// <para>
/// <b>The digits disclose nothing the caller could not already read.</b> A tag is only ever minted from a
/// version the response itself carries, so for every tag a caller receives, <c>updated_at</c> is in the
/// body beside it. A row whose version this caller cannot read yields no tag at all
/// (see <see cref="For"/>) rather than an opaque encoding of a value withheld from the body.
/// </para>
/// </remarks>
internal static class RowVersionETag
{
    /// <summary>The one character an RFC 9110 <c>entity-tag</c> wraps its opaque part in.</summary>
    private const char Quote = '"';

    /// <summary>
    /// The entity tag for a row's stored version, or <see langword="null"/> when this row has no version
    /// to tag — in which case the response carries no <c>ETag</c> at all rather than a tag that cannot be
    /// compared.
    /// </summary>
    /// <param name="record">The row as the port returned it, already policy-masked.</param>
    /// <param name="entity">The entity as the applied schema declares it.</param>
    /// <remarks>
    /// <para>
    /// <see cref="AlvoManagedColumns.VersionColumn"/> is the authority for <em>which</em> column versions a
    /// row, and the only one: reading <c>entity.Audit</c> here would be a second copy of that rule, and the
    /// two copies are how the request layer comes to advertise a tag the port refuses to compare.
    /// </para>
    /// <para>
    /// <b>The value has to be a <see cref="DateTimeOffset"/>, and that is a real second condition rather than a
    /// cast written defensively.</b> An entity may declare its own field called <c>updated_at</c> — the schema
    /// mapper injects a managed column only when the entity does <em>not</em> declare a field of that name
    /// (<c>DescriptorToSchemaMapper.AddManagedColumn</c>) — so an author's declaration wins and an audited
    /// entity can carry a version column of some other type. Refusing to tag it matches what the port does
    /// with it: <see cref="AlvoPrecondition.EnsureMatches"/> refuses any precondition against a stored value
    /// that is not a <see cref="DateTimeOffset"/>, so a tag minted from such a column would advertise a
    /// precondition that can never be satisfied.
    /// </para>
    /// <para>
    /// <b>A <em>masked</em> version column was a different case, and it is why this branch no longer has to
    /// carry it.</b> A hidden <c>updated_at</c> is still a <see cref="DateTimeOffset"/> in the store and the
    /// port would compare it perfectly well — the mask only drops the key from the returned record. So the
    /// consequence was not a refused precondition but a <em>missing</em> one: no <c>ETag</c> minted, nothing
    /// for the caller to send as <c>If-Match</c>, and <b>optimistic concurrency off for that entity with
    /// nothing raised anywhere</b> — the silent lost update this type exists to prevent. A request layer cannot
    /// fix that, because it cannot invent a value the caller may not read; so it is refused at <em>apply</em>
    /// instead, on the same precedent as <c>softDelete</c> and <c>computed</c>. See
    /// <c>Rules.Internal.PolicyCatalogBuilder</c>'s framework-managed-column rule.
    /// </para>
    /// </remarks>
    internal static string? For(AlvoRecord record, EntitySchema entity)
    {
        ArgumentNullException.ThrowIfNull(record);
        return AlvoManagedColumns.VersionColumn(entity) is { } column && record[column] is DateTimeOffset version
            ? Encode(version)
            : null;
    }

    /// <summary>
    /// Reads one caller-supplied entity tag back into the port's precondition channel.
    /// </summary>
    /// <param name="headerValue">One <c>entity-tag</c>, quotes included, exactly as the caller sent it.</param>
    /// <param name="precondition">The row version the tag denotes, when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="headerValue"/> is a tag this API minted.</returns>
    /// <remarks>
    /// <para>
    /// <b>Anything this method does not recognise must be refused by the caller, never ignored.</b> It
    /// deliberately knows nothing about <c>*</c>: that is a statement about the row's existence rather than
    /// about its version, so decoding it here would mean returning a precondition for a header that names no
    /// version at all.
    /// </para>
    /// <para>
    /// A raw <c>W/"…"</c> fails on the leading quote, which is the honest outcome — RFC 9110 §13.1.1 would
    /// never let a weak tag match under the strong comparison <c>If-Match</c> requires, so "cannot be parsed"
    /// and "can never match" are the same answer. <b>That guard is not sufficient on its own, and the caller
    /// must not rely on it.</b> <see cref="Microsoft.Net.Http.Headers.EntityTagHeaderValue"/> lifts the
    /// prefix into its own <c>IsWeak</c> flag, so a header field parsed through it arrives here as the opaque
    /// part alone with the weakness stripped off; <c>DataApiEndpoints.Precondition</c> therefore refuses a
    /// weak tag before it gets this far. This method sees only an opaque tag and cannot know how it was
    /// spelled.
    /// </para>
    /// <para>
    /// <b>One version has exactly one spelling, and that is enforced by requiring the tag to be the one
    /// <see cref="Encode"/> would produce</b> rather than by screening the digits. Strong comparison is
    /// octet-for-octet, so accepting a second spelling of one version — <c>"0638…"</c> — would mean the tag
    /// this API mints is not the only tag it honours. Comparing against the encoder makes the two exact
    /// inverses <em>by construction</em>: the earlier version screened for a leading zero instead, which
    /// rejected every non-canonical spelling but also rejected <c>"0"</c> — a tag <see cref="Encode"/> itself
    /// produces for <see cref="DateTimeOffset.MinValue"/>. One tag it could mint and would not honour is a
    /// small hole, but it is a hole in the claim, and a claim with a hole is worse than a narrower claim.
    /// </para>
    /// </remarks>
    internal static bool TryParse(string? headerValue, out AlvoPrecondition precondition)
    {
        precondition = default;
        if (!TryTicks(headerValue, out var ticks))
        {
            return false;
        }

        var version = new DateTimeOffset(ticks, TimeSpan.Zero);
        if (!string.Equals(Encode(version), headerValue, StringComparison.Ordinal))
        {
            return false;
        }

        precondition = new AlvoPrecondition(version);
        return true;
    }

    /// <summary>The tick count a quoted tag carries, when it carries one this API could have minted.</summary>
    /// <param name="headerValue">One <c>entity-tag</c>, quotes included.</param>
    /// <param name="ticks">The decoded <see cref="DateTimeOffset.UtcTicks"/>.</param>
    /// <remarks>
    /// <para>
    /// <see cref="NumberStyles.None"/> is what makes the digits <em>only</em> digits: it admits no sign, no
    /// whitespace, no group separator and no exponent, so <c>"+1"</c>, <c>" 1"</c> and <c>"1,000"</c> are all
    /// refused rather than quietly denoting some instant. The upper bound is checked here because
    /// <see cref="long"/> reaches far past <see cref="DateTimeOffset.MaxValue"/>, and the
    /// <see cref="DateTimeOffset"/> constructor answers an out-of-range tick count with an exception — a
    /// caller-controlled header must not be able to raise one.
    /// </para>
    /// <para>
    /// The value is read verbatim, with no <c>Trim</c>. Kestrel has already stripped the optional whitespace
    /// RFC 9110 §5.5 allows around a field value, and the only caller hands over one
    /// <see cref="Microsoft.Net.Http.Headers.EntityTagHeaderValue"/>'s own opaque part — which cannot carry
    /// surrounding whitespace and still have parsed. Trimming here looked defensive and was unreachable, which
    /// is worse than either trimming or not: it implied a case that does not exist.
    /// </para>
    /// </remarks>
    private static bool TryTicks(string? headerValue, out long ticks)
    {
        ticks = 0;
        var tag = headerValue ?? string.Empty;
        if (tag.Length < 3 || tag[0] != Quote || tag[^1] != Quote)
        {
            return false;
        }

        return long.TryParse(tag[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out ticks)
            && ticks <= DateTimeOffset.MaxValue.UtcTicks;
    }

    /// <summary>One stored instant as the strong entity tag that denotes it.</summary>
    /// <param name="version">The row's version, as it came out of the database.</param>
    private static string Encode(DateTimeOffset version) =>
        $"{Quote}{version.UtcTicks.ToString(CultureInfo.InvariantCulture)}{Quote}";
}
