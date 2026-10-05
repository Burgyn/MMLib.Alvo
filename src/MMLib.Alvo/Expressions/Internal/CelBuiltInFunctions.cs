using System.Globalization;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>The functions every Alvo build knows, whatever the host registers.</summary>
/// <remarks>
/// Every member is an expression-bodied getter rather than a <c>static readonly</c> field: a static initializer runs
/// once per test-host process, so a mutant inside one is invisible to every test (the #244 comment), and these sets
/// are profile gates in the security core.
/// </remarks>
internal static partial class CelBuiltInFunctions
{
    /// <summary>Gets the profile set of <c>now()</c>, the one call left with its own grammar.</summary>
    internal static IReadOnlySet<CelProfile> MutateOnly => new HashSet<CelProfile> { CelProfile.Mutate };

    /// <summary>Gets the profile set of every in-process function in this slice — built-ins and host functions alike.</summary>
    internal static IReadOnlySet<CelProfile> ConditionAndMutate =>
        new HashSet<CelProfile> { CelProfile.Condition, CelProfile.Mutate };

    /// <summary>
    /// The most characters a <c>replace</c> may grow a text to: the Data API's default request body limit
    /// (<c>AlvoApiOptions.MaxRequestBodyBytes</c>, 1 MiB), the largest value a client could send in one default request.
    /// Derived, not sourced (spec X12, Q4); without it one 2,000-character replacement over a large text is an
    /// out-of-memory inside a write transaction.
    /// </summary>
    internal const int MaxTextLength = 1_048_576;

    /// <summary>The characters <c>trim</c> removes: the four the CEL lexer can escape or type (spec deviation F5).</summary>
    private const string AsciiWhitespace = " \t\n\r";

    /// <summary>Gets every built-in overload.</summary>
    internal static IReadOnlyList<CelFunction> All =>
        [
            LowerAscii, UpperAscii, Now, Replace, Trim, Size, Abs(CelValueType.Int), Abs(CelValueType.Decimal), Round(CelValueType.Int), Round(CelValueType.Decimal),
            Substring(withEnd: false), Substring(withEnd: true), Contains, StartsWith, EndsWith,
        ];

    private static CelFunction Replace => InProcess(
        "replace", CelValueType.String,
        "Replaces every occurrence of search in text with replacement, left to right, comparing characters exactly; an empty search changes nothing.",
        arguments => ReplaceText((string)arguments[0]!, (string)arguments[1]!, (string)arguments[2]!),
        Parameter("text", CelValueType.String), Parameter("search", CelValueType.String), Parameter("replacement", CelValueType.String));

    private static CelFunction Trim => InProcess(
        "trim", CelValueType.String,
        "Removes spaces, tabs, line feeds and carriage returns from both ends of text; nothing else counts as whitespace.",
        arguments => TrimText((string)arguments[0]!), Parameter("text", CelValueType.String));

    private static CelFunction Size => InProcess(
        "size", CelValueType.Int, "The number of Unicode code points in text.",
        arguments => SizeOf((string)arguments[0]!), Parameter("text", CelValueType.String));

    private static CelFunction Abs(CelValueType type) => InProcess(
        "math.abs", type, "The absolute value of x, of the same numeric type.",
        arguments => type == CelValueType.Int ? (object)AbsInt((long)arguments[0]!) : Math.Abs((decimal)arguments[0]!),
        Parameter("x", type));

    private static CelFunction Round(CelValueType type) => InProcess(
        "math.round", type, "x rounded to a whole number, halves away from zero (2.5 is 3, -2.5 is -3), of the same numeric type.",
        arguments => type == CelValueType.Int ? arguments[0] : Math.Round((decimal)arguments[0]!, MidpointRounding.AwayFromZero),
        Parameter("x", type));

    private static CelFunction LowerAscii => InProcess(
        CelCall.LowerAscii, CelValueType.String,
        "Folds A-Z to a-z and changes nothing else: accented and other non-ASCII letters stay as they are.",
        arguments => LowerAsciiText((string)arguments[0]!), Parameter("text", CelValueType.String));

    private static CelFunction UpperAscii => InProcess(
        "upperAscii", CelValueType.String,
        "Folds a-z to A-Z and changes nothing else: accented and other non-ASCII letters stay as they are.",
        arguments => UpperAsciiText((string)arguments[0]!), Parameter("text", CelValueType.String));

    private static CelFunction InProcess(
        string name, CelValueType result, string summary, Func<object?[], object?> body, params CelFunctionArgument[] parameters) =>
        new(name, parameters, result, ResultNullable: false, summary, IsHost: false, ConditionAndMutate, body);

