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
            // The desired counterpart is either the entity that explicitly renamed from this one,
            // or (failing that) the entity that still carries the same name.
            var desiredEntity =
                desired.Entities.FirstOrDefault(d => string.Equals(d.RenamedFrom, currentEntity.Name, StringComparison.Ordinal))
                ?? desired.Entities.FirstOrDefault(d => string.Equals(d.Name, currentEntity.Name, StringComparison.Ordinal));

            var alignedName = currentEntity.Name;
            if (desiredEntity is not null
                && string.Equals(desiredEntity.RenamedFrom, currentEntity.Name, StringComparison.Ordinal)
                && !string.Equals(desiredEntity.Name, currentEntity.Name, StringComparison.Ordinal))
            {
                alignedName = desiredEntity.Name;
                renames.Add(new RenameEntry(
                    new RenameTableOperation { Name = currentEntity.Name, NewName = desiredEntity.Name },
                    new SchemaChange { Kind = SchemaChangeKind.RenameEntity, Entity = desiredEntity.Name, FromName = currentEntity.Name }));
            }

            var alignedFields = new List<FieldSchema>(currentEntity.Fields.Count);
            foreach (var currentField in currentEntity.Fields)
            {
                var alignedFieldName = currentField.Name;
                var desiredField = desiredEntity?.Fields
                    .FirstOrDefault(f => string.Equals(f.RenamedFrom, currentField.Name, StringComparison.Ordinal));

                if (desiredField is not null && !string.Equals(desiredField.Name, currentField.Name, StringComparison.Ordinal))
                {
                    alignedFieldName = desiredField.Name;
                    renames.Add(new RenameEntry(
                        new RenameColumnOperation { Table = alignedName, Name = currentField.Name, NewName = desiredField.Name },
                        new SchemaChange
                        {
                            Kind = SchemaChangeKind.RenameField,
                            Entity = alignedName,
                            Field = desiredField.Name,
                            FromName = currentField.Name,
                        }));
                }

                alignedFields.Add(currentField with { Name = alignedFieldName, RenamedFrom = null });
            }

            alignedEntities.Add(currentEntity with { Name = alignedName, RenamedFrom = null, Fields = alignedFields });
        }

        return new Result(new SchemaModel(alignedEntities), renames);
    }
}
