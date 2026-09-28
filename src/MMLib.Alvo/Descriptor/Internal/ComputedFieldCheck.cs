using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// The validator's pass over every <c>computed</c> field: compiled for <see cref="CelProfile.Computed"/> and rendered
/// through the scalar SQL entry point, so an expression a stored generated column cannot carry is a structured
/// refusal when the descriptor is saved — never an exception when the migrator first builds its model.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> The rule-compilation pass compiles <c>rules</c>/<c>hidden</c>/<c>readOnly</c> and the
/// hooks, and nothing compiled <c>computed</c> until the EF driver's <c>ComputedColumnSql</c> did so inside the
/// migration model builder. <c>"computed": "now()"</c> therefore validated clean, and Preview then threw out of the
/// planner — the security core's fail-fast-compile rule broken for exactly one CEL slot.
/// </para>
/// <para>
/// <b>It renders as well as compiles, through the core's own <see cref="SqlPredicateRenderer"/>,</b> because two
/// refusals live past the compiler: a constant other than text in a value position becomes a bind parameter, which
/// DDL has no form for, and a comparison over anything but a field or a literal has no scalar rendering. Asking the renderer that the driver
/// itself uses — over a neutral field renderer, since the question is the shape and not the dialect's spelling —
/// keeps this pass from growing a second copy of what the renderer accepts. The driver keeps its own guard as a
/// backstop, for a host that replaced <see cref="IPredicateRenderer"/> or a model built without this validator.
/// </para>
/// <para>
/// <b>Text (assistant-reliability design, ruling 2).</b> CEL's <c>+</c> over two strings is admitted in the Computed
/// profile and a text constant it joins is written inline through the dialect's own literal quoting. The rules that
/// make that honest are split by who can decide them: the compiler refuses a mixed pair (no implicit conversion), an
/// operand that can be null (CEL's <c>+</c> has no null overload and SQL's <c>||</c> yields <c>NULL</c> — the explicit
/// fallback is <c>has(f) ? f : ''</c>) and a constant holding a control character; this pass, which knows the declared
/// field, refuses an expression that reads no field, a result the declared type does not hold, and a join longer than
/// the declared <c>maxLength</c> (<see cref="ComputedValueShape"/>).
/// </para>
/// <para>
/// <b>A deliberate deviation: the core's own renderer, not the one in the container.</b> The driver renders with the
/// DI-resolved <see cref="IPredicateRenderer"/>; this pass uses <see cref="SqlPredicateRenderer"/> directly, because
/// <see cref="DescriptorValidator"/> is constructed without a container too (a CLI <c>validate</c>, a test) and must
/// answer the same there. The cost is one-sided and stated: a host whose replacement renderer could carry more —
/// inline a constant, say — is refused here for an expression its own driver would have accepted. The opposite
/// error, accepting what the driver then refuses, cannot happen for the core renderer's shapes, and for a
/// narrower replacement the driver's structured backstop still answers.
/// </para>
/// </remarks>
internal static class ComputedFieldCheck
{
    private static readonly SqlPredicateRenderer _renderer = new();

    /// <summary>Every refusal over <paramref name="schema"/>'s <c>computed</c> fields, each at its own pointer.</summary>
    /// <param name="schema">The mapped schema, whose entities the expressions are type-checked against.</param>
    /// <param name="compiler">The same compiler the driver compiles the column's CEL with.</param>
    internal static IEnumerable<DescriptorValidationError> Errors(SchemaModel schema, ICelCompiler compiler) =>
        from entity in schema.Entities
        from field in entity.Fields
        where field.ComputedExpression is not null
        from error in ErrorsFor(entity, field, compiler)
        select error;

    private static IEnumerable<DescriptorValidationError> ErrorsFor(
        EntitySchema entity, FieldSchema field, ICelCompiler compiler)
    {
        var result = compiler.Compile(field.ComputedExpression!, CelProfile.Computed, entity);

        return result.IsSuccess
            ? CompiledErrors(entity, field, result.Expression!)
            : result.Errors.Select(error => CompileError(entity, field, error));
    }

