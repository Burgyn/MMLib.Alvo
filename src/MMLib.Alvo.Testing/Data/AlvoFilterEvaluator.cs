using MMLib.Alvo.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// Evaluates an <see cref="AlvoFilter"/> tree against a single row — <see cref="InMemoryAlvoData"/>'s
/// in-memory stand-in for the <c>WHERE</c> clause a real provider renders a caller's query filter
/// into. Applied strictly on top of the resolved policy predicate, never as a substitute for it.
/// </summary>
/// <remarks>
/// Follows SQL's three-valued logic, not two-valued boolean logic: a comparison against a
/// <see langword="null"/> field (or a comparison the evaluator cannot resolve, e.g. mismatched
/// types) is <c>UNKNOWN</c>, not <see langword="false"/> — and <c>UNKNOWN</c> propagates through
/// <see cref="AlvoAnd"/>/<see cref="AlvoOr"/>/<see cref="AlvoNot"/> exactly as SQL's <c>AND</c>/
/// <c>OR</c>/<c>NOT</c> do, only collapsing to "excluded" at the very end — <see cref="Matches"/>
/// includes a row only when the tree resolves to exactly <see langword="true"/>. This is what
/// keeps <c>neq</c> from matching a <see langword="null"/> field (SQL's <c>&lt;&gt;</c> yields
/// <c>UNKNOWN</c> there, never a match) and keeps <c>not(eq(...))</c> over a <see langword="null"/>
/// field from flipping into a match the way naive boolean negation would.
/// </remarks>
internal static class AlvoFilterEvaluator
{
    /// <summary>Answers whether <paramref name="record"/> matches <paramref name="filter"/>.</summary>
    /// <param name="filter">The filter to evaluate, or <see langword="null"/> to match every row.</param>
    /// <param name="record">The candidate row.</param>
    public static bool Matches(AlvoFilter? filter, AlvoRecord record) => Evaluate(filter, record) == true;

    private static bool? Evaluate(AlvoFilter? filter, AlvoRecord record) => filter switch
    {
        null => true,
        AlvoComparison comparison => Compare(record[comparison.Field], comparison.Operator, comparison.Value),
        AlvoAnd and => EvaluateAnd(and.Filters, record),
        AlvoOr or => EvaluateOr(or.Filters, record),
        AlvoNot not => Negate(Evaluate(not.Filter, record)),
        _ => false,
    };

    private static bool? EvaluateAnd(IReadOnlyList<AlvoFilter> filters, AlvoRecord record)
    {
        bool? result = true;
        foreach (var filter in filters)
        {
            result = And(result, Evaluate(filter, record));
        }

        return result;
    }

    private static bool? EvaluateOr(IReadOnlyList<AlvoFilter> filters, AlvoRecord record)
    {
        bool? result = false;
        foreach (var filter in filters)
        {
            result = Or(result, Evaluate(filter, record));
        }

        return result;
    }

    /// <summary>SQL's three-valued <c>AND</c>: <see langword="false"/> is absorbing; otherwise an unknown propagates.</summary>
    private static bool? And(bool? left, bool? right)
    {
        if (left == false || right == false)
        {
            return false;
        }

        return left is null || right is null ? null : true;
    }

    /// <summary>SQL's three-valued <c>OR</c>: <see langword="true"/> is absorbing; otherwise an unknown propagates.</summary>
    private static bool? Or(bool? left, bool? right)
    {
        if (left == true || right == true)
        {
            return true;
        }

        return left is null || right is null ? null : false;
    }

    /// <summary>SQL's three-valued <c>NOT</c>: <c>NOT UNKNOWN</c> is <c>UNKNOWN</c>, never a match.</summary>
    private static bool? Negate(bool? value) => value switch
    {
        true => false,
        false => true,
        null => null,
    };

