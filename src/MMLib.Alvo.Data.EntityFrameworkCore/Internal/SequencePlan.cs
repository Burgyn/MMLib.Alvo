using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// What a migration plan owes the framework's sequence counters: a refusal when the engine cannot keep one, and the
/// statements that carry a declared rename of a sequenced entity or field over to its counter rows.
/// </summary>
internal static class SequencePlan
{
    /// <summary>Refuses <paramref name="desired"/> when it declares a sequence this engine cannot allocate.</summary>
    /// <param name="desired">The schema being planned.</param>
    /// <param name="dialect">The engine's dialect.</param>
    /// <exception cref="DescriptorValidationException">A field declares a sequence and the dialect declines one.</exception>
    internal static void EnsureAllocatable(SchemaModel desired, IAlvoSqlDialect dialect)
    {
        var sequenced = desired.Entities
            .SelectMany(entity => entity.Fields.Where(field => field.Sequence is not null).Select(field => (Entity: entity.Name, Field: field.Name)))
            .FirstOrDefault();
        if (sequenced.Entity is null || dialect.SequenceAllocationStatement("sequences") is not null)
        {
            return;
        }

        throw new DescriptorValidationException(new DescriptorValidationResult(
        [
            new DescriptorValidationError(
                $"/entities/{sequenced.Entity}/fields/{sequenced.Field}/sequence",
                "This database engine cannot enforce 'sequence'. Nothing was applied.",
                "Remove 'sequence', or run Alvo on SQLite or PostgreSQL.",
                DescriptorValidationSeverity.Error),
        ]));
    }

    /// <summary>
    /// The statements that move the counter rows of every renamed sequenced entity and field to the new names — led by
    /// the counter table's own idempotent DDL, so a rename before any create has a table to update. Empty when no
    /// declared rename touches a sequence.
    /// </summary>
    /// <param name="renames">The plan's declared renames, entities before their fields.</param>
    /// <param name="desired">The schema being planned.</param>
    /// <param name="table">The prefixed counter table.</param>
    internal static IReadOnlyList<string> RenameStatements(
        IEnumerable<MigrationOperation> renames, SchemaModel desired, string table)
    {
        var statements = renames
            .Select(rename => RenameStatement(rename, desired, table))
            .OfType<string>()
            .ToList();

        return statements.Count == 0 ? [] : [SequenceTable.Ddl(table), .. statements];
    }

    private static string? RenameStatement(MigrationOperation rename, SchemaModel desired, string table) => rename switch
    {
        RenameTableOperation entity when IsSequenced(desired, entity.NewName!, field: null) =>
            SequenceTable.RenameEntity(table, entity.Name, entity.NewName!),
        RenameColumnOperation field when IsSequenced(desired, field.Table, field.NewName) =>
            SequenceTable.RenameField(table, field.Table, field.Name, field.NewName),
        _ => null,
    };

    private static bool IsSequenced(SchemaModel desired, string entity, string? field) =>
        desired.Entities
            .Where(candidate => string.Equals(candidate.Name, entity, StringComparison.Ordinal))
            .SelectMany(candidate => candidate.Fields)
            .Any(candidate => candidate.Sequence is not null && (field is null || string.Equals(candidate.Name, field, StringComparison.Ordinal)));
}