    private static IEnumerable<DescriptorValidationError> CompiledErrors(
        EntitySchema entity, FieldSchema field, CompiledExpression expression) =>
        ComputedValueShape.Refusal(entity, field, expression) is { } shape
            ? [Refusal(entity, field, shape.Message, shape.Fix)]
            : RenderErrors(entity, field, expression);

    private static DescriptorValidationError CompileError(EntitySchema entity, FieldSchema field, CelCompilationError error) =>
        Refusal(
            entity,
            field,
            $"Field '{entity.Name}.{field.Name}' declares a 'computed' expression that does not compile: {error.Message}",
            error.FixSuggestion
                ?? "Keep 'computed' to arithmetic over this entity's own fields, e.g. \"unit_price * amount\".");

    private static IEnumerable<DescriptorValidationError> RenderErrors(
        EntitySchema entity, FieldSchema field, CompiledExpression expression)
    {
        SqlExpression rendered;
        try
        {
            rendered = _renderer.Render(expression, NeutralFields.Instance);
        }
        catch (NotSupportedException)
        {
            return [UnrenderableShape(entity, field)];
        }

        return rendered.Parameters.Count == 0 ? [] : [BoundConstant(entity, field, rendered)];
    }

    /// <summary>A constant would be a bind parameter in DDL, which has none — <c>ComputedColumnSql</c>'s spike Q9.</summary>
    private static DescriptorValidationError BoundConstant(EntitySchema entity, FieldSchema field, SqlExpression rendered) =>
        Refusal(
            entity,
            field,
            $"Field '{entity.Name}.{field.Name}' declares \"computed\": \"{field.ComputedExpression}\", which carries "
            + $"the constant value(s) {string.Join(", ", rendered.Parameters.Values.Select(value => $"'{value}'"))}. "
            + "A computed field becomes a stored generated column, and a column definition is DDL, which has no "
            + "bind-parameter form — so a constant other than a text constant joined into the value cannot be "
            + "carried into it.",
            "Keep 'computed' to arithmetic over this entity's own fields (\"unit_price * amount\", "
            + "\"net_total + vat_total\") or to text joined from them (\"first_name + ' ' + last_name\"), and hold a "
            + "contextual constant such as a tax rate in a field of its own that a before-hook maintains.");

    private static DescriptorValidationError UnrenderableShape(EntitySchema entity, FieldSchema field) =>
        Refusal(
            entity,
            field,
            $"Field '{entity.Name}.{field.Name}' declares \"computed\": \"{field.ComputedExpression}\", which is legal "
            + "CEL but has no generated-column rendering: inside a computed expression a comparison compares a "
            + "field with a field, never an arithmetic result, a negation or a nested condition.",
            "Compare two of this row's fields directly (\"net > vat ? net : vat\"), or keep the value out of a "
            + "generated column: a plain field a caller or a before-hook writes.");

    private static DescriptorValidationError Refusal(
        EntitySchema entity, FieldSchema field, string message, string fix) =>
        new($"/entities/{entity.Name}/fields/{field.Name}/computed", message, fix, DescriptorValidationSeverity.Error);

    /// <summary>
    /// A field renderer that spells nothing a dialect would: the pass asks whether the expression renders and what it
    /// binds, never what the SQL text is, so a driver's quoting and casting are not its business.
    /// </summary>
    private sealed class NeutralFields : IFieldSqlRenderer
    {
        public static NeutralFields Instance { get; } = new();

        public string TrueLiteral => "TRUE";

        public string FalseLiteral => "FALSE";

        public string RenderField(EntitySchema entity, string fieldName) => fieldName;

        public string RenderParameter(string parameterName) => parameterName;

        public string RenderCaseInsensitiveLike(string left, string right) => $"{left} LIKE {right}";

        /// <summary>
        /// Answers every text constant, as every shipped dialect does for the text the compiler admits — so the pass
        /// refuses what a constant <em>means</em> in a column (see <see cref="ComputedValueShape"/>), never how a
        /// dialect would quote it.
        /// </summary>
        public string? RenderStringLiteral(string value) => "'…'";

        public string RenderStringConcatenation(string left, string right) => $"({left} || {right})";
    }
}
