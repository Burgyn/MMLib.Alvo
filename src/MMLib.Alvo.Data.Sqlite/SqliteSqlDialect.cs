using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Sqlite;

/// <summary>SQLite's <see cref="IAlvoSqlDialect"/>: unqualified quoted tables, SQLite storage classes, no row lock.</summary>
public sealed class SqliteSqlDialect : IAlvoSqlDialect
{
    /// <inheritdoc/>
    public string RowLockHint => string.Empty;

    /// <inheritdoc/>
    public string RenderTable(EntitySchema entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return AlvoSqlIdentifier.Quote(entity.Name);
    }

    /// <inheritdoc/>
    public string RenderColumn(string columnName) => AlvoSqlIdentifier.Quote(columnName);

    /// <inheritdoc/>
    public string RenderNullProjection(FieldSchema field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return $"CAST(NULL AS {StorageClass(field.Type)})";
    }

    private static string StorageClass(FieldType type) => type switch
    {
        FieldType.Integer => "INTEGER",
        FieldType.Boolean => "INTEGER",
        FieldType.Uuid or FieldType.Ref or FieldType.String or FieldType.Text or FieldType.Json
            or FieldType.Enum or FieldType.Decimal or FieldType.Date or FieldType.DateTime => "TEXT",
        _ => throw new NotSupportedException($"Unsupported field type '{type}'."),
    };
}
