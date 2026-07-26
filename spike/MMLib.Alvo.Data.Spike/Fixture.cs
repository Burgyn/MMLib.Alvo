using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MMLib.Alvo.Schema;
using System.Data.Common;
using System.Text;

namespace MMLib.Alvo.Data.Spike;

/// <summary>
/// The one entity every probe runs against — deliberately shaped like a real descriptor entity:
/// framework-managed <c>id</c>/<c>tenant_id</c>, an ownership column the policy predicate keys off,
/// a <c>hidden</c> field question 4 must keep out of the <c>SELECT</c> list, and one column of every
/// awkward type (decimal, bool, timestamptz).
/// </summary>
public static class Fixture
{
    public const string EntityName = "vehicle";

    public static readonly Guid AliceId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid BobId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    public static readonly Guid AcmeTenant = Guid.Parse("11111111-0000-0000-0000-000000000001");
    public static readonly Guid OtherTenant = Guid.Parse("22222222-0000-0000-0000-000000000002");

    public static readonly Guid AliceCar = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    public static readonly Guid AliceVan = Guid.Parse("dddddddd-0000-0000-0000-000000000002");
    public static readonly Guid BobCar = Guid.Parse("dddddddd-0000-0000-0000-000000000003");
    public static readonly Guid OtherTenantCar = Guid.Parse("dddddddd-0000-0000-0000-000000000004");

