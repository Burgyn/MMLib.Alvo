using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo;
using MMLib.Alvo.Data.PostgreSql.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the PostgreSQL-backed schema migration provider on an <see cref="IAlvoBuilder"/>.</summary>
public static class AlvoPostgreSqlBuilderExtensions
{
    /// <summary>
    /// Wires <see cref="ISchemaMigrator"/> and <see cref="ISchemaIntrospector"/> to PostgreSQL, using
    /// EF Core's migrations differ, SQL generator, and scaffolding model factory for the given
    /// connection string.
    /// </summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="connectionString">The PostgreSQL ADO.NET connection string.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// Registered as singletons: each service owns one ADO.NET connection for the lifetime of the
    /// container. The underlying EF-backed migrator opens its connection once and leaves it open
    /// for the transaction it runs; neither it nor the introspector implements
    /// <see cref="IDisposable"/>, so a shorter-than-singleton lifetime would leak a connection
    /// every time the container disposes a scope instead of closing it. Schema migration is an
    /// administrative operation invoked rarely — never per-request — so a single long-lived
    /// connection per service is the appropriate shape here, not a scoped or transient one.
    /// </remarks>
    public static IAlvoBuilder UsePostgreSql(this IAlvoBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        builder.Services.TryAddSingleton<ISchemaMigrator>(_ => PostgreSqlMigrationServices.CreateMigrator(connectionString));
        builder.Services.TryAddSingleton<ISchemaIntrospector>(_ => PostgreSqlMigrationServices.CreateIntrospector(connectionString));

        return builder;
    }
}