    /// <summary><c>replace</c>: ordinal, left to right, non-overlapping; an empty search returns the text (SQL's answer).</summary>
    /// <param name="text">The text searched.</param>
    /// <param name="search">The text to find.</param>
    /// <param name="replacement">The text to put in its place.</param>
    /// <returns>The replaced text.</returns>
    /// <exception cref="CelFunctionException">The result would grow past <see cref="MaxTextLength"/>.</exception>
    internal static string ReplaceText(string text, string search, string replacement)
    {
        if (search.Length == 0)
        {
            return text;
        }

        var grown = (long)text.Length + ((long)Occurrences(text, search) * (replacement.Length - search.Length));
        if (replacement.Length > search.Length && grown > MaxTextLength)
        {
            throw new CelFunctionException("replace", string.Create(
                CultureInfo.InvariantCulture,
                $"its result would be {grown:N0} characters, over the {MaxTextLength:N0} a text may grow to here"));
        }

        return text.Replace(search, replacement, StringComparison.Ordinal);
    }

    /// <summary><c>trim</c>: the four ASCII whitespace characters from both ends, nothing else.</summary>
    /// <param name="text">The text to trim.</param>
    /// <returns>The trimmed text.</returns>
    internal static string TrimText(string text) => text.AsSpan().Trim(AsciiWhitespace).ToString();

    /// <summary><c>size</c>: Unicode code points; a lone surrogate counts as one (it enumerates as U+FFFD).</summary>
    /// <param name="text">The text to measure.</param>
    /// <returns>The number of code points.</returns>
    internal static long SizeOf(string text) => text.EnumerateRunes().Count();

    /// <summary><c>lowerAscii</c>: an explicit A–Z loop, never <c>ToLowerInvariant</c>, which folds non-ASCII letters too.</summary>
    /// <param name="text">The text to fold.</param>
    /// <returns>The folded text.</returns>
    internal static string LowerAsciiText(string text) => Fold(text, 'A', 'Z', 'a' - 'A');

    /// <summary><c>upperAscii</c>: an explicit a–z loop, never <c>ToUpperInvariant</c>.</summary>
    /// <param name="text">The text to fold.</param>
    /// <returns>The folded text.</returns>
    internal static string UpperAsciiText(string text) => Fold(text, 'a', 'z', 'A' - 'a');

    /// <summary>Shifts every character from <paramref name="from"/> to <paramref name="to"/> by <paramref name="shift"/>, and nothing else.</summary>
    /// <remarks>
    /// <b>The invariant-culture casing methods are not equivalent and must never replace this.</b> They fold every
    /// non-ASCII letter they have a mapping for — <c>Ä</c>→<c>ä</c>, <c>Σ</c>→<c>σ</c>, and <c>ẞ</c>→<c>ß</c>, which no
    /// reverse mapping recovers — and a stored value folded that way is a permanently wrong row. Which characters they
    /// fold is also a runtime and ICU detail (<c>İ</c>, U+0130, is the famous trap); a positive range is an ASCII fold
    /// on every runtime by construction.
    /// </remarks>
    private static string Fold(string text, char from, char to, int shift) =>
        string.Create(text.Length, (text, from, to, shift), static (span, state) =>
        {
            for (var index = 0; index < state.text.Length; index++)
            {
                var character = state.text[index];
                span[index] = character >= state.from && character <= state.to ? (char)(character + state.shift) : character;
            }
        });

    private static long AbsInt(long value) => value == long.MinValue
        ? throw new CelFunctionException("math.abs", "the absolute value of the smallest Int is not an Int")
        : Math.Abs(value);

    private static int Occurrences(string text, string search)
    {
        var count = 0;
        for (var at = text.IndexOf(search, StringComparison.Ordinal); at >= 0; at = text.IndexOf(search, at + search.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static CelFunction Now => new(
        CelCall.Now, [], CelValueType.Timestamp, ResultNullable: false,
        "The instant this write is stamped with — the same one its audit columns get, never a clock read.",
        IsHost: false, MutateOnly, Body: null);

    /// <summary>A non-nullable parameter of a CEL type, with the CLR type a body receives for it.</summary>
    /// <param name="name">The parameter's name.</param>
    /// <param name="type">Its CEL type.</param>
    /// <returns>The parameter.</returns>
    internal static CelFunctionArgument Parameter(string name, CelValueType type) => new(name, type, Nullable: false, ClrTypeOf(type));

    /// <summary>The CLR type a function body receives for a CEL type.</summary>
    /// <param name="type">A scalar CEL type.</param>
    /// <returns>The CLR type.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> is not a type a function parameter can have.</exception>
    internal static System.Type ClrTypeOf(CelValueType type) => type switch
    {
        CelValueType.String => typeof(string),
        CelValueType.Int => typeof(long),
        CelValueType.Decimal => typeof(decimal),
        CelValueType.Bool => typeof(bool),
        CelValueType.Timestamp => typeof(DateTimeOffset),
        CelValueType.Uuid => typeof(Guid),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No CEL function parameter has this type."),
    };
}
