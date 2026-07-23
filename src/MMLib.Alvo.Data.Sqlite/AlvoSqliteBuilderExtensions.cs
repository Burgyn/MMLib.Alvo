using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMLib.Alvo;
using MMLib.Alvo.Data.Sqlite.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the SQLite-backed schema migration provider on an <see cref="IAlvoBuilder"/>.</summary>
public static class AlvoSqliteBuilderExtensions
{
    /// <summary>
    /// Wires <see cref="ISchemaMigrator"/>, <see cref="ISchemaIntrospector"/>, and
    /// <see cref="IAppliedSchemaStore"/> to SQLite, using EF Core's migrations differ, SQL
    /// generator, and scaffolding model factory for the given connection string.
    /// </summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="connectionString">The SQLite ADO.NET connection string.</param>
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
    public static IAlvoBuilder UseSqlite(this IAlvoBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Ensures IOptions<AlvoOptions> resolves (to the defaults, if AddAlvo() never configured
        // it) regardless of whether this provider is attached through AddAlvo() or directly onto a
        // bare IAlvoBuilder — a provider must not assume a particular caller.
        builder.Services.AddOptions<AlvoOptions>();

        builder.Services.TryAddSingleton<ISchemaMigrator>(_ => SqliteMigrationServices.CreateMigrator(connectionString));
        builder.Services.TryAddSingleton<ISchemaIntrospector>(sp => SqliteMigrationServices.CreateIntrospector(
            connectionString, sp.GetRequiredService<IOptions<AlvoOptions>>().Value.SchemaPrefix));
        builder.Services.TryAddSingleton<IAppliedSchemaStore>(sp =>
            SqliteMigrationServices.CreateAppliedSchemaStore(connectionString, sp.GetRequiredService<IOptions<AlvoOptions>>().Value));

        return builder;
    }
}
