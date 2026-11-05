using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// A field's <c>validation</c> as the database will hold it: one canonical <see cref="CelProfile.Rule"/> predicate over the
/// row (HX-11 Rulings 13, 14, 30), which a provider writes as a <c>CHECK</c> constraint.
/// </summary>
/// <remarks>
/// <para>
/// <b>The frozen schema's "context: value, new" read as one row.</b> A bare <c>value</c> is the declaring field and
/// <c>new.&lt;f&gt;</c> is the row's own <c>&lt;f&gt;</c>; <c>old.</c> and <c>changed()</c> describe a transition a
/// <c>CHECK</c> never sees, so they are refused with the before-hook as the alternative.
/// </para>
/// <para>
/// <b>A bare <c>value</c> is ambiguous on an entity with a field named <c>value</c></b> (Ruling 14): binding it either way
/// silently would constrain the wrong column, so the author is asked to write the name.
/// </para>
/// <para>
/// A <c>duration('…')</c> constant is folded here, as the type checker folds it, so the stored text is the canonical
/// <c>duration('&lt;seconds&gt;s')</c> whichever way the author spelled it.
/// </para>
/// </remarks>
internal static class FieldValidation
{
    private const string TransitionFix = "A validation sees one row; a rule about a change belongs in a before-hook condition.";
    private const string SyntaxFix = "Write the validation as a CEL predicate over this row, e.g. \"value >= 0\".";

    /// <summary>The canonical CEL text of a field's <c>validation</c>, over the row's own field names.</summary>
    /// <param name="entity">The declaring entity.</param>
    /// <param name="field">The declaring field, which a bare <c>value</c> names.</param>
    /// <param name="source">The declared expression.</param>
    /// <param name="fieldNames">Every field the entity declares.</param>
    /// <returns>The canonical text.</returns>
    /// <exception cref="InvalidDataException">The expression does not parse, reads a transition, or reads an ambiguous <c>value</c>.</exception>
    internal static string Canonical(string entity, string field, string source, IReadOnlyCollection<string> fieldNames)
    {
        var (canonical, refused) = Rewrite(entity, field, source, fieldNames);
        return canonical ?? throw new InvalidDataException($"{refused!.Value.Message} {refused.Value.Fix}");
    }

    /// <summary>The validator's finding for a <c>validation</c> that cannot be rewritten, or <see langword="null"/>.</summary>
    /// <param name="entity">The declaring entity.</param>
    /// <param name="field">The declaring field.</param>
    /// <param name="source">The declared expression.</param>
    /// <param name="fieldNames">Every field the entity declares.</param>
    internal static DescriptorValidationError? Refusal(
        string entity, string field, string source, IReadOnlyCollection<string> fieldNames) =>
        Rewrite(entity, field, source, fieldNames).Refused is { } refused
            ? new(Pointer(entity, field), refused.Message, refused.Fix, DescriptorValidationSeverity.Error)
            : null;

    /// <summary>The refusal of a <c>validation</c> on a <c>storage: dynamic</c> entity, at its pointer (Ruling 30).</summary>
    /// <param name="entity">The dynamic entity.</param>
    /// <param name="field">The field declaring the validation.</param>
    internal static DescriptorValidationError DynamicRefusal(string entity, string field) =>
        new(Pointer(entity, field), DynamicMessage(entity, field), DynamicFix(entity), DescriptorValidationSeverity.Error);

    /// <summary>The sentence that refuses a <c>validation</c> on a <c>storage: dynamic</c> entity.</summary>
    /// <param name="entity">The dynamic entity.</param>
    /// <param name="field">The field declaring the validation.</param>
    internal static string DynamicMessage(string entity, string field) =>
        $"Field '{entity}.{field}' declares 'validation', which becomes a database CHECK constraint, and '{entity}' declares "
        + "'storage: dynamic' — dynamic entities have no table to hold one until #41 defines how their integrity is enforced.";

