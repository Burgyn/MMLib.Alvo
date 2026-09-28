using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Renders one <c>computed</c> field's CEL into the SQL a <b>stored generated column</b> carries — the only
/// place that translation happens, and the only string in this package that reaches DDL unparameterized.
/// </summary>
/// <remarks>
/// <para>
/// <b>Compiled, never spliced.</b> #20 removed a raw descriptor-string-to-DDL splice as an
/// arbitrary-DDL-injection vector, and this is what revives the feature without reviving the vector: the text
/// that reaches DDL is produced by <see cref="IPredicateRenderer"/>'s scalar entry point from a CEL AST the
/// compiler accepted for <see cref="CelProfile.Computed"/>, so it can contain nothing but this entity's own
/// field references (delimited by <see cref="IFieldSqlRenderer.RenderField"/>), arithmetic, text joined by
/// <see cref="IFieldSqlRenderer.RenderStringConcatenation"/> from fields and constants quoted by
/// <see cref="IFieldSqlRenderer.RenderStringLiteral"/>, and
/// <c>CASE WHEN</c>. A descriptor string never appears in it.
/// </para>
/// <para>
/// <b>A bound value is refused rather than inlined (spike Q9).</b> The scalar renderer routes every
/// non-boolean literal through its parameter bag, and DDL has no bind-parameter form at all. Inlining one
/// here would put literal formatting — decimal separators, string quoting and escaping, each of them
/// engine-specific — in this shared package, which is the one thing the dialect seam exists to prevent, and
/// it would do so in text that is then <em>persisted</em> in the database's own schema. So the field is
/// refused, naming the constant. Every <c>computed</c> example the sources give is field-only arithmetic
/// (<c>unit_price * amount</c>, <c>net_total + vat_total</c>) and renders clean, and
/// <c>baas-analyza:1358</c> deliberately puts a contextual constant — a VAT rate, which is time-valid
/// business logic rather than arithmetic — in a before-hook instead. Widening this later is additive;
/// getting the escaping wrong once is persisted.
/// </para>
/// <para>
/// <b>Widened once, for text, and only through the dialect (assistant-reliability design, ruling 2).</b> A text
/// constant in a value position — the <c>' '</c> of <c>first_name + ' ' + last_name</c> — is quoted by this
/// driver's own <see cref="IFieldSqlRenderer.RenderStringLiteral"/> and never by this package, so the quoting
/// stays per engine and the refusal above still answers every other constant, and every text constant of a
/// dialect that declines to quote it.
/// </para>
/// <para>
/// <b>It renders, and it does not decide whether the engine can hold one.</b> That question needs the
/// column's EF-resolved store type, which only exists once the relational model has been initialized, so
/// <see cref="EfCoreSchemaMigrator"/> asks <see cref="IAlvoSqlDialect.GeneratedColumnDefinition"/> there —
/// see its own remarks. Splitting the two keeps this type free of the dialect and therefore usable from the
/// model builder, which runs before any store type is resolved.
/// </para>
/// <para>
/// <b>A backstop, not the gate.</b> The core's descriptor validator compiles and renders every <c>computed</c>
/// the same way (<c>ComputedFieldCheck</c>) and refuses it when the descriptor is saved, so a validated
/// descriptor never reaches a refusal here. It still refuses, for a host that replaced
/// <see cref="IPredicateRenderer"/> or built a model from a schema no validator saw — and it refuses with
/// <see cref="DescriptorValidationException"/>, the documented type the Management API and the dashboard
/// already answer as a structured refusal at the field's pointer, never an exception nothing maps.
/// </para>
/// </remarks>
internal sealed class ComputedColumnSql
{
    private readonly ICelCompiler _compiler;
    private readonly IPredicateRenderer _predicates;
    private readonly IFieldSqlRenderer _fields;

