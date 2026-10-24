using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

using System.Collections.Frozen;
using System.Linq.Expressions;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// Translates a typed handler's <c>when</c> into the CEL text the untyped core compiles (spec 2026-10-14 §4.3), once, at
/// registration — so the boot, the catalog and the apply guard see exactly what a CEL-string registration gives them.
/// </summary>
/// <remarks>
/// The walk builds <see cref="CelNode"/>s only — every literal through <see cref="CelText"/>'s one escaping rule, every
/// name checked as a CEL identifier — and the text is parsed back once, so a lambda too long or too deep for the parser
/// is refused here, never at boot. The members that read calls and constants live in <c>ConditionTranslator.Calls.cs</c>.
/// </remarks>
internal sealed partial class ConditionTranslator
{
    private static readonly FrozenDictionary<ExpressionType, CelBinaryOperator> _comparisons = new Dictionary<ExpressionType, CelBinaryOperator>
    {
        [ExpressionType.Equal] = CelBinaryOperator.Equal,
        [ExpressionType.NotEqual] = CelBinaryOperator.NotEqual,
        [ExpressionType.LessThan] = CelBinaryOperator.Less,
        [ExpressionType.LessThanOrEqual] = CelBinaryOperator.LessOrEqual,
        [ExpressionType.GreaterThan] = CelBinaryOperator.Greater,
        [ExpressionType.GreaterThanOrEqual] = CelBinaryOperator.GreaterOrEqual,
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<ExpressionType, CelBinaryOperator> _arithmetic = new Dictionary<ExpressionType, CelBinaryOperator>
    {
        [ExpressionType.Add] = CelBinaryOperator.Add,
        [ExpressionType.AddChecked] = CelBinaryOperator.Add,
        [ExpressionType.Subtract] = CelBinaryOperator.Subtract,
        [ExpressionType.SubtractChecked] = CelBinaryOperator.Subtract,
        [ExpressionType.Multiply] = CelBinaryOperator.Multiply,
        [ExpressionType.MultiplyChecked] = CelBinaryOperator.Multiply,
        [ExpressionType.Divide] = CelBinaryOperator.Divide,
    }.ToFrozenDictionary();

    private readonly ParameterExpression _event;
    private readonly string _id;
    private readonly HandlerVerb _verb;
    private readonly RecordShape _shape;

    private ConditionTranslator(ParameterExpression @event, string id, HandlerVerb verb, RecordShape shape) =>
        (_event, _id, _verb, _shape) = (@event, id, verb, shape);

    internal static string Translate<T>(Expression<Func<EventCondition<T>, bool>> when, string id, HandlerVerb verb)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(when);
        var translator = new ConditionTranslator(when.Parameters[0], id, verb, RecordShape.Of(typeof(T)));
        var text = translator.Write(when.Body);
        if (text.Length > CelParser.MaxSourceLength)
        {
            throw translator.Refuse(when.Body, $"it translates to {text.Length} characters of CEL, past the {CelParser.MaxSourceLength} a condition may hold", "shorten it, or move part of the check into the handler body.");
        }

        translator.EnsureParses(when.Body, text);
        return text;
    }

    private string Write(Expression body)
    {
        var tree = Predicate(body);
        try
        {
            return CelText.Write(tree);
        }
        catch (ArgumentException failure)
        {
            throw Refuse(body, $"its CEL cannot be written: {failure.Message}", TranslationRefusal.WhenFix);
        }
    }

    private void EnsureParses(Expression body, string text)
    {
        try
        {
            CelParser.Parse(text);
        }
        catch (CelSyntaxException failure)
        {
            throw Refuse(body, $"its CEL does not parse back: {failure.Message}", "split the condition, or move part of the check into the handler body.");
        }
    }

