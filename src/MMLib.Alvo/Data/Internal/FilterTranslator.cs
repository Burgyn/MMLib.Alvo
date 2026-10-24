using MMLib.Alvo.Expressions.Internal;

using System.Collections.Frozen;
using System.Linq.Expressions;

namespace MMLib.Alvo.Data.Internal;

/// <summary>
/// Translates a typed query's predicate into the public <see cref="AlvoFilter"/> tree (spec 2026-10-14 X7, §4.3), on every
/// call: a captured value is read then and placed as a typed value the driver binds as a parameter, never as text.
/// </summary>
/// <remarks>
/// <para>
/// Every field is a mapped property of the row or a literal, identifier-checked key of the row's record (a wrapper's
/// <c>Record</c>, or the row itself when the query is typed over <see cref="AlvoRecord"/>). Values are read and typed in
/// <c>FilterTranslator.Values.cs</c>; calls and lists in <c>FilterTranslator.Calls.cs</c>.
/// </para>
/// <para>
/// The filter keeps SQL's null semantics, not C#'s (plan Ruling 20): <c>x.Code != "a"</c> becomes <c>neq</c> and
/// <c>!x.IsPaid</c> becomes <c>not(is_paid eq true)</c>, and neither ever matches a row whose column is null.
/// </para>
/// <para>
/// A captured lazy sequence is enumerated once per translation, so a caller translates once per query call and never
/// reuses a filter across calls whose captured values may have changed.
/// </para>
/// </remarks>
internal sealed partial class FilterTranslator
{
    private static readonly FrozenDictionary<ExpressionType, AlvoFilterOperator> _operators = new Dictionary<ExpressionType, AlvoFilterOperator>
    {
        [ExpressionType.Equal] = AlvoFilterOperator.Eq,
        [ExpressionType.NotEqual] = AlvoFilterOperator.Neq,
        [ExpressionType.LessThan] = AlvoFilterOperator.Lt,
        [ExpressionType.LessThanOrEqual] = AlvoFilterOperator.Lte,
        [ExpressionType.GreaterThan] = AlvoFilterOperator.Gt,
        [ExpressionType.GreaterThanOrEqual] = AlvoFilterOperator.Gte,
    }.ToFrozenDictionary();

    private static readonly FrozenSet<ExpressionType> _arithmetic =
    [
        ExpressionType.Add, ExpressionType.AddChecked, ExpressionType.Subtract, ExpressionType.SubtractChecked, ExpressionType.Multiply,
        ExpressionType.MultiplyChecked, ExpressionType.Divide, ExpressionType.Modulo, ExpressionType.Negate, ExpressionType.NegateChecked,
    ];

    /// <summary>How deep and how large a predicate may be before it is walked: room for a flat or-chain of every term a filter may hold.</summary>
    private static readonly int _maxDepth = AlvoFilter.MaxTerms * 2;

    private static readonly int _maxNodes = AlvoFilter.MaxTerms * 16;

    private readonly ParameterExpression _row;
    private readonly string _entity;
    private readonly RecordShape _shape;

    private FilterTranslator(ParameterExpression row, string entity, RecordShape shape) => (_row, _entity, _shape) = (row, entity, shape);

    internal static AlvoFilter Translate<T>(Expression<Func<T, bool>> where, string entity)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(where);
        if (!LambdaShape.IsWithin(where.Body, _maxDepth, _maxNodes))
        {
            throw TranslationRefusal.QueryTooLarge(entity, _maxDepth, _maxNodes);
        }

