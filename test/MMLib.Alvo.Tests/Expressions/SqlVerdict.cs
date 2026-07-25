using MMLib.Alvo.Data;
using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// SQL's three-valued logic: a comparison against <see langword="null"/> is neither true nor false,
/// it is <see cref="Unknown"/> — and stays that way until something (<c>COALESCE</c>, the top-level
/// <c>WHERE</c>/<c>USING</c> evaluation) collapses it.
/// </summary>
internal enum SqlTri
{
    /// <summary>The expression is definitely true.</summary>
    True,

    /// <summary>The expression is definitely false.</summary>
    False,

    /// <summary>The expression involved a <see langword="null"/> operand and could not be decided.</summary>
    Unknown,
}

/// <summary>
/// A minimal, independent three-valued evaluator for exactly the SQL grammar
/// <see cref="MMLib.Alvo.Expressions.Internal.SqlPredicateRenderer"/> emits: <c>COALESCE</c>,
/// <c>AND</c>/<c>OR</c>/<c>NOT</c>, <c>IS NOT NULL</c>, the six comparison operators,
/// <c>TRUE</c>/<c>FALSE</c>, <c>@pN</c> parameters, <c>"quoted"</c> identifiers, and
/// <c>field IN (...)</c>. It models PostgreSQL/SQLite's own <c>NULL</c>-propagating logic directly —
/// it does not call <see cref="MMLib.Alvo.Expressions.Internal.CelInterpreter"/> or reuse any of its
/// normalization code — so the differential test this backs proves something real: a bug that made
/// both backends agree by sharing logic could never show up here.
/// </summary>
/// <remarks>
/// The renderer's Computed (scalar) entry point — <c>CASE WHEN ... THEN ... ELSE ... END</c> and bare
/// arithmetic — is out of scope: the differential test only exercises
/// <see cref="MMLib.Alvo.Expressions.IPredicateRenderer.Render(CompiledExpression, MMLib.Alvo.AlvoContext, IFieldSqlRenderer)"/>
/// against <see cref="MMLib.Alvo.Expressions.Internal.CelInterpreter.EvaluatePredicate"/>, never
/// <c>EvaluateScalar</c>, so this evaluator only needs to understand the predicate grammar.
/// </remarks>
internal static class SqlVerdict
{
    /// <summary>
    /// Evaluates a rendered predicate the way a <c>WHERE</c>/<c>USING</c> clause would: the row
    /// matches only when the predicate is <see cref="SqlTri.True"/> — <see cref="SqlTri.False"/> and
    /// <see cref="SqlTri.Unknown"/> both exclude it, exactly as a real database does.
    /// </summary>
    /// <param name="predicate">The rendered predicate.</param>
    /// <param name="row">The candidate row the predicate's field references resolve against.</param>
    public static bool Evaluate(SqlPredicate predicate, AlvoRecord row)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(row);
        return EvaluateTri(predicate.Sql, row, predicate.Parameters) == SqlTri.True;
    }

    /// <summary>Evaluates raw SQL text to its three-valued verdict — exposed so the evaluator's own semantics can be pinned directly.</summary>
    /// <param name="sql">The SQL boolean expression.</param>
    /// <param name="row">The row field references resolve against.</param>
    /// <param name="parameters">The bound parameter values <c>@pN</c> references resolve against.</param>
    internal static SqlTri EvaluateTri(string sql, AlvoRecord row, IReadOnlyDictionary<string, object?> parameters)
    {
        var cursor = new SqlCursor(sql, row, parameters);
        var result = cursor.ParseTri();
        cursor.ExpectEnd();
        return result;
    }
}