    private CelNode Predicate(Expression node) => node switch
    {
        BinaryExpression { NodeType: ExpressionType.AndAlso } both => new CelBinary(CelBinaryOperator.And, Predicate(both.Left), Predicate(both.Right)),
        BinaryExpression { NodeType: ExpressionType.OrElse } either => new CelBinary(CelBinaryOperator.Or, Predicate(either.Left), Predicate(either.Right)),
        UnaryExpression { NodeType: ExpressionType.Not } not => new CelUnary(CelUnaryOperator.Not, Predicate(not.Operand)),
        BinaryExpression comparison when _comparisons.TryGetValue(comparison.NodeType, out var op) => Comparison(comparison, op),
        MethodCallExpression call => Call(call),
        MemberExpression { Member.Name: "HasValue" } has when LambdaShape.IsNullable(has.Expression) => new CelHas(Field(has.Expression!)),
        _ => Operand(node),
    };

    private CelNode Comparison(BinaryExpression comparison, CelBinaryOperator op) =>
        NullTest(comparison, op) ?? EnumComparison(comparison, op) ?? Typed(comparison, op, Operand(comparison.Left), Operand(comparison.Right));

    private CelNode? NullTest(BinaryExpression comparison, CelBinaryOperator op)
    {
        var tested = IsNull(comparison.Right) ? comparison.Left : IsNull(comparison.Left) ? comparison.Right : null;
        if (tested is null)
        {
            return null;
        }

        if (op is not (CelBinaryOperator.Equal or CelBinaryOperator.NotEqual))
        {
            throw Refuse(comparison, "only == and != can test for null", TranslationRefusal.WhenFix);
        }

        var has = new CelHas(Field(tested));
        return op == CelBinaryOperator.NotEqual ? has : new CelUnary(CelUnaryOperator.Not, has);
    }

    private static bool IsNull(Expression node) => LambdaShape.Unwrapped(node) is ConstantExpression { Value: null };

