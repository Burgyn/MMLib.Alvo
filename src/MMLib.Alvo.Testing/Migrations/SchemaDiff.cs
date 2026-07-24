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
        var currentByName = current.Entities.ToDictionary(e => e.Name);
        var desiredByName = desired.Entities.ToDictionary(e => e.Name);
        var claimedCurrentEntityNames = new HashSet<string>();

        var steps = new List<MigrationStep>();
        steps.AddRange(DiffEntities(desired, currentByName, desiredByName, claimedCurrentEntityNames));
        steps.AddRange(DropRemovedEntities(current, desiredByName, claimedCurrentEntityNames));
        return steps;
    }

    private static IEnumerable<MigrationStep> DiffEntities(
        SchemaModel desired,
        Dictionary<string, EntitySchema> currentByName,
        Dictionary<string, EntitySchema> desiredByName,
        HashSet<string> claimedCurrentEntityNames)
    {
        foreach (var desiredEntity in desired.Entities)
        {
            foreach (var step in DiffEntity(desiredEntity, currentByName, desiredByName, claimedCurrentEntityNames))
            {
                yield return step;
            }
        }
    }

    private static IEnumerable<MigrationStep> DiffEntity(
        EntitySchema desiredEntity,
        Dictionary<string, EntitySchema> currentByName,
        Dictionary<string, EntitySchema> desiredByName,
        HashSet<string> claimedCurrentEntityNames)
    {
        if (desiredEntity.RenamedFrom is { } renamedFrom
            && currentByName.TryGetValue(renamedFrom, out var renameSource)
            && !desiredByName.ContainsKey(renamedFrom))
        {
            claimedCurrentEntityNames.Add(renamedFrom);
            yield return RenameEntityStep(desiredEntity.Name, renamedFrom);

            foreach (var step in ComputeFieldSteps(desiredEntity.Name, renameSource, desiredEntity))
            {
                yield return step;
            }

            yield break;
        }

        if (currentByName.TryGetValue(desiredEntity.Name, out var existingEntity))
        {
            claimedCurrentEntityNames.Add(desiredEntity.Name);

            foreach (var step in ComputeFieldSteps(desiredEntity.Name, existingEntity, desiredEntity))
            {
                yield return step;
            }

            yield break;
        }

        yield return CreateEntityStep(desiredEntity.Name);

        foreach (var step in ComputeFieldSteps(desiredEntity.Name, entitySchema: null, desiredEntity))
        {
            yield return step;
        }
    }

    private static IEnumerable<MigrationStep> DropRemovedEntities(
        SchemaModel current,
        Dictionary<string, EntitySchema> desiredByName,
        HashSet<string> claimedCurrentEntityNames)
    {
        foreach (var currentEntity in current.Entities)
        {
            if (claimedCurrentEntityNames.Contains(currentEntity.Name) || desiredByName.ContainsKey(currentEntity.Name))
            {
                continue;
            }

            yield return DropEntityStep(currentEntity.Name);
        }
    }

    private static IEnumerable<MigrationStep> ComputeFieldSteps(string entityName, EntitySchema? entitySchema, EntitySchema desiredEntity)
    {
        var currentFieldsByName = (entitySchema?.Fields ?? []).ToDictionary(f => f.Name);
        var desiredFieldsByName = desiredEntity.Fields.ToDictionary(f => f.Name);
        var claimedCurrentFieldNames = new HashSet<string>();

        foreach (var step in DiffFields(entityName, desiredEntity, currentFieldsByName, desiredFieldsByName, claimedCurrentFieldNames))
        {
            yield return step;
        }

        foreach (var step in DropRemovedFields(entityName, entitySchema, desiredFieldsByName, claimedCurrentFieldNames))
        {
            yield return step;
        }
    }

    private static IEnumerable<MigrationStep> DiffFields(
        string entityName,
        EntitySchema desiredEntity,
        Dictionary<string, FieldSchema> currentFieldsByName,
        Dictionary<string, FieldSchema> desiredFieldsByName,
        HashSet<string> claimedCurrentFieldNames)
    {
        foreach (var desiredField in desiredEntity.Fields)
        {
            foreach (var step in DiffField(entityName, desiredField, currentFieldsByName, desiredFieldsByName, claimedCurrentFieldNames))
            {
                yield return step;
            }
        }
    }

    private static IEnumerable<MigrationStep> DiffField(
        string entityName,
        FieldSchema desiredField,
        Dictionary<string, FieldSchema> currentFieldsByName,
        Dictionary<string, FieldSchema> desiredFieldsByName,
        HashSet<string> claimedCurrentFieldNames)
    {
        if (desiredField.RenamedFrom is { } renamedFrom
            && currentFieldsByName.TryGetValue(renamedFrom, out var renameSource)
            && !desiredFieldsByName.ContainsKey(renamedFrom))
        {
            foreach (var step in RenamedFieldSteps(entityName, desiredField, renamedFrom, renameSource, claimedCurrentFieldNames))
            {
                yield return step;
            }

            yield break;
        }

        if (currentFieldsByName.TryGetValue(desiredField.Name, out var existingField))
        {
            foreach (var step in ExistingFieldSteps(entityName, desiredField, existingField, claimedCurrentFieldNames))
            {
                yield return step;
            }

            yield break;
        }

        yield return AddFieldStep(entityName, desiredField.Name);
    }

    private static IEnumerable<MigrationStep> RenamedFieldSteps(
        string entityName,
        FieldSchema desiredField,
        string renamedFrom,
        FieldSchema renameSource,
        HashSet<string> claimedCurrentFieldNames)
    {
        claimedCurrentFieldNames.Add(renamedFrom);
        yield return RenameFieldStep(entityName, desiredField.Name, renamedFrom);

        foreach (var alterStep in ComputeAlterStep(entityName, renameSource, desiredField))
        {
            yield return alterStep;
        }
    }

    private static IEnumerable<MigrationStep> ExistingFieldSteps(
        string entityName,
        FieldSchema desiredField,
        FieldSchema existingField,
        HashSet<string> claimedCurrentFieldNames)
    {
        claimedCurrentFieldNames.Add(desiredField.Name);

        foreach (var alterStep in ComputeAlterStep(entityName, existingField, desiredField))
        {
            yield return alterStep;
        }
    }

    private static IEnumerable<MigrationStep> DropRemovedFields(
        string entityName,
        EntitySchema? entitySchema,
        Dictionary<string, FieldSchema> desiredFieldsByName,
        HashSet<string> claimedCurrentFieldNames)
    {
        foreach (var currentField in entitySchema?.Fields ?? [])
        {
            if (claimedCurrentFieldNames.Contains(currentField.Name) || desiredFieldsByName.ContainsKey(currentField.Name))
            {
                continue;
            }

            yield return DropFieldStep(entityName, currentField.Name);
        }
    }

    private static IEnumerable<MigrationStep> ComputeAlterStep(string entityName, FieldSchema current, FieldSchema desired)
    {
        if (IsUnchanged(current, desired))
        {
            yield break;
        }

        var isDestructive = IsDestructiveNarrowing(current, desired);
        var reason = isDestructive ? $"narrows or tightens field '{entityName}.{desired.Name}'" : null;

        yield return AlterFieldStep(entityName, desired.Name, isDestructive, reason);
    }

    private static bool IsUnchanged(FieldSchema current, FieldSchema desired) =>
        current.Type == desired.Type
        && current.Required == desired.Required
        && current.Nullable == desired.Nullable
        && current.MaxLength == desired.MaxLength
        && current.Precision == desired.Precision
        && current.Scale == desired.Scale;

    private static bool IsDestructiveNarrowing(FieldSchema current, FieldSchema desired)
    {
        var isNarrowing = current.MaxLength is { } currentMaxLength
            && desired.MaxLength is { } desiredMaxLength
            && desiredMaxLength < currentMaxLength;

        return isNarrowing || (!current.Required && desired.Required);
    }

    private static MigrationStep CreateEntityStep(string entity) =>
        new(new SchemaChange { Kind = SchemaChangeKind.CreateEntity, Entity = entity }, IsDestructive: false, Reason: null);

    private static MigrationStep RenameEntityStep(string entity, string fromName) =>
        new(
            new SchemaChange { Kind = SchemaChangeKind.RenameEntity, Entity = entity, FromName = fromName },
            IsDestructive: false,
            Reason: null);

    private static MigrationStep DropEntityStep(string entity) =>
        new(
            new SchemaChange { Kind = SchemaChangeKind.DropEntity, Entity = entity, IsDestructive = true },
            IsDestructive: true,
            Reason: $"drops entity '{entity}' and its data");

    private static MigrationStep AddFieldStep(string entity, string field) =>
        new(new SchemaChange { Kind = SchemaChangeKind.AddField, Entity = entity, Field = field }, IsDestructive: false, Reason: null);

    private static MigrationStep RenameFieldStep(string entity, string field, string fromName) =>
        new(
            new SchemaChange { Kind = SchemaChangeKind.RenameField, Entity = entity, Field = field, FromName = fromName },
            IsDestructive: false,
            Reason: null);

    private static MigrationStep DropFieldStep(string entity, string field) =>
        new(
            new SchemaChange { Kind = SchemaChangeKind.DropField, Entity = entity, Field = field, IsDestructive = true },
            IsDestructive: true,
            Reason: $"drops field '{entity}.{field}' and its data");

    private static MigrationStep AlterFieldStep(string entity, string field, bool isDestructive, string? reason) =>
        new(
            new SchemaChange { Kind = SchemaChangeKind.AlterField, Entity = entity, Field = field, IsDestructive = isDestructive },
            isDestructive,
            reason);
}
