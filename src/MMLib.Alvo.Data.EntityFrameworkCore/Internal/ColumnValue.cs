using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The one authority for "what does a column of this CLR type hold, given this value" — used by every path
/// on which a caller-supplied value meets a column, whether it is being compared or written.
/// </summary>
/// <remarks>
/// <para>
/// It is one type because it was two rules. The read path funnelled every filter operand through this
/// conversion, complete with the NUL refusal, the midpoint-rounding refusal and the UTC normalisation; the
/// write path's only type gate was the reflection binder driving EF's <c>SetProperty</c>. So
/// <c>price=gt.5</c> filtered and <c>price=5</c> could not be written, and every value
/// <c>System.Text.Json</c> produces for a JSON number or an RFC 3339 string failed on the write side with a
/// raw reflection <see cref="ArgumentException"/> — in a framework whose stated primary user is an agent
/// emitting JSON. Two copies of one rule, and the one nobody was looking at was the one that was wrong: the
/// same shape as the framework-managed column list.
/// </para>
/// <para>
/// The read path's funnel is the authority rather than the other way round because it is the tested one, and
/// because its three refusals are the ones that exist for a measured reason. A write inherits all of them,
/// which is the point: a value no engine can carry must be refused identically wherever it arrives.
/// </para>
/// <para>
/// <b>This converts; it does not validate.</b> An integer written to a string column becomes its invariant
/// text, exactly as it already did when compared against one. Deciding that a JSON number is the wrong
/// <em>shape</em> for a declared <c>string</c> field is schema-derived request validation, which belongs above
/// this port — and a per-path guess about it is how the two paths came to disagree in the first place.
/// </para>
/// </remarks>
internal static class ColumnValue
{
    /// <summary>
    /// <paramref name="value"/> as a column of <paramref name="clrType"/> holds it.
    /// </summary>
    /// <param name="clrType">The column's CLR type, nullable or not.</param>
    /// <param name="column">The column's name, for the refusal message.</param>
    /// <param name="value">The caller-supplied value.</param>
    /// <exception cref="InvalidOperationException">The column cannot hold <paramref name="value"/>.</exception>
    internal static object? For(Type clrType, string column, object? value)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        if (value is null)
        {
            return null;
        }

        EnsureRepresentable(value, column);
        var target = Nullable.GetUnderlyingType(clrType) ?? clrType;

