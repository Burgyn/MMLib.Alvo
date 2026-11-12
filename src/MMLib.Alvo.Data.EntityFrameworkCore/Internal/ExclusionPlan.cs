using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// What a migration plan owes the exclusions the schemas declare (HX-12): the dialect's statements that drop the ones that
/// go and create the ones that come, outside EF's operation list, so an EF table rebuild sequenced after them cannot take
/// an engine's triggers with the old table.
/// </summary>
/// <remarks>
/// <para>
/// <b>The diff is a set difference of names</b> (D10): a name is a function of the whole definition, so a changed one is a
/// drop of the old name and a create of the new. A table the plan drops takes its exclusion along, so it needs no drop.
/// </para>
/// <para>
/// <b>Rebuild survival</b> (Review Focus 1). An enforcement that does not survive a table rebuild —
/// <see cref="ExclusionConstraintSql.SurvivesTableRebuild"/> <see langword="false"/>, SQLite's triggers — is replayed after the
/// DDL whenever EF touches its table, every create being idempotent.
/// </para>
/// <para>
/// <b>Rows already there.</b> A trigger judges only the row being written, so an exclusion added by triggers to a table that
/// already holds rows is followed by a no-op update of every row with a partner in the overlap join: each fires the update
/// trigger, which aborts with the exclusion's tag when an overlap exists, inside the apply's transaction — the same refusal a
/// table constraint (PostgreSQL's <c>EXCLUDE</c>) raises when it is added over overlapping rows.
/// </para>
/// </remarks>
internal static class ExclusionPlan
{
    /// <summary>One declared exclusion, resolved to the dialect's statements.</summary>
    /// <param name="Entity">The declaring entity.</param>
    /// <param name="Index">The index declaring the exclusion.</param>
    /// <param name="Definition">The exclusion over the entity's table.</param>
    /// <param name="Sql">How the engine enforces it.</param>
    internal sealed record Declared(EntitySchema Entity, IndexSchema Index, ExclusionConstraintDefinition Definition, ExclusionConstraintSql Sql)
    {
        /// <summary>Gets the JSON pointer of the declaration's <c>exclude</c>.</summary>
        internal string Pointer => ExclusionPlan.Pointer(Entity, Index);
    }

    /// <summary>The plan's exclusion statements and steps.</summary>
    /// <param name="Before">The drops, run before EF's DDL (and so before any rename).</param>
    /// <param name="After">The creates, replays and checks of existing rows, run after EF's DDL.</param>
    /// <param name="Added">The exclusions the plan adds.</param>
    /// <param name="Changes">One change per exclusion added or dropped.</param>
    internal sealed record Result(
        IReadOnlyList<string> Before, IReadOnlyList<string> After, IReadOnlyList<Declared> Added, IReadOnlyList<SchemaChange> Changes);

    /// <summary>Refuses <paramref name="desired"/> when it declares an exclusion this engine cannot enforce.</summary>
    /// <param name="desired">The schema being planned.</param>
    /// <param name="dialect">The engine's dialect.</param>
    /// <param name="filters">Renders an exclusion's <c>where</c>.</param>
    /// <exception cref="DescriptorValidationException">The dialect declines an exclusion <paramref name="desired"/> declares.</exception>
    internal static void EnsureEnforceable(SchemaModel desired, IAlvoSqlDialect dialect, IndexFilterSql? filters)
    {
        var declined = Exclusions(desired)
            .FirstOrDefault(pair => dialect.ExclusionConstraint(ExclusionDefinitions.For(pair.Entity, pair.Index, filters)) is null);
        if (declined.Entity is null)
        {
            return;
        }

        throw new DescriptorValidationException(new DescriptorValidationResult(
        [
            new DescriptorValidationError(
                Pointer(declined.Entity, declined.Index),
                "This database engine cannot enforce 'exclude'. Nothing was applied.",
                "Remove 'exclude', or run Alvo on SQLite or PostgreSQL.",
                DescriptorValidationSeverity.Error),
        ]));
    }

    /// <summary>The statements and steps that bring the exclusions from the aligned current schema to the desired one.</summary>
    /// <param name="prePass">The rename pre-pass: the aligned current schema and the exclusions its renames released.</param>
    /// <param name="desired">The schema being planned.</param>
    /// <param name="dialect">The engine's dialect.</param>
    /// <param name="filters">Renders an exclusion's <c>where</c>.</param>
    /// <param name="touched">Every table an EF operation of the plan names — the tables an engine may rebuild.</param>
    /// <param name="epoch">Wraps a qualified <c>datetime</c> column into this engine's comparable epoch expression.</param>
    internal static Result Plan(
        RenamePrePass.Result prePass, SchemaModel desired, IAlvoSqlDialect dialect, IndexFilterSql? filters,
        IReadOnlySet<string> touched, Func<string, string> epoch)
    {
        var had = Declare(prePass.AlignedCurrent, dialect, filters);
        var wants = Declare(desired, dialect, filters);
        var released = prePass.ReleasedExclusions.Select(pair => Resolve(pair.Entity, pair.Index, dialect, filters)).OfType<Declared>().ToList();
        var kept = had.Keys.Where(name => wants.TryGetValue(name, out var wanted) && SameEnforcement(had[name], wanted)).ToHashSet(StringComparer.Ordinal);
        var dropped = had.Values.Where(declared => !kept.Contains(declared.Definition.Name) && HasTable(desired, declared.Definition.Table)).ToList();
        var added = wants.Values.Where(declared => !kept.Contains(declared.Definition.Name)).ToList();
        var replayed = wants.Values.Where(declared => kept.Contains(declared.Definition.Name)
            && !declared.Sql.SurvivesTableRebuild && touched.Contains(declared.Definition.Table));

        return new Result(
            [.. released.Concat(dropped).SelectMany(declared => declared.Sql.Drop)],
            [.. added.Concat(replayed).SelectMany(declared => declared.Sql.Create), .. ExistingRowChecks(added, prePass.AlignedCurrent, epoch)],
            added,
            [.. released.Concat(dropped).Select(declared => Change(declared, SchemaChangeKind.DropIndex)), .. added.Select(declared => Change(declared, SchemaChangeKind.AddIndex))]);
    }