    public static EntitySchema Entity { get; } = new()
    {
        Name = EntityName,
        Tenancy = TenancyMode.Scoped,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Nullable = false },
            new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid, Nullable = false },
            new FieldSchema { Name = "owner_id", Type = FieldType.Uuid, Nullable = true },
            new FieldSchema { Name = "plate", Type = FieldType.String, Nullable = false, MaxLength = 32 },
            new FieldSchema { Name = "status", Type = FieldType.String, Nullable = true, MaxLength = 32 },
            new FieldSchema { Name = "secret_note", Type = FieldType.String, Nullable = true, MaxLength = 200 },
            new FieldSchema { Name = "mileage", Type = FieldType.Integer, Nullable = true },
            new FieldSchema { Name = "price", Type = FieldType.Decimal, Nullable = true, Precision = 18, Scale = 2 },
            new FieldSchema { Name = "is_active", Type = FieldType.Boolean, Nullable = true },
            new FieldSchema { Name = "created_at", Type = FieldType.DateTime, Nullable = false },
        ],
    };

    public static SchemaModel Model { get; } = new([Entity]);

    /// <summary>The field the descriptor marks <c>hidden</c> — question 4's subject.</summary>
    public const string HiddenField = "secret_note";

    public static string QualifiedTable(SpikeEngine engine) =>
        engine.Schema is null
            ? Quote(EntityName)
            : $"{Quote(engine.Schema)}.{Quote(EntityName)}";

    public static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// Creates the table by hand (the migrator is out of scope here) and seeds it <b>through EF's own
    /// property-bag change tracker</b>, so every stored value carries the provider's own type mapping.
    /// Seeding by hand-rolled ADO.NET instead is what produced the spike's first false negative —
    /// see the verdict, question 6.
    /// </summary>
    public static async Task CreateAndSeedAsync(SpikeEngine engine)
    {
        await using var connection = engine.CreateConnection();
        await connection.OpenAsync();

        var columns = new StringBuilder();
        foreach (var field in Entity.Fields)
        {
            if (columns.Length > 0)
            {
                columns.Append(",\n  ");
            }

            columns.Append($"{Quote(field.Name)} {engine.ColumnType(field.Type)}{(field.Nullable ? " NULL" : " NOT NULL")}");
        }

        await Execute(connection, $"DROP TABLE IF EXISTS {QualifiedTable(engine)};");
        await Execute(
            connection,
            $"CREATE TABLE {QualifiedTable(engine)} (\n  {columns},\n  PRIMARY KEY ({Quote("id")})\n);");

        var options = new DbContextOptionsBuilder();
        engine.UseProvider(options, connection);
        await using var context = new SpikeContext(options.Options, Model, engine.Schema);
        Seed(context, AliceCar, AcmeTenant, AliceId, "ACME-001", "open", "alice-secret", 1000, 10000.50m, true);
        Seed(context, AliceVan, AcmeTenant, AliceId, "ACME-002", "closed", "alice-secret-2", 2000, 20000.00m, false);
        Seed(context, BobCar, AcmeTenant, BobId, "ACME-003", "open", "bob-secret", 3000, 30000.00m, true);
        Seed(context, OtherTenantCar, OtherTenant, AliceId, "OTHR-001", "open", "other-secret", 4000, 40000.00m, true);
        await context.SaveChangesAsync();
    }

    private static void Seed(
        SpikeContext context, Guid id, Guid tenant, Guid? owner,
        string plate, string status, string secret, long mileage, decimal price, bool active) =>
        context.Rows(EntityName).Add(new Dictionary<string, object>
        {
            ["id"] = id,
            ["tenant_id"] = tenant,
            ["owner_id"] = owner!,
            ["plate"] = plate,
            ["status"] = status,
            ["secret_note"] = secret,
            ["mileage"] = mileage,
            ["price"] = price,
            ["is_active"] = active,
            ["created_at"] = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });

    private static async Task Execute(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// A <see cref="DbContext"/> whose model is built at runtime from a <see cref="SchemaModel"/> as
/// property-bag entity types — option (a) under test. No CLR entity class exists anywhere.
/// </summary>
public sealed class SpikeContext(DbContextOptions options, SchemaModel schema, string? dbSchema, bool allOptional = false)
    : DbContext(options)
{
    /// <summary>Whether this context's runtime model marks every property optional (see the ctor).</summary>
    public bool AllOptional => allOptional;

    public DbSet<Dictionary<string, object>> Rows(string entity) => Set<Dictionary<string, object>>(entity);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        foreach (var entity in schema.Entities)
        {
            var builder = modelBuilder.SharedTypeEntity<Dictionary<string, object>>(entity.Name);
            builder.ToTable(entity.Name, dbSchema);

            foreach (var field in entity.Fields)
            {
                // allOptional: every property is nullable + not-required in the RUNTIME model, so the
                // read path can project NULL over a hidden column even when the column is NOT NULL in
                // the database. Question 4's recommended mechanism.
                var property = builder
                    .IndexerProperty(allOptional ? Nullable(ClrType(field), true) : ClrType(field), field.Name)
                    .IsRequired(!allOptional && !field.Nullable);
                if (field.MaxLength is { } maxLength)
                {
                    property.HasMaxLength(maxLength);
                }

                if (field.Precision is { } precision && field.Scale is { } scale)
                {
                    property.HasPrecision(precision, scale);
                }
            }

            builder.HasKey("id");
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

    // Same mapping DescriptorModelBuilder.ClrType uses.
    private static Type ClrType(FieldSchema field) => field.Type switch
    {
        FieldType.Uuid or FieldType.Ref => Nullable(typeof(Guid), field.Nullable),
        FieldType.String or FieldType.Text or FieldType.Json or FieldType.Enum => typeof(string),
        FieldType.Integer => Nullable(typeof(long), field.Nullable),
        FieldType.Decimal => Nullable(typeof(decimal), field.Nullable),
        FieldType.Boolean => Nullable(typeof(bool), field.Nullable),
        FieldType.Date => Nullable(typeof(DateOnly), field.Nullable),
        FieldType.DateTime => Nullable(typeof(DateTimeOffset), field.Nullable),
        _ => throw new NotSupportedException(field.Type.ToString()),
    };

    private static Type Nullable(Type type, bool nullable) =>
        nullable && type.IsValueType && System.Nullable.GetUnderlyingType(type) is null
            ? typeof(Nullable<>).MakeGenericType(type)
            : type;
}

/// <summary>
/// EF caches one model per <see cref="DbContext"/> CLR type, so two <see cref="SpikeContext"/>
/// instances that build <em>different</em> runtime models (all-optional vs schema-faithful, or two
/// descriptor versions) would silently share the first one's model. PR2 needs exactly this seam.
/// </summary>
public sealed class SpikeModelCacheKeyFactory : Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), (context as SpikeContext)?.AllOptional ?? false, designTime);
}
