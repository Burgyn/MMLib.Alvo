namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// CEL's timestamp and duration overloads — <c>ts ± dur</c>, <c>dur + ts</c>, <c>ts - ts</c>, <c>dur ± dur</c> — typed from a
/// checked tree's leaves (no new node, X7) and evaluated with CEL's range errors (HX-11 Rulings 9, 10).
/// </summary>
internal static class CelTemporal
{
    /// <summary>Why a shifted timestamp is refused.</summary>
    internal const string TimestampRange = "the result is outside the range of a Timestamp";

    /// <summary>Why a duration sum or difference is refused.</summary>
    internal const string DurationRange = "the result is outside the range of a Duration";

    private const string NotTemporal = "an operand is neither a Timestamp nor a Duration";

    /// <summary>The temporal type a checked node yields, or <see langword="null"/> when it yields neither.</summary>
    /// <param name="node">A type-checked node.</param>
    /// <returns><see cref="CelValueType.Timestamp"/>, <see cref="CelValueType.Duration"/> or <see langword="null"/>.</returns>
    internal static CelValueType? TypeOf(CelNode node) => node switch
    {
        CelLiteral { Type: CelValueType.Duration or CelValueType.Timestamp } literal => literal.Type,
        CelFieldRef { Type: CelValueType.Timestamp } => CelValueType.Timestamp,
        CelOuterFieldRef { Type: CelValueType.Timestamp } => CelValueType.Timestamp,
        CelCall { ResultType: CelValueType.Timestamp } => CelValueType.Timestamp,
        CelBinary binary when IsTemporal(binary) => ResultOf(binary),
        _ => null,
    };

    /// <summary>Whether a checked <c>+</c> or <c>-</c> is one of CEL's timestamp/duration overloads.</summary>
    /// <param name="binary">A type-checked binary node.</param>
    /// <returns><see langword="true"/> when either operand is a timestamp or a duration.</returns>
    internal static bool IsTemporal(CelBinary binary) =>
        binary.Operator is CelBinaryOperator.Add or CelBinaryOperator.Subtract
        && (TypeOf(binary.Left) is not null || TypeOf(binary.Right) is not null);

    /// <summary>
    /// Applies a temporal <c>+</c> or <c>-</c>. A null operand answers <see langword="null"/>. A result outside CEL's range
    /// fails closed on the hook path as the caller's data (Ruling 10); elsewhere — a rule's in-process evaluation — it
    /// throws an <see cref="OverflowException"/>, which the interpreter's entry point turns into a false verdict for the
    /// <b>whole</b> predicate (Ruling 57), so no <c>!</c> above it can read the error as a grant.
    /// </summary>
    /// <param name="op">The operator.</param>
    /// <param name="left">The left value.</param>
    /// <param name="right">The right value.</param>
    /// <param name="failClosed">Whether this is the fail-closed hook path.</param>
    /// <returns>A <see cref="DateTimeOffset"/>, a <see cref="TimeSpan"/>, or <see langword="null"/>.</returns>
    /// <exception cref="CelFunctionException"><paramref name="failClosed"/> is set and the result is out of range.</exception>
    /// <exception cref="OverflowException"><paramref name="failClosed"/> is not set and the result is out of range.</exception>
    internal static object? Apply(CelBinaryOperator op, object? left, object? right, bool failClosed)
    {
        if (left is null || right is null)
        {
            return null;
        }

        var (value, reason) = Compute(op, left, right);
        if (value is not null)
        {
            return value;
        }

        return failClosed ? throw new CelFunctionException(Name(op), reason!) : RuleFailure(op, reason!);
    }

    /// <summary>The instant a timestamp-typed value denotes; a duration is never one.</summary>
    /// <param name="value">An evaluated operand.</param>
    /// <returns>The instant, or <see langword="null"/>.</returns>
    internal static DateTimeOffset? Instant(object value) =>
        value is not TimeSpan && CelInterpreter.TryToDateTimeOffset(value, out var instant) ? instant : null;

    /// <summary>
    /// Off the hook path, a range error denies the whole verdict (Ruling 57) and any other operand the overloads do not
    /// cover answers <see langword="null"/>, as an uncomparable value does there.
    /// </summary>
    private static object? RuleFailure(CelBinaryOperator op, string reason) =>
        reason is TimestampRange or DurationRange ? throw new OverflowException($"'{Name(op)}': {reason}.") : null;

    private static CelValueType? ResultOf(CelBinary binary) => (TypeOf(binary.Left), TypeOf(binary.Right)) switch
    {
        (CelValueType.Timestamp, CelValueType.Timestamp) => CelValueType.Duration,
        (CelValueType.Duration, CelValueType.Duration) => CelValueType.Duration,
        _ => CelValueType.Timestamp,
    };

    private static (object? Value, string? Reason) Compute(CelBinaryOperator op, object left, object right) => (left, right) switch
    {
        (TimeSpan l, TimeSpan r) => Duration(op == CelBinaryOperator.Add ? l.Ticks + (decimal)r.Ticks : l.Ticks - (decimal)r.Ticks),
        (TimeSpan d, _) when op == CelBinaryOperator.Add && Instant(right) is { } t => Shift(t, d.Ticks),
        (_, TimeSpan d) when Instant(left) is { } t => Shift(t, op == CelBinaryOperator.Add ? d.Ticks : -(decimal)d.Ticks),
        _ when Instant(left) is { } a && Instant(right) is { } b => Duration(a.UtcTicks - (decimal)b.UtcTicks),
        _ => (null, NotTemporal),
    };

    private static (object? Value, string? Reason) Shift(DateTimeOffset instant, decimal ticks)
    {
        var target = instant.UtcTicks + ticks;
        return target >= DateTimeOffset.MinValue.UtcTicks && target <= DateTimeOffset.MaxValue.UtcTicks
            ? (new DateTimeOffset((long)target, TimeSpan.Zero), null)
            : (null, TimestampRange);
    }

    private static (object? Value, string? Reason) Duration(decimal ticks) =>
        Math.Abs(ticks) <= CelDuration.MaxMagnitude.Ticks ? (TimeSpan.FromTicks((long)ticks), null) : (null, DurationRange);

    private static string Name(CelBinaryOperator op) => op == CelBinaryOperator.Add ? "_+_" : "_-_";
}
