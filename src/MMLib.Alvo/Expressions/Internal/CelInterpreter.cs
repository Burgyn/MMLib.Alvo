using MMLib.Alvo.Data;
using System.Globalization;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Evaluates a <see cref="CompiledExpression"/> in memory — Alvo's <c>WITH CHECK</c> backend,
/// used whenever there is a candidate row but no stored row a SQL predicate could filter (a
/// <c>create</c>, or a hook <c>Condition</c>). Task 9's SQL <c>USING</c> renderer is written to
/// agree with the semantics documented here exactly, because a differential property test proves
/// the two backends never disagree on any well-typed expression and record.
/// </summary>
/// <remarks>
/// <para>
/// <b>Null rule.</b> A comparison (<c>==</c>, <c>!=</c>, <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>,
/// <c>&gt;=</c>) where either operand is <see langword="null"/> evaluates to <see langword="false"/>
/// — never "unknown", never an exception — matching a SQL predicate wrapped in
/// <c>COALESCE(..., FALSE)</c>. <c>!</c> applies to the already-collapsed boolean, so
/// <c>!(owner_id == @user.id)</c> over a <see langword="null"/> <c>owner_id</c> is
/// <see langword="true"/>. A field absent from an <see cref="AlvoRecord"/> reads as
/// <see langword="null"/>, identically to a field present with a <see langword="null"/> value. A
/// missing <c>@tenant.id</c> (an <see cref="AlvoContext"/> with no <see cref="AlvoContext.Tenant"/>)
/// falls out of this same rule: it evaluates to <see langword="null"/>, so any comparison against
/// it is <see langword="false"/> — the absence of a tenant denies, it never matches "any tenant".
/// </para>
/// <para>
/// <b>Short-circuit.</b> <c>&amp;&amp;</c> and <c>||</c> use CEL's absorbing forms and never
/// evaluate the operand their left side already decided: <c>false &amp;&amp; x</c> is
/// <see langword="false"/> without evaluating <c>x</c>, <c>true || x</c> is <see langword="true"/>
/// without evaluating <c>x</c>.
/// </para>
/// <para>
/// <b><c>changed(f)</c>.</b> <see langword="false"/> when there is no previous row (a create
/// changes nothing); otherwise it compares the previous and current values of <c>f</c> with a
/// null-safe equality distinct from the comparison null rule above — <see langword="null"/> versus
/// <see langword="null"/> is unchanged, <see langword="null"/> versus a value is changed.
/// </para>
/// <para>
/// <b>Numeric widening.</b> A record's values arrive weakly typed, so a numeric comparison widens
/// both operands to <see langword="decimal"/> before comparing; a <see langword="double"/>/
/// <see langword="float"/> outside <see langword="decimal"/>'s range never throws — the comparison
/// is simply <see langword="false"/>. A <see cref="Guid"/> may be compared against a string that
/// parses as one, and a timestamp may be compared across <see cref="DateTimeOffset"/>,
/// <see cref="DateTime"/>, and a round-trip-parseable string, all using
/// <see cref="CultureInfo.InvariantCulture"/>. Any other cross-kind comparison is
/// <see langword="false"/> rather than throwing. When normalization fails for any reason — a
/// cross-kind pairing, an unrecognized CLR value — both <c>==</c> and <c>!=</c> evaluate to
/// <see langword="false"/>; <c>!=</c> is not "true by default" when the operands cannot even be
/// compared. This matches a SQL predicate wrapped in <c>COALESCE(..., FALSE)</c> rather than
/// three-valued <c>NULL</c> logic, and the SQL renderer must not "fix" this into a
/// <see langword="true"/> for <c>!=</c>.
/// </para>
/// <para>
/// <b>String collation caveat.</b> This interpreter compares strings with ordinal semantics
/// (<see cref="StringComparer.Ordinal"/>) — case-sensitive, culture-invariant, byte-for-byte. A SQL
/// backend's <c>==</c>/<c>!=</c> instead uses the compared column's collation, which on a
/// case-insensitive or otherwise non-deterministic collation can disagree with this ordinal
/// comparison for two strings that differ only in case or normalization. F3 does not support a
/// non-default column collation, so the two backends agree in every configuration F3 ships, but this
/// is a real divergence risk the differential test cannot see (it only proves the two backends agree
/// under the ordinal/default-collation assumption both are built on) — a future collation-aware
/// storage driver must revisit this class's remarks and <see cref="SqlPredicateRenderer"/>'s.
/// </para>
/// <para>
/// A null literal (<c>== null</c>/<c>!= null</c>) never reaches this interpreter — the compiler
/// rejects it and directs the author to <c>has(field)</c>/<c>!has(field)</c> instead, because
/// <c>owner_id == null</c> would otherwise always be <see langword="false"/> (per the null rule
/// above) regardless of whether <c>owner_id</c> is actually <see langword="null"/>, silently
/// making <c>!(owner_id == null)</c> always <see langword="true"/>.
/// </para>
/// <para>
/// No exception ever escapes <see cref="EvaluatePredicate"/> or <see cref="EvaluateScalar"/> for
/// any well-typed <see cref="CompiledExpression"/> and any <see cref="AlvoRecord"/>, including one
/// whose values are of an unexpected CLR type (a nested dictionary, an array, a
/// <c>System.Text.Json.JsonElement</c>) — such a value simply fails every type pattern below and
/// collapses to <see langword="false"/>/<see langword="null"/>.
/// </para>
/// </remarks>
internal static class CelInterpreter
{
    /// <summary>
    /// Evaluates a Rule or Condition expression's boolean verdict. The result is
    /// <see langword="false"/> unless the tree evaluates to exactly <see langword="true"/>.
    /// </summary>
    /// <param name="expression">The compiled Rule or Condition expression.</param>
    /// <param name="current">
    /// The row being written — the candidate image on a create/update. This must be the
    /// <b>complete post-image</b> of the row, every persisted field, never a partial PATCH
    /// payload: a field the caller simply didn't mention is indistinguishable from one explicitly
    /// set to <see langword="null"/>, so <c>changed(f)</c> (and any comparison reading <c>f</c>)
    /// reports a field a partial payload omits as changed to <see langword="null"/> even when its
    /// stored value never moved — turning a guard like <c>!changed(tenant_id)</c> into a denial
    /// of every ordinary PATCH that doesn't happen to repeat <c>tenant_id</c>.
    /// </param>
    /// <param name="previous">
    /// The row as it was before the change, or <see langword="null"/> on a create; only read by
    /// <c>old.</c> field references and <c>changed(...)</c>.
    /// </param>
    /// <param name="context">The caller/tenant context <c>@user</c>/<c>@tenant</c> resolve against.</param>
    public static bool EvaluatePredicate(CompiledExpression expression, AlvoRecord current, AlvoRecord? previous, AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var state = new EvalState(current, previous, context);
            return AsBoolean(Evaluate(expression.Root, state));
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>
    /// Evaluates a field-mask flag (<c>hidden</c>/<c>readOnly</c>) — a context-only Rule-profile
    /// expression with no row to read, so it is always evaluated against <see cref="AlvoRecord.Empty"/>
    /// and no previous row. This is deliberately the mirror image of <see cref="EvaluatePredicate"/>'s
    /// fail-safe direction: an authorization predicate must fail closed to <see langword="false"/>
    /// (deny) on anything it cannot resolve, but a mask fails closed the <b>other</b> way — a field is
    /// masked unless the expression resolves to exactly <see langword="false"/>, so an exception, or
    /// any evaluated value other than the two booleans, exposes nothing rather than silently
    /// widening access. Collapsing both to "false on trouble" (as <see cref="EvaluatePredicate"/> does)
    /// would be the wrong direction for a mask: it would disclose a field a rule author meant to hide
    /// from exactly the callers a resolution failure is most likely to affect.
    /// </summary>
    /// <param name="expression">The compiled, context-only Rule-profile expression.</param>
    /// <param name="context">The caller/tenant context <c>@user</c>/<c>@tenant</c> resolve against.</param>
    public static bool EvaluateMask(CompiledExpression expression, AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var state = new EvalState(AlvoRecord.Empty, null, context);
            return Evaluate(expression.Root, state) is not false;
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return true;
        }
    }

