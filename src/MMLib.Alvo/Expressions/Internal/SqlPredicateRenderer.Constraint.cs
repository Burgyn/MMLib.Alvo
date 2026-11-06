using System.Globalization;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// The constraint entry point (HX-11 Rulings 16, 17): the predicate walk with every constant written inline, for a place
/// that has no bind parameters — a <c>CHECK</c>, a stored rollup filter.
/// </summary>
/// <remarks>
/// <para>
/// <b>The literals are the descriptor author's, never a request's</b> — the stated deviation from "translation never
/// interpolates user input" (Ruling 16). A number is written invariant-culture, a boolean as the dialect's own literal, text
/// only through the dialect's <see cref="IFieldSqlRenderer.RenderStringLiteral"/> (a declined text refuses the render), and a
/// duration as its whole microseconds. Acceptance (5) is the guard: on both engines, no text the writer accepts escapes the
/// <c>CHECK</c> it is written into.
/// </para>
/// <para>
/// <b>Nothing outside the row reaches the writer.</b> <see cref="StoredPredicateSurface"/> refuses the caller, the tenant, a
/// role test and <c>now()</c> before the walk, and the walk runs with no caller at all, so a context read that slipped past
/// would throw rather than resolve. A <c>uuid</c> constant is refused too: no CEL literal of one reaches a Rule, and refusing
/// keeps DDL from gaining a format by accident.
/// </para>
/// <para>
/// <b>A <c>timestamp</c> constant</b> — a Rule's folded <c>timestamp('…')</c> — is written as the text a stored instant is:
/// UTC, <c>yyyy-MM-dd HH:mm:ss.FFFFFFF+00:00</c>, the form SQLite's provider stores and compares a <c>datetime</c> in, and a
/// literal PostgreSQL resolves to a <c>timestamptz</c> constant when the statement is parsed — so a partial index's
/// predicate stays <c>IMMUTABLE</c> (HX-12 D11). It goes through the dialect's own text-literal writer like any text.
/// </para>
/// </remarks>
internal sealed partial class SqlPredicateRenderer
{
    /// <inheritdoc />
    string IPredicateRenderer.RenderConstraint(CompiledExpression expression, IFieldSqlRenderer fields)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(fields);
        RequireConstraintProfile(expression);
        RefuseWhatIsNotTheRow(expression);

        try
        {
            var rendered = RenderPredicate(expression.Root, expression.Entity, context: null, fields, InlineLiterals.Instance);
            return rendered.IsTwoValued ? rendered.Sql : fields.RenderTwoValued(rendered.Sql);
        }
        catch (CelFunctionException failure) when (IsTemporalRangeError(failure))
        {
            throw new NotSupportedException(
                $"'{expression.Source}' holds constant duration arithmetic outside CEL's range ({failure.Reason}); a constraint "
                + "that refuses every row is not written.",
                failure);
        }
    }

    private static void RequireConstraintProfile(CompiledExpression expression)
    {
        if (expression.Profile != CelProfile.Rule)
        {
            throw new InvalidOperationException(
                $"'{expression.Source}' was compiled for the {expression.Profile} profile; a constraint is a {CelProfile.Rule} "
                + "predicate over its own row.");
        }
    }

    private static void RefuseWhatIsNotTheRow(CompiledExpression expression)
    {
        var refusals = StoredPredicateSurface.Refusals(expression, "A constraint");
        if (refusals.Count > 0)
        {
            throw new NotSupportedException(string.Join(" ", refusals.Select(refusal => refusal.Message)));
        }
    }

    /// <summary>Writes each constant into the SQL text: the one literal writer for DDL (Ruling 16).</summary>
    private sealed class InlineLiterals : ILiteralSink
    {
        public static InlineLiterals Instance { get; } = new();

        public string Constant(object? value, IFieldSqlRenderer fields) => value switch
        {
            long number => Integer(number, fields),
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            string text => fields.RenderStringLiteral(text) ?? throw DeclinedText(fields),
            DateTimeOffset instant => fields.RenderStringLiteral(StoredText(instant)) ?? throw DeclinedText(fields),
            _ => throw new NotSupportedException(
                $"A {(value is null ? "null" : value.GetType().Name)} constant has no inline form in a constraint; "
                + "only numbers, booleans, text, timestamps and durations are written into one."),
        };

        private static string StoredText(DateTimeOffset instant) =>
            instant.ToUniversalTime().ToString("yyyy'-'MM'-'dd HH':'mm':'ss.FFFFFFFzzz", CultureInfo.InvariantCulture);

        public string Integer(long value, IFieldSqlRenderer fields) => value.ToString(CultureInfo.InvariantCulture);

        private static NotSupportedException DeclinedText(IFieldSqlRenderer fields) =>
            new("A text constant holds a character a SQL literal cannot carry (a control character, a line or paragraph "
                + $"separator, or a lone surrogate), or {fields.GetType().Name} writes no text literal; it cannot be written "
                + "into a constraint.");
    }
}
