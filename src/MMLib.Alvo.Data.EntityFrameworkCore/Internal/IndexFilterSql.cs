using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Renders an index's <see cref="IndexSchema.Where"/> into the predicate its partial index carries: inline-literal SQL over
/// the entity's unqualified, delimited columns.
/// </summary>
/// <remarks>
/// <para>
/// <b>Compiled, never spliced</b> — <see cref="CheckConstraintSql"/>'s discipline. The text comes from the constraint entry
/// point of <see cref="IPredicateRenderer"/> over a CEL AST the compiler accepted for <see cref="CelProfile.Rule"/>; it never
/// holds the descriptor's string.
/// </para>
/// <para>
/// <b>Unlike a <c>CHECK</c>, no <c>IS NULL</c> pass.</b> The renderer's two-valued fold answers an empty operand false, so a
/// row whose filter reads an empty field is simply outside the index — what the predicate says, and what PostgreSQL's
/// <c>WHERE</c> does with an unknown verdict anyway.
/// </para>
/// <para>
/// <b>A backstop, not the gate.</b> The core's validator (<c>IndexFilterCheck</c>) compiles and renders every filter the
/// same way when the descriptor is saved; this still refuses, at the same pointer, for a schema no validator saw.
/// </para>
/// </remarks>
internal sealed class IndexFilterSql
{
    private readonly ICelCompiler _compiler;
    private readonly IPredicateRenderer _predicates;
    private readonly IFieldSqlRenderer _fields;

    /// <summary>Initializes a renderer over one driver's expression seam.</summary>
    /// <param name="compiler">The core's CEL compiler.</param>
    /// <param name="predicates">The core's renderer, entered through its constraint entry point.</param>
    /// <param name="fields">This driver's field renderer, which delimits every identifier and writes every literal.</param>
    internal IndexFilterSql(ICelCompiler compiler, IPredicateRenderer predicates, IFieldSqlRenderer fields)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(fields);
        _compiler = compiler;
        _predicates = predicates;
        _fields = fields;
    }

    /// <summary>The SQL predicate of <paramref name="index"/>'s partial index.</summary>
    /// <param name="entity">The entity the index belongs to — what the CEL is type-checked against.</param>
    /// <param name="index">An index of <paramref name="entity"/> declaring <see cref="IndexSchema.Where"/>.</param>
    /// <exception cref="DescriptorValidationException">The filter does not compile, or has no constraint rendering.</exception>
    internal string For(EntitySchema entity, IndexSchema index)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(index.Where);

        var result = _compiler.Compile(index.Where, CelProfile.Rule, entity);
        return result.IsSuccess
            ? Rendered(entity, index, result.Expression!)
            : throw Refused(entity, index, result.Errors.Select(error => $"does not compile: {error.Message}"));
    }

    /// <summary>
    /// The fields <paramref name="index"/>'s <c>where</c> reads, empty when it declares none, or <see langword="null"/> when
    /// it does not compile or holds a node the walk does not know — so a caller deciding whether its columns exist yet is never
    /// told a partial answer.
    /// </summary>
    /// <param name="entity">The entity the index belongs to.</param>
    /// <param name="index">One of its indexes.</param>
    internal IReadOnlySet<string>? Reads(EntitySchema entity, IndexSchema index)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(index);
        if (index.Where is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var result = _compiler.Compile(index.Where, CelProfile.Rule, entity);
        var reads = new HashSet<string>(StringComparer.Ordinal);
        return result.IsSuccess && CheckConstraintSql.Collect(result.Expression!.Root, reads) ? reads : null;
    }

    /// <summary>The JSON pointer of <paramref name="entity"/>'s index <paramref name="index"/>'s <c>where</c>.</summary>
    /// <param name="entity">The declaring entity.</param>
    /// <param name="index">One of its indexes.</param>
    internal static string Pointer(EntitySchema entity, IndexSchema index) =>
        $"/entities/{entity.Name}/indexes/{Position(entity, index)}/where";

    private static int Position(EntitySchema entity, IndexSchema index)
    {
        var position = entity.Indexes.ToList().IndexOf(index);
        return position < 0 ? 0 : position;
    }

    private string Rendered(EntitySchema entity, IndexSchema index, CompiledExpression expression)
    {
        try
        {
            return _predicates.RenderConstraint(expression, _fields);
        }
        catch (NotSupportedException unsupported)
        {
            throw Refused(entity, index, [$"cannot be written into a partial index: {unsupported.Message}"]);
        }
    }

    private static DescriptorValidationException Refused(EntitySchema entity, IndexSchema index, IEnumerable<string> reasons) =>
        new(new DescriptorValidationResult([.. reasons.Select(reason => new DescriptorValidationError(
            Pointer(entity, index),
            $"An index of '{entity.Name}' declares a 'where' that {reason}",
            "Write the filter as a comparison over this entity's own fields and constants, e.g. \"active\".",
            DescriptorValidationSeverity.Error))]));
}
