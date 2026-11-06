using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// The validator's pass over every rollup's <c>where</c>: compiled for <see cref="CelProfile.Rule"/> against the
/// <em>child</em> entity, held to the stored-predicate surface, and rendered as a constraint — so a filter the recompute
/// cannot carry is a structured refusal at <c>/entities/&lt;parent&gt;/fields/&lt;field&gt;/rollup/where</c> when the descriptor
/// is saved, never an exception inside a child write (HX-11 Ruling 31).
/// </summary>
/// <remarks>
/// <para>
/// <b>A stored predicate, like a <c>validation</c>.</b> The recompute runs the filter in SQL on every child write, with no
/// caller and no instant: a caller read or <c>now()</c> would make the stored aggregate depend on who wrote last, or when.
/// So it gets <see cref="CheckConstraintCheck"/>'s three steps — compile, the surface, the render over
/// <see cref="NeutralFields"/> — with the child as the row.
/// </para>
/// <para>
/// A rollup whose child the schema does not hold is the mapper's refusal (<c>RollupResolver</c>), never reached here.
/// </para>
/// </remarks>
internal static class RollupFilterCheck
{
    private const string Slot = "A rollup filter";
    private const string CompileFix = "Write the filter as a comparison over the child's own fields and constants, e.g. \"status == 'held'\".";
    private const string LiteralFix = "Remove the control character, line or paragraph separator, or lone surrogate from the text constant.";
    private const string HiddenFix = "Filter on child fields every reader of the parent may see, or unhide the field.";

    private static readonly IPredicateRenderer _renderer = new SqlPredicateRenderer();

    /// <summary>Every refusal over <paramref name="schema"/>'s rollup filters, each at its own pointer.</summary>
    /// <param name="schema">The mapped schema, whose child entities the filters are type-checked against.</param>
    /// <param name="compiler">The compiler the driver compiles the filter with.</param>
    /// <param name="descriptor">The descriptor, whose <c>hidden</c> flags decide which child fields a filter may read.</param>
    internal static IEnumerable<DescriptorValidationError> Errors(SchemaModel schema, ICelCompiler compiler, AlvoDescriptor descriptor)
    {
        var reads = new ValidationReadSet(schema, descriptor);
        return
            from parent in schema.Entities
            from field in parent.Fields
            where field.Rollup?.Where is not null
            let child = Child(schema, field.Rollup!)
            where child is not null
            from error in ErrorsFor(parent, field, child, compiler, reads)
            select error;
    }

    /// <summary>The pointer of <paramref name="parent"/>'s field <paramref name="field"/>'s filter.</summary>
    /// <param name="parent">The entity declaring the rollup.</param>
    /// <param name="field">The rollup field.</param>
    internal static string Pointer(string parent, string field) => $"/entities/{parent}/fields/{field}/rollup/where";

    private static EntitySchema? Child(SchemaModel schema, RollupSchema rollup) =>
        schema.Entities.FirstOrDefault(entity => string.Equals(entity.Name, rollup.From, StringComparison.Ordinal));

    private static IEnumerable<DescriptorValidationError> ErrorsFor(
        EntitySchema parent, FieldSchema field, EntitySchema child, ICelCompiler compiler, ValidationReadSet reads)
    {
        var result = compiler.Compile(field.Rollup!.Where!, CelProfile.Rule, child);
        if (!result.IsSuccess)
        {
            return result.Errors.Select(error => Refusal(parent, field, $"does not compile: {error.Message}", error.FixSuggestion ?? CompileFix));
        }

        IEnumerable<DescriptorValidationError> hidden = HiddenReadRefusal(parent, field, child, result.Expression!, reads) is { } refused ? [refused] : [];
        return hidden.Concat(ShapeErrors(parent, field, result.Expression!));
    }

    /// <summary>
    /// The refusal of a filter whose closed read set — the child's derived fields seen through, as a validation's are —
    /// holds a hidden field (HX-11 Ruling 71): which children a filtered aggregate counts is a predicate oracle over every
    /// field the filter reads, answered to anyone who can read the parent.
    /// </summary>
    /// <param name="parent">The entity declaring the rollup.</param>
    /// <param name="field">The rollup field.</param>
    /// <param name="child">The aggregated entity, whose row the filter reads.</param>
    /// <param name="expression">The compiled filter.</param>
    /// <param name="reads">The read-set closure over the schema and the descriptor's hidden flags.</param>
    private static DescriptorValidationError? HiddenReadRefusal(
        EntitySchema parent, FieldSchema field, EntitySchema child, CompiledExpression expression, ValidationReadSet reads)
    {
        var exposed = reads.Closure(child, CelDataOrigin.FieldsRead(expression.Root)).Where(reads.IsHidden).ToList();
        if (exposed.Count == 0)
        {
            return null;
        }

        var named = string.Join(", ", exposed.Select(read => $"'{read.Field}'").Order(StringComparer.Ordinal));
        return Refusal(
            parent,
            field,
            $"reads a hidden child field ({named}): the aggregate would disclose a value its readers cannot see.",
            HiddenFix);
    }

    private static IEnumerable<DescriptorValidationError> ShapeErrors(EntitySchema parent, FieldSchema field, CompiledExpression expression)
    {
        var surface = StoredPredicateSurface.Refusals(expression, Slot);
        return surface.Count > 0
            ? surface.Select(error => Refusal(parent, field, $"cannot be stored: {error.Message}", error.FixSuggestion ?? CompileFix))
            : RenderErrors(parent, field, expression);
    }

    private static IEnumerable<DescriptorValidationError> RenderErrors(EntitySchema parent, FieldSchema field, CompiledExpression expression)
    {
        try
        {
            _renderer.RenderConstraint(expression, NeutralFields.Instance);
            return [];
        }
        catch (NotSupportedException failure)
        {
            return [Refusal(parent, field, $"cannot be written into the recompute: {failure.Message}", LiteralFix)];
        }
    }

    private static DescriptorValidationError Refusal(EntitySchema parent, FieldSchema field, string reason, string fix) =>
        new(
            Pointer(parent.Name, field.Name),
            $"Field '{parent.Name}.{field.Name}' declares a 'rollup.where' that {reason}",
            fix,
            DescriptorValidationSeverity.Error);
}
