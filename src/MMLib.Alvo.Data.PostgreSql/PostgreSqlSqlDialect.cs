using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.PostgreSql;

/// <summary>
/// PostgreSQL's <see cref="IAlvoSqlDialect"/>: unqualified quoted tables (<c>AlvoOptions.SchemaPrefix</c>
/// is a table-name prefix, not a database schema), PostgreSQL column types, and a real row lock.
/// </summary>
public sealed class PostgreSqlSqlDialect : IAlvoSqlDialect
{
    /// <inheritdoc/>
    public string RowLockHint => " FOR UPDATE";

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
        return $"CAST(NULL AS {ColumnType(field.Type)})";
    }

    private static string ColumnType(FieldType type) => type switch
    {
        FieldType.Uuid or FieldType.Ref => "uuid",
        FieldType.String or FieldType.Text or FieldType.Enum => "text",
        FieldType.Json => "jsonb",
        FieldType.Integer => "bigint",
        FieldType.Decimal => "numeric(18,2)",
        FieldType.Boolean => "boolean",
        FieldType.Date => "date",
        FieldType.DateTime => "timestamptz",
        _ => throw new NotSupportedException($"Unsupported field type '{type}'."),
    };
}
