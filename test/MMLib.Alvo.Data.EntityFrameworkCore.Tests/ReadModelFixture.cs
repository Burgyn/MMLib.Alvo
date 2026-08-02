using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// Builds the real property-bag read model for a schema and hands back one entity type, so a test that
/// needs a column's <em>store type</em> reads the one EF resolved rather than a hand-written table. No
/// connection is ever opened — a model is metadata, and it outlives the context that built it.
/// </summary>
internal static class ReadModelFixture
{
    internal static IEntityType Rows(EntitySchema entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return Rows(new SchemaModel([entity]), entity.Name);
    }

    internal static IEntityType Rows(SchemaModel schema, string entity)
    {
        using var context = new AlvoDataContext(SqliteOptions(), schema, Guid.NewGuid());
        return context.Model.FindEntityType(entity)
            ?? throw new InvalidOperationException($"'{entity}' is not mapped by the read model.");
    }

    /// <summary>
    /// A property-bag entity type with no key at all — a shape <see cref="AlvoDataContext"/> cannot produce
    /// (it always calls <c>HasKey</c>) and that therefore only a foreign model could hand the read path.
    /// </summary>
    internal static IEntityType KeylessRows()
    {
        using var context = new KeylessContext(SqliteOptions());
        return context.Model.FindEntityType(KeylessContext.EntityName)!;
    }

    private static DbContextOptions SqliteOptions()
    {
        var options = new DbContextOptionsBuilder();
        options.UseSqlite("Data Source=:memory:", static sqlite => sqlite.UseRelationalNulls());
        return options.Options;
    }

    private sealed class KeylessContext(DbContextOptions options) : DbContext(options)
    {
        internal const string EntityName = "keyless";

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var builder = modelBuilder.SharedTypeEntity<Dictionary<string, object>>(EntityName);
            builder.IndexerProperty<string>("title");
            builder.HasNoKey();
        }
    }
}