        return new FilterTranslator(where.Parameters[0], entity, RecordShape.Of(typeof(T))).Filter(where.Body);
    }

    private AlvoFilter Filter(Expression node)
    {
        if (!LambdaShape.Touches(node, _row))
        {
            throw Refuse(node, "it reads no field", "compute it before the query, and leave the term out when it does not apply.");
        }

        return node switch
        {
            BinaryExpression { NodeType: ExpressionType.AndAlso } => new AlvoAnd([.. Terms(node, ExpressionType.AndAlso).Select(Filter)]),
            BinaryExpression { NodeType: ExpressionType.OrElse } => new AlvoOr([.. Terms(node, ExpressionType.OrElse).Select(Filter)]),
            UnaryExpression { NodeType: ExpressionType.Not } not when not.Type == typeof(bool) => new AlvoNot(Filter(not.Operand)),
            BinaryExpression comparison when _operators.TryGetValue(comparison.NodeType, out var op) => Comparison(comparison, op),
            MethodCallExpression call => Call(call),
            MemberExpression { Member.Name: "HasValue" } has when LambdaShape.IsNullable(has.Expression) => new AlvoNot(IsNull(has.Expression!)),
            _ when node.Type == typeof(bool) => new AlvoComparison(Field(node), AlvoFilterOperator.Eq, true),
            _ => throw Refuse(node, Describe(node)),
        };
    }

    private static IEnumerable<Expression> Terms(Expression node, ExpressionType connective) =>
        node is BinaryExpression binary && binary.NodeType == connective ? Terms(binary.Left, connective).Concat(Terms(binary.Right, connective)) : [node];

    private AlvoFilter Comparison(BinaryExpression comparison, AlvoFilterOperator op)
    {
        if (comparison.Left.Type == typeof(object) || comparison.Right.Type == typeof(object))
        {
            throw Refuse(comparison, "a comparison of two objects compares references in C#, which a filter has no equivalent of", "compare the values without casting them to object.");
        }

        var (field, value, flipped) = Sides(comparison);
        var effective = flipped ? Flipped(op) : op;
        var enumType = EnumOf(field);
        if (enumType is not null && effective is not (AlvoFilterOperator.Eq or AlvoFilterOperator.Neq))
        {
            throw Refuse(comparison, "an enum field compares with == and != only");
        }

        var name = Field(enumType is null ? field : LambdaShape.Bare(field));
        var operand = Value(value, enumType);
        return operand is null ? Null(comparison, effective, name) : new AlvoComparison(name, effective, operand);
    }

    private (Expression Field, Expression Value, bool Flipped) Sides(BinaryExpression comparison) =>
        (LambdaShape.Touches(comparison.Left, _row), LambdaShape.Touches(comparison.Right, _row)) switch
        {
            (true, false) => (comparison.Left, comparison.Right, false),
            (false, true) => (comparison.Right, comparison.Left, true),
            _ => throw Refuse(comparison, "it compares two fields, and a filter compares a field with a value", "compare each field with a value, or filter the rows after reading them."),
        };

    private static AlvoFilterOperator Flipped(AlvoFilterOperator op) => op switch
    {
        AlvoFilterOperator.Lt => AlvoFilterOperator.Gt,
        AlvoFilterOperator.Lte => AlvoFilterOperator.Gte,
        AlvoFilterOperator.Gt => AlvoFilterOperator.Lt,
        AlvoFilterOperator.Gte => AlvoFilterOperator.Lte,
        _ => op,
    };

    private AlvoFilter Null(BinaryExpression comparison, AlvoFilterOperator op, string field) => op switch
    {
        AlvoFilterOperator.Eq => new AlvoComparison(field, AlvoFilterOperator.Is, null),
        AlvoFilterOperator.Neq => new AlvoNot(new AlvoComparison(field, AlvoFilterOperator.Is, null)),
        _ => throw Refuse(comparison, "only == and != can test for null"),
    };

    private AlvoComparison IsNull(Expression field) => new(Field(field), AlvoFilterOperator.Is, null);

    private static Type? EnumOf(Expression field) => LambdaShape.HidesEnum(field)
        ? LambdaShape.Underlying(LambdaShape.Bare(field).Type)
        : LambdaShape.Underlying(field.Type) is { IsEnum: true } type ? type : null;

    private string Field(Expression node)
    {
        var unwrapped = LambdaShape.Unwrapped(node);

        return unwrapped switch
        {
            MemberExpression { Member.Name: "Value" } value when LambdaShape.IsNullable(value.Expression) => Field(value.Expression!),
            MemberExpression { Expression: ParameterExpression parameter } member when parameter == _row => FieldName(member, _shape.Mapped(member.Member)?.Field ?? throw Refuse(member, Describe(member))),
            MethodCallExpression call when LambdaShape.RecordKey(call) is { } key && IsRowRecord(call.Object!) => FieldName(call, key),
            _ => throw Refuse(unwrapped, Describe(unwrapped)),
        };
    }

    private string FieldName(Expression node, string name) => CelText.IsFieldName(name)
        ? name
        : throw Refuse(node, $"'{name}' is not a field name a filter can name", "name a declared field: letters, digits and '_', starting with a letter or '_'.");

    private bool IsRowRecord(Expression record) =>
        record == _row || record is MemberExpression { Member.Name: nameof(IAlvoEntity.Record), Expression: var owner } && owner == _row && _shape.IsWrapper;

    private string Describe(Expression node) => node switch
    {
        BinaryExpression or UnaryExpression when _arithmetic.Contains(node.NodeType) => "arithmetic has no filter equivalent",
        MethodCallExpression { Object: { } record } call when record.Type == typeof(AlvoRecord) && LambdaShape.RecordKey(call) is null => "a record's key must be a literal string, such as Record[\"legacy_code\"]",
        MethodCallExpression call => $"method '{call.Method.Name}' has no filter equivalent",
        MemberExpression { Expression: ParameterExpression } member => $"{_shape.Name}.{member.Member.Name} maps to no field: it has neither a public setter nor [AlvoField]",
        ConditionalExpression => "a conditional (?:) has no filter equivalent",
        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert => $"the conversion to {convert.Type.Name} has no filter equivalent",
        _ => $"a {node.NodeType} expression has no filter equivalent",
    };

    private AlvoTranslationException Refuse(Expression node, string reason, string fix = TranslationRefusal.QueryFix) =>
        TranslationRefusal.Query(_entity, node, reason, fix);
}