    /// <summary>The JSON pointer of a field's <c>validation</c>.</summary>
    /// <param name="entity">The declaring entity.</param>
    /// <param name="field">The declaring field.</param>
    internal static string Pointer(string entity, string field) => $"/entities/{entity}/fields/{field}/validation";

    private static string DynamicFix(string entity) => $"Remove 'validation', or make '{entity}' a physical entity.";

    private static (string? Canonical, (string Message, string Fix)? Refused) Rewrite(
        string entity, string field, string source, IReadOnlyCollection<string> fieldNames)
    {
        try
        {
            var tree = CelParser.Parse(source, CelFunctionCatalog.BuiltIns);
            return (CelText.Write(new Rewriter(entity, field, fieldNames).Visit(tree)), null);
        }
        catch (CelSyntaxException syntax)
        {
            return (null, ($"Field '{entity}.{field}' declares a 'validation' that does not parse: {syntax.Message}", syntax.FixSuggestion ?? SyntaxFix));
        }
        catch (RefusedException refused)
        {
            return (null, (refused.Message, refused.Fix));
        }
        catch (ArgumentException unspellable)
        {
            return (null, ($"Field '{entity}.{field}' declares a 'validation' that has no canonical CEL text: {WithoutParameter(unspellable)}", SyntaxFix));
        }
    }

    private static string WithoutParameter(ArgumentException failure) =>
        failure.ParamName is { } name
            ? failure.Message.Replace($" (Parameter '{name}')", string.Empty, StringComparison.Ordinal)
            : failure.Message;

    /// <summary>Rewrites the row's spellings to its field names and folds duration constants; refuses what one row cannot answer.</summary>
    private sealed class Rewriter(string entity, string field, IReadOnlyCollection<string> fieldNames)
    {
        private const string Value = "value";
        private const string ConditionalFix = "Write it with '&&' and '||', e.g. \"!has(starts_at) || value > starts_at\".";

        public CelNode Visit(CelNode node) => node switch
        {
            CelFieldRef reference => Field(reference),
            CelHas has => has with { Field = Field(has.Field) },
            CelChanged changed => throw Transition($"calls 'changed({changed.FieldName})'"),
            CelCall call => (CelNode?)FoldedDuration(call) ?? call with { Arguments = [.. call.Arguments.Select(Visit)] },
            CelUnary unary => unary with { Operand = Visit(unary.Operand) },
            CelBinary binary => binary with { Left = Visit(binary.Left), Right = Visit(binary.Right) },
            CelConditional => throw new RefusedException(
                $"Field '{entity}.{field}' declares a validation that uses the conditional '?:', which a validation cannot hold.",
                ConditionalFix),
            _ => node,
        };

        private CelFieldRef Field(CelFieldRef reference) => reference.State switch
        {
            CelRecordState.Old => throw Transition($"reads 'old.{reference.FieldName}'"),
            CelRecordState.New => reference with { State = CelRecordState.Current },
            _ when reference.FieldName == Value => reference with { FieldName = OwnName() },
            _ => reference,
        };

        private string OwnName() =>
            field != Value && fieldNames.Contains(Value)
                ? throw new RefusedException(
                    $"Field '{entity}.{field}' declares a validation reading 'value', and '{entity}' also declares a field named "
                    + $"'value' — so it could mean '{field}' or the field 'value'.",
                    $"Write the field's own name: '{field}'.")
                : field;

        private static CelLiteral? FoldedDuration(CelCall call) =>
            call is { Name: CelBuiltInFunctions.DurationName, Arguments: [CelLiteral { Value: string text }] }
            && CelDuration.TryParse(text, out var span, out _)
                ? new CelLiteral(CelValueType.Duration, span)
                : null;

        private RefusedException Transition(string what) =>
            new($"Field '{entity}.{field}' declares a validation that {what}: a validation is a CHECK constraint over one row, "
                + "which sees no value before the write and no change.", TransitionFix);
    }

    /// <summary>A rewrite refusal, carried out of the recursion to the one place that shapes it.</summary>
    private sealed class RefusedException(string message, string fix) : Exception(message)
    {
        public string Fix { get; } = fix;
    }
}