/// <summary>A hand-rolled recursive-descent parser/evaluator over the renderer's small, fixed grammar.</summary>
file sealed class SqlCursor(string text, AlvoRecord row, IReadOnlyDictionary<string, object?> parameters)
{
    private static readonly string[] _comparisonOperators = [" <> ", " <= ", " >= ", " = ", " < ", " > "];

    private int _pos;

    /// <summary>Throws when the whole input was not consumed — a shape this grammar cannot produce reached the evaluator.</summary>
    public void ExpectEnd()
    {
        if (_pos != text.Length)
        {
            throw Unexpected("end of input");
        }
    }

    /// <summary>
    /// Parses a three-valued boolean expression: a literal, <c>COALESCE</c>, a parenthesized form, or
    /// (the one shape the renderer never emits standalone, but which this evaluator still understands
    /// so its own semantics can be pinned directly) a bare comparison/<c>IN</c>/field, e.g. <c>"x" = @p0</c>.
    /// </summary>
    public SqlTri ParseTri()
    {
        if (TryConsume("TRUE"))
        {
            return SqlTri.True;
        }

        if (TryConsume("FALSE"))
        {
            return SqlTri.False;
        }

        if (TryConsume("COALESCE("))
        {
            return ParseCoalesce();
        }

        return TryConsume("(") ? ParseParenthesized() : ParseValueOrComparison();
    }

    private SqlTri ParseParenthesized()
    {
        if (TryConsume("NOT "))
        {
            var inner = ParseTri();
            Expect(")");
            return SqlTriLogic.Not(inner);
        }

        if (Peek() is '"' or '@')
        {
            var value = ParseValue();
            Expect(" IS NOT NULL)");
            return value is null ? SqlTri.False : SqlTri.True;
        }

        var left = ParseTri();
        var isAnd = TryConsume(" AND ");
        if (!isAnd)
        {
            Expect(" OR ");
        }

        var right = ParseTri();
        Expect(")");
        return isAnd ? SqlTriLogic.And(left, right) : SqlTriLogic.Or(left, right);
    }

    private SqlTri ParseCoalesce()
    {
        var inner = ParseValueOrComparison();
        Expect(", ");
        var fallback = ParseTri();
        Expect(")");
        return inner == SqlTri.Unknown ? fallback : inner;
    }

    /// <summary>
    /// Parses whatever can occupy a two-valued-or-<c>UNKNOWN</c> slot without its own literal keyword:
    /// a bare value (a boolean field or parameter), a comparison, or an <c>IN (...)</c> list.
    /// </summary>
    private SqlTri ParseValueOrComparison()
    {
        var first = ParseValue();
        foreach (var op in _comparisonOperators)
        {
            if (TryConsume(op))
            {
                return SqlTriLogic.Compare(first, ParseValue(), op.Trim());
            }
        }

        if (TryConsume(" IN ("))
        {
            var values = ParseValueList();
            Expect(")");
            return SqlTriLogic.In(first, values);
        }

        return first switch
        {
            true => SqlTri.True,
            false => SqlTri.False,
            null => SqlTri.Unknown,
            _ => throw Unexpected("a boolean value"),
        };
    }

    private List<object?> ParseValueList()
    {
        var values = new List<object?> { ParseValue() };
        while (TryConsume(", "))
        {
            values.Add(ParseValue());
        }

        return values;
    }

    private object? ParseValue()
    {
        if (Peek() == '"')
        {
            return row[ParseQuotedIdent()];
        }

        if (Peek() == '@')
        {
            var name = ParseParamName();
            return parameters.TryGetValue(name, out var value) ? value : throw Unexpected($"a bound parameter named '{name}'");
        }

        if (TryConsume("TRUE"))
        {
            return true;
        }

        if (TryConsume("FALSE"))
        {
            return false;
        }

        throw Unexpected("a quoted field, an @parameter, TRUE, or FALSE");
    }

    private string ParseQuotedIdent()
    {
        Expect("\"");
        var builder = new System.Text.StringBuilder();
        while (true)
        {
            if (_pos >= text.Length)
            {
                throw Unexpected("a closing quote");
            }

            var c = text[_pos++];
            if (c != '"')
            {
                builder.Append(c);
                continue;
            }

            if (_pos < text.Length && text[_pos] == '"')
            {
                builder.Append('"');
                _pos++;
                continue;
            }

            return builder.ToString();
        }
    }

    private string ParseParamName()
    {
        Expect("@");
        var start = _pos;
        while (_pos < text.Length && char.IsLetterOrDigit(text[_pos]))
        {
            _pos++;
        }

        return text[start.._pos];
    }

    private bool TryConsume(string token)
    {
        if (_pos + token.Length > text.Length || string.CompareOrdinal(text, _pos, token, 0, token.Length) != 0)
        {
            return false;
        }

        _pos += token.Length;
        return true;
    }

    private void Expect(string token)
    {
        if (!TryConsume(token))
        {
            throw Unexpected($"'{token}'");
        }
    }

    private char Peek() => _pos < text.Length ? text[_pos] : '\0';

    private InvalidOperationException Unexpected(string what) =>
        new($"SqlVerdict expected {what} at position {_pos} of '{text}', found '{(_pos < text.Length ? text[_pos..] : "<end>")}'.");
}

