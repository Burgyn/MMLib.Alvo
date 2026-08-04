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
/// <see cref="Of"/> is called from one place — <see cref="ColumnValue"/>, which is the single funnel every
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

    /// <summary>
    /// <paramref name="instant"/> as the round-trippable text a framework bookkeeping table stores it as.
    /// </summary>
    /// <param name="instant">The instant to render.</param>
    /// <remarks>
    /// Here, rather than spelled out at each call site, for the reason this whole type exists: two copies of
    /// one conversion are how the two copies come to disagree. It was written twice —
    /// <see cref="Internal.VersionRowWriter"/> and <see cref="Internal.IdempotencyTable"/> — before it lived anywhere, and both
    /// read it now. <c>"O"</c> under the invariant culture, because those columns are <c>TEXT</c> on both
    /// shipped engines and a culture-sensitive rendering of an instant is a value the writing process cannot
    /// necessarily read back.
    /// </remarks>
    internal static string Text(DateTimeOffset instant) =>
        instant.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// <paramref name="instant"/> at the finest precision every engine Alvo supports can round-trip: whole
    /// microseconds, truncated rather than rounded.
    /// </summary>
    /// <param name="instant">An instant this process just read off a clock.</param>
    /// <remarks>
    /// <para>
    /// <b>An instant the framework mints for itself is minted at storage precision, because the stored value
    /// is the authoritative one.</b> A .NET clock keeps 100-nanosecond ticks, while a <c>datetime</c> column
    /// on the reference engine is a PostgreSQL <c>timestamptz</c>, which keeps microseconds and drops the rest
    /// on the way in. An audit stamp taken straight off the clock is therefore a value the row it is written
    /// to cannot hold: measured on a real engine, <c>…4567</c> stamped and <c>…4560</c> read back — 7 ticks
    /// apart, and no longer the same instant as the <c>time</c> on the event that write emitted. That is the
    /// difference between "one write, one instant" and three timestamps that agree only when the clock happens
    /// to land on a whole microsecond, which is a coincidence one host produces on every write (macOS reads
    /// the wall clock at microsecond granularity) and another almost never does (Linux reads it at nanosecond
    /// granularity).
    /// </para>
    /// <para>
    /// <see cref="Data.AlvoPrecondition"/> already states this hazard for the version channel and closes it by
    /// only ever comparing values that came <em>out of</em> the database. An event envelope cannot do that —
    /// its <c>time</c> is the write's own instant, shared with the millisecond embedded in the event id and
    /// with the outbox row's <c>created_at</c>, not a column read back — so the instant itself is made
    /// storable instead, once, where the clock is read.
    /// </para>
    /// <para>
    /// <b>Truncated, never rounded.</b> Rounding up would stamp a row with an instant that had not yet
    /// happened when the write ran, and truncation is what the engine itself does, so the stamped value is
    /// bit-for-bit the value the engine that keeps the least will hand back.
    /// </para>
    /// <para>
    /// <b>Why one microsecond rather than each engine's own answer.</b> SQLite stores the rendered text and
    /// keeps all seven digits, so leaving the floor to the engine means one write records a different instant
    /// per engine and an event's <c>time</c> equals its row's stamp on one of them only — §0 principle 3, on
    /// the framework's own bookkeeping. One microsecond is the coarsest of the engines Alvo ships or targets
    /// (PostgreSQL <c>timestamptz</c>, SQLite text, Azure SQL <c>datetime2</c>), so it is the precision every
    /// one of them agrees on.
    /// </para>
    /// <para>
    /// <b>A caller-supplied <c>datetime</c> value is deliberately not truncated here.</b> This is about the
    /// instants the framework mints and then compares against itself; a caller's own value is stored at
    /// whatever precision the engine they chose keeps, exactly as it already was, and truncating it would
    /// silently move a filter boundary the caller wrote (a <c>gt</c> bound floored by 900 ns admits the row it
    /// was meant to exclude).
    /// </para>
    /// </remarks>
    internal static DateTimeOffset Storable(DateTimeOffset instant) =>
        instant.AddTicks(-(instant.Ticks % TimeSpan.TicksPerMicrosecond));

    /// <summary>Whether a column of <paramref name="clrType"/> holds an instant.</summary>
    /// <param name="clrType">The column's CLR type, nullable or not.</param>
    internal static bool IsTimestamp(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        return (Nullable.GetUnderlyingType(clrType) ?? clrType) == typeof(DateTimeOffset);
    }
}
