using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// One exclusion an entity declares, resolved to its table's columns: no two rows with equal
/// <see cref="EqualityColumns"/> may hold overlapping half-open <c>[StartColumn, EndColumn)</c> ranges.
/// </summary>
/// <param name="Table">The entity's table.</param>
/// <param name="Name">The exclusion's stable name — a function of its whole definition, so a changed one is a new name.</param>
/// <param name="EqualityColumns">
/// The columns compared for equality, <c>tenant_id</c> leading on a scoped entity; empty when the whole table is one group.
/// </param>
/// <param name="StartColumn">The column where a range starts (inclusive).</param>
/// <param name="EndColumn">The column where a range ends (exclusive).</param>
/// <param name="RangeType">
/// <see cref="FieldType.Date"/> or <see cref="FieldType.DateTime"/>: the type both range columns share.
/// </param>
/// <param name="Filter">
/// The predicate a row must satisfy to take part, as inline-literal SQL over unqualified columns written by the constraint
/// renderer from the index's <c>where</c> — never caller text — or <see langword="null"/> when every row takes part.
/// </param>
public sealed record ExclusionConstraintDefinition(
    string Table,
    string Name,
    IReadOnlyList<string> EqualityColumns,
    string StartColumn,
    string EndColumn,
    FieldType RangeType,
    string? Filter);
