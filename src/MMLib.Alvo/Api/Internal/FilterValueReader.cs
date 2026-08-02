using MMLib.Alvo.Schema;
using System.Globalization;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Reads one caller-supplied filter operand as the CLR type the field it is compared against is carried as.
/// </summary>
/// <remarks>
/// <para>
/// <b>The target type comes from <see cref="FieldClrType"/>, which is the framework's one
/// <see cref="FieldType"/>-to-CLR mapping.</b> A third table here is the defect that type exists to make
/// unrepresentable — PR3's first pass had one in the HTTP layer and one in the EF package, and they already
/// disagreed on failure mode. It also settles the one distinction
/// <c>Expressions.CelFieldType</c> cannot: <c>date</c> and <c>datetime</c> are both
/// <c>CelValueType.Timestamp</c>, but a <c>date</c> column holds a <see cref="DateOnly"/> and a
/// <c>datetime</c> a <see cref="DateTimeOffset"/>, and binding the wrong one of the two matches nothing and
/// raises nothing.
/// </para>
/// <para>
/// <b>Parsing here is not an optimisation over letting the port do it.</b> The port's <c>ColumnValue</c>
/// converts and refuses too — deliberately, as the backstop for a caller who reaches it directly — but it
/// answers with an <c>ArgumentException</c> whose text names a column and a CLR type. Refusing at the parser
/// is what turns <c>year=gte.notanumber</c> into a structured violation with a fix suggestion, and what
/// stops a statement being composed at all.
/// </para>
/// <para>
/// Every conversion is <see cref="CultureInfo.InvariantCulture"/> and every numeric one refuses a thousands
/// separator and an exponent: a query string is a wire format, not a locale, and <c>1,000</c> is a candidate
/// list in this grammar rather than a number.
/// </para>
/// </remarks>
internal static class FilterValueReader
{
    private const NumberStyles IntegralStyles = NumberStyles.AllowLeadingSign;

    private const NumberStyles FractionalStyles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    /// <summary>
    /// An instant with no offset is read as UTC rather than as the host's local time, so the same query
    /// answers the same rows whatever timezone the server happens to run in.
    /// </summary>
    private const DateTimeStyles InstantStyles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// Reads <paramref name="raw"/> as a value of <paramref name="field"/>, or reports that the field's type
    /// cannot hold it.
    /// </summary>
    /// <param name="field">The declared field the value is compared against.</param>
    /// <param name="raw">The caller-supplied text, already URL-decoded.</param>
    /// <param name="value">The value, in the CLR type the port binds through the column.</param>
    /// <param name="violation">Why the value was refused.</param>
    internal static bool TryRead(FieldSchema field, string raw, out object? value, out AlvoViolation? violation)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(raw);

        if (ContainsNul(raw))
        {
            value = null;
            violation = QueryViolations.UnrepresentableText();
            return false;
        }

        value = Read(FieldClrType.Of(field), raw);
        violation = value is null ? QueryViolations.UnrepresentableValue(field) : null;
        return violation is null;
    }

    /// <summary>
    /// Reads a pattern operand for <c>like</c>/<c>ilike</c>. A pattern is text whatever the column holds, so
    /// it bypasses the type conversion above — but not the NUL refusal, which is about what an engine can
    /// carry rather than about what a column means.
    /// </summary>
    /// <param name="raw">The caller-supplied pattern, already URL-decoded.</param>
    /// <param name="pattern">The pattern.</param>
    /// <param name="violation">Why the pattern was refused.</param>
    internal static bool TryReadPattern(string raw, out string? pattern, out AlvoViolation? violation)
    {
        ArgumentNullException.ThrowIfNull(raw);

        pattern = ContainsNul(raw) ? null : raw;
        violation = pattern is null ? QueryViolations.UnrepresentableText() : null;
        return violation is null;
    }

    /// <summary>
    /// A NUL in a text value is refused here rather than at the engine, because the engines disagree about
    /// it: PostgreSQL's <c>UTF8</c> has no representation for one and Npgsql raises a raw provider error,
    /// while SQLite binds it and quietly answers. The port refuses it too, for a caller that reaches it
    /// directly; this refusal is the one that produces a structured violation instead.
    /// </summary>
    private static bool ContainsNul(string raw) => raw.Contains('\0', StringComparison.Ordinal);

    /// <summary>
    /// <paramref name="raw"/> as <paramref name="target"/>, or <see langword="null"/> when it cannot be read
    /// as one. A <see langword="null"/> is never a legitimate operand here — <c>is.null</c> is the only way
    /// to compare against one, and it never reaches this method — so it doubles as the failure signal.
    /// </summary>
    private static object? Read(Type target, string raw)
    {
        if (target == typeof(string))
        {
            return raw;
        }

        if (target == typeof(long))
        {
            return long.TryParse(raw, IntegralStyles, CultureInfo.InvariantCulture, out var number) ? number : null;
        }

        if (target == typeof(decimal))
        {
            return decimal.TryParse(raw, FractionalStyles, CultureInfo.InvariantCulture, out var number) ? number : null;
        }

        return ReadNonNumeric(target, raw);
    }

    private static object? ReadNonNumeric(Type target, string raw)
    {
        if (target == typeof(bool))
        {
            return ReadBoolean(raw);
        }

        if (target == typeof(Guid))
        {
            return Guid.TryParse(raw, CultureInfo.InvariantCulture, out var uuid) ? uuid : null;
        }

        if (target == typeof(DateOnly))
        {
            return DateOnly.TryParseExact(raw, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : null;
        }

        if (target == typeof(DateTimeOffset))
        {
            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, InstantStyles, out var instant)
                ? instant
                : null;
        }

        return null;
    }

    /// <summary>
    /// Only the two spellings JSON and PostgREST both use, and case-sensitively: <c>TRUE</c> is admitted by
    /// <see cref="bool.TryParse(string?, out bool)"/> along with surrounding whitespace, which would give one
    /// value several wire forms in a grammar whose every other token is ordinal.
    /// </summary>
    private static object? ReadBoolean(string raw) => raw switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };
}
