using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Neutralizes EF's <em>guessed</em> renames. EF's <see cref="IMigrationsModelDiffer"/> pairs an
/// unmatched dropped column/table with an unmatched added one of a compatible type and emits a
/// single <see cref="RenameColumnOperation"/>/<see cref="RenameTableOperation"/>. In Alvo a rename
/// is only ever legitimate when the descriptor <em>declares</em> it via <c>RenamedFrom</c> — those
/// are handled by <see cref="RenamePrePass"/> and never appear in the differ's residual. Therefore
/// <em>any</em> rename operation in the residual is a heuristic guess: accepting it would silently
/// carry a dropped column's data into an unrelated new column and slip a destructive change past
/// the guardrail without <c>AllowDestructive</c>.
/// </summary>
/// <remarks>
/// Each guessed rename is split back into the destructive Drop + fresh Add pair it stands in for,
/// so <see cref="DestructiveScan"/> marks the drop destructive and the data is <em>not</em> carried
/// over (a drop loses data, an add starts empty) — the correct semantics for two unrelated members.
/// The replacement Drop/Add operations are recovered from EF itself via a single-difference scoped
/// diff (source and target identical except for the one member), which yields exactly the operation
/// EF would emit for a genuine drop/add — every column facet (type, nullability, default, length,
/// precision, computed SQL) intact — rather than being hand-reconstructed and risking drift.
/// </remarks>
internal static class RenameGuessSplitter
{
    /// <summary>
    /// Replaces every guessed rename in <paramref name="residual"/> with its equivalent Drop+Add
    /// (or DropTable+CreateTable) pair, preserving the order of all other operations.
    /// </summary>
    public static IReadOnlyList<MigrationOperation> Normalize(
        IReadOnlyList<MigrationOperation> residual,
        SchemaModel alignedCurrent,
        SchemaModel desired,
        Func<SchemaModel, IModel> buildModel,
        IMigrationsModelDiffer differ)
    {
        // Fast path: nothing guessed, return the residual untouched (the common case).
        if (!residual.Any(op => op is RenameColumnOperation or RenameTableOperation))
        {
            return residual;
        }

        var normalized = new List<MigrationOperation>(residual.Count + 2);
        foreach (var operation in residual)
        {
            normalized.AddRange(SplitRenameGuess(operation, alignedCurrent, desired, buildModel, differ));
        }

        return normalized;
    }

    private static IEnumerable<MigrationOperation> SplitRenameGuess(
        MigrationOperation operation,
        SchemaModel alignedCurrent,
        SchemaModel desired,
        Func<SchemaModel, IModel> buildModel,
        IMigrationsModelDiffer differ) =>
        operation switch
        {
            RenameColumnOperation rename => SplitColumnRenameGuess(rename, alignedCurrent, desired, buildModel, differ),
            RenameTableOperation rename => SplitTableRenameGuess(rename, alignedCurrent, desired, buildModel, differ),
            _ => [operation],
        };

    private static IEnumerable<MigrationOperation> SplitColumnRenameGuess(
        RenameColumnOperation rename,
        SchemaModel alignedCurrent,
        SchemaModel desired,
        Func<SchemaModel, IModel> buildModel,
        IMigrationsModelDiffer differ) =>
        [
            .. ScopedDiff(alignedCurrent, RemoveField(alignedCurrent, rename.Table, rename.Name), buildModel, differ),
            .. ScopedDiff(RemoveField(desired, rename.Table, rename.NewName!), desired, buildModel, differ),
        ];

    private static IEnumerable<MigrationOperation> SplitTableRenameGuess(
        RenameTableOperation rename,
        SchemaModel alignedCurrent,
        SchemaModel desired,
        Func<SchemaModel, IModel> buildModel,
        IMigrationsModelDiffer differ) =>
        [
            .. ScopedDiff(alignedCurrent, RemoveEntity(alignedCurrent, rename.Name), buildModel, differ),
            .. ScopedDiff(RemoveEntity(desired, rename.NewName!), desired, buildModel, differ),
        ];

    // Diffs two schemas that differ in exactly one member, so EF returns exactly the drop (or add)
    // operation for that member, with all its facets, and no cross-operation interference.
    private static IReadOnlyList<MigrationOperation> ScopedDiff(
        SchemaModel from, SchemaModel to, Func<SchemaModel, IModel> buildModel, IMigrationsModelDiffer differ) =>
        differ.GetDifferences(buildModel(from).GetRelationalModel(), buildModel(to).GetRelationalModel());

    private static SchemaModel RemoveField(SchemaModel schema, string entityName, string fieldName) =>
        new(schema.Entities
            .Select(entity => string.Equals(entity.Name, entityName, StringComparison.Ordinal)
                ? entity with
                {
                    Fields = [.. entity.Fields.Where(f => !string.Equals(f.Name, fieldName, StringComparison.Ordinal))],
                    // Drop any index that referenced the removed field, or DescriptorModelBuilder's
                    // HasIndex would point at a property that no longer exists and FinalizeModel throws.
                    Indexes = [.. entity.Indexes.Where(index => !index.Fields.Contains(fieldName, StringComparer.Ordinal))],
                }
                : entity)
            .ToList());

    private static SchemaModel RemoveEntity(SchemaModel schema, string entityName) =>
        new([.. schema.Entities.Where(e => !string.Equals(e.Name, entityName, StringComparison.Ordinal))]);
}
