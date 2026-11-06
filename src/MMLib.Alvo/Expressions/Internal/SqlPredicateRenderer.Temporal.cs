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
    private static bool IsTemporalComparison(CelBinary comparison, RenderScope scope) =>
        IsTemporalOperand(comparison.Left) || IsTemporalOperand(comparison.Right)
        || CelTemporal.TypeOf(comparison.Left) == CelValueType.Duration
        || CelTemporal.TypeOf(comparison.Right) == CelValueType.Duration
        || IsImageInstant(comparison.Left, scope) || IsImageInstant(comparison.Right, scope);

    /// <summary>
    /// An outer timestamp read from a post-image is a constant instant, so its comparison takes the temporal path: a
    /// <c>datetime</c> column against it keeps the sargable leaf, and a <c>date</c> column meets it in microseconds rather
    /// than as text.
    /// </summary>
    private static bool IsImageInstant(CelNode node, RenderScope scope) =>
        node is CelOuterFieldRef { Type: CelValueType.Timestamp } && scope.Outer is RowSource.Image;

    private static bool IsTemporalOperand(CelNode node) =>
        node is CelBinary binary && CelTemporal.IsTemporal(binary) || node is CelCall { Name: CelCall.Now };

    private static bool IsTemporalRangeError(CelFunctionException failure) =>
        failure.Reason is CelTemporal.TimestampRange or CelTemporal.DurationRange;

    private static SqlPredicate DeniedWhole(IFieldSqlRenderer fields) =>
        new(fields.RenderBooleanPredicate(false), new Dictionary<string, object?>());

    private static PredicateFragment RenderTemporalComparison(CelBinary comparison, RenderScope scope)
    {
        if (TryFoldedLeafComparison(comparison, scope) is { } leaf)
        {
            return leaf;
        }

        var left = Microseconds(comparison.Left, scope);
        var right = Microseconds(comparison.Right, scope);
        var sql = $"{left} {ComparisonOperatorText(comparison.Operator)} {right}";
        return new PredicateFragment(scope.Fields.RenderTwoValued(sql), true);
    }

    /// <summary>
    /// The sargable shape Ruling 4 promises: a bare <c>datetime</c> field against a side that folds to one instant renders
    /// <c>"field" OP @instant</c>. Anything else answers <see langword="null"/> and takes the microsecond path.
    /// </summary>
    private static PredicateFragment? TryFoldedLeafComparison(CelBinary comparison, RenderScope scope)
    {
        var fieldOnLeft = comparison.Left is CelFieldRef;
        if ((fieldOnLeft ? comparison.Left : comparison.Right) is not CelFieldRef field
            || !IsDateTimeField(field, scope.Entity)
            || Folded(fieldOnLeft ? comparison.Right : comparison.Left, scope) is not DateTimeOffset instant)
        {
            return null;
        }

        var column = RenderField(field, scope);
        var bound = scope.Sink.Constant(instant, scope.Fields);
        var (left, right) = scope.Fields.RenderComparableOperands(
            fieldOnLeft ? column : bound, fieldOnLeft ? bound : column, CelValueType.Timestamp);
        var sql = $"{left} {ComparisonOperatorText(comparison.Operator)} {right}";
        return new PredicateFragment(scope.Fields.RenderTwoValued(sql), true);
    }

    private static bool IsDateTimeField(CelFieldRef field, EntitySchema entity) =>
        entity.Fields.FirstOrDefault(declared => string.Equals(declared.Name, field.FieldName, StringComparison.Ordinal))
            is { Type: FieldType.DateTime };

    private static string Microseconds(CelNode node, RenderScope scope) =>
        Folded(node, scope) is { } constant
            ? scope.Sink.Integer(ToMicroseconds(constant), scope.Fields)
            : node switch
            {
                CelFieldRef field => Epoch(RenderField(field, scope), scope.Fields),
                CelOuterFieldRef outer => Epoch(RenderOuterField(outer, scope), scope.Fields),
                CelBinary binary when CelTemporal.IsTemporal(binary) => MicrosecondArithmetic(binary, scope),
                _ => throw Unsupported(node),
            };

    private static string MicrosecondArithmetic(CelBinary binary, RenderScope scope)
    {
        var left = Microseconds(binary.Left, scope);
        var right = Microseconds(binary.Right, scope);
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
    private static object? Folded(CelNode node, RenderScope scope) => node switch
    {
        CelLiteral { Type: CelValueType.Duration or CelValueType.Timestamp } literal => literal.Value,
        CelCall { Name: CelCall.Now } => BoundInstant(),
        CelOuterFieldRef outer when scope.Outer is RowSource.Image => OuterImageValue(outer, scope),
        CelBinary binary when CelTemporal.IsTemporal(binary)
            && Folded(binary.Left, scope) is { } left && Folded(binary.Right, scope) is { } right =>
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