    /// <summary>Initializes a renderer over one driver's expression seam.</summary>
    /// <param name="compiler">The core's CEL compiler — the fail-fast boundary the rendered AST comes from.</param>
    /// <param name="predicates">The core's renderer, entered through its scalar (Computed) overload.</param>
    /// <param name="fields">This driver's field renderer, which delimits every identifier that reaches the DDL.</param>
    internal ComputedColumnSql(ICelCompiler compiler, IPredicateRenderer predicates, IFieldSqlRenderer fields)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(fields);
        _compiler = compiler;
        _predicates = predicates;
        _fields = fields;
    }

    /// <summary>
    /// The SQL scalar expression <paramref name="field"/>'s generated column is computed from, or
    /// <see langword="null"/> when the field declares no <c>computed</c>.
    /// </summary>
    /// <param name="entity">The entity the field belongs to — what the CEL is type-checked against.</param>
    /// <param name="field">The field being configured.</param>
    /// <exception cref="DescriptorValidationException">
    /// The CEL does not compile for the <see cref="CelProfile.Computed"/> profile, has no scalar rendering, or
    /// renders a bound value DDL cannot carry.
    /// </exception>
    internal string? For(EntitySchema entity, FieldSchema field)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(field);

        if (field.ComputedExpression is not { } source)
        {
            return null;
        }

        var rendered = Rendered(entity, field, Compiled(entity, field, source));
        EnsureNoBoundValue(entity, field, source, rendered);

        return rendered.Sql;
    }

    /// <summary>
    /// The compiled expression, or a refusal carrying every problem the compiler found at once.
    /// </summary>
    /// <remarks>
    /// It reports every error rather than the first, as the validator's own pass does: an agent fixing one field
    /// wants the whole list in one round trip (§0 principle 4), which is exactly the shape
    /// <see cref="CelCompilationResult"/> already has.
    /// </remarks>
    private CompiledExpression Compiled(EntitySchema entity, FieldSchema field, string source)
    {
        var result = _compiler.Compile(source, CelProfile.Computed, entity);

        return result.IsSuccess
            ? result.Expression!
            : throw Refused(result.Errors.Select(error => Error(
                entity,
                field,
                $"Field '{entity.Name}.{field.Name}' declares a 'computed' expression that does not compile: "
                + error.Message,
                error.FixSuggestion ?? "Keep 'computed' to arithmetic over this entity's own fields.")));
    }

    /// <summary>The scalar render, with the renderer's own "no rendering for this shape" refused at the field.</summary>
    private SqlExpression Rendered(EntitySchema entity, FieldSchema field, CompiledExpression expression)
    {
        try
        {
            return _predicates.Render(expression, _fields);
        }
        catch (NotSupportedException unsupported)
        {
            throw Refused([Error(
                entity,
                field,
                $"Field '{entity.Name}.{field.Name}' declares \"computed\": \"{expression.Source}\", which has no "
                + $"generated-column rendering: {unsupported.Message}",
                "Compare two of this row's fields directly, or hold the value in a plain field a before-hook writes.")]);
        }
    }

    private static DescriptorValidationError Error(EntitySchema entity, FieldSchema field, string message, string fix) =>
        new($"/entities/{entity.Name}/fields/{field.Name}/computed", message, fix, DescriptorValidationSeverity.Error);

    private static DescriptorValidationException Refused(IEnumerable<DescriptorValidationError> errors) =>
        new(new DescriptorValidationResult([.. errors]));

    /// <summary>
    /// Refuses a <c>computed</c> whose render produced bind parameters, naming the constants that made it one.
    /// </summary>
    /// <remarks>
    /// The message names the values rather than the parameter markers: <c>@p0</c> tells an author nothing,
    /// while <c>1.2</c> is the thing they wrote. The fix is the one the sources themselves use, so it is
    /// spelled out rather than left as "not supported".
    /// </remarks>
    private static void EnsureNoBoundValue(
        EntitySchema entity, FieldSchema field, string source, SqlExpression rendered)
    {
        if (rendered.Parameters.Count == 0)
        {
            return;
        }

        var constants = string.Join(", ", rendered.Parameters.Values.Select(value => $"'{value}'"));
        throw Refused([Error(
            entity,
            field,
            $"Field '{entity.Name}.{field.Name}' declares \"computed\": \"{source}\", which carries the "
            + $"constant value(s) {constants}. A computed field becomes a stored generated column, and a "
            + "column definition is DDL, which has no bind-parameter form — so a constant cannot be carried "
            + "into it.",
            "Keep 'computed' to arithmetic over this entity's own fields "
            + "(\"unit_price * amount\", \"net_total + vat_total\"), and hold a contextual constant such as a "
            + "tax rate in a field of its own that a before-hook maintains, which is where it belongs anyway: "
            + "a rate is time-valid business logic rather than arithmetic over this row.")]);
    }
}
