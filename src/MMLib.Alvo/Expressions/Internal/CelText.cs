using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// The one writer of CEL text: a tree as the canonical source the parser reads back, and every string literal through
/// one escaping rule — the exact inverse of <see cref="CelLexer"/>'s escapes — so parsing what it writes gives back
/// what it was given.
/// </summary>
/// <remarks>
/// <para>
/// Canonical spelling: one space around each binary operator, a call's arguments separated by a comma and a space, and
/// parentheses only where precedence needs them — around a child that binds looser than its parent, around a right
/// operand of equal precedence, which the parser would otherwise regroup to the left, and around either operand of a
/// comparison that is itself a comparison, because comparisons do not associate. Two spellings of one condition are
/// therefore one text, which is what lets a typed registration and an untyped one show the same condition.
/// </para>
/// <para>
/// Names are the one part of the text no literal escape covers, so the writer refuses a field, <c>changed()</c> or call
/// name it cannot spell rather than letting it read back as a different tree.
/// </para>
/// <para>
/// A literal is spelled by its type and value together — a String holding a string, an Int holding a long, a Decimal
/// holding a decimal, a Bool holding a bool — and any other pairing is refused. CEL has no negative number token: a
/// negative literal is written <c>-n</c> and reads back as <c>CelUnary(Negate, n)</c>, the same meaning in a different
/// shape. A translator should therefore emit a negation over a non-negative literal, or accept that shape back.
/// </para>
/// </remarks>
internal static class CelText
{
    private const int OrLevel = 1;
    private const int AndLevel = 2;
    private const int RelationLevel = 3;
    private const int AdditiveLevel = 4;
    private const int MultiplicativeLevel = 5;
    private const int UnaryLevel = 6;
    private const int PrimaryLevel = 7;

    private static readonly HashSet<string> _reserved = new(StringComparer.Ordinal) { "old", "new", "true", "false", "null", "in", "has" };

    /// <summary>The canonical text of <paramref name="node"/>.</summary>
    /// <param name="node">A tree of literals, field and context references, operators, <c>has</c>, <c>changed</c> and calls.</param>
    /// <returns>The text <see cref="CelParser"/> reads back as the same tree.</returns>
    /// <exception cref="ArgumentException">The tree holds a name, a literal or a node kind with no canonical spelling.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The tree holds <see cref="long.MinValue"/>.</exception>
    internal static string Write(CelNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return Text(node);
    }

