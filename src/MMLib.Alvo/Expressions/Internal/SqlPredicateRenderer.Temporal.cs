using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Duration arithmetic in SQL (HX-11 Rulings 4, 11): a subtree with no field folds into one bound value in-process; one
/// that reads a field is rendered in whole microseconds since the Unix epoch through the dialect.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fold uses the interpreter's own arithmetic</b> (<see cref="CelTemporal.Apply"/>, fail-closed), so a folded
/// <c>now() - duration('24h')</c> is the instant the in-process <c>WITH CHECK</c> computes. A fold that leaves CEL's range
/// throws its range error up to <see cref="Render(CompiledExpression, AlvoContext, IFieldSqlRenderer, string)"/>, which
/// denies the <b>whole</b> predicate — the interpreter's answer for the same rule (Ruling 57) — so no <c>!</c> above the
/// comparison can turn the error into a grant.
/// </para>
/// <para>
/// <b>Only a <c>datetime</c> field keeps the sargable leaf</b> (<c>"created_at" &gt; @p0</c>). A <c>date</c> field always
/// enters the microsecond domain (review I10): SQLite would compare its <c>YYYY-MM-DD</c> text with a bound timestamp's
/// text, and PostgreSQL would cast it in the session time zone.
/// </para>
/// </remarks>
internal sealed partial class SqlPredicateRenderer
{
    private static bool IsTemporalComparison(CelBinary comparison) =>
        IsTemporalOperand(comparison.Left) || IsTemporalOperand(comparison.Right)
        || CelTemporal.TypeOf(comparison.Left) == CelValueType.Duration
        || CelTemporal.TypeOf(comparison.Right) == CelValueType.Duration;

    private static bool IsTemporalOperand(CelNode node) =>
        node is CelBinary binary && CelTemporal.IsTemporal(binary) || node is CelCall { Name: CelCall.Now };

    private static bool IsTemporalRangeError(CelFunctionException failure) =>
        failure.Reason is CelTemporal.TimestampRange or CelTemporal.DurationRange;

    private static SqlPredicate DeniedWhole(IFieldSqlRenderer fields) =>
        new(fields.RenderBooleanPredicate(false), new Dictionary<string, object?>());

    private static PredicateFragment RenderTemporalComparison(
        CelBinary comparison, EntitySchema entity, IFieldSqlRenderer fields, ILiteralSink sink)
    {
        if (TryFoldedLeafComparison(comparison, entity, fields, sink) is { } leaf)
        {
            return leaf;
        }

        var left = Microseconds(comparison.Left, entity, fields, sink);
        var right = Microseconds(comparison.Right, entity, fields, sink);
        return new PredicateFragment(fields.RenderTwoValued($"{left} {ComparisonOperatorText(comparison.Operator)} {right}"), true);
    }

    /// <summary>
    /// The sargable shape Ruling 4 promises: a bare <c>datetime</c> field against a side that folds to one instant renders
    /// <c>"field" OP @instant</c>. Anything else answers <see langword="null"/> and takes the microsecond path.
    /// </summary>
    private static PredicateFragment? TryFoldedLeafComparison(
        CelBinary comparison, EntitySchema entity, IFieldSqlRenderer fields, ILiteralSink sink)
    {
        var fieldOnLeft = comparison.Left is CelFieldRef;
        if ((fieldOnLeft ? comparison.Left : comparison.Right) is not CelFieldRef field
            || !IsDateTimeField(field, entity)
            || Folded(fieldOnLeft ? comparison.Right : comparison.Left) is not DateTimeOffset instant)
        {
            return null;
        }

        var column = RenderField(field, entity, fields);
        var bound = sink.Constant(instant, fields);
        var (left, right) = fields.RenderComparableOperands(
            fieldOnLeft ? column : bound, fieldOnLeft ? bound : column, CelValueType.Timestamp);
        return new PredicateFragment(fields.RenderTwoValued($"{left} {ComparisonOperatorText(comparison.Operator)} {right}"), true);
    }

    private static bool IsDateTimeField(CelFieldRef field, EntitySchema entity) =>
        entity.Fields.FirstOrDefault(declared => string.Equals(declared.Name, field.FieldName, StringComparison.Ordinal))
            is { Type: FieldType.DateTime };

    private static string Microseconds(CelNode node, EntitySchema entity, IFieldSqlRenderer fields, ILiteralSink sink) =>
        Folded(node) is { } constant
            ? sink.Integer(ToMicroseconds(constant), fields)
            : node switch
            {
                CelFieldRef field => Epoch(RenderField(field, entity, fields), fields),
                CelBinary binary when CelTemporal.IsTemporal(binary) => MicrosecondArithmetic(binary, entity, fields, sink),
                _ => throw Unsupported(node),
            };

    private static string MicrosecondArithmetic(CelBinary binary, EntitySchema entity, IFieldSqlRenderer fields, ILiteralSink sink)
    {
        var left = Microseconds(binary.Left, entity, fields, sink);
        var right = Microseconds(binary.Right, entity, fields, sink);
        return $"({left} {(binary.Operator == CelBinaryOperator.Add ? "+" : "-")} {right})";
    }

    private static string Epoch(string timestampSql, IFieldSqlRenderer fields) =>
        fields.RenderEpochMicroseconds(timestampSql)
        ?? throw new NotSupportedException(
            $"{fields.GetType().Name} declares no epoch rendering (IFieldSqlRenderer.RenderEpochMicroseconds), so duration "
            + "arithmetic over a field cannot be evaluated by this engine.");

    /// <summary>
    /// The constant a subtree with no field folds to, or <see langword="null"/> when it reads a field. Out-of-range
    /// arithmetic throws the fail-closed range error, which the entry point turns into a denial.
    /// </summary>
    private static object? Folded(CelNode node) => node switch
    {
        CelLiteral { Type: CelValueType.Duration or CelValueType.Timestamp } literal => literal.Value,
        CelCall { Name: CelCall.Now } => BoundInstant(),
        CelBinary binary when CelTemporal.IsTemporal(binary) && Folded(binary.Left) is { } left && Folded(binary.Right) is { } right =>
            CelTemporal.Apply(binary.Operator, left, right, failClosed: true),
        _ => null,
    };

    private static long ToMicroseconds(object value) => value switch
    {
        TimeSpan duration => duration.Ticks / TimeSpan.TicksPerMicrosecond,
        DateTimeOffset instant => (instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / TimeSpan.TicksPerMicrosecond,
        _ => throw new InvalidOperationException($"'{value.GetType().Name}' is not a temporal constant."),
    };
}
