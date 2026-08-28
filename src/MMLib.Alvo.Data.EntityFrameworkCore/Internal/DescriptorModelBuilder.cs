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
    /// <summary>Builds the migration model for <paramref name="model"/>.</summary>
    /// <param name="model">The applied schema the physical model is built from.</param>
    /// <param name="newBuilder">Creates a conventionless builder seeded with the provider's convention set.</param>
    /// <param name="computed">
    /// Renders a <c>computed</c> field's CEL to this driver's SQL, or <see langword="null"/> when the container
    /// registered no expression services — see <see cref="EnsureComputedIsRenderable"/> for why that is a
    /// refusal here rather than a silently plain column.
    /// </param>
    public static IModel Build(SchemaModel model, Func<ModelBuilder> newBuilder, ComputedColumnSql? computed = null)
    {
        var builder = newBuilder();

        foreach (var entity in model.Entities)
        {
            ConfigureEntity(builder, entity, computed);
        }

        var knownEntityNames = model.Entities.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var entity in model.Entities)
        {
            ConfigureReferences(builder, entity, knownEntityNames);
        }

        return builder.FinalizeModel();
    }

    private static void ConfigureEntity(ModelBuilder builder, EntitySchema entity, ComputedColumnSql? computed)
    {
        var entityBuilder = builder.Entity(entity.Name);
        entityBuilder.ToTable(entity.Name);

        foreach (var field in entity.Fields)
        {
            ConfigureField(entityBuilder, entity, field, computed);
        }

        entityBuilder.HasKey("id");

        ConfigureIndexes(entityBuilder, entity);
    }

    /// <summary>
    /// Emits every index the entity earns — the per-field ones a <c>unique</c>/<c>indexed</c> facet asks for
    /// and the entity's own declared ones — <b>after</b> the field loop, and scopes each <em>unique</em> one
    /// to the tenant on a <see cref="TenancyMode.Scoped"/> entity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the tenant column is in the index (#137).</b> A bare <c>HasIndex(field).IsUnique()</c> on a
    /// scoped entity enforces uniqueness across the <em>whole instance</em>, so tenant B's create of a value
    /// only tenant A holds is refused while the same create of a free value succeeds — the two requests differ
    /// in exactly one thing, whether another tenant holds the value, so any observable difference between the
    /// answers discloses that fact. That is a cross-tenant existence oracle, and it contradicts the premise
    /// that Alvo's app-side rules are as safe as native row-level security. <c>(tenant_id, field)</c> keeps the
    /// constraint doing its job <em>within</em> a tenant and removes the signal between tenants; note that
    /// mapping the underlying violation to a clean <c>409</c> (#138) does <b>not</b> close it, because
    /// <c>409</c>-versus-<c>201</c> is the same one-bit signal as <c>500</c>-versus-<c>201</c>.
    /// </para>
    /// <para>
    /// <b>Why it cannot stay inside the field loop.</b> <c>DescriptorToSchemaMapper</c> appends the managed
    /// columns <em>after</em> the declared ones, so <c>tenant_id</c> is not yet a property of the entity type
    /// when a unique field earlier in the list is configured — naming it there fails, and worse, would fail
    /// only for the entities whose field order happens to put it last, which is all of them.
    /// </para>
    /// <para>
    /// <b>Only unique indexes are scoped.</b> A non-unique index enforces nothing and therefore discloses
    /// nothing; prefixing one would change emitted DDL for no security gain and would overrule an author's own
    /// column-order decision, which is the whole content of a non-unique index.
    /// </para>
    /// <para>
    /// <b><see langword="internal"/> rather than private, because two models have to agree about this.</b>
    /// This builder's model is the one the migrator <em>creates</em> the index from;
    /// <c>AlvoDataContext</c>'s runtime model is the one that has to <em>recognise</em> it, because
    /// <c>ConstraintViolationTranslator</c> resolves the constraint name PostgreSQL reports against
    /// <c>IEntityType.GetIndexes()</c>. While the runtime model declared no indexes at all that resolution
    /// always came back empty, and a duplicate on PostgreSQL kept surfacing as a 500 — measured by
    /// <c>MMLib.Alvo.Testing.Data.AlvoDataConstraintTests</c> after SQLite had already passed on the strength
    /// of the columns its own message names. One method called from both places is what makes the two models'
    /// index sets one decision rather than two.
    /// </para>
    /// </remarks>
    internal static void ConfigureIndexes(EntityTypeBuilder entityBuilder, EntitySchema entity)
    {
        foreach (var field in entity.Fields.Where(field => field.Unique))
        {
            entityBuilder.HasIndex([.. UniqueColumns(entity, [field.Name])]).IsUnique();
        }

        foreach (var index in entity.Indexes)
        {
            var columns = index.Unique ? UniqueColumns(entity, index.Fields) : index.Fields;
            entityBuilder.HasIndex([.. columns]).IsUnique(index.Unique);
        }
    }

    /// <summary>
    /// The columns one unique index actually spans: <paramref name="fields"/> on a non-scoped entity, and
    /// <c>tenant_id</c> ahead of them on a scoped one.
    /// </summary>
    /// <remarks>
    /// The tenant column leads rather than trails so the index also serves the tenant-narrowed reads every
    /// query on a scoped entity performs — a composite index is usable by a prefix of its columns, and
    /// <c>tenant_id</c> is the one predicate every such statement carries. A descriptor that already named it
    /// keeps its own position: EF refuses an index naming one property twice, so an unconditional prepend
    /// would turn a legal descriptor into a startup crash.
    /// </remarks>
    /// <param name="entity">The entity the index belongs to.</param>
    /// <param name="fields">The fields the index was declared over.</param>
    private static IReadOnlyList<string> UniqueColumns(EntitySchema entity, IReadOnlyList<string> fields) =>
        entity.Tenancy == TenancyMode.Scoped && !fields.Contains(AlvoManagedColumns.TenantId, StringComparer.Ordinal)
            ? [AlvoManagedColumns.TenantId, .. fields]
            : fields;

    private static void ConfigureField(
        EntityTypeBuilder entityBuilder, EntitySchema entity, FieldSchema field, ComputedColumnSql? computed)
    {
        var property = entityBuilder.Property(FieldClrTypeMap.Exact(field), field.Name).IsRequired(!field.Nullable);

        if (field.MaxLength is { } maxLength)
        {
            property.HasMaxLength(maxLength);
        }

        if (field.Precision is { } precision)
        {
            property = field.Scale is { } scale ? property.HasPrecision(precision, scale) : property.HasPrecision(precision);
        }

        ConfigureComputed(property, entity, field, computed);

        // A unique field's index is emitted by ConfigureIndexes, after this loop — see its remarks: on a
        // scoped entity it spans tenant_id, which is not a property of the entity type yet. `!field.Unique`
        // preserves the previous if/else: a field declaring both facets earns the unique index only.
        if (!field.Unique && field.Indexed)
        {
            entityBuilder.HasIndex(field.Name);
        }
    }

    /// <summary>
    /// Marks a <c>computed</c> field a <b>stored</b> generated column, so <em>EF's own per-provider migrations
    /// generator</em> spells the DDL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what satisfies "the core never spells the DDL", and it is not a shortcut.</b> Measured
    /// (spike Q5–Q7): from this one annotation EF emits <c>numeric(18,2) GENERATED ALWAYS AS (…) STORED</c> in
    /// PostgreSQL's <c>CREATE TABLE</c> and its in-place <c>ALTER TABLE … ADD</c>, the legal short form
    /// <c>AS (…) STORED</c> on SQLite, and — the finding this whole seam turns on — SQLite's create-new / copy /
    /// drop / rename rebuild, whose <c>INSERT … SELECT</c> correctly omits the generated column. None of that
    /// could be assembled here without also owning every column's type mapping.
    /// </para>
    /// <para>
    /// <b><c>stored: true</c>, never virtual.</b> SQLite accepts <c>VIRTUAL</c> exactly where it refuses
    /// <c>STORED</c>, and PostgreSQL 16 has no <c>VIRTUAL</c> at all, so letting each engine pick would make
    /// one descriptor produce a column one engine can index and filter on and the other cannot — §0 principle
    /// 3's failure mode, silently.
    /// </para>
    /// </remarks>
    private static void ConfigureComputed(
        PropertyBuilder property, EntitySchema entity, FieldSchema field, ComputedColumnSql? computed)
    {
        if (field.ComputedExpression is null)
        {
            return;
        }

        EnsureComputedIsRenderable(entity, field, computed);
        property.HasComputedColumnSql(computed!.For(entity, field), stored: true);
    }

    /// <summary>
    /// Refuses a <c>computed</c> field when no expression services reached this model builder, naming the
    /// registration that is missing.
    /// </summary>
    /// <remarks>
    /// A driver's <c>UseSqlite</c>/<c>UsePostgreSql</c> can be attached to a bare builder that never called
    /// <c>AddAlvo()</c>, and in that container there is no CEL compiler and no renderer. Falling through to a
    /// plain column would be the worst available outcome: the descriptor says the database maintains the value,
    /// the column would hold whatever anyone wrote, and nothing would report it. A missing registration is a
    /// composition mistake, so it is named as one rather than surfacing as a
    /// <see cref="NullReferenceException"/> or as an <c>InvalidOperationException</c> about a service the
    /// author never asked for.
    /// </remarks>
    private static void EnsureComputedIsRenderable(EntitySchema entity, FieldSchema field, ComputedColumnSql? computed)
    {
        if (computed is null)
        {
            throw new InvalidOperationException(
                $"Field '{entity.Name}.{field.Name}' declares 'computed', which becomes a stored generated "
                + "column whose SQL is rendered from CEL — but this container registered no expression "
                + "services, so there is nothing to render it with. Call AddAlvo() on the same service "
                + "collection as UseSqlite()/UsePostgreSql().");
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
}