    /// <summary>A single-quoted CEL string literal holding exactly <paramref name="value"/>.</summary>
    /// <param name="value">Any text; a quote, a backslash, a line feed, a carriage return and a tab are escaped.</param>
    /// <returns>The literal.</returns>
    internal static string StringLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var character in value)
        {
            AppendEscaped(builder, character);
        }

        return builder.Append('\'').ToString();
    }

    /// <summary>
    /// Whether <paramref name="name"/> can stand as a field in CEL text: the lexer's identifier grammar — a letter or
    /// <c>_</c> first, then letters, digits and <c>_</c> — and neither an image nor a keyword.
    /// </summary>
    /// <param name="name">A field name.</param>
    /// <returns><see langword="true"/> when it reads back as the same field.</returns>
    internal static bool IsFieldName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.Length > 0
            && (char.IsLetter(name[0]) || name[0] == '_')
            && name.All(character => char.IsLetterOrDigit(character) || character == '_')
            && !_reserved.Contains(name);
    }

    private static bool IsCallName(string name)
    {
        var segments = name.Split('.');

        return segments.Length <= 2 && segments.All(IsFieldName) && name != "changed";
    }

    private static string Name(string name) => IsFieldName(name)
        ? name
        : throw new ArgumentException(
            $"'{name}' is not a CEL field name: it must start with a letter or '_', hold only letters, digits and '_', and be neither old, new nor a keyword.",
            nameof(name));

    private static string CallName(string name) => IsCallName(name)
        ? name
        : throw new ArgumentException($"'{name}' is not a CEL function name that reads back as the same call.", nameof(name));

    private static StringBuilder AppendEscaped(StringBuilder builder, char character) => character switch
    {
        '\\' => builder.Append(@"\\"),
        '\'' => builder.Append(@"\'"),
        '\n' => builder.Append(@"\n"),
        '\r' => builder.Append(@"\r"),
        '\t' => builder.Append(@"\t"),
        _ => builder.Append(character),
    };

    private static string Text(CelNode node)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();

        return Spelled(node);
    }

    private static string Spelled(CelNode node) => node switch
    {
        CelLiteral literal => Literal(literal),
        CelFieldRef field => Field(field),
        CelContextRef context => Context(context.Value),
        CelHas has => $"has({Field(has.Field)})",
        CelChanged changed => $"changed({Name(changed.FieldName)})",
        CelCall call => $"{CallName(call.Name)}({string.Join(", ", call.Arguments.Select(Text))})",
        CelUnary unary => Unary(unary),
        CelBinary binary => Binary(binary),
        _ => throw new ArgumentException($"A {node.GetType().Name} has no canonical text here.", nameof(node)),
    };

    private static string Unary(CelUnary unary) =>
        UnaryOperator(unary.Operator) + Child(unary.Operand, UnaryLevel, isRight: false);

    private static string UnaryOperator(CelUnaryOperator op) => op switch
    {
        CelUnaryOperator.Not => "!",
        CelUnaryOperator.Negate => "-",
        _ => throw new ArgumentException($"The operator {op} has no CEL spelling.", nameof(op)),
    };

    private static string Binary(CelBinary binary)
    {
        var level = Level(binary);

        return $"{Child(binary.Left, level, isRight: false)} {Operator(binary.Operator)} {Child(binary.Right, level, isRight: true)}";
    }

    private static string Child(CelNode child, int parentLevel, bool isRight)
    {
        var text = Text(child);

        return NeedsParentheses(Level(child), parentLevel, isRight) ? $"({text})" : text;
    }

    private static bool NeedsParentheses(int level, int parentLevel, bool isRight) =>
        level < parentLevel || (level == parentLevel && (isRight || parentLevel == RelationLevel));

    private static int Level(CelNode node) => node switch
    {
        CelBinary { Operator: CelBinaryOperator.Or } => OrLevel,
        CelBinary { Operator: CelBinaryOperator.And } => AndLevel,
        CelBinary { Operator: CelBinaryOperator.Add or CelBinaryOperator.Subtract } => AdditiveLevel,
        CelBinary { Operator: CelBinaryOperator.Multiply or CelBinaryOperator.Divide } => MultiplicativeLevel,
        CelBinary => RelationLevel,
        CelUnary or CelLiteral { Value: < 0L } or CelLiteral { Value: < 0m } => UnaryLevel,
        _ => PrimaryLevel,
    };

    private static string Literal(CelLiteral literal) => (literal.Type, literal.Value) switch
    {
        (CelValueType.String, string text) => StringLiteral(text),
        (CelValueType.Bool, bool flag) => flag ? "true" : "false",
        (CelValueType.Int, long.MinValue) => throw new ArgumentOutOfRangeException(nameof(literal), "long.MinValue has no CEL spelling: its magnitude is not an Int."),
        (CelValueType.Int, long number) => number.ToString(CultureInfo.InvariantCulture),
        (CelValueType.Decimal, decimal number) => DecimalText(number),
        _ => throw new ArgumentException(
            $"A {literal.Type} literal holding {literal.Value?.GetType().Name ?? "null"} has no CEL spelling here.",
            nameof(literal)),
    };

    private static string DecimalText(decimal number)
    {
        var text = number.ToString(CultureInfo.InvariantCulture);

        return text.Contains('.', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static string Field(CelFieldRef field) => field.State switch
    {
        CelRecordState.New => "new." + Name(field.FieldName),
        CelRecordState.Old => "old." + Name(field.FieldName),
        _ => Name(field.FieldName),
    };

    private static string Context(CelContextValue value) => value switch
    {
        CelContextValue.UserId => "@user.id",
        CelContextValue.UserRoles => "@user.roles",
        CelContextValue.TenantId => "@tenant.id",
        _ => throw new ArgumentException($"The context value {value} has no CEL spelling.", nameof(value)),
    };

    private static string Operator(CelBinaryOperator op) => op switch
    {
        CelBinaryOperator.Equal => "==",
        CelBinaryOperator.NotEqual => "!=",
        CelBinaryOperator.Less => "<",
        CelBinaryOperator.LessOrEqual => "<=",
        CelBinaryOperator.Greater => ">",
        CelBinaryOperator.GreaterOrEqual => ">=",
        CelBinaryOperator.And => "&&",
        CelBinaryOperator.Or => "||",
        CelBinaryOperator.In => "in",
        CelBinaryOperator.Add => "+",
        CelBinaryOperator.Subtract => "-",
        CelBinaryOperator.Multiply => "*",
        CelBinaryOperator.Divide => "/",
        _ => throw new ArgumentException($"The operator {op} has no CEL spelling.", nameof(op)),
    };
}