    private CelBinary? EnumComparison(BinaryExpression comparison, CelBinaryOperator op)
    {
        var (field, constant, type) = EnumSides(comparison);
        if (field is null)
        {
            return null;
        }

        if (op is not (CelBinaryOperator.Equal or CelBinaryOperator.NotEqual))
        {
            throw Refuse(comparison, "an enum field compares with == and != only", TranslationRefusal.WhenFix);
        }

        if (!LambdaValues.TryConstant(LambdaShape.Unwrapped(constant!), out var raw, out var reason) || raw is null)
        {
            throw Refuse(constant!, reason.Length > 0 ? reason : "null is tested with == null against a field", ConstantFix);
        }

        return new CelBinary(op, Operand(field), EnumText(constant!, (Enum)Enum.ToObject(type!, raw)));
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

    private CelBinary Typed(Expression node, CelBinaryOperator op, CelNode left, CelNode right)
    {
        if (left is not CelLiteral { Type: CelValueType.Uuid } && right is not CelLiteral { Type: CelValueType.Uuid })
        {
            return new CelBinary(op, left, right);
        }

        return op is CelBinaryOperator.Equal or CelBinaryOperator.NotEqual
            ? new CelBinary(op, AsText(left), AsText(right))
            : throw Refuse(node, "an id compares with == and != only", TranslationRefusal.WhenFix);
    }

    private static CelNode AsText(CelNode node) => node is CelLiteral { Type: CelValueType.Uuid, Value: Guid id }
        ? new CelLiteral(CelValueType.String, id.ToString("D"))
        : new CelCall("string", [node]);

    private CelNode Operand(Expression node)
    {
        var unwrapped = LambdaShape.Unwrapped(node);
        if (!LambdaShape.Touches(unwrapped, _event))
        {
            return Constant(unwrapped);
        }

        return unwrapped switch
        {
            BinaryExpression arithmetic when _arithmetic.TryGetValue(arithmetic.NodeType, out var op) => new CelBinary(op, Operand(arithmetic.Left), Operand(arithmetic.Right)),
            UnaryExpression { NodeType: ExpressionType.Negate or ExpressionType.NegateChecked } negate => new CelUnary(CelUnaryOperator.Negate, Operand(negate.Operand)),
            MemberExpression member when CallerValue(member) is { } caller => caller,
            _ => Field(unwrapped),
        };
    }

    private CelContextRef? CallerValue(MemberExpression member) => member switch
    {
        { Expression: MemberExpression { Member.Name: nameof(EventCondition<>.Caller), Expression: var owner }, Member.Name: nameof(AlvoConditionCaller.User) } when owner == _event => new(CelContextValue.UserId, CelValueType.Uuid),
        { Expression: MemberExpression { Member.Name: nameof(EventCondition<>.Caller), Expression: var owner }, Member.Name: nameof(AlvoConditionCaller.Tenant) } when owner == _event => new(CelContextValue.TenantId, CelValueType.Uuid),
        _ => null,
    };

    private CelFieldRef Field(Expression node)
    {
        var unwrapped = LambdaShape.Unwrapped(node);

        return unwrapped switch
        {
            MemberExpression { Member.Name: "Value" } value when LambdaShape.IsNullable(value.Expression) => Field(value.Expression!),
            MemberExpression member when Image(member.Expression) is { } state => MappedField(member, state),
            MethodCallExpression call when LambdaShape.RecordKey(call) is { } key && RecordImage(call.Object!) is { } state => new CelFieldRef(FieldName(call, key), CelValueType.Null, state),
            _ => throw Refuse(unwrapped, Describe(unwrapped), TranslationRefusal.WhenFix),
        };
    }

    private CelRecordState? Image(Expression? node)
    {
        if (node is not MemberExpression { Expression: ParameterExpression parameter, Member.Name: nameof(EventCondition<>.New) or nameof(EventCondition<>.Old) } image || parameter != _event)
        {
            return null;
        }

        var state = image.Member.Name == nameof(EventCondition<>.New) ? CelRecordState.New : CelRecordState.Old;
        return Carries(state) ? state : throw Refuse(image, ImageReason(state), TranslationRefusal.WhenFix);
    }

    private bool Carries(CelRecordState state) => (_verb, state) switch
    {
        (HandlerVerb.Deleted, CelRecordState.New) => false,
        (HandlerVerb.Created or HandlerVerb.Saved, CelRecordState.Old) => false,
        _ => true,
    };

    private string ImageReason(CelRecordState state) => state == CelRecordState.Old
        ? $"On{_verb} carries no record before the write (old.); register OnUpdated for a condition on the change"
        : "OnDeleted carries no record after the write (new.)";

    private CelRecordState? RecordImage(Expression record) =>
        Image(record) ?? (record is MemberExpression { Member.Name: nameof(IAlvoEntity.Record) } wrapped && _shape.IsWrapper ? Image(wrapped.Expression) : null);

    private string FieldName(Expression node, string name) => CelText.IsFieldName(name)
        ? name
        : throw Refuse(node, $"'{name}' is not a field name a condition can spell", "name a declared field: letters, digits and '_', starting with a letter or '_'.");

    private CelFieldRef MappedField(MemberExpression member, CelRecordState state) => _shape.Mapped(member.Member) is { } mapped
        ? new CelFieldRef(FieldName(member, mapped.Field), CelValueType.Null, state)
        : throw Refuse(member, $"{_shape.Name}.{member.Member.Name} maps to no field: it has neither a public setter nor [AlvoField]", TranslationRefusal.WhenFix);

    private string Describe(Expression node) => node switch
    {
        MethodCallExpression call => $"method '{call.Method.Name}' has no CEL equivalent",
        ConditionalExpression => "a conditional (?:) has no CEL equivalent",
        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert => $"the conversion to {convert.Type.Name} has no CEL equivalent",
        MemberExpression member when LambdaShape.IsClock(member) => ClockReason,
        MemberExpression member => $"'{member.Member.Name}' is not a field of {_shape.Name} a condition can read",
        _ => $"a {node.NodeType} expression has no CEL equivalent",
    };

    private AlvoTranslationException Refuse(Expression node, string reason, string fix) => TranslationRefusal.When(_id, node, reason, fix);
}
