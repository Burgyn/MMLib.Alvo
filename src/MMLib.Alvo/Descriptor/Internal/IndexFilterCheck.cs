using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>
/// The validator's pass over every index's <c>where</c>: compiled for <see cref="CelProfile.Rule"/> against its entity, held
/// to the stored-predicate surface less durations and temporal arithmetic, refused over a hidden field, and rendered as a
/// constraint — so a predicate no partial index can carry is a structured refusal at
/// <c>/entities/&lt;e&gt;/indexes/&lt;i&gt;/where</c> when the descriptor is saved (D11).
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="RollupFilterCheck"/>'s three steps, with the entity as the row</b> — bare field names only (a <c>new.</c>
/// prefix is the Rule profile's compile error), no <c>value</c>, and no <c>timestamp('…')</c> constant (review R4). An index predicate is stored and judged on every write with no caller and no instant, as a
/// <c>CHECK</c> is; PostgreSQL further demands an <c>IMMUTABLE</c> predicate, and a duration over a field renders through
/// its epoch extraction, which is not — so temporal arithmetic is refused here for both engines alike.
/// </para>
/// <para>
/// <b>A hidden field is refused</b> (Ruling 71's argument): which rows a partial unique index holds is a predicate oracle
/// over every field its filter reads, answered by whether a write is refused.
/// </para>
/// </remarks>
internal static class IndexFilterCheck
{
    private const string Slot = "An index filter";
    private const string CompileFix = "Write the filter as a comparison over this entity's own fields and constants, e.g. \"active\".";
    private const string LiteralFix = "Remove the control character, line or paragraph separator, or lone surrogate from the text constant.";
    private const string HiddenFix = "Filter on fields every reader may see, or unhide the field.";
    private const string TemporalFix = "Compare a timestamp field with another field, or store the derived instant in a field and compare that.";

    private static readonly IPredicateRenderer _renderer = new SqlPredicateRenderer();

    /// <summary>Every refusal over <paramref name="schema"/>'s index filters, each at its own pointer.</summary>
    /// <param name="schema">The mapped schema, whose entities the filters are type-checked against.</param>
    /// <param name="compiler">The compiler the driver compiles the filter with.</param>
    /// <param name="descriptor">The descriptor, whose <c>hidden</c> flags decide which fields a filter may read.</param>
    internal static IEnumerable<DescriptorValidationError> Errors(SchemaModel schema, ICelCompiler compiler, AlvoDescriptor descriptor)
    {
        var reads = new ValidationReadSet(schema, descriptor);
        return
            from entity in schema.Entities
            from position in Enumerable.Range(0, entity.Indexes.Count)
            where entity.Indexes[position].Where is not null
            from error in ErrorsFor(entity, position, compiler, reads)
            select error;
    }

    /// <summary>
    /// The refusal of every <c>where</c> and <c>exclude</c> an index of a <c>storage: dynamic</c> entity declares, read from
    /// the parsed descriptor before the mapper drops the entity.
    /// </summary>
    /// <param name="descriptor">The parsed descriptor.</param>
    internal static IReadOnlyList<DescriptorValidationError> DynamicErrors(AlvoDescriptor descriptor) =>
    [
        .. from entity in descriptor.Entities
           where !DescriptorToSchemaMapper.IsPhysical(entity.Value)
           let indexes = entity.Value.Indexes ?? []
           from position in Enumerable.Range(0, indexes.Count)
           from keyword in DynamicKeywords(indexes[position])
           select new DescriptorValidationError(
               $"{Pointer(entity.Key, position)}/{keyword}",
               DynamicMessage(entity.Key, keyword),
               $"Remove '{keyword}', or make '{entity.Key}' a physical entity.",
               DescriptorValidationSeverity.Error),
    ];

