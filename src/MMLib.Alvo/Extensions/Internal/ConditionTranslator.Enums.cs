using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Expressions;

using System.Globalization;
using System.Linq.Expressions;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// Enums in a <c>when</c>. CEL holds an enum field as its member's declared text, while C# compares an enum as its number,
/// so an enum converted to a number translates only where it meets a member of its own enum — never in arithmetic, an
/// ordering or against a number field, where the two would mean different things.
/// </summary>
internal sealed partial class ConditionTranslator
{
    private const string EnumNumberReason = "an enum field is its member's declared text in CEL, so it converts to a number only to compare with a member of its enum";

    private CelBinary? EnumComparison(BinaryExpression comparison, CelBinaryOperator op)
    {
        var (field, constant, type) = EnumSides(comparison);
        if (field is null)
        {
            return null;
        }

        if (!LambdaValues.TryConstant(LambdaShape.Unwrapped(constant!), out var raw, out var reason) || raw is null)
        {
            throw Refuse(constant!, reason.Length > 0 ? reason : "null is tested with == null against a field", ConstantFix);
        }

        return new CelBinary(op, Operand(field), EnumText(constant!, EnumMember(constant!, type!, raw)));
    }

    private (Expression? Field, Expression? Constant, Type? Type) EnumSides(BinaryExpression comparison)
    {
        if (LambdaShape.EnumBehind(comparison.Left) is { } left && LambdaShape.Touches(comparison.Left, _event) && !LambdaShape.Touches(comparison.Right, _event))
        {
            return (((UnaryExpression)comparison.Left).Operand, comparison.Right, left);
        }

        return LambdaShape.EnumBehind(comparison.Right) is { } right && LambdaShape.Touches(comparison.Right, _event) && !LambdaShape.Touches(comparison.Left, _event)
            ? (((UnaryExpression)comparison.Right).Operand, comparison.Left, right)
            : (null, null, null);
    }

    private CelBinary? EnumPair(BinaryExpression comparison, CelBinaryOperator op)
    {
        if (!HidesEnum(comparison.Left) || !HidesEnum(comparison.Right))
        {
            return null;
        }

        var (left, right) = (Bare(comparison.Left), Bare(comparison.Right));
        return LambdaShape.Underlying(left.Type) == LambdaShape.Underlying(right.Type)
            ? new CelBinary(op, Operand(left), Operand(right))
            : throw Refuse(comparison, "two enum fields compare only when they share one enum", TranslationRefusal.WhenFix);
    }

    private static bool HidesEnum(Expression node)
    {
        while (node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            var from = LambdaShape.Underlying(convert.Operand.Type);
            if (from.IsEnum && from != LambdaShape.Underlying(convert.Type))
            {
                return true;
            }

            node = convert.Operand;
        }

        return false;
    }

    private static Expression Bare(Expression node)
    {
        while (!LambdaShape.Underlying(node.Type).IsEnum && node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            node = convert.Operand;
        }

        return node;
    }

    private Enum EnumMember(Expression node, Type enumType, object raw)
    {
        if (raw is Enum)
        {
            return (Enum)Enum.ToObject(enumType, raw);
        }

        if (raw is not (sbyte or byte or short or ushort or int or uint or long or ulong))
        {
            throw Refuse(node, $"a {raw.GetType().Name} is not a member of {enumType.Name}", ConstantFix);
        }

        var underlying = Enum.GetUnderlyingType(enumType);
        try
        {
            return (Enum)Enum.ToObject(enumType, Convert.ChangeType(raw, underlying, CultureInfo.InvariantCulture));
        }
        catch (OverflowException)
        {
            throw Refuse(node, $"{raw} is outside the range of {enumType.Name}'s underlying {underlying.Name}", ConstantFix);
        }
    }

    private CelLiteral EnumText(Expression node, Enum member) => EnumMap.Of(member.GetType()).ValueOf(member) is { } text
        ? new CelLiteral(CelValueType.String, text)
        : throw Refuse(node, $"{member.GetType().Name} has no member {member}", TranslationRefusal.WhenFix);
}
