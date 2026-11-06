using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MMLib.Alvo.Schema;
using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Counts the rows a CHECK constraint about to be added would refuse — the pre-apply scan that lets a plan, and so a dry
/// run, report how many rows break a new validation before anything is applied (HX-11 Rulings 22, 23, 49).
/// </summary>
/// <remarks>
/// <para>
/// <b>Across every tenant, and only a number.</b> The count is operator-facing: it reaches a Management API caller, never
/// a Data API caller, and names no row, id or value.
/// </para>
/// <para>
/// <b>The SQL is the constraint's own.</b> What is counted is the text the constraint carries, rendered by
/// <see cref="CheckConstraintSql"/> from a compiled expression with inline literals — never a request's input — and the
/// table name is delimited. A <c>CHECK</c> admits a row whose predicate is unknown, so the rows it refuses are exactly
/// those for which <c>NOT (…)</c> is true.
/// </para>
/// </remarks>
internal static class CheckViolationScan
{
    /// <summary>One constraint the plan adds that can be counted against the table as it stands.</summary>
    /// <param name="Entity">The desired entity the constraint belongs to.</param>
    /// <param name="Field">The validated field.</param>
    /// <param name="Add">The operation adding the constraint, whose SQL is counted.</param>
    internal readonly record struct Target(EntitySchema Entity, FieldSchema Field, AddCheckConstraintOperation Add);

    /// <summary>
    /// The constraints <paramref name="residual"/> adds that can be counted before the apply (HX-11 Ruling 22).
    /// </summary>
    /// <remarks>
    /// A constraint is countable only when its table exists in <paramref name="current"/> under that name and is not
    /// renamed by this plan, <b>and every column it reads</b> exists there under the same name and is neither the target of
    /// a declared rename nor altered by this plan — otherwise the count would name a column that does not exist yet, or
    /// read one whose values the apply is about to change. A rollup whose definition the plan changes is one of those: the
    /// table holds the old definition's aggregate until the apply's backfill (<see cref="RollupBackfill"/>), so a count
    /// over it would judge values the apply replaces (Task 9 review m4). Every other constraint is guarded by the DDL
    /// alone, and an engine refusal there is translated by <see cref="CheckApplyRefusal"/>.
    /// </remarks>
    /// <param name="current">The schema the database holds.</param>
    /// <param name="desired">The schema being planned.</param>
    /// <param name="renames">The plan's declared renames.</param>
    /// <param name="residual">The plan's remaining operations.</param>
    /// <param name="checks">The renderer that compiles a validation, to learn what it reads.</param>
    internal static IReadOnlyList<Target> Targets(
        SchemaModel current, SchemaModel desired, IReadOnlyList<MigrationOperation> renames,
        IReadOnlyList<MigrationOperation> residual, CheckConstraintSql checks) =>
        [.. residual.OfType<AddCheckConstraintOperation>()
            .Select(add => Countable(add, current, desired, renames, residual, checks))
            .OfType<Target>()];

    private static Target? Countable(
        AddCheckConstraintOperation add, SchemaModel current, SchemaModel desired, IReadOnlyList<MigrationOperation> renames,
        IReadOnlyList<MigrationOperation> residual, CheckConstraintSql checks)
    {
        if (Named(current, add.Table) is not { } stored
            || Named(desired, add.Table) is not { } entity
            || renames.OfType<RenameTableOperation>().Any(rename => rename.NewName == add.Table)
            || CheckConstraintNames.Owner(desired, entity, add.Name) is not { } owner
            || owner.Entity.Name != add.Table)
        {
            return null;
        }

        return checks.Reads(owner.Entity, owner.Field) is { } reads
            && reads.All(column => Unchanged(column, stored, renames, residual) && !Recomputed(column, stored, entity))
            ? new Target(owner.Entity, owner.Field, add)
            : null;
    }

    private static bool Unchanged(
        string column, EntitySchema stored, IReadOnlyList<MigrationOperation> renames, IReadOnlyList<MigrationOperation> residual) =>
        stored.Fields.Any(field => field.Name == column)
        && !renames.OfType<RenameColumnOperation>().Any(rename => rename.Table == stored.Name && rename.NewName == column)
        && !residual.OfType<AlterColumnOperation>().Any(alter => alter.Table == stored.Name && alter.Name == column);

    private static bool Recomputed(string column, EntitySchema stored, EntitySchema desired) =>
        desired.Fields.FirstOrDefault(field => field.Name == column) is { Rollup: not null } field
        && RollupBackfill.Changed(stored, field);

    private static EntitySchema? Named(SchemaModel schema, string name) =>
        schema.Entities.FirstOrDefault(entity => string.Equals(entity.Name, name, StringComparison.Ordinal));

    /// <summary>The number of rows of <paramref name="table"/> that <paramref name="checkSql"/> refuses.</summary>
    /// <param name="connection">An open connection to the database the plan is for.</param>
    /// <param name="table">The table's name, undelimited.</param>
    /// <param name="checkSql">The constraint's SQL.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task<long> CountAsync(DbConnection connection, string table, string checkSql, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkSql);

        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = $"SELECT COUNT(*) FROM {AlvoSqlIdentifier.Quote(table)} WHERE NOT ({checkSql})";
            var count = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return Convert.ToInt64(count, CultureInfo.InvariantCulture);
        }
    }
}
