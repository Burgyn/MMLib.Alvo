using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Builds a runtime EF Core <see cref="IModel"/> from a <see cref="SchemaModel"/>, one entity
/// type per <see cref="EntitySchema"/> and one shadow property per <see cref="FieldSchema"/>.
/// Provider-agnostic: the caller supplies a fresh conventionless <see cref="ModelBuilder"/> for
/// whichever provider (SQLite, PostgreSQL, ...) the resulting model targets.
/// </summary>
internal static class DescriptorModelBuilder
{
    public static IModel Build(SchemaModel model, Func<ModelBuilder> newBuilder)
    {
        var builder = newBuilder();

        foreach (var entity in model.Entities)
        {
            ConfigureEntity(builder, entity);
        }

        var knownEntityNames = model.Entities.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var entity in model.Entities)
        {
            ConfigureReferences(builder, entity, knownEntityNames);
        }

        return builder.FinalizeModel();
    }

    private static void ConfigureEntity(ModelBuilder builder, EntitySchema entity)
    {
        var entityBuilder = builder.Entity(entity.Name);
        entityBuilder.ToTable(entity.Name);

        foreach (var field in entity.Fields)
        {
            ConfigureField(entityBuilder, field);
        }

        entityBuilder.HasKey("id");

        foreach (var index in entity.Indexes)
        {
            entityBuilder.HasIndex([.. index.Fields]).IsUnique(index.Unique);
        }
    }

    private static void ConfigureField(EntityTypeBuilder entityBuilder, FieldSchema field)
    {
        var property = entityBuilder.Property(ClrType(field), field.Name).IsRequired(field.Required);

        if (field.MaxLength is { } maxLength)
        {
            property.HasMaxLength(maxLength);
        }

        if (field.Precision is { } precision)
        {
            property = field.Scale is { } scale ? property.HasPrecision(precision, scale) : property.HasPrecision(precision);
        }

        if (field.ComputedExpression is { } computedExpression)
        {
            property.HasComputedColumnSql(computedExpression, stored: true);
        }

        if (field.Unique)
        {
            entityBuilder.HasIndex(field.Name).IsUnique();
        }
        else if (field.Indexed)
        {
            entityBuilder.HasIndex(field.Name);
        }
    }

    private static void ConfigureReferences(ModelBuilder builder, EntitySchema entity, HashSet<string> knownEntityNames)
    {
        var refFields = entity.Fields.Where(f => f.Type == FieldType.Ref && f.Reference is not null);
        foreach (var field in refFields)
        {
            var reference = field.Reference!;
            if (!knownEntityNames.Contains(reference.TargetEntity))
            {
                continue;
            }

            builder.Entity(entity.Name)
                .HasOne(reference.TargetEntity)
                .WithMany()
                .HasForeignKey(field.Name)
                .OnDelete(ToDeleteBehavior(reference.OnDelete));
        }
    }

    private static DeleteBehavior ToDeleteBehavior(OnDelete onDelete) => onDelete switch
    {
        OnDelete.Cascade => DeleteBehavior.Cascade,
        OnDelete.SetNull => DeleteBehavior.SetNull,
        _ => DeleteBehavior.Restrict,
    };

    private static Type ClrType(FieldSchema field) => field.Type switch
    {
        FieldType.Uuid or FieldType.Ref => Nullable(typeof(Guid), field.Required),
        FieldType.String or FieldType.Text or FieldType.Json or FieldType.Enum => typeof(string),
        FieldType.Integer => Nullable(typeof(long), field.Required),
        FieldType.Decimal => Nullable(typeof(decimal), field.Required),
        FieldType.Boolean => Nullable(typeof(bool), field.Required),
        FieldType.Date => Nullable(typeof(DateOnly), field.Required),
        FieldType.DateTime => Nullable(typeof(DateTimeOffset), field.Required),
        _ => throw new NotSupportedException($"Unsupported field type '{field.Type}'."),
    };

    private static Type Nullable(Type valueType, bool required) =>
        required ? valueType : typeof(Nullable<>).MakeGenericType(valueType);
}
