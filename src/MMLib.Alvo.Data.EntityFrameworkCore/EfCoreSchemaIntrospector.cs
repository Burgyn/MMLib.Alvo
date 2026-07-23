using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.EntityFrameworkCore.Scaffolding.Metadata;
using MMLib.Alvo.Schema;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// An <see cref="ISchemaIntrospector"/> that reverse-engineers a live database into a
/// <see cref="SchemaModel"/> using EF Core's provider-supplied <see cref="IDatabaseModelFactory"/>.
/// </summary>
/// <remarks>
/// This is the inverse of <see cref="DescriptorModelBuilder"/>: it maps a provider's
/// <see cref="DatabaseModel"/> (tables/columns/indexes/foreign keys) back onto a
/// <see cref="SchemaModel"/> (entities/fields/indexes/references). The mapping is necessarily
/// lossy on engines with weak column typing (e.g. SQLite's type affinities), so it only recovers
/// what round-tripping and drift detection need: names, coarse field types, nullability, indexes,
/// and foreign keys.
/// </remarks>
internal sealed class EfCoreSchemaIntrospector : ISchemaIntrospector
{
    private readonly IDatabaseModelFactory _databaseModelFactory;
    private readonly DbConnection _connection;

    public EfCoreSchemaIntrospector(IDatabaseModelFactory databaseModelFactory, DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(databaseModelFactory);
        ArgumentNullException.ThrowIfNull(connection);

        _databaseModelFactory = databaseModelFactory;
        _connection = connection;
    }

    /// <inheritdoc/>
    public Task<SchemaModel> IntrospectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var databaseModel = _databaseModelFactory.Create(_connection, new DatabaseModelFactoryOptions());
        var entities = databaseModel.Tables.Select(ToEntitySchema).ToList();

        return Task.FromResult(new SchemaModel(entities));
    }

    private static EntitySchema ToEntitySchema(DatabaseTable table)
    {
        var foreignKeysByColumn = BuildForeignKeyMap(table);
        var fields = table.Columns.Select(c => ToFieldSchema(c, foreignKeysByColumn)).ToList();
        var indexes = table.Indexes
            .Select(i => new IndexSchema([.. i.Columns.Select(c => c.Name)], i.IsUnique))
            .ToList();

        return new EntitySchema
        {
            Name = table.Name,
            Fields = fields,
            Indexes = indexes,
        };
    }

    private static Dictionary<string, DatabaseForeignKey> BuildForeignKeyMap(DatabaseTable table)
    {
        // A field maps to at most one FK/RefSchema (Alvo references are single-column), so the
        // last foreign key declared on a column wins if there were somehow more than one.
        var map = new Dictionary<string, DatabaseForeignKey>(StringComparer.Ordinal);
        foreach (var fk in table.ForeignKeys)
        {
            foreach (var column in fk.Columns)
            {
                map[column.Name] = fk;
            }
        }

        return map;
    }

    private static FieldSchema ToFieldSchema(DatabaseColumn column, Dictionary<string, DatabaseForeignKey> foreignKeysByColumn)
    {
        var isRef = foreignKeysByColumn.TryGetValue(column.Name, out var foreignKey);
        var type = isRef ? FieldType.Ref : ToFieldType(column.StoreType);

        return new FieldSchema
        {
            Name = column.Name,
            Type = type,
            Nullable = column.IsNullable,
            Required = !column.IsNullable,
            Reference = isRef ? ToRefSchema(foreignKey!) : null,
        };
    }

    private static RefSchema ToRefSchema(DatabaseForeignKey foreignKey) =>
        new(foreignKey.PrincipalTable.Name, ToOnDelete(foreignKey.OnDelete));

    private static OnDelete ToOnDelete(ReferentialAction? action) => action switch
    {
        ReferentialAction.Cascade => OnDelete.Cascade,
        ReferentialAction.SetNull => OnDelete.SetNull,
        _ => OnDelete.Restrict,
    };

    // Inverse of DescriptorModelBuilder.ClrType: providers only ever hand back their own store
    // type names (SQLite's type affinities, PostgreSQL's SQL types, ...), so this matches
    // case-insensitively against the common families rather than any single provider's spelling.
    private static FieldType ToFieldType(string? storeType)
    {
        if (string.IsNullOrEmpty(storeType))
        {
            return FieldType.String;
        }

        var normalized = storeType.Trim();

        if (Contains(normalized, "INT"))
        {
            return FieldType.Integer;
        }

        if (Contains(normalized, "BOOL"))
        {
            return FieldType.Boolean;
        }

        if (Contains(normalized, "TIMESTAMP") || Contains(normalized, "DATETIME"))
        {
            return FieldType.DateTime;
        }

        if (Contains(normalized, "DATE"))
        {
            return FieldType.Date;
        }

        if (Contains(normalized, "NUMERIC") || Contains(normalized, "DECIMAL") ||
            Contains(normalized, "REAL") || Contains(normalized, "DOUBLE") || Contains(normalized, "FLOAT"))
        {
            return FieldType.Decimal;
        }

        if (Contains(normalized, "JSON"))
        {
            return FieldType.Json;
        }

        // TEXT/VARCHAR/CHAR/CLOB/UUID and anything unrecognized default to String: on SQLite,
        // String/Text/Json/Enum/Uuid all collapse to the TEXT affinity, so this family cannot be
        // told apart from StoreType alone (Task 10 report).
        return FieldType.String;
    }

    private static bool Contains(string value, string token) =>
        value.Contains(token, StringComparison.OrdinalIgnoreCase);
}
