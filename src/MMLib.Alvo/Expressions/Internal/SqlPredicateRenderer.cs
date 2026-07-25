using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Renders a <see cref="CompiledExpression"/> to SQL, tracking per rendered subtree whether it is
/// already two-valued (evaluates to true or false and never SQL's three-valued <c>UNKNOWN</c>) and
/// wrapping only what is not. A comparison renders as <c>COALESCE(&lt;a&gt; &lt;op&gt; &lt;b&gt;,
/// FALSE)</c> and is then two-valued; <c>AND</c>/<c>OR</c>/<c>NOT</c> over already two-valued
/// operands stay two-valued without an extra wrap; <c>IS NOT NULL</c> and a boolean literal are
/// two-valued from the start. This is what lets the SQL backend agree, term for term, with
/// <see cref="CelInterpreter"/>'s in-memory null rule — see that type's class remarks for the exact
/// semantics this renderer must match, including the string-collation caveat on <c>==</c>/<c>!=</c>.
/// </summary>
internal sealed class SqlPredicateRenderer : IPredicateRenderer
{
    /// <inheritdoc />
    public SqlPredicate Render(CompiledExpression expression, AlvoContext context, IFieldSqlRenderer fields)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fields);
        RequirePredicateProfile(expression);

        var bag = new ParameterBag();
        var rendered = RenderPredicate(expression.Root, expression.Entity, context, fields, bag);
        var sql = rendered.IsTwoValued ? rendered.Sql : Wrap(rendered.Sql, fields);
        return new SqlPredicate(sql, bag.Snapshot());
    }

    /// <inheritdoc />
    public SqlExpression Render(CompiledExpression expression, IFieldSqlRenderer fields)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(fields);
        RequireScalarProfile(expression);

        var bag = new ParameterBag();
        var sql = RenderScalar(expression.Root, expression.Entity, fields, bag);
        return new SqlExpression(sql, bag.Snapshot());
    }

    private static void RequirePredicateProfile(CompiledExpression expression)
    {
        if (expression.Profile == CelProfile.Computed)
        {
            throw new InvalidOperationException(
                $"'{expression.Source}' was compiled for the Computed profile; use the scalar " +
                $"{nameof(IPredicateRenderer)}.{nameof(Render)}(expression, fields) entry point instead.");
        }
    }

    private static void RequireScalarProfile(CompiledExpression expression)
    {
        if (expression.Profile != CelProfile.Computed)
        {
            throw new InvalidOperationException(
                $"'{expression.Source}' was compiled for the {expression.Profile} profile; use the predicate " +
                $"{nameof(IPredicateRenderer)}.{nameof(Render)}(expression, context, fields) entry point instead.");
        }
    }

    private static string Wrap(string sql, IFieldSqlRenderer fields) => $"COALESCE({sql}, {fields.FalseLiteral})";

    private static NotSupportedException Unsupported(CelNode node) =>
        new($"'{node.GetType().Name}' cannot be rendered to SQL by this entry point.");

    private readonly record struct PredicateFragment(string Sql, bool IsTwoValued);

    private sealed class ParameterBag
    {
        private readonly Dictionary<string, object?> _values = [];

        public string Add(object? value)
        {
            var name = $"p{_values.Count}";
            _values.Add(name, value);
            return name;
        }

        public Dictionary<string, object?> Snapshot() => _values;
    }

    // --- Predicate rendering (Rule/Condition profiles; two-valued) --------------------------------

    private PredicateFragment RenderPredicate(
        CelNode node, EntitySchema entity, AlvoContext context, IFieldSqlRenderer fields, ParameterBag bag) => node switch
        {
            CelLiteral { Type: CelValueType.Bool } literal => RenderBoolLiteral(literal, fields),
            CelUnary { Operator: CelUnaryOperator.Not } unary => RenderNot(unary, entity, context, fields, bag),
            CelBinary { Operator: CelBinaryOperator.And or CelBinaryOperator.Or } logical =>
                RenderLogical(logical, entity, context, fields, bag),
            CelBinary { Operator: CelBinaryOperator.In } inNode => RenderIn(inNode, entity, context, fields, bag),
            CelBinary comparison => RenderComparison(comparison, entity, context, fields, bag),
            CelHas has => RenderHas(has, entity, fields),
            _ => throw Unsupported(node),
        };

    private static PredicateFragment RenderBoolLiteral(CelLiteral literal, IFieldSqlRenderer fields) =>
        new(literal.Value is true ? fields.TrueLiteral : fields.FalseLiteral, true);

    private PredicateFragment RenderNot(
        CelUnary unary, EntitySchema entity, AlvoContext context, IFieldSqlRenderer fields, ParameterBag bag)
    {
        var operand = RenderPredicate(unary.Operand, entity, context, fields, bag);
        return new PredicateFragment($"(NOT {operand.Sql})", true);
    }

    private PredicateFragment RenderLogical(
        CelBinary binary, EntitySchema entity, AlvoContext context, IFieldSqlRenderer fields, ParameterBag bag)
    {
        var left = RenderPredicate(binary.Left, entity, context, fields, bag);
        var right = RenderPredicate(binary.Right, entity, context, fields, bag);
        var op = binary.Operator == CelBinaryOperator.And ? "AND" : "OR";
        return new PredicateFragment($"({left.Sql} {op} {right.Sql})", true);
    }

    private static PredicateFragment RenderHas(CelHas has, EntitySchema entity, IFieldSqlRenderer fields)
    {
        var field = RenderField(has.Field, entity, fields);
        return new PredicateFragment($"({field} IS NOT NULL)", true);
    }

    private static PredicateFragment RenderComparison(
        CelBinary binary, EntitySchema entity, AlvoContext context, IFieldSqlRenderer fields, ParameterBag bag)
    {
        if (IsAbsentContextOperand(binary.Left, context) || IsAbsentContextOperand(binary.Right, context))
        {
            return new PredicateFragment(fields.FalseLiteral, true);
        }

        var left = RenderOperand(binary.Left, entity, context, fields, bag);
        var right = RenderOperand(binary.Right, entity, context, fields, bag);
        var sql = $"{left} {ComparisonOperatorText(binary.Operator)} {right}";
        return new PredicateFragment(Wrap(sql, fields), true);
    }

    private static PredicateFragment RenderIn(
        CelBinary binary, EntitySchema entity, AlvoContext context, IFieldSqlRenderer fields, ParameterBag bag)
    {
        if (binary.Left is CelLiteral { Type: CelValueType.String, Value: string text })
        {
            var isMember = context.Roles.Select(role => role.Name).Contains(text, StringComparer.Ordinal);
            return new PredicateFragment(isMember ? fields.TrueLiteral : fields.FalseLiteral, true);
        }

        var left = RenderOperand(binary.Left, entity, context, fields, bag);
        var roleParameters = context.Roles
            .Select(role => role.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => RenderLiteralOperand(new CelLiteral(CelValueType.String, name), fields, bag));
        var sql = $"{left} IN ({string.Join(", ", roleParameters)})";
        return new PredicateFragment(Wrap(sql, fields), true);
    }

    private static bool IsAbsentContextOperand(CelNode node, AlvoContext context) =>
        node is CelContextRef contextRef && !TryResolveContext(contextRef, context, out _);

    private static bool TryResolveContext(CelContextRef contextRef, AlvoContext context, out object value)
    {
        switch (contextRef.Value)
        {
            case CelContextValue.UserId:
                value = context.User.Value;
                return true;
            case CelContextValue.TenantId when context.Tenant is { } tenant:
                value = tenant.Value;
                return true;
            case CelContextValue.TenantId:
                value = null!;
                return false;
            default:
                throw Unsupported(contextRef);
        }
    }

    private static string ComparisonOperatorText(CelBinaryOperator op) => op switch
    {
        CelBinaryOperator.Equal => "=",
        CelBinaryOperator.NotEqual => "<>",
        CelBinaryOperator.Less => "<",
        CelBinaryOperator.LessOrEqual => "<=",
        CelBinaryOperator.Greater => ">",
        CelBinaryOperator.GreaterOrEqual => ">=",
        _ => throw new NotSupportedException($"'{op}' is not a comparison operator."),
    };

    // --- Operand rendering (shared scalar values, used both inside a predicate and as a scalar) ----

    private static string RenderOperand(
        CelNode node, EntitySchema entity, AlvoContext context, IFieldSqlRenderer fields, ParameterBag bag) => node switch
        {
            CelLiteral literal => RenderLiteralOperand(literal, fields, bag),
            CelFieldRef fieldRef => RenderField(fieldRef, entity, fields),
            CelContextRef contextRef => RenderContextOperand(contextRef, context, fields, bag),
            _ => throw Unsupported(node),
        };

    private static string RenderLiteralOperand(CelLiteral literal, IFieldSqlRenderer fields, ParameterBag bag)
    {
        if (literal.Type == CelValueType.Bool)
        {
            return literal.Value is true ? fields.TrueLiteral : fields.FalseLiteral;
        }

        var name = bag.Add(literal.Value);
        return fields.RenderParameter(name);
    }

    private static string RenderField(CelFieldRef fieldRef, EntitySchema entity, IFieldSqlRenderer fields)
    {
        if (fieldRef.State != CelRecordState.Current)
        {
            throw new NotSupportedException(
                "SQL rendering only supports the current row; old./new. field references and changed(...) are " +
                "evaluated in-process by the Condition backend, never rendered to SQL.");
        }

        return fields.RenderField(entity, fieldRef.FieldName);
    }

    private static string RenderContextOperand(
        CelContextRef contextRef, AlvoContext context, IFieldSqlRenderer fields, ParameterBag bag)
    {
        TryResolveContext(contextRef, context, out var value);
        var name = bag.Add(value);
        return fields.RenderParameter(name);
    }

    // --- Scalar rendering (Computed profile; never wrapped, no context) -----------------------------

    private string RenderScalar(CelNode node, EntitySchema entity, IFieldSqlRenderer fields, ParameterBag bag) => node switch
    {
        CelLiteral literal => RenderLiteralOperand(literal, fields, bag),
        CelFieldRef fieldRef => RenderField(fieldRef, entity, fields),
        CelUnary { Operator: CelUnaryOperator.Negate } unary => $"(-{RenderScalar(unary.Operand, entity, fields, bag)})",
        CelBinary { Operator: CelBinaryOperator.Add or CelBinaryOperator.Subtract or CelBinaryOperator.Multiply or CelBinaryOperator.Divide } arithmetic =>
            RenderArithmeticScalar(arithmetic, entity, fields, bag),
        CelConditional conditional => RenderConditional(conditional, entity, fields, bag),
        _ => throw Unsupported(node),
    };

    private string RenderArithmeticScalar(CelBinary binary, EntitySchema entity, IFieldSqlRenderer fields, ParameterBag bag)
    {
        var left = RenderScalar(binary.Left, entity, fields, bag);
        var right = RenderScalar(binary.Right, entity, fields, bag);
        return $"({left} {ArithmeticOperatorText(binary.Operator)} {right})";
    }

    private static string ArithmeticOperatorText(CelBinaryOperator op) => op switch
    {
        CelBinaryOperator.Add => "+",
        CelBinaryOperator.Subtract => "-",
        CelBinaryOperator.Multiply => "*",
        CelBinaryOperator.Divide => "/",
        _ => throw new NotSupportedException($"'{op}' is not an arithmetic operator."),
    };

    private string RenderConditional(CelConditional conditional, EntitySchema entity, IFieldSqlRenderer fields, ParameterBag bag)
    {
        var condition = RenderScalarBoolean(conditional.Condition, entity, fields, bag);
        var whenTrue = RenderScalar(conditional.WhenTrue, entity, fields, bag);
        var whenFalse = RenderScalar(conditional.WhenFalse, entity, fields, bag);
        return $"(CASE WHEN {condition} THEN {whenTrue} ELSE {whenFalse} END)";
    }

    private string RenderScalarBoolean(CelNode node, EntitySchema entity, IFieldSqlRenderer fields, ParameterBag bag) => node switch
    {
        CelLiteral { Type: CelValueType.Bool } literal => literal.Value is true ? fields.TrueLiteral : fields.FalseLiteral,
        CelFieldRef fieldRef => RenderField(fieldRef, entity, fields),
        CelUnary { Operator: CelUnaryOperator.Not } unary => $"(NOT {RenderScalarBoolean(unary.Operand, entity, fields, bag)})",
        CelBinary { Operator: CelBinaryOperator.And or CelBinaryOperator.Or } logical => RenderScalarLogical(logical, entity, fields, bag),
        CelHas has => $"({RenderField(has.Field, entity, fields)} IS NOT NULL)",
        CelBinary comparison => RenderScalarComparison(comparison, entity, fields, bag),
        CelConditional nested => RenderConditional(nested, entity, fields, bag),
        _ => throw Unsupported(node),
    };

    private string RenderScalarLogical(CelBinary binary, EntitySchema entity, IFieldSqlRenderer fields, ParameterBag bag)
    {
        var left = RenderScalarBoolean(binary.Left, entity, fields, bag);
        var right = RenderScalarBoolean(binary.Right, entity, fields, bag);
        var op = binary.Operator == CelBinaryOperator.And ? "AND" : "OR";
        return $"({left} {op} {right})";
    }

    private static string RenderScalarComparison(CelBinary binary, EntitySchema entity, IFieldSqlRenderer fields, ParameterBag bag)
    {
        var left = RenderScalarOperand(binary.Left, entity, fields, bag);
        var right = RenderScalarOperand(binary.Right, entity, fields, bag);
        return $"{left} {ComparisonOperatorText(binary.Operator)} {right}";
    }

    private static string RenderScalarOperand(CelNode node, EntitySchema entity, IFieldSqlRenderer fields, ParameterBag bag) => node switch
    {
        CelLiteral literal => RenderLiteralOperand(literal, fields, bag),
        CelFieldRef fieldRef => RenderField(fieldRef, entity, fields),
        _ => throw Unsupported(node),
    };
}
