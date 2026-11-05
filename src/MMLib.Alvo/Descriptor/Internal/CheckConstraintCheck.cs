using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// The validator's pass over every field's <c>validation</c>: compiled for <see cref="CelProfile.Rule"/> against its
/// entity, held to the stored-predicate surface, and rendered as a constraint — so a validation no <c>CHECK</c> can carry
/// is a structured refusal at <c>/entities/&lt;e&gt;/fields/&lt;f&gt;/validation</c> when the descriptor is saved, never an
/// exception when the migrator first builds its model (HX-11 Rulings 13, 16, 29).
/// </summary>
/// <remarks>
/// <para>
/// <b>It renders as well as compiles,</b> for the reason <see cref="ComputedFieldCheck"/> does: a text constant a SQL
/// literal cannot carry is refused by the renderer, past the compiler. It asks the core's own
/// <see cref="SqlPredicateRenderer"/> over <see cref="NeutralFields"/>, whose literal rule mirrors the driver's.
/// </para>
/// <para>
/// The mapper has already rewritten the declaration into <see cref="FieldSchema.CheckExpression"/>; a rewrite refusal
/// (a syntax error, <c>old.</c>, <c>changed()</c>, an ambiguous <c>value</c>) never reaches here, because the semantic
/// pass reports it and the mapper refuses the model.
/// </para>
/// </remarks>
internal static class CheckConstraintCheck
{
    private const string Slot = "A validation";
    private const string CompileFix = "Write the validation as a comparison over this row's own fields and constants, e.g. \"value >= 0\".";
    private const string LiteralFix = "Remove the control character, line or paragraph separator, or lone surrogate from the text constant.";
    private const string HiddenFix =
        "Validate the field against its own value only, or enforce the cross-field rule in a before-hook, whose refusal names no stored value.";

    private static readonly IPredicateRenderer _renderer = new SqlPredicateRenderer();

    /// <summary>Every refusal over <paramref name="schema"/>'s validated fields, each at its own pointer.</summary>
    /// <param name="schema">The mapped schema, whose entities the expressions are type-checked against.</param>
    /// <param name="compiler">The compiler the driver compiles the constraint with.</param>
    /// <param name="descriptor">The descriptor, whose <c>hidden</c> flags decide which fields a validation may read across.</param>
    internal static IEnumerable<DescriptorValidationError> Errors(SchemaModel schema, ICelCompiler compiler, AlvoDescriptor descriptor)
    {
        var reads = new ValidationReadSet(schema, descriptor);
        return
            from entity in schema.Entities
            from field in entity.Fields
            where field.CheckExpression is not null
            from error in ErrorsFor(entity, field, compiler, reads)
            select error;
    }

    private static IEnumerable<DescriptorValidationError> ErrorsFor(
        EntitySchema entity, FieldSchema field, ICelCompiler compiler, ValidationReadSet reads)
    {
        var result = compiler.Compile(field.CheckExpression!, CelProfile.Rule, entity);
        if (!result.IsSuccess)
        {
            return result.Errors.Select(error => Refusal(entity, field, $"does not compile: {error.Message}", error.FixSuggestion ?? CompileFix));
        }

        IEnumerable<DescriptorValidationError> hidden = HiddenReadRefusal(entity, field, result.Expression!, reads) is { } refused ? [refused] : [];
        return hidden.Concat(ShapeErrors(entity, field, result.Expression!));
    }

    private static IEnumerable<DescriptorValidationError> ShapeErrors(EntitySchema entity, FieldSchema field, CompiledExpression expression)
    {
        var surface = StoredPredicateSurface.Refusals(expression, Slot);
        return surface.Count > 0
            ? surface.Select(error => Refusal(entity, field, $"cannot be a CHECK constraint: {error.Message}", error.FixSuggestion ?? CompileFix))
            : RenderErrors(entity, field, expression);
    }

    /// <summary>
    /// The refusal of a validation whose closed read set — derived fields seen through to what they derive from — holds a
    /// hidden field and anything beyond the validated field itself (HX-11 Ruling 61): the 422 or success a write answers
    /// would let a caller bisect a hidden stored value — spec H1, which keeps hidden fields out of access rules, carried
    /// over to validations. Only a stored field validated against its own value alone is exempt, because only then is the
    /// judged value one the caller sent.
    /// </summary>
    /// <param name="entity">The declaring entity.</param>
    /// <param name="field">The validated field.</param>
    /// <param name="expression">The compiled validation.</param>
    /// <param name="reads">The read-set closure over the schema and the descriptor's hidden flags.</param>
    private static DescriptorValidationError? HiddenReadRefusal(
        EntitySchema entity, FieldSchema field, CompiledExpression expression, ValidationReadSet reads)
    {
        var own = new ValidationReadSet.Read(entity.Name, field.Name);
        var closed = reads.Closure(entity, [field.Name, .. CelDataOrigin.FieldsRead(expression.Root)]);
        var exposed = closed.Where(reads.IsHidden).ToList();
        if (closed.Count == 1 || exposed.Count == 0)
        {
            return null;
        }

        return Refusal(
            entity,
            field,
            $"reads {Named(entity, closed.Where(read => read != own))} across a hidden field ({Named(entity, exposed)}): whether a "
            + "write is refused would disclose a stored value the caller cannot read.",
            HiddenFix);
    }

    private static string Named(EntitySchema entity, IEnumerable<ValidationReadSet.Read> reads) =>
        Quoted(reads
            .Select(read => string.Equals(read.Entity, entity.Name, StringComparison.Ordinal) ? read.Field : $"{read.Entity}.{read.Field}")
            .Order(StringComparer.Ordinal));

    private static string Quoted(IEnumerable<string> names) => string.Join(", ", names.Select(name => $"'{name}'"));

    private static IEnumerable<DescriptorValidationError> RenderErrors(EntitySchema entity, FieldSchema field, CompiledExpression expression)
    {
        try
        {
            _renderer.RenderConstraint(expression, NeutralFields.Instance);
            return [];
        }
        catch (NotSupportedException failure)
        {
            return [Refusal(entity, field, $"cannot be written into a CHECK constraint: {failure.Message}", LiteralFix)];
        }
    }

    private static DescriptorValidationError Refusal(EntitySchema entity, FieldSchema field, string reason, string fix) =>
        new(
            FieldValidation.Pointer(entity.Name, field.Name),
            $"Field '{entity.Name}.{field.Name}' declares a 'validation' that {reason}",
            fix,
            DescriptorValidationSeverity.Error);
}
