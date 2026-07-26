using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The one <see cref="DbContext"/> the Alvo data path uses, whose model is built at request time from
/// the applied <see cref="SchemaModel"/> as property-bag entity types — records have no CLR types, so
/// there is no entity class to map. <see langword="internal"/> and never handed out: a tracked,
/// mutated property bag saved through the change tracker emits <c>UPDATE … WHERE id = @p</c> with
/// <b>no policy predicate at all</b>, so reachability of this type from outside the data path is an
/// authorization bypass, not a style question.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every property is optional</b>, regardless of the column's own nullability. A <c>hidden</c> field
/// is removed from a response by projecting a typed SQL <c>NULL</c> in its place (the column itself is
/// never read), and a required property would make the shaper throw on that <c>NULL</c> — with a
/// different exception type on each engine. Required-ness is enforced where it belongs: by the
/// database's own <c>NOT NULL</c> on the write path, and by schema-derived request validation above
/// this layer.
/// </para>
/// <para>
/// Queries do not track. That is set once here rather than as an <c>AsNoTracking()</c> per call site,
/// because one forgotten call site is enough to turn a returned row into a tracked entity that a later
/// <c>SaveChanges</c> would write back around policy. Inserts still work — tracking behaviour governs
/// queries, not <see cref="DbContext.Add(object)"/>.
/// </para>
/// <para>
/// Only <see cref="EntityStorage.Physical"/> entities are mapped. A dynamic entity is therefore absent
/// from this model and refused exactly like an unknown one; F7 serves it by registering a dynamic
/// <see cref="IAlvoSqlDialect"/> and field renderer, not by branching here.
/// </para>
/// <para>
/// No foreign key or navigation is configured. The migration model owns the physical relationships; a
/// <c>Ref</c> field is a <c>uuid</c> column here, and relation embedding is not part of this query path.
/// </para>
/// </remarks>
internal sealed class AlvoDataContext : DbContext
{
    internal const string IdColumn = "id";
    internal const string TenantIdColumn = "tenant_id";

    private readonly SchemaModel _schema;

    internal AlvoDataContext(DbContextOptions options, SchemaModel schema, Guid modelToken)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schema = schema;
        ModelToken = modelToken;
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    /// <summary>
    /// Gets the token identifying the applied schema this context's model was built from — the value
    /// <see cref="AlvoModelCacheKeyFactory"/> puts in the model cache key, so a descriptor re-apply gets
    /// a freshly built model instead of silently reusing the previous one.
    /// </summary>
    internal Guid ModelToken { get; }

    internal DbSet<Dictionary<string, object>> Rows(string entity) => Set<Dictionary<string, object>>(entity);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        foreach (var entity in _schema.Entities.Where(entity => entity.Storage == EntityStorage.Physical))
        {
            ConfigureEntity(modelBuilder, entity);
        }
    }

    /// <summary>
    /// Both of these belong to the context rather than to whoever builds it. The model cache key in
    /// particular: EF caches one model per <see cref="DbContext"/> CLR type, so an
    /// <see cref="AlvoDataContext"/> constructed without <see cref="AlvoModelCacheKeyFactory"/> in place
    /// silently serves the first schema the process ever built, whatever <see cref="ModelToken"/> says.
    /// Setting it here means the type cannot be constructed wrongly — a caller that forgot would otherwise
    /// get a stale model with no error at all.
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        optionsBuilder
            .ReplaceService<IModelCacheKeyFactory, AlvoModelCacheKeyFactory>()
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
    }

    private static void ConfigureEntity(ModelBuilder modelBuilder, EntitySchema entity)
    {
        var builder = modelBuilder.SharedTypeEntity<Dictionary<string, object>>(entity.Name);
        builder.ToTable(entity.Name);

        foreach (var field in entity.Fields)
        {
            ConfigureField(builder, field);
        }

        builder.HasKey(IdColumn);
    }

    private static void ConfigureField(EntityTypeBuilder<Dictionary<string, object>> builder, FieldSchema field)
    {
        var property = builder.IndexerProperty(FieldClrTypeMap.Optional(field), field.Name).IsRequired(false);

        if (field.MaxLength is { } maxLength)
        {
            property.HasMaxLength(maxLength);
        }

        if (field.Precision is { } precision)
        {
            property = field.Scale is { } scale ? property.HasPrecision(precision, scale) : property.HasPrecision(precision);
        }
    }
}