        return target.IsInstanceOfType(value) && !NeedsNormalising(target)
            ? value
            : Converted(value, target, column);
    }

    /// <summary>
    /// Refuses a value no engine can carry, before it reaches one that would answer for the other.
    /// </summary>
    /// <remarks>
    /// A <c>NUL</c> inside a text value is the case. PostgreSQL's <c>UTF8</c> encoding has no representation
    /// for it and Npgsql surfaces <c>22021: invalid byte sequence for encoding "UTF8": 0x00</c> — a raw
    /// provider exception out of <see cref="IAlvoData.QueryAsync"/>, off this port's failure contract
    /// entirely — while SQLite accepts the parameter and quietly answers. One caller-supplied value, an
    /// unhandled 500 on one engine and a silent answer on the other: §0 principle 3, on the channel a caller
    /// controls per request. It is refused here so both engines refuse it identically, through the same funnel
    /// that names the column for every other value a column cannot hold.
    /// </remarks>
    private static void EnsureRepresentable(object value, string column)
    {
        if (value is string text && text.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A text value containing a NUL character cannot be stored in or compared against column "
                + $"'{column}': no engine Alvo supports can represent one. Remove the NUL.");
        }
    }

    /// <summary>
    /// Whether a value the column's CLR type already accepts must still be converted.
    /// </summary>
    /// <remarks>
    /// A timestamp is the one such type, and the short-circuit above is exactly how the normalisation went
    /// missing: a <see cref="DateTimeOffset"/> <em>is</em> an instance of <see cref="DateTimeOffset"/>, so the
    /// caller's own offset went straight to the provider — silently wrong rows on SQLite, and a raw Npgsql
    /// refusal out of a read on PostgreSQL. Every other type here has one representation per value, so
    /// "already the right type" really does mean "nothing to do".
    /// </remarks>
    private static bool NeedsNormalising(Type target) => StoredInstant.IsTimestamp(target);

    private static object Converted(object value, Type target, string column)
    {
        try
        {
            return Convert(value, target);
        }
        catch (Exception exception)
            when (exception is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"A value of type '{value.GetType()}' cannot be used for column '{column}', which holds " +
                $"'{target}'. Supply a value the column's type can hold.",
                exception);
        }
    }

    /// <summary>
    /// The conversions <see cref="System.Convert.ChangeType(object?, Type, IFormatProvider?)"/> cannot
    /// do — none of <see cref="Guid"/>, <see cref="DateOnly"/>, <see cref="DateTimeOffset"/> and
    /// <see cref="TimeOnly"/> implements <see cref="IConvertible"/> — plus that method for the numeric,
    /// string and boolean cases it does handle.
    /// </summary>
    private static object Convert(object value, Type target)
    {
        if (target == typeof(Guid))
        {
            return Guid.Parse(AsText(value), CultureInfo.InvariantCulture);
        }

        if (target == typeof(DateOnly))
        {
            return AsDate(value);
        }

        if (target == typeof(DateTimeOffset))
        {
            return StoredInstant.Of(value);
        }

        if (target == typeof(TimeOnly))
        {
            return TimeOnly.Parse(AsText(value), CultureInfo.InvariantCulture);
        }

        EnsureNoFractionLost(value, target);
        return System.Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <see cref="System.Convert.ChangeType(object?, Type, IFormatProvider?)"/> <b>rounds</b> a fractional
    /// value into an integral type (midpoint-to-even) rather than refusing it, which would make the enclosing
    /// method's own contract false in the one case a caller reaches most easily.
    /// </summary>
    /// <remarks>
    /// <c>mileage=gt.12.7</c> bound as <c>13</c> answers <c>mileage &gt; 13</c> and drops the row with
    /// <c>mileage = 13</c>; <c>lte.12.7</c> admits one the caller excluded. Both are silent, and both are the
    /// wrong-but-plausible representation this funnel exists to prevent. There <em>is</em> a correct answer for
    /// a fractional bound against an integral column, but it is per-operator (floor for <c>gt</c>, ceiling for
    /// <c>lt</c>, no match at all for <c>eq</c>) and it is request-validation work, not something a value
    /// conversion may decide — so the value is refused and the caller gets a structured error. Throwing
    /// <see cref="InvalidCastException"/> hands the refusal to <see cref="Converted"/>, so it carries the
    /// column's name like every other rejection here.
    /// </remarks>
    private static void EnsureNoFractionLost(object value, Type target)
    {
        if (IsIntegral(target) && HasFraction(value))
        {
            throw new InvalidCastException(
                $"'{value}' has a fractional part and would be rounded to fit an integral column.");
        }
    }

    private static bool IsIntegral(Type target) => Type.GetTypeCode(target) is
        TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16
        or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64;

    private static bool HasFraction(object value) => value switch
    {
        decimal number => number != decimal.Truncate(number),
        double number => number != Math.Truncate(number),
        float number => number != MathF.Truncate(number),
        _ => false,
    };

    /// <summary>
    /// A <c>date</c> column takes the calendar date the caller wrote, read in the offset they wrote it
    /// with — not the UTC date, which would shift the day for any caller east or west of UTC.
    /// </summary>
    private static DateOnly AsDate(object value) => value switch
    {
        DateTimeOffset offset => DateOnly.FromDateTime(offset.DateTime),
        DateTime instant => DateOnly.FromDateTime(instant),
        _ => DateOnly.Parse(AsText(value), CultureInfo.InvariantCulture),
    };

    private static string AsText(object value) =>
        value as string ?? throw new InvalidCastException($"'{value.GetType()}' cannot be read as text.");
}
