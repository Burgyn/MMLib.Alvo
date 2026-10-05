namespace MMLib.Alvo.Expressions.Internal;

/// <summary>The math built-ins of spec §5.2 beyond C1's: whole numbers either way, and the larger or smaller of two.</summary>
internal static partial class CelBuiltInFunctions
{
    /// <summary>Why <c>math.round(x, digits)</c> refuses a <c>digits</c> outside what a <see cref="decimal"/> can hold (spec §5.5).</summary>
    internal const string DigitsReason = "digits must be from 0 to 28";

    private static CelFunction Ceil(CelValueType type) => InProcess(
        "math.ceil", type, "The smallest whole number not below x (1.2 is 2, -1.5 is -1), of the same numeric type.",
        arguments => type == CelValueType.Int ? arguments[0] : Math.Ceiling((decimal)arguments[0]!), Parameter("x", type));

    private static CelFunction Floor(CelValueType type) => InProcess(
        "math.floor", type, "The largest whole number not above x (1.8 is 1, -1.2 is -2), of the same numeric type.",
        arguments => type == CelValueType.Int ? arguments[0] : Math.Floor((decimal)arguments[0]!), Parameter("x", type));

    private static CelFunction Greatest(CelValueType type) => InProcess(
        "math.greatest", type, "The larger of a and b; a when they are equal.",
        arguments => type == CelValueType.Int
            ? ((long)arguments[0]! >= (long)arguments[1]! ? arguments[0] : arguments[1])
            : ((decimal)arguments[0]! >= (decimal)arguments[1]! ? arguments[0] : arguments[1]),
        Parameter("a", type), Parameter("b", type));

    private static CelFunction Least(CelValueType type) => InProcess(
        "math.least", type, "The smaller of a and b; a when they are equal.",
        arguments => type == CelValueType.Int
            ? ((long)arguments[0]! <= (long)arguments[1]! ? arguments[0] : arguments[1])
            : ((decimal)arguments[0]! <= (decimal)arguments[1]! ? arguments[0] : arguments[1]),
        Parameter("a", type), Parameter("b", type));

    private static CelFunction RoundToDigits => InProcess(
        "math.round", CelValueType.Decimal,
        "x rounded to digits places after the point, halves away from zero (2.345 to 2 places is 2.35); digits is from 0 to 28.",
        arguments => RoundTo((decimal)arguments[0]!, (long)arguments[1]!),
        Parameter("x", CelValueType.Decimal), Parameter("digits", CelValueType.Int));

    /// <summary>
    /// <c>math.round(x, digits)</c>: halves away from zero, like the one-argument form (deviation F17 — cel-go has no
    /// <c>digits</c> overload; rounding a price to cents is why Alvo does). Never pads: 1.2 to 5 places stays 1.2.
    /// </summary>
    /// <remarks>
    /// The range is checked on the <see cref="long"/> before any narrowing, so a <c>digits</c> such as 2^32 + 28 can
    /// never wrap into range; outside it the call fails the write rather than clamping (fail-closed, spec §5.5).
    /// </remarks>
    /// <param name="value">The number to round.</param>
    /// <param name="digits">How many places after the point to keep, 0 to 28.</param>
    /// <returns>The rounded number.</returns>
    /// <exception cref="CelFunctionException"><paramref name="digits"/> is outside 0 to 28.</exception>
    internal static decimal RoundTo(decimal value, long digits) => digits is >= 0 and <= 28
        ? Math.Round(value, (int)digits, MidpointRounding.AwayFromZero)
        : throw new CelFunctionException("math.round", DigitsReason);
}
