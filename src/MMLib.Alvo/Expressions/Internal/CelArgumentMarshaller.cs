namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Turns the loosely typed values a record carries (<c>int</c>/<c>long</c>/<c>decimal</c>/<c>double</c>,
/// <c>DateTimeOffset</c>/<c>DateTime</c>/<c>DateOnly</c>/text, <c>Guid</c>/text) into the CLR type a function body
/// takes. A value that does not convert reads as <see langword="null"/> — the interpreter's existing rule for a value
/// of an unexpected CLR type — and never throws.
/// </summary>
internal static class CelArgumentMarshaller
{
    /// <summary>The value as <paramref name="target"/>, or <see langword="null"/> when it is absent or does not convert.</summary>
    /// <param name="value">The evaluated argument.</param>
    /// <param name="target">The parameter's CLR type (never a <see cref="Nullable{T}"/>).</param>
    /// <returns>The converted value, boxed, or <see langword="null"/>.</returns>
    internal static object? Convert(object? value, System.Type target) => value is null ? null : target switch
    {
        _ when target == typeof(string) => value as string,
        _ when target == typeof(bool) => ToBool(value),
        _ when target == typeof(decimal) => ToDecimal(value),
        _ when target == typeof(long) => ToInteger(value),
        _ when target == typeof(int) => ToInt32(value),
        _ when target == typeof(DateTimeOffset) => ToInstant(value),
        _ when target == typeof(Guid) => ToGuid(value),
        _ => null,
    };

    /// <summary>A body's result in the representation the rest of the interpreter uses: an <c>int</c> becomes a <c>long</c>.</summary>
    /// <param name="result">What the body returned.</param>
    /// <returns>The normalised result.</returns>
    internal static object? Normalize(object? result) => result is int whole ? (long)whole : result;

    private static bool? ToBool(object value) => value is bool flag ? flag : null;

    private static decimal? ToDecimal(object value) => CelInterpreter.TryToDecimal(value, out var number) ? number : null;

    private static long? ToInteger(object value) =>
        CelInterpreter.TryToDecimal(value, out var number) && decimal.Truncate(number) == number
        && number is >= long.MinValue and <= long.MaxValue
            ? (long)number
            : null;

    private static int? ToInt32(object value) =>
        ToInteger(value) is long whole && whole is >= int.MinValue and <= int.MaxValue ? (int)whole : null;

    private static object? ToInstant(object value) => value switch
    {
        DateOnly date => (object)new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        _ => CelInterpreter.TryToDateTimeOffset(value, out var instant) ? instant : null,
    };

    private static object? ToGuid(object value) => value switch
    {
        Guid id => (object)id,
        string text when Guid.TryParse(text, out var parsed) => parsed,
        _ => null,
    };
}
