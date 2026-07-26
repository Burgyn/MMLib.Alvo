using MMLib.Alvo.Data;
using System.Globalization;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// Evaluates an <see cref="AlvoFilter"/> tree against a single row — <see cref="InMemoryAlvoData"/>'s
/// in-memory stand-in for the <c>WHERE</c> clause a real provider renders a caller's query filter
/// into. Applied strictly on top of the resolved policy predicate, never as a substitute for it.
/// </summary>
internal static class AlvoFilterEvaluator
{
    /// <summary>Answers whether <paramref name="record"/> matches <paramref name="filter"/>.</summary>
    /// <param name="filter">The filter to evaluate, or <see langword="null"/> to match every row.</param>
    /// <param name="record">The candidate row.</param>
    public static bool Matches(AlvoFilter? filter, AlvoRecord record) => filter switch
    {
        null => true,
        AlvoComparison comparison => Compare(record[comparison.Field], comparison.Operator, comparison.Value),
        AlvoAnd and => and.Filters.All(nested => Matches(nested, record)),
        AlvoOr or => or.Filters.Any(nested => Matches(nested, record)),
        AlvoNot not => !Matches(not.Filter, record),
        _ => false,
    };

    private static bool Compare(object? fieldValue, AlvoFilterOperator op, object? operand) => op switch
    {
        AlvoFilterOperator.Eq => ValuesEqual(fieldValue, operand),
        AlvoFilterOperator.Neq => !ValuesEqual(fieldValue, operand),
        AlvoFilterOperator.Gt => Order(fieldValue, operand) is > 0,
        AlvoFilterOperator.Gte => Order(fieldValue, operand) is >= 0,
        AlvoFilterOperator.Lt => Order(fieldValue, operand) is < 0,
        AlvoFilterOperator.Lte => Order(fieldValue, operand) is <= 0,
        AlvoFilterOperator.Like => Like(fieldValue, operand, ignoreCase: false),
        AlvoFilterOperator.ILike => Like(fieldValue, operand, ignoreCase: true),
        AlvoFilterOperator.In => In(fieldValue, operand),
        AlvoFilterOperator.Is => Is(fieldValue, operand),
        _ => false,
    };

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return TryNormalize(left, right, out var normalizedLeft, out var normalizedRight)
            && normalizedLeft.Equals(normalizedRight);
    }

    private static int? Order(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return null;
        }

        if (!TryNormalize(left, right, out var normalizedLeft, out var normalizedRight))
        {
            return null;
        }

        return (normalizedLeft, normalizedRight) switch
        {
            (decimal l, decimal r) => l.CompareTo(r),
            (string l, string r) => string.CompareOrdinal(l, r),
            (DateTimeOffset l, DateTimeOffset r) => l.CompareTo(r),
            _ => null,
        };
    }

    private static bool Like(object? fieldValue, object? pattern, bool ignoreCase)
    {
        if (fieldValue is not string text || pattern is not string patternText)
        {
            return false;
        }

        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(patternText)
            .Replace("%", ".*", StringComparison.Ordinal)
            .Replace("_", ".", StringComparison.Ordinal) + "$";
        var options = ignoreCase
            ? System.Text.RegularExpressions.RegexOptions.IgnoreCase
            : System.Text.RegularExpressions.RegexOptions.None;
        return System.Text.RegularExpressions.Regex.IsMatch(text, regex, options);
    }

    private static bool In(object? fieldValue, object? operand)
    {
        if (fieldValue is null || operand is not System.Collections.IEnumerable values)
        {
            return false;
        }

        foreach (var candidate in values)
        {
            if (ValuesEqual(fieldValue, candidate))
            {
                return true;
            }
        }

        return false;
    }

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
            normalizedLeft = Convert.ToDecimal(left, CultureInfo.InvariantCulture);
            normalizedRight = Convert.ToDecimal(right, CultureInfo.InvariantCulture);
            return true;
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

    private static bool IsNumeric(object value) => value is
        int or long or short or byte or sbyte or ushort or uint or ulong or float or double or decimal;

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