    private static bool? Compare(object? fieldValue, AlvoFilterOperator op, object? operand) => op switch
    {
        AlvoFilterOperator.Eq => NullSafeEquality(fieldValue, operand, negate: false),
        AlvoFilterOperator.Neq => NullSafeEquality(fieldValue, operand, negate: true),
        AlvoFilterOperator.Gt => NullSafeOrder(fieldValue, operand, comparison => comparison > 0),
        AlvoFilterOperator.Gte => NullSafeOrder(fieldValue, operand, comparison => comparison >= 0),
        AlvoFilterOperator.Lt => NullSafeOrder(fieldValue, operand, comparison => comparison < 0),
        AlvoFilterOperator.Lte => NullSafeOrder(fieldValue, operand, comparison => comparison <= 0),
        AlvoFilterOperator.Like => Like(fieldValue, operand, ignoreCase: false),
        AlvoFilterOperator.ILike => Like(fieldValue, operand, ignoreCase: true),
        AlvoFilterOperator.In => In(fieldValue, operand),
        AlvoFilterOperator.Is => Is(fieldValue, operand),
        _ => false,
    };

    /// <summary>
    /// <c>eq</c>/<c>neq</c>: <see langword="null"/> on either side, or a pairing that cannot be
    /// normalized, is <c>UNKNOWN</c> — never a match either way. Use <see cref="Is"/> to test null.
    /// </summary>
    private static bool? NullSafeEquality(object? left, object? right, bool negate)
    {
        if (left is null || right is null || !TryNormalize(left, right, out var normalizedLeft, out var normalizedRight))
        {
            return null;
        }

        var equal = normalizedLeft.Equals(normalizedRight);
        return negate ? !equal : equal;
    }

    private static bool? NullSafeOrder(object? left, object? right, Func<int, bool> predicate)
    {
        if (left is null || right is null || !TryNormalize(left, right, out var normalizedLeft, out var normalizedRight))
        {
            return null;
        }

        var comparison = Order(normalizedLeft, normalizedRight);
        return comparison is int value ? predicate(value) : null;
    }

    private static int? Order(object left, object right) => (left, right) switch
    {
        (decimal l, decimal r) => l.CompareTo(r),
        (string l, string r) => string.CompareOrdinal(l, r),
        (DateTimeOffset l, DateTimeOffset r) => l.CompareTo(r),
        _ => null,
    };

    /// <summary>
    /// A pattern match against <see langword="null"/> is <c>UNKNOWN</c>, matching SQL's own
    /// <c>LIKE</c>. The pattern is caller-controlled (it reaches here straight off a query string in
    /// PR3), so the translated regex runs under <see cref="RegexOptions.NonBacktracking"/> — no
    /// construct this translation ever emits needs backtracking, and this rules out a ReDoS from a
    /// pattern like a long run of <c>%</c> wildcards.
    /// </summary>
    private static bool? Like(object? fieldValue, object? pattern, bool ignoreCase)
    {
        if (fieldValue is not string text || pattern is not string patternText)
        {
            return null;
        }

        var regex = "^" + Regex.Escape(patternText)
            .Replace("%", ".*", StringComparison.Ordinal)
            .Replace("_", ".", StringComparison.Ordinal) + "$";
        var options = RegexOptions.NonBacktracking | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        return Regex.IsMatch(text, regex, options);
    }

    /// <summary>
    /// List membership. A <see langword="null"/> field is <c>UNKNOWN</c>, matching SQL's <c>IN</c>.
    /// A <see cref="string"/> operand is excluded rather than treated as a character sequence —
    /// <see cref="string"/> itself satisfies <see cref="System.Collections.IEnumerable"/>, which
    /// would otherwise silently test membership against the string's individual characters.
    /// </summary>
    private static bool? In(object? fieldValue, object? operand)
    {
        if (fieldValue is null)
        {
            return null;
        }

        if (operand is string || operand is not System.Collections.IEnumerable values)
        {
            return null;
        }

        var sawUnresolved = false;
        foreach (var candidate in values)
        {
            switch (NullSafeEquality(fieldValue, candidate, negate: false))
            {
                case true:
                    return true;
                case null:
                    sawUnresolved = true;
                    break;
            }
        }

        return sawUnresolved ? null : false;
    }