    /// <summary>
    /// Evaluates a Computed expression's scalar value. Arithmetic on a <see langword="null"/>
    /// operand, and a division by zero, both yield <see langword="null"/> rather than throwing —
    /// a generated column must never make a write crash.
    /// </summary>
    /// <param name="expression">The compiled Computed expression.</param>
    /// <param name="current">The row the computed value is derived from.</param>
    public static object? EvaluateScalar(CompiledExpression expression, AlvoRecord current)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(current);

        try
        {
            var state = new EvalState(current, null, null);
            return Evaluate(expression.Root, state);
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>
    /// Evaluates a <see cref="CelProfile.Mutate"/> expression's value against the candidate row, inside the
    /// write's own transaction. This is the <b>only</b> backend for that profile — a
    /// <see cref="CelProfile.Mutate"/> expression is never handed to <see cref="SqlPredicateRenderer"/>,
    /// which refuses its function calls by name — so the two-valued fold and the collation caveat this
    /// class's remarks describe have no second backend to agree with here.
    /// </summary>
    /// <param name="expression">The compiled <see cref="CelProfile.Mutate"/> expression.</param>
    /// <param name="current">
    /// The candidate row the mutate value is derived from — the <b>complete post-image</b>, for the same
    /// reason <see cref="EvaluatePredicate"/> requires one.
    /// </param>
    /// <param name="previous">The row as it was before the change, or <see langword="null"/> on a create.</param>
    /// <returns>
    /// The value to assign, or <see langword="null"/>. <see langword="null"/> is a value here rather than a
    /// failure signal — a fold over a missing field yields a missing field, not an empty string — so it is
    /// the caller's business whether writing it is allowed.
    /// </returns>
    public static object? EvaluateMutation(CompiledExpression expression, AlvoRecord current, AlvoRecord? previous)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(current);

        try
        {
            var state = new EvalState(current, previous, null);
            return Evaluate(expression.Root, state);
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static object? Evaluate(CelNode node, in EvalState state) => node switch
    {
        CelLiteral literal => literal.Value,
        CelFieldRef fieldRef => ResolveField(fieldRef, state),
        CelContextRef contextRef => ResolveContext(contextRef, state.Context),
        CelUnary unary => EvaluateUnary(unary, state),
        CelBinary binary => EvaluateBinary(binary, state),
        CelHas has => ResolveField(has.Field, state) is not null,
        CelConditional conditional => EvaluateConditional(conditional, state),
        CelChanged changed => EvaluateChanged(changed, state),
        CelCall call => EvaluateCall(call, state),
        _ => null,
    };

    private static string? EvaluateCall(CelCall call, in EvalState state) => call switch
    {
        { Name: CelCall.LowerAscii, Argument: { } argument } => LowerAscii(Evaluate(argument, state)),
        _ => null,
    };

    /// <summary>
    /// Applies <c>lowerAscii</c>. A value that is not a string is <see langword="null"/> rather than an
    /// error: the type checker already refused a non-string argument, so this can only be reached by a
    /// record whose stored value disagrees with its declared type, and this class never throws.
    /// </summary>
    private static string? LowerAscii(object? value) => value is string text ? FoldAsciiUpperCase(text) : null;

    /// <summary>
    /// Folds <c>A</c>–<c>Z</c> and nothing else — spelled out character by character, so nothing
    /// culture- or Unicode-sensitive can creep in later.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="string.ToLowerInvariant"/> is not equivalent and must never replace this.</b> It folds
    /// every non-ASCII letter it has a mapping for — <c>Ž</c>→<c>ž</c>, <c>Ä</c>→<c>ä</c>, <c>Σ</c>→<c>σ</c>,
    /// and <c>ẞ</c>→<c>ß</c>, which no reverse mapping recovers — and a stored value folded that way is a
    /// permanently wrong row: fixing the expression afterwards does not restore the bytes.
    /// </para>
    /// <para>
    /// <b>The set of characters it folds is a runtime detail, which is the deeper reason this loop is
    /// positive rather than a list of exceptions.</b> <c>İ</c> (U+0130) is the famous trap and is exactly
    /// where the reputation misleads: .NET 10's invariant casing leaves it <em>unchanged</em> (measured, not
    /// assumed), while a full Unicode case mapping folds it to two code points. Either way an author asked
    /// for an ASCII fold and must get one on every runtime and ICU version — which "fold A–Z" satisfies by
    /// construction and "fold, but skip the ones we know about" cannot.
    /// </para>
    /// </remarks>
    private static string FoldAsciiUpperCase(string value)
    {
        var folded = value.ToCharArray();
        for (var index = 0; index < folded.Length; index++)
        {
            if (folded[index] is >= 'A' and <= 'Z')
            {
                folded[index] = (char)(folded[index] + 32);
            }
        }

        return new string(folded);
    }

    private static object? ResolveField(CelFieldRef fieldRef, in EvalState state)
    {
        var record = fieldRef.State == CelRecordState.Old ? state.Previous : state.Current;
        return record?[fieldRef.FieldName];
    }

    private static object? ResolveContext(CelContextRef contextRef, AlvoContext? context)
    {
        if (context is null)
        {
            return null;
        }

        return contextRef.Value switch
        {
            CelContextValue.UserId => context.User.Value,
            CelContextValue.UserRoles => context.Roles.Select(role => role.Name).ToArray(),
            CelContextValue.TenantId => context.Tenant?.Value,
            _ => null,
        };
    }

    private static object? EvaluateUnary(CelUnary unary, in EvalState state) => unary.Operator switch
    {
        CelUnaryOperator.Not => !AsBoolean(Evaluate(unary.Operand, state)),
        CelUnaryOperator.Negate => Negate(Evaluate(unary.Operand, state)),
        _ => null,
    };

    private static object? EvaluateBinary(CelBinary binary, in EvalState state) => binary.Operator switch
    {
        CelBinaryOperator.And or CelBinaryOperator.Or => EvaluateLogical(binary, state),
        CelBinaryOperator.In => EvaluateIn(binary, state),
        CelBinaryOperator.Add or CelBinaryOperator.Subtract or CelBinaryOperator.Multiply or CelBinaryOperator.Divide =>
            EvaluateArithmetic(binary.Operator, Evaluate(binary.Left, state), Evaluate(binary.Right, state)),
        _ => EvaluateComparison(binary, state),
    };

    private static bool EvaluateLogical(CelBinary binary, in EvalState state)
    {
        var left = AsBoolean(Evaluate(binary.Left, state));
        return binary.Operator == CelBinaryOperator.And
            ? left && AsBoolean(Evaluate(binary.Right, state))
            : left || AsBoolean(Evaluate(binary.Right, state));
    }

    /// <summary>
    /// Evaluates role membership. The right operand's value is only ever the caller's role set, so
    /// <see cref="RoleMembership"/> asserts that it really is <c>@user.roles</c> first; the resulting
    /// <see cref="NotSupportedException"/> is caught by this class's entry points and collapses to a
    /// denial (or, for a mask, to "masked") rather than escaping — fail-closed in both directions.
    /// </summary>
    private static bool EvaluateIn(CelBinary binary, in EvalState state)
    {
        RoleMembership.RequireUserRolesOperand(binary.Right);

        var left = Evaluate(binary.Left, state);
        var right = Evaluate(binary.Right, state);
        return left is string text && right is IEnumerable<string> values && values.Contains(text, StringComparer.Ordinal);
    }

    private static bool EvaluateComparison(CelBinary binary, in EvalState state) =>
        Compare(Evaluate(binary.Left, state), Evaluate(binary.Right, state), binary.Operator);

    private static object? EvaluateConditional(CelConditional conditional, in EvalState state) =>
        AsBoolean(Evaluate(conditional.Condition, state))
            ? Evaluate(conditional.WhenTrue, state)
            : Evaluate(conditional.WhenFalse, state);

    private static bool EvaluateChanged(CelChanged changed, in EvalState state)
    {
        if (state.Previous is null)
        {
            return false;
        }

        return !ValuesEqual(state.Previous[changed.FieldName], state.Current[changed.FieldName]);
    }

    private static bool AsBoolean(object? value) => value is true;

    /// <summary>
    /// The single place the null rule is expressed: either operand missing collapses the whole
    /// comparison to <see langword="false"/>, for every relational and equality operator alike.
    /// </summary>
    private static bool Compare(object? left, object? right, CelBinaryOperator op)
    {
        if (left is null || right is null)
        {
            return false;
        }

        if (!TryNormalize(left, right, out var normalizedLeft, out var normalizedRight))
        {
            return false;
        }

        return op switch
        {
            CelBinaryOperator.Equal => ValuesEqualCore(normalizedLeft, normalizedRight),
            CelBinaryOperator.NotEqual => !ValuesEqualCore(normalizedLeft, normalizedRight),
            CelBinaryOperator.Less => CompareOrder(normalizedLeft, normalizedRight) is int lt && lt < 0,
            CelBinaryOperator.LessOrEqual => CompareOrder(normalizedLeft, normalizedRight) is int le && le <= 0,
            CelBinaryOperator.Greater => CompareOrder(normalizedLeft, normalizedRight) is int gt && gt > 0,
            CelBinaryOperator.GreaterOrEqual => CompareOrder(normalizedLeft, normalizedRight) is int ge && ge >= 0,
            _ => false,
        };
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return TryNormalize(left, right, out var normalizedLeft, out var normalizedRight)
            && ValuesEqualCore(normalizedLeft, normalizedRight);
    }

    private static bool ValuesEqualCore(object left, object right) => (left, right) switch
    {
        (decimal l, decimal r) => l == r,
        (string l, string r) => string.Equals(l, r, StringComparison.Ordinal),
        (bool l, bool r) => l == r,
        (Guid l, Guid r) => l == r,
        (DateTimeOffset l, DateTimeOffset r) => l == r,
        _ => false,
    };

    private static int? CompareOrder(object left, object right) => (left, right) switch
    {
        (decimal l, decimal r) => l.CompareTo(r),
        (string l, string r) => string.CompareOrdinal(l, r),
        (DateTimeOffset l, DateTimeOffset r) => l.CompareTo(r),
        _ => null,
    };

    private static bool TryNormalize(object left, object right, out object normalizedLeft, out object normalizedRight)
    {
        if (IsNumericValue(left) && IsNumericValue(right))
        {
            return TryNormalizeNumeric(left, right, out normalizedLeft, out normalizedRight);
        }

        if (IsDirectlyComparable(left) && left.GetType() == right.GetType())
        {
            normalizedLeft = left;
            normalizedRight = right;
            return true;
        }

        return TryNormalizeGuid(left, right, out normalizedLeft, out normalizedRight)
            || TryNormalizeTimestamp(left, right, out normalizedLeft, out normalizedRight);
    }

    private static bool IsDirectlyComparable(object value) => value is bool or string or Guid or DateTimeOffset;

    private static bool TryNormalizeNumeric(object left, object right, out object normalizedLeft, out object normalizedRight)
    {
        if (TryToDecimal(left, out var leftDecimal) && TryToDecimal(right, out var rightDecimal))
        {
            normalizedLeft = leftDecimal;
            normalizedRight = rightDecimal;
            return true;
        }

        normalizedLeft = left;
        normalizedRight = right;
        return false;
    }

    private static bool TryNormalizeGuid(object left, object right, out object normalizedLeft, out object normalizedRight)
    {
        if (left is Guid leftGuid && right is string rightText && Guid.TryParse(rightText, out var parsedRight))
        {
            normalizedLeft = leftGuid;
            normalizedRight = parsedRight;
            return true;
        }

        if (right is Guid rightGuid && left is string leftText && Guid.TryParse(leftText, out var parsedLeft))
        {
            normalizedLeft = parsedLeft;
            normalizedRight = rightGuid;
            return true;
        }

        normalizedLeft = left;
        normalizedRight = right;
        return false;
    }

    private static bool TryNormalizeTimestamp(object left, object right, out object normalizedLeft, out object normalizedRight)
    {
        normalizedLeft = left;
        normalizedRight = right;

        if (!IsTimestampCandidate(left) || !IsTimestampCandidate(right))
        {
            return false;
        }

        if (!TryToDateTimeOffset(left, out var leftOffset) || !TryToDateTimeOffset(right, out var rightOffset))
        {
            return false;
        }

        normalizedLeft = leftOffset;
        normalizedRight = rightOffset;
        return true;
    }

    private static bool IsTimestampCandidate(object value) => value is DateTimeOffset or DateTime or string;

    private static bool TryToDateTimeOffset(object value, out DateTimeOffset result)
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
            case string text:
                return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);
            default:
                result = default;
                return false;
        }
    }

    private static bool IsNumericValue(object value) => value is
        int or long or short or byte or sbyte or ushort or uint or ulong or float or double or decimal;

    private static bool TryToDecimal(object value, out decimal result)
    {
        switch (value)
        {
            case decimal d:
                result = d;
                return true;
            case int or long or short or byte or sbyte or ushort or uint or ulong:
                result = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return true;
            case double or float:
                return TryDoubleToDecimal(Convert.ToDouble(value, CultureInfo.InvariantCulture), out result);
            default:
                result = default;
                return false;
        }
    }

    private static bool TryDoubleToDecimal(double value, out decimal result)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= (double)decimal.MinValue || value >= (double)decimal.MaxValue)
        {
            result = default;
            return false;
        }

        try
        {
            result = (decimal)value;
            return true;
        }
        catch (OverflowException)
        {
            result = default;
            return false;
        }
    }

    private static decimal? EvaluateArithmetic(CelBinaryOperator op, object? left, object? right)
    {
        if (!TryPrepareArithmeticOperands(left, right, op, out var leftDecimal, out var rightDecimal))
        {
            return null;
        }

        try
        {
            return ApplyArithmetic(op, leftDecimal, rightDecimal);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static bool TryPrepareArithmeticOperands(
        object? left, object? right, CelBinaryOperator op, out decimal leftDecimal, out decimal rightDecimal)
    {
        leftDecimal = default;
        rightDecimal = default;

        if (left is null || right is null || !TryToDecimal(left, out leftDecimal) || !TryToDecimal(right, out rightDecimal))
        {
            return false;
        }

        return op != CelBinaryOperator.Divide || rightDecimal != 0m;
    }

    private static decimal? ApplyArithmetic(CelBinaryOperator op, decimal left, decimal right) => op switch
    {
        CelBinaryOperator.Add => left + right,
        CelBinaryOperator.Subtract => left - right,
        CelBinaryOperator.Multiply => left * right,
        CelBinaryOperator.Divide => left / right,
        _ => null,
    };

    private static object? Negate(object? value) => value is not null && TryToDecimal(value, out var decimalValue)
        ? -decimalValue
        : null;

    private readonly struct EvalState(AlvoRecord current, AlvoRecord? previous, AlvoContext? context)
    {
        public AlvoRecord Current { get; } = current;

        public AlvoRecord? Previous { get; } = previous;

        public AlvoContext? Context { get; } = context;
    }
}
