using MMLib.Alvo.Data;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Parses <c>order=&lt;field&gt;[.asc|.desc][.nullsfirst|.nullslast][,…]</c> into the port's
/// <see cref="AlvoSort"/> keys.
/// </summary>
/// <remarks>
/// <para>
/// A sort key is subject to exactly the same confidentiality rule as a filter, and for a sharper reason:
/// ordering by a field discloses that field's ordering across the whole page, so a masked field must be
/// refused here and refused <em>identically</em> to one the entity does not declare.
/// </para>
/// <para>
/// <b>An unrecognized modifier is refused rather than ignored.</b> <c>order=year.sideways</c> ignored is a
/// page sorted ascending, which is a different answer than the caller asked for and one no response tells
/// them about. The four modifiers are PostgREST's own spellings, and the port needs the null placement
/// explicitly because SQLite and PostgreSQL disagree on the default for a given direction.
/// </para>
/// </remarks>
internal static class SortParser
{
    private const string Ascending = "asc";

    private const string Descending = "desc";

    private const string NullsFirst = "nullsfirst";

    private const string NullsLast = "nullslast";

    /// <summary>Parses the whole <c>order</c> parameter.</summary>
    /// <param name="raw">The caller-supplied parameter value.</param>
    /// <param name="fields">The caller's resolvable fields.</param>
    /// <param name="sort">The parsed keys, outermost first.</param>
    /// <param name="violation">Why the parameter was refused.</param>
    internal static bool TryParse(
        string raw, QueryFieldResolver fields, out IReadOnlyList<AlvoSort> sort, out AlvoViolation? violation)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(fields);

        sort = [];
        if (raw.Length == 0)
        {
            violation = QueryViolations.MalformedOrder();
            return false;
        }

        var keys = new List<AlvoSort>();
        foreach (var token in raw.AsSpan().Split(','))
        {
            if (!TryAddKey(raw[token], fields, keys, out violation))
            {
                return false;
            }
        }

        violation = null;
        sort = keys;
        return true;
    }

    private static bool TryAddKey(
        string token, QueryFieldResolver fields, List<AlvoSort> keys, out AlvoViolation? violation)
    {
        var parts = token.Split('.', SortKeyParts + 1);
        if (fields.Resolve(parts[0]) is not { } declared)
        {
            violation = QueryViolations.UnavailableField(ReservedQueryKeys.Order);
            return false;
        }

        if (keys.Any(key => string.Equals(key.Field, declared.Name, StringComparison.Ordinal)))
        {
            violation = QueryViolations.RepeatedSortKey();
            return false;
        }

        if (!TryReadModifiers(parts, out var descending, out var nulls))
        {
            violation = QueryViolations.MalformedOrder();
            return false;
        }

        violation = null;
        keys.Add(new AlvoSort(declared.Name, descending, nulls));
        return true;
    }

    /// <summary>
    /// Reads the modifiers a key carries, in PostgREST's own order and each at most once. The order is
    /// enforced rather than tolerated so that one sort key has exactly one spelling.
    /// </summary>
    /// <summary>
    /// How many dot-separated parts one sort key can carry: the field, a direction and a null placement.
    /// </summary>
    /// <remarks>
    /// Passed as a split limit rather than checked afterwards, so a single key of a million dots costs four
    /// substrings and a refusal instead of a million substrings and the same refusal — the transport used to
    /// be what bounded that, and a request body is not. A fourth part is still refused by
    /// <see cref="TryReadModifiers"/> exactly as it was, because the limit leaves the tail in the last part
    /// rather than discarding it.
    /// </remarks>
    private const int SortKeyParts = 3;

    private static bool TryReadModifiers(string[] parts, out bool descending, out AlvoNullPlacement nulls)
    {
        descending = false;
        nulls = AlvoNullPlacement.Last;
        var readDirection = false;
        var readPlacement = false;

        for (var index = 1; index < parts.Length; index++)
        {
            var modifier = parts[index];
            if (!readDirection && !readPlacement && modifier is Ascending or Descending)
            {
                (descending, readDirection) = (modifier == Descending, true);
            }
            else if (!readPlacement && modifier is NullsFirst or NullsLast)
            {
                (nulls, readPlacement) = (
                    modifier == NullsFirst ? AlvoNullPlacement.First : AlvoNullPlacement.Last, true);
            }
            else
            {
                return false;
            }
        }

        return true;
    }
}
