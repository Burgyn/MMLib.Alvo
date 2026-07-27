using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The one authority for what instant a <c>datetime</c> value denotes — used by every path on which a
/// timestamp becomes a stored value or a bound comparison operand.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every timestamp is normalised to UTC.</b> An offset is a spelling of an instant, never a property of
/// it: the reference storage type (<c>timestamptz</c>) discards the offset by definition, Npgsql refuses to
/// write any other one at all, and SQLite stores the rendered text and then compares it lexically — so a row
/// written at <c>-02:00</c> sorted before one written at <c>+00:00</c> that it actually followed, and a filter
/// bound at <c>-02:00</c> matched every row or none. Same payload, one engine refusing it and the other
/// answering it wrongly: §0 principle 3, on the channel a caller controls per request.
/// </para>
/// <para>
/// Refusing a non-UTC offset instead was the rejected alternative. It costs the same two call sites (so buys
/// no simplicity), it rejects well-formed RFC 3339 on the most-used filter type in a framework whose stated
/// primary user is an agent emitting JSON, and it would leave SQLite's lexical order equal to instant order
/// only *because* no non-UTC row exists — an invariant every future write path has to remember. Normalising
/// makes it a property of the stored data instead.
/// </para>
/// <para>
/// <b>A <c>date</c> column is deliberately not covered here.</b> Its rule is the calendar date the caller
/// wrote, read at the offset they wrote it with (<c>PredicateParameterBinder.AsDate</c>); normalising one to
/// UTC would shift the day for any caller east or west of UTC.
/// </para>
/// <para>
/// One helper, called from one place — <see cref="ColumnValue"/>, which is the single funnel every
/// caller-supplied value meets a column through. It used to expose a second entry point (<c>Stored</c>) that
/// the write paths called directly, which is precisely how the write path ended up applying the timestamp
/// normalisation and none of the funnel's other rules. A second copy of a conversion is how the two copies
/// come to disagree, and a disagreement here is invisible until it costs a row.
/// </para>
/// </remarks>
internal static class StoredInstant
{
    /// <summary>
    /// The instant <paramref name="value"/> denotes, as a UTC <see cref="DateTimeOffset"/>.
    /// </summary>
    /// <param name="value">A <see cref="DateTimeOffset"/>, a <see cref="DateTime"/>, a <see cref="DateOnly"/>, or text.</param>
    /// <exception cref="FormatException">Text that is not a timestamp.</exception>
    /// <exception cref="InvalidCastException"><paramref name="value"/> is not a timestamp and is not text.</exception>
    /// <remarks>
    /// Every arm is host-independent, which is the point of the two unobvious ones. A
    /// <see cref="DateTimeKind.Unspecified"/> <see cref="DateTime"/> — what <c>System.Text.Json</c> produces
    /// for an offset-less JSON timestamp — is read <em>as</em> UTC rather than in the process's zone, and text
    /// is parsed with <see cref="DateTimeStyles.AssumeUniversal"/> for the same reason. Without both, two
    /// replicas of one service in two regions bind two different instants for one request and CI, which runs
    /// UTC, never shows it. <see cref="DateTimeStyles.RoundtripKind"/> does not solve this: it governs a parsed
    /// value's <see cref="DateTimeKind"/> and leaves an offset-less input local.
    /// </remarks>
    internal static DateTimeOffset Of(object value) => value switch
    {
        DateTimeOffset offset => offset.ToUniversalTime(),
        DateTime { Kind: DateTimeKind.Unspecified } naive =>
            new DateTimeOffset(DateTime.SpecifyKind(naive, DateTimeKind.Utc)),
        DateTime instant => new DateTimeOffset(instant).ToUniversalTime(),
        DateOnly date => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        string text => DateTimeOffset.Parse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        _ => throw new InvalidCastException($"'{value.GetType()}' cannot be read as a timestamp."),
    };

    /// <summary>Whether a column of <paramref name="clrType"/> holds an instant.</summary>
    /// <param name="clrType">The column's CLR type, nullable or not.</param>
    internal static bool IsTimestamp(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        return (Nullable.GetUnderlyingType(clrType) ?? clrType) == typeof(DateTimeOffset);
    }
}
