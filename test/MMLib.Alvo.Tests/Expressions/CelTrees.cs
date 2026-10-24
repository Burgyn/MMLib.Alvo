using CsCheck;

using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// Trees in the shape <see cref="CelParser"/> builds them — non-negative literals, untyped field references,
/// context references typed as the parser types them, every operator, <c>has</c>, <c>changed</c> and catalog calls — and
/// the structural equality that compares a call's arguments by content, which record equality over a list does not.
/// </summary>
internal static class CelTrees
{
    private static readonly string[] _fields = ["status", "total", "_x", "a1", "čaj"];
    private static readonly string[] _functions = ["startsWith", "contains", "string", "size", "math.abs", "math.greatest"];

    private static readonly Gen<CelFieldRef> _field = Gen.Select(
        Gen.OneOfConst(_fields),
        Gen.Enum<CelRecordState>(),
        (name, state) => new CelFieldRef(name, CelValueType.Null, state));

    private static readonly Gen<CelNode> _leaf = Gen.OneOf(
        Gen.Long[0, long.MaxValue].Select(number => (CelNode)new CelLiteral(CelValueType.Int, number)),
        Gen.Decimal.Select(number => (CelNode)new CelLiteral(CelValueType.Decimal, Math.Abs(number))),
        LiteralText.Any(4).Select(text => (CelNode)new CelLiteral(CelValueType.String, text)),
        Gen.Bool.Select(flag => (CelNode)new CelLiteral(CelValueType.Bool, flag)),
        _field.Select(field => (CelNode)field),
        _field.Select(field => (CelNode)new CelHas(field)),
        Gen.OneOfConst(_fields).Select(name => (CelNode)new CelChanged(name)),
        Gen.OneOfConst(
            (CelNode)new CelContextRef(CelContextValue.UserId, CelValueType.Uuid),
            new CelContextRef(CelContextValue.UserRoles, CelValueType.StringList),
            new CelContextRef(CelContextValue.TenantId, CelValueType.Uuid),
            new CelCall(CelCall.Now, [])));

    /// <summary>Trees nested at most <paramref name="depth"/> levels below their root.</summary>
    public static Gen<CelNode> Any(int depth)
    {
        var tree = _leaf;
        for (var level = 0; level < depth; level++)
        {
            tree = Composite(tree);
        }

        return tree;
    }

    /// <summary>Whether two trees are the same, comparing a call's arguments element by element.</summary>
    public static bool Same(CelNode left, CelNode right) => (left, right) switch
    {
        (CelBinary a, CelBinary b) => a.Operator == b.Operator && Same(a.Left, b.Left) && Same(a.Right, b.Right),
        (CelUnary a, CelUnary b) => a.Operator == b.Operator && Same(a.Operand, b.Operand),
        (CelCall a, CelCall b) => a.Name == b.Name && a.Arguments.Count == b.Arguments.Count
            && a.Arguments.Zip(b.Arguments).All(pair => Same(pair.First, pair.Second)),
        _ => left == right,
    };

    private static Gen<CelNode> Composite(Gen<CelNode> child) => Gen.OneOf(
        _leaf,
        Gen.Select(Gen.Enum<CelUnaryOperator>(), child, (op, operand) => (CelNode)new CelUnary(op, operand)),
        Gen.Select(Gen.Enum<CelBinaryOperator>(), child, child, (op, left, right) => (CelNode)new CelBinary(op, left, right)),
        Gen.Select(Gen.OneOfConst(_functions), child.Array[1, 3], (name, arguments) => (CelNode)new CelCall(name, arguments)));
}
