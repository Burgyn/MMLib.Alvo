using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMLib.Alvo;
using MMLib.Alvo.Data.PostgreSql.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the PostgreSQL-backed schema migration provider on an <see cref="IAlvoBuilder"/>.</summary>
public static class AlvoPostgreSqlBuilderExtensions
{
    /// <summary>
    /// Wires <see cref="ISchemaMigrator"/>, <see cref="ISchemaIntrospector"/>, and
    /// <see cref="IAppliedSchemaStore"/> to PostgreSQL, using EF Core's migrations differ, SQL
    /// generator, and scaffolding model factory for the given connection string.
    /// </summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="connectionString">The PostgreSQL ADO.NET connection string.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// Registered as singletons: each service owns one ADO.NET connection for the lifetime of the
    /// container. The underlying EF-backed migrator opens its connection once and leaves it open
    /// for the transaction it runs. Both the migrator and the introspector implement
    /// <see cref="IDisposable"/> and release their connection when the container disposes them —
    /// which only happens at container shutdown for a singleton, so a shorter-than-singleton
    /// lifetime would still churn a connection open/close every time the container disposes a
    /// scope instead of closing it. Schema migration is an administrative operation invoked
    /// rarely — never per-request — so a single long-lived connection per service is the
    /// appropriate shape here, not a scoped or transient one.
    /// </remarks>
    public static IAlvoBuilder UsePostgreSql(this IAlvoBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Ensures IOptions<AlvoOptions> resolves (to the defaults, if AddAlvo() never configured
        // it) regardless of whether this provider is attached through AddAlvo() or directly onto a
        // bare IAlvoBuilder — a provider must not assume a particular caller.
        builder.Services.AddOptions<AlvoOptions>();

        builder.Services.TryAddSingleton<ISchemaMigrator>(_ => PostgreSqlMigrationServices.CreateMigrator(connectionString));
        builder.Services.TryAddSingleton<ISchemaIntrospector>(sp => PostgreSqlMigrationServices.CreateIntrospector(
            connectionString, sp.GetRequiredService<IOptions<AlvoOptions>>().Value.SchemaPrefix));
        builder.Services.TryAddSingleton<IAppliedSchemaStore>(sp =>
            PostgreSqlMigrationServices.CreateAppliedSchemaStore(connectionString, sp.GetRequiredService<IOptions<AlvoOptions>>().Value));

        return builder;
    }
}