    /// <summary>
    /// The dedicated null/boolean identity test — unlike every other operator here, this always
    /// resolves to a definite <see langword="true"/>/<see langword="false"/>, exactly as SQL's
    /// <c>IS NULL</c>/<c>IS NOT NULL</c>/<c>IS TRUE</c>/<c>IS FALSE</c> never yield <c>UNKNOWN</c>.
    /// </summary>
    private static bool Is(object? fieldValue, object? operand) => operand switch
    {
        null => fieldValue is null,
        bool expected => fieldValue is bool actual && actual == expected,
        _ => false,
    };

    private static bool TryNormalize(object left, object right, out object normalizedLeft, out object normalizedRight)
    {
        if (IsNumeric(left) && IsNumeric(right))
        {
            return TryNormalizeNumeric(left, right, out normalizedLeft, out normalizedRight);
        }

        if (left is Guid leftGuid && right is string rightText && Guid.TryParse(rightText, out var parsedRight))
        {
            normalizedLeft = leftGuid;
            normalizedRight = parsedRight;
            return true;
        }

        if (right is Guid rightGuid && left is string leftText && Guid.TryParse(leftText, out var parsedLeft))
        {
            normalizedLeft = parsedLeft;
            normalizedRight = rightGuid;
            return true;
        }

        if (TryToDateTimeOffset(left, out var leftOffset) && TryToDateTimeOffset(right, out var rightOffset))
        {
            normalizedLeft = leftOffset;
            normalizedRight = rightOffset;
            return true;
        }

        if (left.GetType() == right.GetType())
        {
            normalizedLeft = left;
            normalizedRight = right;
            return true;
        }

        normalizedLeft = left;
        normalizedRight = right;
        return false;
    }

    private static bool TryNormalizeNumeric(object left, object right, out object normalizedLeft, out object normalizedRight)
    {
        if (TryToDecimal(left, out var leftDecimal) && TryToDecimal(right, out var rightDecimal))
        {
            normalizedLeft = leftDecimal;
            normalizedRight = rightDecimal;
            return true;
        }

        normalizedLeft = left;
        normalizedRight = right;
        return false;
    }

    private static bool IsNumeric(object value) => value is
        int or long or short or byte or sbyte or ushort or uint or ulong or float or double or decimal;

    /// <summary>
    /// Converts a numeric value to <see langword="decimal"/> for cross-type comparison. A
    /// <see langword="double"/>/<see langword="float"/> outside <see langword="decimal"/>'s range
    /// (or <c>NaN</c>/infinity) fails rather than throwing <see cref="OverflowException"/> — a
    /// caller-supplied filter value must never crash the query, only fail to match.
    /// </summary>
    private static bool TryToDecimal(object value, out decimal result)
    {
        switch (value)
        {
            case decimal d:
                result = d;
                return true;
            case int or long or short or byte or sbyte or ushort or uint or ulong:
                result = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return true;
            case double or float:
                return TryDoubleToDecimal(Convert.ToDouble(value, CultureInfo.InvariantCulture), out result);
            default:
                result = default;
                return false;
        }
    }

    private static bool TryDoubleToDecimal(double value, out decimal result)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= (double)decimal.MinValue || value >= (double)decimal.MaxValue)
        {
            result = default;
            return false;
        }

        try
        {
            result = (decimal)value;
            return true;
        }
        catch (OverflowException)
        {
            result = default;
            return false;
        }
    }

    private static bool TryToDateTimeOffset(object value, out DateTimeOffset result)
    {
        switch (value)
        {
            case DateTimeOffset dto:
                result = dto;
                return true;
            case DateTime dt:
                result = dt.Kind == DateTimeKind.Unspecified
                    ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc))
                    : new DateTimeOffset(dt);
                return true;
            default:
                result = default;
                return false;
        }
    }
}
