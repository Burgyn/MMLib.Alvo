using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using System.Data.Common;
using System.Globalization;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Counts the pairs of rows an exclusion about to be added forbids — the pre-apply scan that lets a plan, and so a dry run,
/// report overlapping rows before anything is applied (HX-12), as <see cref="CheckViolationScan"/> does for a validation.
/// </summary>
/// <remarks>
/// <b>Across every tenant, and only a number</b>, operator-facing like <see cref="CheckViolationScan"/>'s count. The SQL is the
/// exclusion's own overlap join (<see cref="ExclusionOverlapSql.Pairs"/>), each pair counted once; its filter is the
/// constraint renderer's inline-literal SQL, never a request's input.
/// </remarks>
internal static class ExclusionOverlapScan
{
    private const string Fix = "Correct or remove the overlapping rows first — or narrow the exclusion with 'where' — then apply again.";

    /// <summary>
    /// The added exclusions that can be counted against their table as it stands: the table exists under that name, no
    /// declared rename touches it, and it holds every column the exclusion compares. Any other is guarded by the apply alone.
    /// </summary>
    /// <param name="current">The schema the database holds.</param>
    /// <param name="prePass">The rename pre-pass, whose released exclusions name the renamed entities.</param>
    /// <param name="added">The exclusions the plan adds.</param>
    internal static IReadOnlyList<ExclusionPlan.Declared> Countable(
        SchemaModel current, RenamePrePass.Result prePass, IReadOnlyList<ExclusionPlan.Declared> added) =>
        [.. added.Where(declared => IsCountable(declared.Definition, current, prePass))];

    /// <summary>The number of forbidden pairs among the rows <paramref name="definition"/>'s table holds.</summary>
    /// <param name="connection">An open connection to the database the plan is for.</param>
    /// <param name="definition">The exclusion.</param>
    /// <param name="epoch">Wraps a qualified <c>datetime</c> column into this engine's comparable epoch expression.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task<long> CountAsync(
        DbConnection connection, ExclusionConstraintDefinition definition, Func<string, string> epoch, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = $"SELECT COUNT(*) FROM {ExclusionOverlapSql.Pairs(definition, epoch, newRowId: null)}";
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The refusal of an exclusion over <paramref name="pairs"/> overlapping pairs.</summary>
    /// <param name="declared">The exclusion.</param>
    /// <param name="pairs">How many pairs overlap.</param>
    internal static DescriptorValidationError Overlapping(ExclusionPlan.Declared declared, long pairs) => new(
        declared.Pointer,
        $"{pairs} pair(s) of '{declared.Entity.Name}' rows overlap, so this exclusion cannot be added. Nothing was applied.",
        Fix,
        DescriptorValidationSeverity.Error);

    /// <summary>The refusal of an exclusion whose count the database could not run.</summary>
    /// <param name="declared">The exclusion.</param>
    internal static DescriptorValidationError Uncountable(ExclusionPlan.Declared declared) => new(
        declared.Pointer,
        $"Could not count the rows of '{declared.Entity.Name}' that overlap, so this exclusion cannot be added. Nothing was applied.",
        "Check that the database still matches the applied schema — a column this exclusion reads may be missing — then apply again.",
        DescriptorValidationSeverity.Error);

    /// <summary>The refusal the apply raises when the engine finds overlapping rows the plan did not count.</summary>
    /// <param name="entity">The declaring entity.</param>
    /// <param name="index">The index declaring the exclusion.</param>
    internal static DescriptorValidationError Overlapping(EntitySchema entity, IndexSchema index) => new(
        ExclusionPlan.Pointer(entity, index),
        $"Rows of '{entity.Name}' overlap, so this exclusion cannot be added. Nothing was applied.",
        Fix,
        DescriptorValidationSeverity.Error);

    private static bool IsCountable(ExclusionConstraintDefinition definition, SchemaModel current, RenamePrePass.Result prePass) =>
        current.Entities.FirstOrDefault(entity => string.Equals(entity.Name, definition.Table, StringComparison.Ordinal)) is { } stored
        && !prePass.ReleasedExclusions.Any(released => string.Equals(released.Entity.Name, stored.Name, StringComparison.Ordinal))
        && prePass.Renames.All(rename => rename.Change.Entity != stored.Name)
        && definition.EqualityColumns.Append(definition.StartColumn).Append(definition.EndColumn)
            .All(column => stored.Fields.Any(field => string.Equals(field.Name, column, StringComparison.Ordinal)));
}
