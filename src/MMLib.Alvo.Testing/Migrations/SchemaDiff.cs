using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Testing.Migrations;

/// <summary>
/// Computes a semantic diff between two <see cref="SchemaModel"/> instances as
/// an ordered list of <see cref="MigrationStep"/>s. Test-support only:
/// production Data.* providers compute their own plan (e.g. via EF's differ)
/// and do not depend on this type.
/// </summary>
public static class SchemaDiff
{
    /// <summary>
    /// Computes the steps needed to migrate from <paramref name="current"/> to
    /// <paramref name="desired"/>, detecting entity/field renames via
    /// <see cref="EntitySchema.RenamedFrom"/>/<see cref="FieldSchema.RenamedFrom"/>
    /// instead of emitting a drop followed by an add.
    /// </summary>
    /// <param name="current">The current schema.</param>
    /// <param name="desired">The desired schema.</param>
    /// <returns>The ordered migration steps.</returns>
    public static IReadOnlyList<MigrationStep> Compute(SchemaModel current, SchemaModel desired)
    {
        var steps = new List<MigrationStep>();
        var currentByName = current.Entities.ToDictionary(e => e.Name);
        var desiredByName = desired.Entities.ToDictionary(e => e.Name);
        var claimedCurrentEntityNames = new HashSet<string>();

        foreach (var desiredEntity in desired.Entities)
        {
            if (desiredEntity.RenamedFrom is { } renamedFrom
                && currentByName.TryGetValue(renamedFrom, out var renameSource)
                && !desiredByName.ContainsKey(renamedFrom))
            {
                claimedCurrentEntityNames.Add(renamedFrom);
                steps.Add(new MigrationStep(
                    new SchemaChange
                    {
                        Kind = SchemaChangeKind.RenameEntity,
                        Entity = desiredEntity.Name,
                        FromName = renamedFrom,
                    },
                    IsDestructive: false,
                    Reason: null));
                steps.AddRange(ComputeFieldSteps(desiredEntity.Name, renameSource, desiredEntity));
                continue;
            }

            if (currentByName.TryGetValue(desiredEntity.Name, out var existingEntity))
            {
                claimedCurrentEntityNames.Add(desiredEntity.Name);
                steps.AddRange(ComputeFieldSteps(desiredEntity.Name, existingEntity, desiredEntity));
                continue;
            }

            steps.Add(new MigrationStep(
                new SchemaChange { Kind = SchemaChangeKind.CreateEntity, Entity = desiredEntity.Name },
                IsDestructive: false,
                Reason: null));
            steps.AddRange(ComputeFieldSteps(desiredEntity.Name, entitySchema: null, desiredEntity));
        }

        foreach (var currentEntity in current.Entities)
        {
            if (claimedCurrentEntityNames.Contains(currentEntity.Name) || desiredByName.ContainsKey(currentEntity.Name))
            {
                continue;
            }

            steps.Add(new MigrationStep(
                new SchemaChange { Kind = SchemaChangeKind.DropEntity, Entity = currentEntity.Name, IsDestructive = true },
                IsDestructive: true,
                Reason: $"drops entity '{currentEntity.Name}' and its data"));
        }

        return steps;
    }

    private static IEnumerable<MigrationStep> ComputeFieldSteps(string entityName, EntitySchema? entitySchema, EntitySchema desiredEntity)
    {
        var currentFieldsByName = (entitySchema?.Fields ?? []).ToDictionary(f => f.Name);
        var desiredFieldsByName = desiredEntity.Fields.ToDictionary(f => f.Name);
        var claimedCurrentFieldNames = new HashSet<string>();

        foreach (var desiredField in desiredEntity.Fields)
        {
            if (desiredField.RenamedFrom is { } renamedFrom
                && currentFieldsByName.TryGetValue(renamedFrom, out var renameSource)
                && !desiredFieldsByName.ContainsKey(renamedFrom))
            {
                claimedCurrentFieldNames.Add(renamedFrom);
                yield return new MigrationStep(
                    new SchemaChange
                    {
                        Kind = SchemaChangeKind.RenameField,
                        Entity = entityName,
                        Field = desiredField.Name,
                        FromName = renamedFrom,
                    },
                    IsDestructive: false,
                    Reason: null);

                foreach (var alterStep in ComputeAlterStep(entityName, renameSource, desiredField))
                {
                    yield return alterStep;
                }

                continue;
            }

            if (currentFieldsByName.TryGetValue(desiredField.Name, out var existingField))
            {
                claimedCurrentFieldNames.Add(desiredField.Name);
                foreach (var alterStep in ComputeAlterStep(entityName, existingField, desiredField))
                {
                    yield return alterStep;
                }

                continue;
            }

            yield return new MigrationStep(
                new SchemaChange { Kind = SchemaChangeKind.AddField, Entity = entityName, Field = desiredField.Name },
                IsDestructive: false,
                Reason: null);
        }

        foreach (var currentField in entitySchema?.Fields ?? [])
        {
            if (claimedCurrentFieldNames.Contains(currentField.Name) || desiredFieldsByName.ContainsKey(currentField.Name))
            {
                continue;
            }

            yield return new MigrationStep(
                new SchemaChange
                {
                    Kind = SchemaChangeKind.DropField,
                    Entity = entityName,
                    Field = currentField.Name,
                    IsDestructive = true,
                },
                IsDestructive: true,
                Reason: $"drops field '{entityName}.{currentField.Name}' and its data");
        }
    }

    private static IEnumerable<MigrationStep> ComputeAlterStep(string entityName, FieldSchema current, FieldSchema desired)
    {
        if (current.Type == desired.Type
            && current.Required == desired.Required
            && current.Nullable == desired.Nullable
            && current.MaxLength == desired.MaxLength
            && current.Precision == desired.Precision
            && current.Scale == desired.Scale)
        {
            yield break;
        }

        var isNarrowing = current.MaxLength is { } currentMaxLength
            && desired.MaxLength is { } desiredMaxLength
            && desiredMaxLength < currentMaxLength;
        var isDestructive = isNarrowing || (!current.Required && desired.Required);

        yield return new MigrationStep(
            new SchemaChange
            {
                Kind = SchemaChangeKind.AlterField,
                Entity = entityName,
                Field = desired.Name,
                IsDestructive = isDestructive,
            },
            isDestructive,
            isDestructive ? $"narrows or tightens field '{entityName}.{desired.Name}'" : null);
    }
}
