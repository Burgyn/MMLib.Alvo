using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The reusable seam every EF Core-backed Alvo database provider builds on. It owns the service
/// glue that is identical across relational providers — resolving EF Core's migrations differ, SQL
/// generator, model-runtime initializer, and scaffolding factory from a throwaway
/// <see cref="DbContext"/>, then wiring <see cref="ISchemaMigrator"/>,
/// <see cref="ISchemaIntrospector"/>, and <see cref="IAppliedSchemaStore"/> — so a provider package
/// (SQLite, PostgreSQL, or an out-of-repo engine such as Oracle) only supplies the handful of
/// provider-specific callbacks on <see cref="RelationalProviderRegistration"/>.
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
    /// All three services are registered as idempotent (<c>TryAdd</c>) singletons: each owns one
    /// ADO.NET connection for the container's lifetime.
    /// Schema migration is an administrative operation invoked rarely — never per request — so a
    /// single long-lived connection per service is the appropriate shape, not a scoped or transient
    /// one. The connection string is resolved from <paramref name="registration"/> at provider-build
    /// time (when a service is first materialized), never eagerly at call time, so an options-bound
    /// connection string is honored.
    /// </remarks>
    public static IAlvoBuilder AddRelationalProvider(this IAlvoBuilder builder, RelationalProviderRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(registration);

        // Ensures IOptions<AlvoOptions> resolves (to the defaults, if AddAlvo() never configured it)
        // regardless of whether this provider is attached through AddAlvo() or directly onto a bare
        // IAlvoBuilder — a provider must not assume a particular caller.
        builder.Services.AddOptions<AlvoOptions>();

        builder.Services.TryAddSingleton<ISchemaMigrator>(sp => CreateMigrator(sp, registration));
        builder.Services.TryAddSingleton<ISchemaIntrospector>(sp => CreateIntrospector(sp, registration));
        builder.Services.TryAddSingleton<IAppliedSchemaStore>(sp => CreateAppliedSchemaStore(sp, registration));

        return builder;
    }

    private static EfCoreSchemaMigrator CreateMigrator(IServiceProvider services, RelationalProviderRegistration registration)
    {
        var connectionString = registration.ConnectionString(services);
        using var context = CreateThrowawayContext(registration, connectionString);
        var efServices = context.GetInfrastructure();

        return new EfCoreSchemaMigrator(
            efServices.GetRequiredService<IMigrationsModelDiffer>(),
            efServices.GetRequiredService<IMigrationsSqlGenerator>(),
            efServices.GetRequiredService<IModelRuntimeInitializer>(),
            registration.CreateModelBuilder,
            registration.CreateConnection(connectionString));
    }

    private static EfCoreSchemaIntrospector CreateIntrospector(IServiceProvider services, RelationalProviderRegistration registration)
    {
        var connectionString = registration.ConnectionString(services);
        var schemaPrefix = services.GetRequiredService<IOptions<AlvoOptions>>().Value.SchemaPrefix;
        using var context = CreateThrowawayContext(registration, connectionString);
        var databaseModelFactory = registration.CreateDatabaseModelFactory(context.GetInfrastructure());

        return new EfCoreSchemaIntrospector(
            databaseModelFactory,
            registration.CreateConnection(connectionString),
            SystemSchemaInitializer.AppliedSchemaTableName(schemaPrefix));
    }

    private static AppliedSchemaStore CreateAppliedSchemaStore(IServiceProvider services, RelationalProviderRegistration registration)
    {
        var connectionString = registration.ConnectionString(services);
        var options = services.GetRequiredService<IOptions<AlvoOptions>>().Value;

        return new AppliedSchemaStore(registration.CreateConnection(connectionString), options);
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