    /// <summary>The sentence that refuses an index's <paramref name="keyword"/> on a <c>storage: dynamic</c> entity.</summary>
    /// <param name="entity">The dynamic entity.</param>
    /// <param name="keyword"><c>where</c> or <c>exclude</c>.</param>
    internal static string DynamicMessage(string entity, string keyword) =>
        $"An index of '{entity}' declares '{keyword}', which the database keeps as {Kept(keyword)}, and '{entity}' declares "
        + "'storage: dynamic' — dynamic entities have no table to hold one until #41 defines how their integrity is enforced.";

    /// <summary>The pointer of <paramref name="entity"/>'s index at <paramref name="position"/>.</summary>
    /// <param name="entity">The declaring entity.</param>
    /// <param name="position">The index's position in <c>indexes</c>.</param>
    internal static string Pointer(string entity, int position) => $"/entities/{entity}/indexes/{position}";

    private static string Kept(string keyword) => keyword == "where" ? "a partial index" : "an exclusion constraint";

    private static IEnumerable<string> DynamicKeywords(EntityIndex index)
    {
        if (index.Where is not null)
        {
            yield return "where";
        }

        if (index.Exclude is not null)
        {
            yield return "exclude";
        }
    }

    private static IEnumerable<DescriptorValidationError> ErrorsFor(
        EntitySchema entity, int position, ICelCompiler compiler, ValidationReadSet reads)
    {
        var result = compiler.Compile(entity.Indexes[position].Where!, CelProfile.Rule, entity);
        if (!result.IsSuccess)
        {
            return result.Errors.Select(error => Refusal(entity, position, $"does not compile: {error.Message}", error.FixSuggestion ?? CompileFix));
        }

        IEnumerable<DescriptorValidationError> hidden = HiddenReadRefusal(entity, position, result.Expression!, reads) is { } refused ? [refused] : [];
        return hidden.Concat(ShapeErrors(entity, position, result.Expression!));
    }

    private static DescriptorValidationError? HiddenReadRefusal(
        EntitySchema entity, int position, CompiledExpression expression, ValidationReadSet reads)
    {
        var exposed = reads.Closure(entity, CelDataOrigin.FieldsRead(expression.Root)).Where(reads.IsHidden).ToList();
        if (exposed.Count == 0)
        {
            return null;
        }

        var named = string.Join(", ", exposed.Select(read => $"'{read.Entity}.{read.Field}'").Order(StringComparer.Ordinal));
        return Refusal(
            entity,
            position,
            $"reads a hidden field ({named}): which rows it holds would disclose a value its readers cannot see.",
            HiddenFix);
    }

    private static IEnumerable<DescriptorValidationError> ShapeErrors(EntitySchema entity, int position, CompiledExpression expression)
    {
        var surface = StoredPredicateSurface.Refusals(expression, Slot);
        if (surface.Count > 0)
        {
            return surface.Select(error => Refusal(entity, position, $"cannot be stored: {error.Message}", error.FixSuggestion ?? CompileFix));
        }

        return HoldsTemporal(expression.Root)
            ? [Refusal(entity, position, "holds a duration or temporal arithmetic, which a partial index predicate cannot keep.", TemporalFix)]
            : RenderErrors(entity, position, expression);
    }

    private static bool HoldsTemporal(CelNode node) =>
        node is CelLiteral { Type: CelValueType.Duration }
        || node is CelBinary binary && CelTemporal.IsTemporal(binary)
        || CelTree.Children(node).Any(HoldsTemporal);

    private static IEnumerable<DescriptorValidationError> RenderErrors(EntitySchema entity, int position, CompiledExpression expression)
    {
        try
        {
            _renderer.RenderConstraint(expression, NeutralFields.Instance);
            return [];
        }
        catch (NotSupportedException failure)
        {
            return [Refusal(entity, position, $"cannot be written into a partial index: {failure.Message}", LiteralFix)];
        }
    }

    private static DescriptorValidationError Refusal(EntitySchema entity, int position, string reason, string fix) =>
        new($"{Pointer(entity.Name, position)}/where", $"An index of '{entity.Name}' declares a 'where' that {reason}", fix, DescriptorValidationSeverity.Error);
}