    /// <summary>The JSON pointer of <paramref name="entity"/>'s index <paramref name="index"/>'s <c>exclude</c>.</summary>
    /// <param name="entity">The declaring entity.</param>
    /// <param name="index">One of its indexes.</param>
    internal static string Pointer(EntitySchema entity, IndexSchema index) => $"{IndexPointer(entity, index)}/exclude";

    private static string IndexPointer(EntitySchema entity, IndexSchema index)
    {
        var position = entity.Indexes.ToList().IndexOf(index);
        return $"/entities/{entity.Name}/indexes/{Math.Max(position, 0)}";
    }

    /// <summary>
    /// Every author-declared equality field of <paramref name="added"/>, at its pointer under the index's <c>fields</c> —
    /// the managed <c>tenant_id</c> a scoped entity leads with is not one.
    /// </summary>
    /// <param name="added">The exclusions a plan adds.</param>
    internal static IReadOnlyList<ExclusionEqualityField> EqualityFields(IEnumerable<Declared> added) =>
    [
        .. from declared in added
           from position in Enumerable.Range(0, declared.Index.Fields.Count)
           let name = declared.Index.Fields[position]
           let field = declared.Entity.Fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal))
           where field is not null
           select new ExclusionEqualityField(
               $"{IndexPointer(declared.Entity, declared.Index)}/fields/{position}", name, field.Type),
    ];

    /// <summary>Every index of <paramref name="schema"/> that declares an exclusion, with its entity.</summary>
    /// <param name="schema">The schema to read.</param>
    internal static IEnumerable<(EntitySchema Entity, IndexSchema Index)> Exclusions(SchemaModel schema) =>
        from entity in schema.Entities
        from index in entity.Indexes
        where index.Exclude is not null
        select (entity, index);

    private static Dictionary<string, Declared> Declare(SchemaModel schema, IAlvoSqlDialect dialect, IndexFilterSql? filters) =>
        Exclusions(schema)
            .Select(pair => Resolve(pair.Entity, pair.Index, dialect, filters))
            .OfType<Declared>()
            .DistinctBy(declared => declared.Definition.Name)
            .ToDictionary(declared => declared.Definition.Name, StringComparer.Ordinal);

    private static Declared? Resolve(EntitySchema entity, IndexSchema index, IAlvoSqlDialect dialect, IndexFilterSql? filters)
    {
        var definition = ExclusionDefinitions.For(entity, index, filters);
        return dialect.ExclusionConstraint(definition) is { } sql ? new Declared(entity, index, definition, sql) : null;
    }

    private static IEnumerable<string> ExistingRowChecks(IEnumerable<Declared> added, SchemaModel alignedCurrent, Func<string, string> epoch) =>
        added
            .Where(declared => !declared.Sql.SurvivesTableRebuild && HasTable(alignedCurrent, declared.Definition.Table))
            .Select(declared => ExistingRowCheck(declared.Definition, epoch));

    private static string ExistingRowCheck(ExclusionConstraintDefinition definition, Func<string, string> epoch)
    {
        var table = AlvoSqlIdentifier.Quote(definition.Table);
        var id = AlvoSqlIdentifier.Quote(AlvoDataContext.IdColumn);
        return $"UPDATE {table} SET {id} = {id} WHERE {id} IN (SELECT \"n\".{id} FROM {ExclusionOverlapSql.Pairs(definition, epoch, newRowId: null)})";
    }

    // The name hashes the declaration, not the range's type nor the rendered filter, so one name can stand for a changed
    // enforcement — a date range become datetime — which must be replaced, not kept over the column it can no longer read (M4).
    private static bool SameEnforcement(Declared had, Declared wanted) =>
        had.Definition.RangeType == wanted.Definition.RangeType
        && string.Equals(had.Definition.Filter, wanted.Definition.Filter, StringComparison.Ordinal);

    private static bool HasTable(SchemaModel schema, string table) =>
        schema.Entities.Any(entity => string.Equals(entity.Name, table, StringComparison.Ordinal));

    private static SchemaChange Change(Declared declared, SchemaChangeKind kind) => new()
    {
        Kind = kind,
        Entity = declared.Definition.Table,
        Detail = declared.Definition.Name,
    };
}
