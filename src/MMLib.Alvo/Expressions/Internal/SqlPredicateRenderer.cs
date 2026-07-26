using MMLib.Alvo.Schema;
using System.Collections.Frozen;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Renders a <see cref="CompiledExpression"/> to SQL, tracking per rendered subtree whether it is
/// already two-valued (evaluates to true or false and never SQL's three-valued <c>UNKNOWN</c>) and
/// collapsing only what is not. A comparison and a nullable boolean field each collapse once, through
/// <see cref="IFieldSqlRenderer.RenderTwoValued"/> / <see cref="IFieldSqlRenderer.RenderBooleanFieldAsPredicate"/>
/// (<c>COALESCE(&lt;value&gt;, FALSE)</c> on PostgreSQL and SQLite, a dialect's own shape elsewhere), and
/// are then two-valued; <c>AND</c>/<c>OR</c>/<c>NOT</c> over already two-valued operands stay two-valued
/// without an extra collapse; <c>IS NOT NULL</c> and a boolean constant are two-valued from the start.
/// This is what lets the SQL backend agree, term for term, with <see cref="CelInterpreter"/>'s in-memory
/// null rule — see that type's class remarks for the exact semantics this renderer must match, including
/// the string-collation caveat on <c>==</c>/<c>!=</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type composes structure only; the two-valued fold is the dialect's.</b> No <c>COALESCE</c> is
/// spelled here — every collapse goes through <see cref="IFieldSqlRenderer"/>, because a dialect with no
/// boolean type (T-SQL, which §0 principle 3 requires) cannot use a value-returning
/// <c>COALESCE(&lt;predicate&gt;, 0)</c> where a predicate is expected, and a driver author who can only
/// implement field/parameter/literal rendering would have no way to fix that short of forking this
/// renderer.
/// </para>
/// <para>
/// <b>Root collapse is defense-in-depth, not the live mechanism.</b> <see cref="Render(CompiledExpression, AlvoContext, IFieldSqlRenderer, string)"/>
/// collapses the whole rendered predicate once more only when the root fragment is not already marked
/// two-valued. Every node kind the predicate path renders today already produces an
/// already-two-valued fragment at its own level, so that branch is never taken — it exists so a future
/// node kind that is added without also collapsing itself still fails safe (deny) instead of leaking
/// <c>UNKNOWN</c> to the caller.
/// </para>
/// <para>
/// <b>An absent context value is the policy engine's job to reject, and it now does — the collapse
/// here is unreachable defence-in-depth, not the load-bearing guard.</b> A <c>@tenant.id</c> reference
/// against an <see cref="AlvoContext"/> with no <see cref="AlvoContext.Tenant"/> renders <c>FALSE</c>
/// (see <see cref="IsAbsentContextOperand"/>). That is correct in isolation but it inverts under
/// negation like any other collapsed comparison (<c>!(tenant_id == @tenant.id)</c> renders
/// <c>NOT FALSE</c>, matching every tenant's rows), so it was never a safe guarantee to rely on.
/// <c>PolicyEngine</c>'s required-context gate therefore denies before a predicate reading a context
/// value the caller does not have is ever handed to a data port — for a global entity as much as a
/// tenant-scoped one — which means no policy-driven call can reach this branch. It stays because this
/// renderer is a public seam a provider may drive directly, and rendering <c>FALSE</c> is the right
/// answer for a caller who bypasses the engine; it must never again be read as the isolation
/// guarantee itself.
/// </para>
/// <para>
/// <b>The scalar (Computed) path can diverge from the interpreter in two ways this renderer does not
/// paper over.</b> <see cref="CelInterpreter.EvaluateScalar"/> turns a division by zero or a decimal
/// overflow into <see langword="null"/>; the equivalent SQL (a bare <c>/</c>, an arithmetic expression
/// that overflows the column's numeric type) raises an engine error instead, failing the write. A
/// generated column relying on either behavior needs a guard expressed in CEL itself (e.g. a ternary
/// checking the divisor) — this renderer has no general way to intercept an engine-level arithmetic
/// error.
/// </para>
/// </remarks>
internal sealed class SqlPredicateRenderer : IPredicateRenderer
{
    /// <inheritdoc />
    public SqlPredicate Render(
        CompiledExpression expression, AlvoContext context, IFieldSqlRenderer fields, string parameterPrefix = "alvo_p")
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fields);
        RequirePredicateProfile(expression);
        RequireIdentifierPrefix(parameterPrefix);

        var bag = new ParameterBag(parameterPrefix);
        var rendered = RenderPredicate(expression.Root, expression.Entity, context, fields, bag);
        var sql = rendered.IsTwoValued ? rendered.Sql : fields.RenderTwoValued(rendered.Sql);
        return new SqlPredicate(sql, bag.Snapshot());
    }

    /// <inheritdoc />
    public SqlExpression Render(CompiledExpression expression, IFieldSqlRenderer fields)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(fields);
        RequireScalarProfile(expression);

        var bag = new ParameterBag(DefaultParameterPrefix);
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

    private const string DefaultParameterPrefix = "p";

    /// <summary>
    /// The parameter prefix reaches the SQL text unparameterized — a bind parameter's own name has no
    /// bind-parameter form — so it is validated as a plain identifier rather than trusted, in case a
    /// provider ever derives one from something caller-influenced.
    /// </summary>
    private static void RequireIdentifierPrefix(string parameterPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterPrefix);

        if (!IsPlainIdentifier(parameterPrefix))
        {
            throw new ArgumentException(
                $"'{parameterPrefix}' is not a plain identifier; a parameter prefix must start with an ASCII "
                + "letter or '_' and contain only letters, digits and '_'.",
                nameof(parameterPrefix));
        }
    }

    private static bool IsPlainIdentifier(string text) =>
        (char.IsAsciiLetter(text[0]) || text[0] == '_')
        && text.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static NotSupportedException Unsupported(CelNode node) =>
        new($"'{node.GetType().Name}' cannot be rendered to SQL by this entry point.");

    private readonly record struct PredicateFragment(string Sql, bool IsTwoValued);

    /// <summary>
    /// Collects a single render's bound values, naming them <c>&lt;prefix&gt;0</c>, <c>&lt;prefix&gt;1</c>,
    /// … The prefix is per render, never global, which is what lets a caller compose several predicates
    /// into one command without two of them claiming the same name (see <see cref="SqlPredicate"/>).
    /// </summary>
    private sealed class ParameterBag(string prefix)
    {
        private readonly Dictionary<string, object?> _values = [];

        public string Add(object? value)
        {
            var name = $"{prefix}{_values.Count}";
            _values.Add(name, value);
            return name;
        }

        public FrozenDictionary<string, object?> Snapshot() => _values.ToFrozenDictionary();
    }

    private PredicateFragment RenderPredicate(
        CelNode node, EntitySchema entity, AlvoContext context, IFieldSqlRenderer fields, ParameterBag bag) => node switch
        {
            CelLiteral { Type: CelValueType.Bool } literal => RenderBoolLiteral(literal, fields),
            CelFieldRef { Type: CelValueType.Bool } fieldRef => RenderBoolField(fieldRef, entity, fields),
            CelUnary { Operator: CelUnaryOperator.Not } unary => RenderNot(unary, entity, context, fields, bag),
            CelBinary { Operator: CelBinaryOperator.And or CelBinaryOperator.Or } logical =>
                RenderLogical(logical, entity, context, fields, bag),
            CelBinary { Operator: CelBinaryOperator.In } inNode => RenderIn(inNode, entity, context, fields, bag),
            CelBinary comparison => RenderComparison(comparison, entity, context, fields, bag),
            CelHas has => RenderHas(has, entity, fields),
            _ => throw Unsupported(node),
        };

    private static PredicateFragment RenderBoolLiteral(CelLiteral literal, IFieldSqlRenderer fields) =>
        new(fields.RenderBooleanPredicate(literal.Value is true), true);

    private static PredicateFragment RenderBoolField(CelFieldRef fieldRef, EntitySchema entity, IFieldSqlRenderer fields)
    {
        var field = RenderField(fieldRef, entity, fields);
        return new PredicateFragment(fields.RenderBooleanFieldAsPredicate(field), true);
    }

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
            return new PredicateFragment(fields.RenderBooleanPredicate(false), true);
        }

        var comparable = PromotedType(binary.Left, binary.Right);
        var left = fields.RenderComparableOperand(RenderOperand(binary.Left, entity, context, fields, bag), comparable);
        var right = fields.RenderComparableOperand(RenderOperand(binary.Right, entity, context, fields, bag), comparable);
        var sql = $"{left} {ComparisonOperatorText(binary.Operator)} {right}";
        return new PredicateFragment(fields.RenderTwoValued(sql), true);
    }

    /// <summary>
    /// The type a comparison over these two operands is evaluated at, after CEL's numeric promotion —
    /// <see cref="CelValueType.Decimal"/> wins over <see cref="CelValueType.Int"/>, since the type checker
    /// admits a mixed numeric comparison. It is handed to
    /// <see cref="IFieldSqlRenderer.RenderComparableOperand"/> so a dialect whose storage for that type
    /// does not order the way the type does repairs <b>both</b> sides identically: on SQLite a decimal
    /// lives in a <c>TEXT</c> column, and casting only the column would leave the parameter's own storage
    /// class deciding the comparison.
    /// </summary>
    private static CelValueType PromotedType(CelNode left, CelNode right)
    {
        var leftType = ValueTypeOf(left);
        var rightType = ValueTypeOf(right);

        if (leftType == CelValueType.Decimal || rightType == CelValueType.Decimal)
        {
            return CelValueType.Decimal;
        }

        return leftType == CelValueType.Null ? rightType : leftType;
    }

    /// <summary>
    /// A node's own value type. An operator node carries none, so it takes its operands' promoted type —
    /// which is what makes <c>(price + 1) &gt; 100</c> a decimal comparison rather than an untyped one.
    /// </summary>
    private static CelValueType ValueTypeOf(CelNode node) => node switch
    {
        CelLiteral literal => literal.Type,
        CelFieldRef fieldRef => fieldRef.Type,
        CelContextRef contextRef => contextRef.Type,
        CelUnary unary => ValueTypeOf(unary.Operand),
        CelBinary binary => PromotedType(binary.Left, binary.Right),
        CelConditional conditional => PromotedType(conditional.WhenTrue, conditional.WhenFalse),
        _ => CelValueType.Null,
    };

    /// <summary>
    /// Renders role membership. The right operand is never read — the caller's role set answers it — so
    /// <see cref="RoleMembership"/> asserts that the operand really is <c>@user.roles</c> first.
    /// </summary>
    private static PredicateFragment RenderIn(
        CelBinary binary, EntitySchema entity, AlvoContext context, IFieldSqlRenderer fields, ParameterBag bag)
    {
        RoleMembership.RequireUserRolesOperand(binary.Right);

        if (binary.Left is CelLiteral { Type: CelValueType.String, Value: string text })
        {
            var isMember = context.Roles.Select(role => role.Name).Contains(text, StringComparer.Ordinal);
            return new PredicateFragment(fields.RenderBooleanPredicate(isMember), true);
        }

        var roleNames = context.Roles.Select(role => role.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();
        if (roleNames.Count == 0)
        {
            return new PredicateFragment(fields.RenderBooleanPredicate(false), true);
        }

        var left = RenderOperand(binary.Left, entity, context, fields, bag);
        var roleParameters = roleNames.Select(name => RenderLiteralOperand(new CelLiteral(CelValueType.String, name), fields, bag));
        var sql = $"{left} IN ({string.Join(", ", roleParameters)})";
        return new PredicateFragment(fields.RenderTwoValued(sql), true);
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

    /// <summary>
    /// Renders a node used in a boolean slot inside the scalar (Computed) path — a <c>NOT</c>
    /// operand, an <c>AND</c>/<c>OR</c> operand, or a <c>CASE WHEN</c> condition. Unlike the
    /// predicate path this is not wrapped in <c>COALESCE</c> at the outer <c>CASE WHEN</c> level
    /// (SQL's own <c>CASE WHEN NULL</c> already behaves like <see langword="false"/>), but a
    /// comparison or a nullable boolean field still has to collapse itself here: <c>NOT</c> only
    /// negates correctly when its operand is already two-valued, so <c>!(total &gt; 5)</c> over a
    /// <see langword="null"/> <c>total</c> must render <c>NOT COALESCE(...)</c>, never a bare
    /// <c>NOT (&lt;comparison&gt;)</c> that would let <c>UNKNOWN</c> flip to <c>UNKNOWN</c> instead of
    /// <see langword="true"/>.
    /// </summary>
    private string RenderScalarBoolean(CelNode node, EntitySchema entity, IFieldSqlRenderer fields, ParameterBag bag) => node switch
    {
        CelLiteral { Type: CelValueType.Bool } literal => fields.RenderBooleanPredicate(literal.Value is true),
        CelFieldRef fieldRef => fields.RenderBooleanFieldAsPredicate(RenderField(fieldRef, entity, fields)),
        CelUnary { Operator: CelUnaryOperator.Not } unary => $"(NOT {RenderScalarBoolean(unary.Operand, entity, fields, bag)})",
        CelBinary { Operator: CelBinaryOperator.And or CelBinaryOperator.Or } logical => RenderScalarLogical(logical, entity, fields, bag),
        CelHas has => $"({RenderField(has.Field, entity, fields)} IS NOT NULL)",
        CelBinary comparison => fields.RenderTwoValued(RenderScalarComparison(comparison, entity, fields, bag)),
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
        var comparable = PromotedType(binary.Left, binary.Right);
        var left = fields.RenderComparableOperand(RenderScalarOperand(binary.Left, entity, fields, bag), comparable);
        var right = fields.RenderComparableOperand(RenderScalarOperand(binary.Right, entity, fields, bag), comparable);
        return $"{left} {ComparisonOperatorText(binary.Operator)} {right}";
    }

    private static string RenderScalarOperand(CelNode node, EntitySchema entity, IFieldSqlRenderer fields, ParameterBag bag) => node switch
    {
        CelLiteral literal => RenderLiteralOperand(literal, fields, bag),
        CelFieldRef fieldRef => RenderField(fieldRef, entity, fields),
        _ => throw Unsupported(node),
    };
}
