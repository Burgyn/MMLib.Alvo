using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Turns the descriptor-declared renames (<see cref="EntitySchema.RenamedFrom"/> and
/// <see cref="FieldSchema.RenamedFrom"/>) into a deterministic, data-preserving rename pre-pass.
/// </summary>
/// <remarks>
/// EF's differ can <em>guess</em> a rename by pairing a removed and an added column of the same
/// type, but that heuristic is ambiguous (e.g. two same-typed columns swapping names). Alvo's
/// renames are explicit, so instead of trusting the guess we build an <em>aligned</em> copy of the
/// current schema in which every renamed table/column already carries its target name. Diffing that
/// aligned model against the desired model produces a residual with <em>no</em> drop+add for the
/// renamed members, and we prepend our own <see cref="RenameTableOperation"/>/<see
/// cref="RenameColumnOperation"/> so the SQL is a genuine <c>RENAME</c>.
/// <para>
/// <b>An entity a rename touches has its CHECK constraints re-created</b> (HX-11 Ruling 19). A constraint's name is a
/// function of the entity and field names, and its CEL reads the row's field names, so after a rename the database holds
/// each validation under its <em>old</em> name — both engines carry the constraint through <c>RENAME</c> — while the
/// aligned copy could neither name it nor compile it. So every validated field of such an entity is released: the
/// aligned copy carries no validation for it, a <see cref="DropCheckConstraintOperation"/> removes the old-named
/// constraint after the renames, and the residual diff adds the desired ones under their new names.
/// </para>
/// <para>
/// <b>Its partial indexes are re-created the same way</b> (HX-12 D10): a partial index's name hashes the entity, its fields
/// and its predicate, and the predicate reads field names — so every partial index of such an entity is released: the
/// aligned copy omits it, an explicit <c>DROP INDEX IF EXISTS</c> of its old name runs <em>before</em> the renames, and the
/// residual diff creates the desired one under its new name.
/// </para>
/// <para>
/// <b>Its exclusions are released too</b> (HX-12): an exclusion's name hashes the same way and its enforcement reads the
/// range's field names, so every exclusion of such an entity is omitted from the aligned copy and recorded in
/// <see cref="Result.ReleasedExclusions"/> — the migrator drops each by its old definition before the renames, through the
/// dialect that created it, and creates the desired one after the DDL.
/// </para>
/// </remarks>
internal static class RenamePrePass
{
    internal readonly record struct RenameEntry(MigrationOperation Operation, SchemaChange Change);

    /// <summary>An exclusion a rename released: the current entity that declares it, and the index declaring it.</summary>
    /// <param name="Entity">The entity as the database holds it, before the renames.</param>
    /// <param name="Index">The index declaring the exclusion.</param>
    internal readonly record struct ReleasedExclusion(EntitySchema Entity, IndexSchema Index);

    internal sealed record Result(
        SchemaModel AlignedCurrent,
        IReadOnlyList<RenameEntry> Renames,
        IReadOnlyList<RenameEntry> ReleasedChecks,
        IReadOnlyList<RenameEntry> ReleasedIndexes,
        IReadOnlyList<ReleasedExclusion> ReleasedExclusions);

    /// <summary>
    /// Computes the name-aligned "current" schema and the explicit rename operations, from the
    /// <see cref="EntitySchema.RenamedFrom"/>/<see cref="FieldSchema.RenamedFrom"/> markers on the
    /// <paramref name="desired"/> schema.
    /// </summary>
    public static Result Compute(SchemaModel current, SchemaModel desired)
    {
        var alignedEntities = new List<EntitySchema>(current.Entities.Count);
        var renames = new List<RenameEntry>();
        var releasedChecks = new List<RenameEntry>();
        var releasedIndexes = new List<RenameEntry>();

        foreach (var currentEntity in current.Entities)
        {
            alignedEntities.Add(AlignEntity(currentEntity, desired, renames, releasedChecks, releasedIndexes));
        }

        return new Result(new SchemaModel(alignedEntities), renames, releasedChecks, releasedIndexes, ReleasedExclusions(current, alignedEntities));
    }

    private static List<ReleasedExclusion> ReleasedExclusions(SchemaModel current, List<EntitySchema> aligned) =>
    [
        .. from pair in current.Entities.Zip(aligned)
           where pair.First.Indexes.Count(index => index.Exclude is not null) > pair.Second.Indexes.Count(index => index.Exclude is not null)
           from index in pair.First.Indexes.Where(index => index.Exclude is not null)
           select new ReleasedExclusion(pair.First, index),
    ];

    private static bool IsPartial(IndexSchema index) => index.Where is not null && index.Exclude is null;

    private static EntitySchema AlignEntity(
        EntitySchema currentEntity,
        SchemaModel desired,
        List<RenameEntry> renames,
        List<RenameEntry> releasedChecks,
        List<RenameEntry> releasedIndexes)
    {
        var desiredEntity = FindDesiredCounterpart(currentEntity, desired);
        var alignedName = ResolveEntityRename(currentEntity, desiredEntity, renames);
        var (alignedFields, fieldRenames) = AlignFields(currentEntity, desiredEntity, alignedName, renames);
        var renamed = fieldRenames.Count > 0 || !string.Equals(alignedName, currentEntity.Name, StringComparison.Ordinal);

        return currentEntity with
        {
            Name = alignedName,
            RenamedFrom = null,
            Fields = renamed ? ReleaseChecks(currentEntity, alignedName, alignedFields, releasedChecks) : alignedFields,
            Indexes = renamed
                ? ReleasePartialIndexes(currentEntity, alignedName, AlignIndexes(currentEntity, fieldRenames), releasedIndexes)
                : AlignIndexes(currentEntity, fieldRenames),
        };
    }

    // The aligned indexes without the partial ones and the exclusions, each partial one recorded as a drop of the index the
    // database holds under the name the current entity, fields and predicate give it.
    private static List<IndexSchema> ReleasePartialIndexes(
        EntitySchema currentEntity, string alignedName, List<IndexSchema> alignedIndexes, List<RenameEntry> releasedIndexes)
    {
        foreach (var index in currentEntity.Indexes.Where(IsPartial))
        {
            releasedIndexes.Add(ReleasedIndex(alignedName, IndexNames.Partial(currentEntity, index)));
        }

        return [.. alignedIndexes.Where(index => index.Where is null && index.Exclude is null)];
    }

    private static RenameEntry ReleasedIndex(string alignedName, string name) => new(
        new SqlOperation { Sql = $"DROP INDEX IF EXISTS {AlvoSqlIdentifier.Quote(name)}" },
        new SchemaChange { Kind = SchemaChangeKind.DropIndex, Entity = alignedName, Detail = name });

    // The aligned fields with no validation, each validated one recorded as a drop of the constraint the database holds
    // under the entity's and field's current names, on the table as it is named after the renames.
    private static List<FieldSchema> ReleaseChecks(
        EntitySchema currentEntity, string alignedName, List<FieldSchema> alignedFields, List<RenameEntry> releasedChecks)
    {
        foreach (var (currentField, alignedField) in currentEntity.Fields.Zip(alignedFields))
        {
            if (currentField.CheckExpression is not null)
            {
                releasedChecks.Add(ReleasedCheck(currentEntity, currentField, alignedName, alignedField.Name));
            }
        }

        return [.. alignedFields.Select(field => field with { CheckExpression = null })];
    }

    private static RenameEntry ReleasedCheck(EntitySchema currentEntity, FieldSchema currentField, string alignedName, string alignedField)
    {
        var drop = new DropCheckConstraintOperation
        {
            Table = alignedName,
            Name = CheckConstraintNames.For(currentEntity.Name, currentField.Name),
        };

        return new RenameEntry(drop, DestructiveScan.Classify(drop) with { Field = alignedField });
    }

    // The desired counterpart is either the entity that explicitly renamed from this one, or
    // (failing that) the entity that still carries the same name.
    private static EntitySchema? FindDesiredCounterpart(EntitySchema currentEntity, SchemaModel desired) =>
        desired.Entities.FirstOrDefault(d => string.Equals(d.RenamedFrom, currentEntity.Name, StringComparison.Ordinal))
        ?? desired.Entities.FirstOrDefault(d => string.Equals(d.Name, currentEntity.Name, StringComparison.Ordinal));

    private static string ResolveEntityRename(EntitySchema currentEntity, EntitySchema? desiredEntity, List<RenameEntry> renames)
    {
        if (desiredEntity is null
            || !string.Equals(desiredEntity.RenamedFrom, currentEntity.Name, StringComparison.Ordinal)
            || string.Equals(desiredEntity.Name, currentEntity.Name, StringComparison.Ordinal))
        {
            return currentEntity.Name;
        }

        renames.Add(new RenameEntry(
            new RenameTableOperation { Name = currentEntity.Name, NewName = desiredEntity.Name },
            new SchemaChange { Kind = SchemaChangeKind.RenameEntity, Entity = desiredEntity.Name, FromName = currentEntity.Name }));

        return desiredEntity.Name;
    }

    private static (List<FieldSchema> AlignedFields, Dictionary<string, string> FieldRenames) AlignFields(
        EntitySchema currentEntity, EntitySchema? desiredEntity, string alignedEntityName, List<RenameEntry> renames)
    {
        var alignedFields = new List<FieldSchema>(currentEntity.Fields.Count);
        var fieldRenames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var currentField in currentEntity.Fields)
        {
            alignedFields.Add(AlignField(currentField, desiredEntity, alignedEntityName, fieldRenames, renames));
        }

        return (alignedFields, fieldRenames);
    }

    private static FieldSchema AlignField(
        FieldSchema currentField,
        EntitySchema? desiredEntity,
        string alignedEntityName,
        Dictionary<string, string> fieldRenames,
        List<RenameEntry> renames)
    {
        var desiredField = desiredEntity?.Fields
            .FirstOrDefault(f => string.Equals(f.RenamedFrom, currentField.Name, StringComparison.Ordinal));

        if (desiredField is null || string.Equals(desiredField.Name, currentField.Name, StringComparison.Ordinal))
        {
            return currentField with { RenamedFrom = null };
        }

        // TODO(triage): an A→B / B→A swap-rename could collide mid-pre-pass (both
        // names live at once in the aligned model); the descriptor does not express
        // swaps today, tracked for final triage.
        fieldRenames[currentField.Name] = desiredField.Name;
        renames.Add(new RenameEntry(
            new RenameColumnOperation { Table = alignedEntityName, Name = currentField.Name, NewName = desiredField.Name },
            new SchemaChange
            {
                Kind = SchemaChangeKind.RenameField,
                Entity = alignedEntityName,
                Field = desiredField.Name,
                FromName = currentField.Name,
            }));

        return currentField with { Name = desiredField.Name, RenamedFrom = null };
    }

    // Indexes must point at the aligned (new) column names, or DescriptorModelBuilder's HasIndex
    // would reference a property that no longer exists and FinalizeModel() throws.
    private static List<IndexSchema> AlignIndexes(EntitySchema currentEntity, Dictionary<string, string> fieldRenames) =>
        currentEntity.Indexes
            .Select(index => index with
            {
                Fields = [.. index.Fields.Select(f => fieldRenames.GetValueOrDefault(f, f))],
            })
            .ToList();
}
