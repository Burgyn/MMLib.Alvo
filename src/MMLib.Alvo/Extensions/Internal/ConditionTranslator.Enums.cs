using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Expressions;

using System.Linq.Expressions;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// Enums in a <c>when</c>. CEL holds an enum field as its member's declared text, while C# compares an enum as its number,
/// so an enum field converted to a number translates only where it meets a member of its own enum — never in arithmetic,
/// an ordering or against a number field, where the two would mean different things. An enum constant converted to a
/// number is that number, as C# computes it.
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
        if (!LambdaShape.HidesEnum(comparison.Left) || !LambdaShape.HidesEnum(comparison.Right))
        {
            return null;
        }

        var (left, right) = (LambdaShape.Bare(comparison.Left), LambdaShape.Bare(comparison.Right));
        return LambdaShape.Underlying(left.Type) == LambdaShape.Underlying(right.Type)
            ? new CelBinary(op, Operand(left), Operand(right))
            : throw Refuse(comparison, "two enum fields compare only when they share one enum", TranslationRefusal.WhenFix);
    }

    private bool IsEnumField(Expression node) => LambdaShape.HidesEnum(node) && LambdaShape.Touches(node, _event);

    private Enum EnumMember(Expression node, Type enumType, object raw) =>
        EnumMap.MemberFor(enumType, raw, out var reason) ?? throw Refuse(node, reason, ConstantFix);

    private CelLiteral EnumText(Expression node, Enum member) => EnumMap.Of(member.GetType()).ValueOf(member) is { } text
        ? new CelLiteral(CelValueType.String, text)
        : throw Refuse(node, $"{member.GetType().Name} has no member {member}", TranslationRefusal.WhenFix);
}
