using System.Globalization;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Arithmetic as a hook condition or a mutate value evaluates it (spec §5.6, D-7): CEL's semantics — Int stays Int and
/// '/' truncates toward zero; an overflow or a division by zero is an error — and the error fails closed as a function
/// failure does (rollback, <c>function-failed</c>), never as <see langword="null"/>. Computed never reaches this class:
/// a generated column must never make a write crash.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately a second arithmetic path, not a flag inside the first.</b> <see cref="CelInterpreter"/>'s own
/// arithmetic is Computed's: every operand pair on the <see cref="decimal"/> path, and every failure a
/// <see langword="null"/> — the semantics its SQL rendering must agree with. This class is the hook profiles': Int and
/// Decimal paths, and every failure a <see cref="CelFunctionException"/>. The two overlap in the decimal operators and
/// in recognising a number, and that overlap is the price of keeping one semantics per path: merging them back would put
/// a security-relevant branch (throw or answer null) inside the code a generated column runs, where a later edit could
/// flip it for both. Preflight F7-3 recorded the choice.
/// </para>
/// <para>
/// <see cref="FailsClosed"/> is the one place a profile is mapped to these semantics; the interpreter's flag and the
/// type checker's literal-zero-divisor refusal both read it.
/// </para>
/// </remarks>
internal static class CelHookArithmetic
{
    private const string IntRange = "the result is outside the range of an Int";
    private const string DecimalRange = "the result is outside the range of a Decimal";
    private const string ZeroDivisor = "the divisor is zero";

    /// <summary>
    /// Whether arithmetic in <paramref name="profile"/> fails closed: a hook condition and a mutate value, the two
    /// interpreter-only slots of a write (spec §5.6). Rule, Computed and Access keep answering <see langword="null"/>.
    /// </summary>
    /// <param name="profile">The expression's profile.</param>
    /// <returns><see langword="true"/> for <see cref="CelProfile.Condition"/> and <see cref="CelProfile.Mutate"/>.</returns>
    internal static bool FailsClosed(CelProfile profile) => profile is CelProfile.Condition or CelProfile.Mutate;

    /// <summary>
    /// Applies <c>+ - * /</c>. Two integral operands take the checked 64-bit path; any other two numbers the
    /// <see cref="decimal"/> path; a null or non-numeric operand answers <see langword="null"/>, as everywhere else.
    /// </summary>
    /// <param name="op">The operator.</param>
    /// <param name="left">The left value.</param>
    /// <param name="right">The right value.</param>
    /// <returns>A <see cref="long"/>, a <see cref="decimal"/>, or <see langword="null"/>.</returns>
    /// <exception cref="CelFunctionException">The result overflows, or the divisor is zero.</exception>
    internal static object? Apply(CelBinaryOperator op, object? left, object? right)
    {
        if (TryInt(left, out var leftInt) && TryInt(right, out var rightInt))
        {
            return ApplyInt(op, leftInt, rightInt);
        }

        return left is not null && right is not null
            && CelInterpreter.TryToDecimal(left, out var leftDecimal) && CelInterpreter.TryToDecimal(right, out var rightDecimal)
                ? ApplyDecimal(op, leftDecimal, rightDecimal)
                : null;
    }

    /// <summary>Unary minus. The smallest Int has no negation; a decimal always has one.</summary>
    /// <param name="value">The operand.</param>
    /// <returns>The negated value, or <see langword="null"/>.</returns>
    /// <exception cref="CelFunctionException">The operand is the smallest Int.</exception>
    internal static object? Negate(object? value)
    {
        if (TryInt(value, out var number))
        {
            return number == long.MinValue ? throw new CelFunctionException("-_", IntRange) : -number;
        }

        return value is not null && CelInterpreter.TryToDecimal(value, out var amount) ? -amount : null;
    }

    /// <summary>Checked 64-bit arithmetic; <c>/</c> truncates toward zero, as C#'s and CEL's do.</summary>
    private static long ApplyInt(CelBinaryOperator op, long left, long right)
    {
        try
        {
            return op switch
            {
                CelBinaryOperator.Add => checked(left + right),
                CelBinaryOperator.Subtract => checked(left - right),
                CelBinaryOperator.Multiply => checked(left * right),
                CelBinaryOperator.Divide => right == 0 ? throw Failure(op, ZeroDivisor) : checked(left / right),
                _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Not an arithmetic operator."),
            };
        }
        catch (OverflowException)
        {
            throw Failure(op, IntRange);
        }
    }

    /// <summary><see cref="decimal"/> arithmetic, which raises <see cref="OverflowException"/> past its range.</summary>
    private static decimal ApplyDecimal(CelBinaryOperator op, decimal left, decimal right)
    {
        try
        {
            return op switch
            {
                CelBinaryOperator.Add => left + right,
                CelBinaryOperator.Subtract => left - right,
                CelBinaryOperator.Multiply => left * right,
                CelBinaryOperator.Divide => right == 0m ? throw Failure(op, ZeroDivisor) : left / right,
                _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Not an arithmetic operator."),
            };
        }
        catch (OverflowException)
        {
            throw Failure(op, DecimalRange);
        }
    }

    /// <summary>An integral CLR value as an Int (a record's Integer column, an Int literal); anything else is not one.</summary>
    private static bool TryInt(object? value, out long number)
    {
        switch (value)
        {
            case long whole:
                number = whole;
                return true;
            case int or short or byte or sbyte or ushort or uint:
                number = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                return true;
            default:
                number = 0;
                return false;
        }
    }

    /// <summary>The failure, named by CEL's own overload name for the operator (<c>_/_</c>), as spec §5.5 words it.</summary>
    private static CelFunctionException Failure(CelBinaryOperator op, string reason) => new(op switch
    {
        CelBinaryOperator.Add => "_+_",
        CelBinaryOperator.Subtract => "_-_",
        CelBinaryOperator.Multiply => "_*_",
        _ => "_/_",
    }, reason);
}
