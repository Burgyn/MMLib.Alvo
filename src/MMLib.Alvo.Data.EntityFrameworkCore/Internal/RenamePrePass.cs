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
/// </remarks>
internal static class RenamePrePass
{
    internal readonly record struct RenameEntry(MigrationOperation Operation, SchemaChange Change);

    internal sealed record Result(SchemaModel AlignedCurrent, IReadOnlyList<RenameEntry> Renames);

    /// <summary>
    /// Computes the name-aligned "current" schema and the explicit rename operations, from the
    /// <see cref="EntitySchema.RenamedFrom"/>/<see cref="FieldSchema.RenamedFrom"/> markers on the
    /// <paramref name="desired"/> schema.
    /// </summary>
    public static Result Compute(SchemaModel current, SchemaModel desired)
    {
        var alignedEntities = new List<EntitySchema>(current.Entities.Count);
        var renames = new List<RenameEntry>();

        foreach (var currentEntity in current.Entities)
        {
            alignedEntities.Add(AlignEntity(currentEntity, desired, renames));
        }

        return new Result(new SchemaModel(alignedEntities), renames);
    }

    private static EntitySchema AlignEntity(EntitySchema currentEntity, SchemaModel desired, List<RenameEntry> renames)
    {
        var desiredEntity = FindDesiredCounterpart(currentEntity, desired);
        var alignedName = ResolveEntityRename(currentEntity, desiredEntity, renames);
        var (alignedFields, fieldRenames) = AlignFields(currentEntity, desiredEntity, alignedName, renames);

        return currentEntity with
        {
            Name = alignedName,
            RenamedFrom = null,
            Fields = alignedFields,
            Indexes = AlignIndexes(currentEntity, fieldRenames),
        };
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
