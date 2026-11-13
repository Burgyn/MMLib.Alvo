using MMLib.Alvo.Schema;

using System.Text.Json;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// A bound relation renders as one correlated <c>EXISTS</c> over the related entity's unlocked table, under an alias of
/// its own, with the related <c>list</c> rule and tenant scope ANDed inside — the PostgreSQL RLS shape, where a policy's
/// sub-select is itself filtered by the referenced table's policies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every nested scope renders into its parent's parameter bag</b>, so one top-level predicate has one prefix and its
/// names never repeat however deep the relations go. Aliases are <c>_alvo_r1</c>, <c>_alvo_r2</c>, … per top-level render:
/// lower-case ASCII, valid bare on every engine, and led by an underscore no entity name can start with, so an alias never names
/// the outer table it is correlated with.
/// </para>
/// <para>
/// <b>The outer row is never read bare inside an <c>EXISTS</c></b>: a related table declaring a same-named column would
/// capture it. In a read it is qualified by its table name; in a write probe each outer value is bound as a parameter of
/// the field's declared type, never formatted into the text.
/// </para>
/// </remarks>
internal sealed partial class SqlPredicateRenderer
{
    private const string AliasPrefix = "_alvo_r";

    /// <inheritdoc />
    SqlPredicate IPredicateRenderer.Render(
        CompiledExpression expression, AlvoContext context, IFieldSqlRenderer fields, string parameterPrefix,
        RelationSql relations)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(relations);
        RequirePredicateProfile(expression);
        RequireIdentifierPrefix(parameterPrefix);

