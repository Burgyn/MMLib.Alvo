using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Renders one field's <see cref="FieldSchema.CheckExpression"/> into the SQL its CHECK constraint carries:
/// <c>(&lt;field&gt; IS NULL OR &lt;two-valued predicate&gt;)</c> (HX-11 Rulings 15, 16).
/// </summary>
/// <remarks>
/// <para>
/// <b>Compiled, never spliced</b> — the same discipline <see cref="ComputedColumnSql"/> keeps for a generated column. The
/// text is produced by <see cref="IPredicateRenderer"/>'s constraint entry point from a CEL AST the compiler accepted for
/// <see cref="CelProfile.Rule"/>, so it holds this entity's delimited columns, operators, and constants written inline
/// through the dialect's own literal rules; it never holds the descriptor's string.
/// </para>
/// <para>
/// <b>The declaring field's own NULL passes.</b> SQL's <c>CHECK</c> passes an unknown verdict and JSON Schema passes an
/// absent value; <c>required</c> is the facet for presence. Every other operand goes through the renderer's two-valued
/// fold, so a comparison with another empty field is false and refuses the row, as the interpreter answers it.
/// </para>
/// <para>
/// <b>A backstop, not the gate.</b> The core's validator compiles and renders every validation the same way
/// (<c>CheckConstraintCheck</c>) when the descriptor is saved; this still refuses, at the same pointer, for a host that
/// replaced the renderer or built a model from a schema no validator saw.
/// </para>
/// </remarks>
internal sealed class CheckConstraintSql
{
    private readonly ICelCompiler _compiler;
    private readonly IPredicateRenderer _predicates;
    private readonly IFieldSqlRenderer _fields;

    /// <summary>Initializes a renderer over one driver's expression seam.</summary>
    /// <param name="compiler">The core's CEL compiler.</param>
    /// <param name="predicates">The core's renderer, entered through its constraint entry point.</param>
    /// <param name="fields">This driver's field renderer, which delimits every identifier and writes every literal.</param>
    internal CheckConstraintSql(ICelCompiler compiler, IPredicateRenderer predicates, IFieldSqlRenderer fields)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(fields);
        _compiler = compiler;
        _predicates = predicates;
        _fields = fields;
    }

    /// <summary>
    /// The SQL of <paramref name="field"/>'s CHECK constraint, or <see langword="null"/> when the field declares no
    /// validation.
    /// </summary>
    /// <param name="entity">The entity the field belongs to — what the CEL is type-checked against.</param>
    /// <param name="field">The validated field.</param>
    /// <exception cref="DescriptorValidationException">
    /// The validation does not compile for <see cref="CelProfile.Rule"/>, or has no constraint rendering.
    /// </exception>
    internal string? For(EntitySchema entity, FieldSchema field)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(field);

        if (field.CheckExpression is not { } source)
        {
            return null;
        }

        var rendered = Rendered(entity, field, Compiled(entity, field, source));
        return $"({_fields.RenderField(entity, field.Name)} IS NULL OR {rendered})";
    }

    private CompiledExpression Compiled(EntitySchema entity, FieldSchema field, string source)
    {
        var result = _compiler.Compile(source, CelProfile.Rule, entity);

        return result.IsSuccess
            ? result.Expression!
            : throw Refused(result.Errors.Select(error => Error(
                entity,
                field,
                $"Field '{entity.Name}.{field.Name}' declares a 'validation' that does not compile: {error.Message}",
                error.FixSuggestion ?? "Write the validation as a comparison over this row's own fields and constants.")));
    }

    private string Rendered(EntitySchema entity, FieldSchema field, CompiledExpression expression)
    {
        try
        {
            return _predicates.RenderConstraint(expression, _fields);
        }
        catch (NotSupportedException unsupported)
        {
            throw Refused([Error(
                entity,
                field,
                $"Field '{entity.Name}.{field.Name}' declares a 'validation' that cannot be written into a CHECK constraint: "
                + unsupported.Message,
                "Compare this row's own fields with constants this engine can write inline.")]);
        }
    }

    private static DescriptorValidationError Error(EntitySchema entity, FieldSchema field, string message, string fix) =>
        new($"/entities/{entity.Name}/fields/{field.Name}/validation", message, fix, DescriptorValidationSeverity.Error);

    private static DescriptorValidationException Refused(IEnumerable<DescriptorValidationError> errors) =>
        new(new DescriptorValidationResult([.. errors]));
}
