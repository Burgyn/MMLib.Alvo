using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Classifies an EF <see cref="MigrationOperation"/> into an Alvo <see cref="SchemaChange"/>,
/// deciding its <see cref="SchemaChangeKind"/> and whether it destroys data.
/// </summary>
/// <remarks>
/// Destructive = data can be lost by applying it: dropping a table or column, or an
/// <see cref="AlterColumnOperation"/> that <em>narrows</em> the column (nullable → not-null, a
/// smaller length/precision/scale, or a changed type). Widening alters, adds, creates, and index
/// changes are non-destructive.
/// </remarks>
internal static class DestructiveScan
{
    /// <summary>Maps a single EF migration operation to its Alvo <see cref="SchemaChange"/>.</summary>
    public static SchemaChange Classify(MigrationOperation operation) => operation switch
    {
        CreateTableOperation or DropTableOperation or RenameTableOperation => ClassifyTable(operation),
        AddColumnOperation or DropColumnOperation or RenameColumnOperation or AlterColumnOperation => ClassifyColumn(operation),
        CreateIndexOperation or DropIndexOperation or RenameIndexOperation => ClassifyIndex(operation),
        AddForeignKeyOperation op => ForeignKeyChange(op.Table, op.Columns, added: true),
        DropForeignKeyOperation op => ForeignKeyChange(op.Table, columns: null, added: false),
        AddPrimaryKeyOperation op => TableConstraintChange(op.Table, "Add primary key."),
        DropPrimaryKeyOperation op => TableConstraintChange(op.Table, "Drop primary key."),
        AddUniqueConstraintOperation op => TableConstraintChange(op.Table, "Add unique constraint."),
        DropUniqueConstraintOperation op => TableConstraintChange(op.Table, "Drop unique constraint."),
        _ => new SchemaChange
        {
            Kind = SchemaChangeKind.AlterField,
            Entity = TableOf(operation) ?? string.Empty,
            Detail = operation.GetType().Name,
        },
    };

    // Table-level operations: create, drop, rename.
    private static SchemaChange ClassifyTable(MigrationOperation operation) => operation switch
    {
        CreateTableOperation op => new SchemaChange
        {
            Kind = SchemaChangeKind.CreateEntity,
            Entity = op.Name,
        },
        DropTableOperation op => new SchemaChange
        {
            Kind = SchemaChangeKind.DropEntity,
            Entity = op.Name,
            IsDestructive = true,
            Detail = "Drops the table and all its rows.",
        },
        RenameTableOperation op => new SchemaChange
        {
            Kind = SchemaChangeKind.RenameEntity,
            Entity = op.NewName ?? op.Name,
            FromName = op.Name,
        },
        _ => throw new NotSupportedException($"{operation.GetType().Name} is not a table operation."),
    };

    // Column-level operations: add, drop, rename, alter (the latter delegates to
    // ClassifyAlterColumn/NarrowingReason for the destructive-narrowing analysis).
    private static SchemaChange ClassifyColumn(MigrationOperation operation) => operation switch
    {
        AddColumnOperation op => new SchemaChange
        {
            Kind = SchemaChangeKind.AddField,
            Entity = op.Table,
            Field = op.Name,
        },
        DropColumnOperation op => new SchemaChange
        {
            Kind = SchemaChangeKind.DropField,
            Entity = op.Table,
            Field = op.Name,
            IsDestructive = true,
            Detail = "Drops the column and all its data.",
        },
        RenameColumnOperation op => new SchemaChange
        {
            Kind = SchemaChangeKind.RenameField,
            Entity = op.Table,
            Field = op.NewName,
            FromName = op.Name,
        },
        AlterColumnOperation op => ClassifyAlterColumn(op),
        _ => throw new NotSupportedException($"{operation.GetType().Name} is not a column operation."),
    };

    // Index-level operations: create, drop, rename.
    private static SchemaChange ClassifyIndex(MigrationOperation operation) => operation switch
    {
        CreateIndexOperation op => new SchemaChange
        {
            Kind = SchemaChangeKind.AddIndex,
            Entity = op.Table,
            Detail = op.Name,
        },
        DropIndexOperation op => new SchemaChange
        {
            Kind = SchemaChangeKind.DropIndex,
            Entity = op.Table ?? string.Empty,
            Detail = op.Name,
        },
        RenameIndexOperation op => new SchemaChange
        {
            Kind = SchemaChangeKind.RenameIndex,
            Entity = op.Table ?? string.Empty,
            Detail = $"Rename index '{op.Name}' to '{op.NewName}'.",
        },
        _ => throw new NotSupportedException($"{operation.GetType().Name} is not an index operation."),
    };

    // Best-effort table name for operations we do not model explicitly (there is no common
    // "table operation" base type in EF's model to cast to).
    private static string? TableOf(MigrationOperation operation) =>
        operation.GetType().GetProperty("Table")?.GetValue(operation) as string;

    private static SchemaChange ForeignKeyChange(string table, string[]? columns, bool added) => new()
    {
        Kind = SchemaChangeKind.AlterField,
        Entity = table,
        Field = columns is { Length: > 0 } ? columns[0] : null,
        Detail = added ? "Add foreign key." : "Drop foreign key.",
    };

    private static SchemaChange TableConstraintChange(string table, string detail) => new()
    {
        Kind = SchemaChangeKind.AlterField,
        Entity = table,
        Detail = detail,
    };

    private static SchemaChange ClassifyAlterColumn(AlterColumnOperation op)
    {
        var (destructive, reason) = NarrowingReason(op);
        return new SchemaChange
        {
            Kind = SchemaChangeKind.AlterField,
            Entity = op.Table,
            Field = op.Name,
            IsDestructive = destructive,
            Detail = reason ?? "Alter column.",
        };
    }

    private static (bool Destructive, string? Reason) NarrowingReason(AlterColumnOperation op)
    {
        var old = op.OldColumn;
        if (old is null)
        {
            return (false, null);
        }

        if (old.IsNullable && !op.IsNullable)
        {
            return (true, "Column becomes non-nullable; existing NULL values would be rejected.");
        }

        // A newly-imposed bound is narrowing too: an unbounded column gaining a MaxLength /
        // Precision / Scale can truncate or reject existing values, so old == null && new != null
        // counts alongside the shrink-when-both-bound case.
        if (op.MaxLength is { } newLength && (old.MaxLength is not { } oldLength || newLength < oldLength))
        {
            return (true, old.MaxLength is { } ol
                ? $"Max length shrinks from {ol} to {newLength}; longer values would be truncated."
                : $"Max length is newly bounded to {newLength}; longer values would be truncated.");
        }

        if (op.Precision is { } newPrecision && (old.Precision is not { } oldPrecision || newPrecision < oldPrecision))
        {
            return (true, old.Precision is { } op2
                ? $"Precision shrinks from {op2} to {newPrecision}."
                : $"Precision is newly bounded to {newPrecision}.");
        }

        if (op.Scale is { } newScale && (old.Scale is not { } oldScale || newScale < oldScale))
        {
            return (true, old.Scale is { } os
                ? $"Scale shrinks from {os} to {newScale}."
                : $"Scale is newly bounded to {newScale}.");
        }

        if (old.ClrType != op.ClrType)
        {
            return (true, $"Type changes from {old.ClrType.Name} to {op.ClrType.Name}.");
        }

        return (false, null);
    }
}