        var bag = new ParameterBag(parameterPrefix);
        var scope = RenderScope.Top(expression.Entity, context, fields, bag) with { Relations = relations };
        return RenderWhole(expression.Root, scope, bag);
    }

    /// <inheritdoc />
    SqlPredicate IPredicateRenderer.RenderRelation(
        CelRelation relation, EntitySchema outer, AlvoContext context, IFieldSqlRenderer fields, string parameterPrefix,
        RelationSql relations)
    {
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(relations);
        RequireIdentifierPrefix(parameterPrefix);

        var bag = new ParameterBag(parameterPrefix);
        var scope = RenderScope.Top(outer, context, fields, bag) with { Relations = relations, Row = relations.Outer };
        return RenderWhole(relation, scope, bag);
    }

    private PredicateFragment RenderRelationTerm(CelRelation relation, RenderScope scope)
    {
        var bound = relation.Scope ?? throw new InvalidOperationException(
            $"The relation to '{relation.Related.Name}' is not bound to a caller; only a decision IPolicyEngine.Resolve "
            + "returned can be rendered.");
        if (bound.Denied || scope.Relations is null || ReadsAbsentImageKey(relation, scope))
        {
            return new PredicateFragment(scope.Fields.RenderBooleanPredicate(false), true);
        }

        var alias = scope.NextAlias();
        var inner = scope.Into(relation.Related, alias);
        List<string> terms =
        [
            $"{Column(inner, inner.Row, relation.Related, relation.RelatedKey)} = {Column(scope, inner.Outer!, scope.Entity, relation.OuterKey)}",
            RenderPredicate(relation.Body, inner).Sql,
        ];
        AddScope(terms, bound.Using, inner);
        AddScope(terms, bound.TenantScope, inner);

        return new PredicateFragment(CorrelatedExists(scope.Relations.Table(relation.Related), alias, terms), true);
    }

    private void AddScope(List<string> terms, CompiledExpression? predicate, RenderScope inner)
    {
        if (predicate is not null)
        {
            terms.Add(RenderPredicate(predicate.Root, inner.At(predicate.Entity)).Sql);
        }
    }

    /// <summary>
    /// One field of a row, read where that row lives: a bare column, a qualified column, or a post-image value bound as a
    /// parameter of the field's declared type.
    /// </summary>
    private static string Column(RenderScope scope, RowSource row, EntitySchema entity, string field) => row switch
    {
        RowSource.Unqualified => scope.Fields.RenderField(entity, field),
        RowSource.Columns columns => $"{columns.Qualifier}.{scope.Fields.RenderField(entity, field)}",
        RowSource.Image image => scope.Sink.Constant(RelationImageValue.Of(entity, field, image.Row), scope.Fields),
        _ => throw new InvalidOperationException("Unmapped row source."),
    };

    private static string RenderOuterField(CelOuterFieldRef outer, RenderScope scope) =>
        Column(scope, OuterRow(scope, outer), scope.OuterEntity!, outer.FieldName);

    private static PredicateFragment RenderOuterBoolField(CelOuterFieldRef outer, RenderScope scope) =>
        ReadsAbsentImageValue(outer, scope)
            ? new PredicateFragment(scope.Fields.RenderBooleanPredicate(false), true)
            : RenderBoolField(RenderOuterField(outer, scope), scope.Fields);

    /// <summary>
    /// <c>has(outer)</c>: a column test in a read, a constant in a probe. A post-image's presence is known before the
    /// statement exists, so it renders as a constant predicate rather than a null parameter an engine could not type —
    /// present when the post-image holds a value, and a JSON <c>null</c> counts as absent, as it does everywhere else an
    /// image value is read.
    /// </summary>
    private static PredicateFragment RenderOuterHas(CelOuterHas has, RenderScope scope) =>
        OuterRow(scope, has.Field) is RowSource.Image image
            ? new PredicateFragment(scope.Fields.RenderBooleanPredicate(IsPresent(image.Row[has.Field.FieldName])), true)
            : RenderHas(RenderOuterField(has.Field, scope));

    private static bool IsPresent(object? value) => value is not (null or JsonElement { ValueKind: JsonValueKind.Null });

    private static RowSource OuterRow(RenderScope scope, CelNode reader) => scope.Outer ?? throw new InvalidOperationException(
        $"'{reader.GetType().Name}' reads the outer row outside a relation; only a relation's body has one.");

    private static object? OuterImageValue(CelOuterFieldRef outer, RenderScope scope) =>
        scope.Outer is RowSource.Image image ? RelationImageValue.Of(scope.OuterEntity!, outer.FieldName, image.Row) : null;

    /// <summary>Whether <paramref name="node"/> reads an outer post-image value that is null or not of its field's type.</summary>
    private static bool ReadsAbsentImageValue(CelNode node, RenderScope scope) =>
        scope.Outer is RowSource.Image
        && CelRewriter.Any(node, n => n is CelOuterFieldRef outer && OuterImageValue(outer, scope) is null);

    /// <summary>A post-image whose correlated key is null has no related row: the relation is false and reads no table.</summary>
    private static bool ReadsAbsentImageKey(CelRelation relation, RenderScope scope) =>
        scope.QualifiedRow is RowSource.Image image && RelationImageValue.Of(scope.Entity, relation.OuterKey, image.Row) is null;

    /// <summary>
    /// What one level of a predicate renders against: the row its fields read, the outer row a relation body's outer
    /// fields read, the driver's table source, and the render-wide parameter sink and alias counter every nested scope
    /// shares.
    /// </summary>
    private sealed record RenderScope(EntitySchema Entity, AlvoContext? Context, IFieldSqlRenderer Fields, ILiteralSink Sink)
    {
        public RowSource Row { get; init; } = new RowSource.Unqualified();

        public RowSource? Outer { get; init; }

        public EntitySchema? OuterEntity { get; init; }

        public RelationSql? Relations { get; init; }

        /// <summary>
        /// Gets whether every entity on this scope's path — the top-level one, each related one a relation term entered,
        /// and this scope's — is scoped: the one condition under which a tenant role folds (<see cref="RoleProvenance"/>).
        /// </summary>
        public bool EveryEntityScoped { get; init; }

        private AliasCounter Aliases { get; init; } = new();

        public static RenderScope Top(EntitySchema entity, AlvoContext? context, IFieldSqlRenderer fields, ILiteralSink sink) =>
            new(entity, context, fields, sink) { EveryEntityScoped = RoleProvenance.StillScoped(true, entity.Tenancy) };

        /// <summary>This scope over <paramref name="entity"/>, which joins its path.</summary>
        public RenderScope At(EntitySchema entity) => this with
        {
            Entity = entity,
            EveryEntityScoped = RoleProvenance.StillScoped(EveryEntityScoped, entity.Tenancy),
        };

        public string NextAlias() => AliasPrefix + Aliases.Next();

        /// <summary>
        /// This scope's row as a nested scope must read it: a bare top-level row becomes the driver's qualified outer, since
        /// inside an <c>EXISTS</c> a bare column resolves against the innermost table that declares it.
        /// </summary>
        public RowSource QualifiedRow => Row is RowSource.Unqualified ? Relations!.Outer : Row;

        /// <summary>The scope of a related row under <paramref name="alias"/>; its outer row is this scope's, qualified.</summary>
        public RenderScope Into(EntitySchema related, string alias) => At(related) with
        {
            Row = new RowSource.Columns(alias),
            OuterEntity = Entity,
            Outer = QualifiedRow,
        };
    }

    private sealed class AliasCounter
    {
        private int _count;

        public int Next() => ++_count;
    }
}
