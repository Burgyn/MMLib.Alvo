using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>The tenant guards a plan adds with <c>ON DELETE SET NULL</c> (#161), as the descriptor pointers that declared them.</summary>
internal static class ScopedSetNullGuards
{
    /// <summary>The <c>/entities/{child}/fields/{field}/onDelete</c> pointer of every such guard the plan adds.</summary>
    /// <param name="operations">The plan's operations.</param>
    internal static IReadOnlyList<string> In(IEnumerable<MigrationOperation> operations) =>
    [
        .. operations
            .SelectMany(Added)
            .Where(op => op.OnDelete == ReferentialAction.SetNull && TenantGuards.IsGuard(op))
            .Select(op => $"/entities/{op.Table}/fields/{TenantGuards.GuardedField(op.Columns)}/onDelete")
            .Distinct(StringComparer.Ordinal),
    ];

    private static IEnumerable<AddForeignKeyOperation> Added(MigrationOperation operation) => operation switch
    {
        AddForeignKeyOperation add => [add],
        CreateTableOperation create => create.ForeignKeys,
        _ => [],
    };
}