/// <summary>
/// The evaluator's three-valued logic primitives, independent of parsing — exposed so this behavior
/// can be pinned directly (a bug here would otherwise only surface indirectly, through whichever SQL
/// shape happens to exercise it).
/// </summary>
internal static class SqlTriLogic
{
    /// <summary><c>NOT</c>: flips true/false, and <see cref="SqlTri.Unknown"/> stays <see cref="SqlTri.Unknown"/>.</summary>
    internal static SqlTri Not(SqlTri value) => value switch
    {
        SqlTri.True => SqlTri.False,
        SqlTri.False => SqlTri.True,
        _ => SqlTri.Unknown,
    };

    /// <summary><c>AND</c>: false is absorbing, otherwise unknown poisons the result unless both sides are true.</summary>
    internal static SqlTri And(SqlTri left, SqlTri right) =>
        left == SqlTri.False || right == SqlTri.False ? SqlTri.False :
        left == SqlTri.True && right == SqlTri.True ? SqlTri.True : SqlTri.Unknown;

    /// <summary><c>OR</c>: true is absorbing, otherwise unknown poisons the result unless both sides are false.</summary>
    internal static SqlTri Or(SqlTri left, SqlTri right) =>
        left == SqlTri.True || right == SqlTri.True ? SqlTri.True :
        left == SqlTri.False && right == SqlTri.False ? SqlTri.False : SqlTri.Unknown;

    /// <summary><c>x IN (a, b, ...)</c> as the equivalent disjunction of equalities: a <see langword="null"/> <paramref name="left"/> is unknown unless a match is found first (which cannot happen), matching real SQL.</summary>
    internal static SqlTri In(object? left, IReadOnlyList<object?> values)
    {
        if (left is null)
        {
            return SqlTri.Unknown;
        }

        var sawUnknown = false;
        foreach (var candidate in values)
        {
            var result = Compare(left, candidate, "=");
            if (result == SqlTri.True)
            {
                return SqlTri.True;
            }

            sawUnknown |= result == SqlTri.Unknown;
        }

        return sawUnknown ? SqlTri.Unknown : SqlTri.False;
    }

    /// <summary>A comparison: either operand <see langword="null"/> is <see cref="SqlTri.Unknown"/>, never true or false.</summary>
    internal static SqlTri Compare(object? left, object? right, string op)
    {
        if (left is null || right is null)
        {
            return SqlTri.Unknown;
        }

        var order = TryOrder(left, right)
            ?? throw new InvalidOperationException(
                $"SqlVerdict has no ordering rule for comparing {left} ({left.GetType()}) to {right} ({right.GetType()}).");

        var isTrue = op switch
        {
            "=" => order == 0,
            "<>" => order != 0,
            "<" => order < 0,
            "<=" => order <= 0,
            ">" => order > 0,
            ">=" => order >= 0,
            _ => throw new InvalidOperationException($"'{op}' is not a comparison operator SqlVerdict understands."),
        };

        return isTrue ? SqlTri.True : SqlTri.False;
    }

    private static int? TryOrder(object left, object right)
    {
        if (TryToDecimal(left, out var leftDecimal) && TryToDecimal(right, out var rightDecimal))
        {
            return leftDecimal.CompareTo(rightDecimal);
        }

        if (TryToInstant(left, out var leftInstant) && TryToInstant(right, out var rightInstant))
        {
            return leftInstant.CompareTo(rightInstant);
        }

        return (left, right) switch
        {
            (string l, string r) => string.CompareOrdinal(l, r),
            (bool l, bool r) => l.CompareTo(r),
            (Guid l, Guid r) => l.CompareTo(r),
            _ => null,
        };
    }

    private static bool TryToDecimal(object value, out decimal result)
    {
        switch (value)
        {
            case decimal d:
                result = d;
                return true;
            case int or long or short or byte or sbyte or ushort or uint or ulong:
                result = Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            default:
                result = default;
                return false;
        }
    }

    /// <summary>
    /// Normalizes <see cref="DateTime"/>/<see cref="DateTimeOffset"/> to a comparable instant, treating
    /// an unspecified-kind <see cref="DateTime"/> as UTC — the same convention
    /// <c>CelInterpreter</c> documents, so both sides of the differential test model the identical
    /// (deliberately chosen, machine-independent) rule rather than two arbitrary ones that happen to
    /// coincide only sometimes.
    /// </summary>
    private static bool TryToInstant(object value, out DateTimeOffset result)
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
