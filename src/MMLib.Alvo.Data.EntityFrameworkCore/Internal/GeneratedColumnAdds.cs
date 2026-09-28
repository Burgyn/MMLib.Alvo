using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// Plans a new stored generated column as the two hops an engine that cannot <c>ADD</c> one to a populated table
/// needs: the column added plain, then altered into the generated one — which the provider's SQL generator
/// answers with a table rebuild.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> SQLite refuses <c>ALTER TABLE … ADD COLUMN … STORED</c> on a table that holds a row
/// (<c>cannot add a STORED column</c>). EF Core's SQLite generator implements the create-new / copy / drop / rename
/// rebuild, but reaches it from an <see cref="AlterColumnOperation"/> and never from an
/// <see cref="AddColumnOperation"/>, so a computed field added to a deployed entity failed at apply. The rebuild
/// copies every row, re-creates the table's indexes and foreign keys from the model, and omits the generated
/// column from its <c>INSERT … SELECT</c>; the engine then computes it for each copied row.
/// </para>
/// <para>
/// <b>Only the SQL is rewritten, never the plan's steps.</b> The migrator classifies the differ's own operations,
/// so the change stays one non-destructive <c>AddField</c> whatever DDL the engine needs for it.
/// </para>
/// <para>
/// Asked only of a dialect that answers <see cref="IAlvoSqlDialect.GeneratedColumnAddRequiresTableRebuild"/>: on
/// PostgreSQL the in-place <c>ADD … STORED</c> works over rows, and the same two hops would be more DDL for nothing.
/// </para>
/// </remarks>
internal static class GeneratedColumnAdds
{
    /// <summary>The operations, with every stored generated column addition split into its two hops.</summary>
    /// <param name="operations">The migration's operations, in order.</param>
    internal static List<MigrationOperation> AsRebuilds(IEnumerable<MigrationOperation> operations) =>
        [.. operations.SelectMany(Expand)];

    private static IEnumerable<MigrationOperation> Expand(MigrationOperation operation) =>
        operation is AddColumnOperation { ComputedColumnSql: not null, IsStored: true } add
            ? [Plain(add), IntoGenerated(add)]
            : [operation];

    /// <summary>The column as an ordinary, nullable one — legal to add to a table that holds rows.</summary>
    private static AddColumnOperation Plain(AddColumnOperation add) => new()
    {
        Name = add.Name,
        Table = add.Table,
        Schema = add.Schema,
        ClrType = add.ClrType,
        ColumnType = add.ColumnType,
        IsNullable = true,
        MaxLength = add.MaxLength,
        Precision = add.Precision,
        Scale = add.Scale,
        IsUnicode = add.IsUnicode,
        IsFixedLength = add.IsFixedLength,
        Collation = add.Collation,
    };

    /// <summary>The same column altered into the generated one the model declares, from its plain form.</summary>
    private static AlterColumnOperation IntoGenerated(AddColumnOperation add) => new()
    {
        Name = add.Name,
        Table = add.Table,
        Schema = add.Schema,
        ClrType = add.ClrType,
        ColumnType = add.ColumnType,
        IsNullable = add.IsNullable,
        MaxLength = add.MaxLength,
        Precision = add.Precision,
        Scale = add.Scale,
        IsUnicode = add.IsUnicode,
        IsFixedLength = add.IsFixedLength,
        Collation = add.Collation,
        Comment = add.Comment,
        ComputedColumnSql = add.ComputedColumnSql,
        IsStored = add.IsStored,
        OldColumn = Plain(add),
    };
}
