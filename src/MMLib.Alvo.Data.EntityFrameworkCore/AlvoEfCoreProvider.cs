using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The reusable seam every EF Core-backed Alvo database provider builds on. It owns the service
/// glue that is identical across relational providers — resolving EF Core's migrations differ, SQL
/// generator, model-runtime initializer, and scaffolding factory from a throwaway
/// <see cref="DbContext"/>, then wiring <see cref="ISchemaMigrator"/>,
/// <see cref="ISchemaIntrospector"/>, <see cref="IDescriptorVersionStore"/>, and
/// <see cref="IAppliedSchemaStore"/> — so a provider package (SQLite, PostgreSQL, or an
/// out-of-repo engine such as Oracle) only supplies the handful of provider-specific callbacks on
/// <see cref="RelationalProviderRegistration"/>.
/// </summary>
public static class AlvoEfCoreProvider
{
    /// <summary>
    /// Registers the schema-migration services for an EF Core-backed relational provider described
    /// by <paramref name="registration"/>. This is the single, public entry point a provider's
    /// <c>UseXxx</c> extension funnels through, so <c>UseSqlite</c>, <c>UsePostgreSql</c>, and any
    /// external provider share one implementation of the resolution glue.
    /// </summary>
    /// <param name="builder">The Alvo builder to register services on.</param>
    /// <param name="registration">The provider-specific building blocks (connection, EF services, model factory).</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// All services are registered as idempotent (<c>TryAdd</c>) singletons, backed by one shared
    /// <see cref="RelationalConnectionFactory"/> singleton: the migrator, introspector, and
    /// descriptor-version store each open a fresh ADO.NET connection per call instead of holding
    /// one for the container's lifetime, so two concurrent callers never race on a shared
    /// connection. <see cref="IDescriptorVersionStore"/> and <see cref="IAppliedSchemaStore"/> both
    /// resolve to the same <see cref="EfCoreDescriptorVersionStore"/> singleton, so the code-first
    /// <c>SchemaMigrationRunner</c> (built against <see cref="IAppliedSchemaStore"/>) and any future
    /// runtime caller (built against <see cref="IDescriptorVersionStore"/>) share one append-only
    /// history. The connection string is resolved from <paramref name="registration"/> at
    /// provider-build time (when a service is first materialized), never eagerly at call time, so
    /// an options-bound connection string is honored.
    /// </remarks>
    public static IAlvoBuilder AddRelationalProvider(this IAlvoBuilder builder, RelationalProviderRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(registration);

        // Ensures IOptions<AlvoOptions> resolves (to the defaults, if AddAlvo() never configured it)
        // regardless of whether this provider is attached through AddAlvo() or directly onto a bare
        // IAlvoBuilder — a provider must not assume a particular caller.
        builder.Services.AddOptions<AlvoOptions>();

        builder.Services.TryAddSingleton(sp => CreateConnectionFactory(sp, registration));
        builder.Services.TryAddSingleton<ISchemaMigrator>(sp => CreateMigrator(sp, registration));
        builder.Services.TryAddSingleton<ISchemaIntrospector>(sp => CreateIntrospector(sp, registration));
        builder.Services.TryAddSingleton(CreateDescriptorVersionStore);
        builder.Services.TryAddSingleton<IDescriptorVersionStore>(sp => sp.GetRequiredService<EfCoreDescriptorVersionStore>());
        builder.Services.TryAddSingleton<IAppliedSchemaStore>(sp => sp.GetRequiredService<EfCoreDescriptorVersionStore>());

        return builder;
    }

    private static RelationalConnectionFactory CreateConnectionFactory(IServiceProvider services, RelationalProviderRegistration registration)
    {
        var connectionString = registration.ConnectionString(services);
        return new RelationalConnectionFactory(() => registration.CreateConnection(connectionString));
    }

    private static EfCoreSchemaMigrator CreateMigrator(IServiceProvider services, RelationalProviderRegistration registration)
    {
        var connectionString = registration.ConnectionString(services);
        using var context = CreateThrowawayContext(registration, connectionString);
        var efServices = context.GetInfrastructure();
        var connections = services.GetRequiredService<RelationalConnectionFactory>();

        return new EfCoreSchemaMigrator(
            efServices.GetRequiredService<IMigrationsModelDiffer>(),
            efServices.GetRequiredService<IMigrationsSqlGenerator>(),
            efServices.GetRequiredService<IModelRuntimeInitializer>(),
            registration.CreateModelBuilder,
            connections);
    }

    private static EfCoreSchemaIntrospector CreateIntrospector(IServiceProvider services, RelationalProviderRegistration registration)
    {
        var connectionString = registration.ConnectionString(services);
        var schemaPrefix = services.GetRequiredService<IOptions<AlvoOptions>>().Value.SchemaPrefix;
        using var context = CreateThrowawayContext(registration, connectionString);
        var databaseModelFactory = registration.CreateDatabaseModelFactory(context.GetInfrastructure());
        var connections = services.GetRequiredService<RelationalConnectionFactory>();

        return new EfCoreSchemaIntrospector(
            databaseModelFactory,
            connections,
            SystemSchemaInitializer.DescriptorVersionsTableName(schemaPrefix));
    }

    private static EfCoreDescriptorVersionStore CreateDescriptorVersionStore(IServiceProvider services)
    {
        var connections = services.GetRequiredService<RelationalConnectionFactory>();
        var options = services.GetRequiredService<IOptions<AlvoOptions>>().Value;

        return new EfCoreDescriptorVersionStore(connections, options);
    }

    // A short-lived context configured with the provider's UseXxx, spun up only to reach its
    // internal EF service provider via DbContext.GetInfrastructure(). It is disposed immediately
    // after the services are resolved: the resolved services (differ, SQL generator, model runtime
    // initializer, scaffolding factory) are already-built object graphs that don't reach back into
    // the disposed provider, so nothing here leaks a second, host-visible container.
    private static DbContext CreateThrowawayContext(RelationalProviderRegistration registration, string connectionString)
    {
        var optionsBuilder = new DbContextOptionsBuilder();
        registration.ConfigureProvider(optionsBuilder, connectionString);

        return new DbContext(optionsBuilder.Options);
    }
}
