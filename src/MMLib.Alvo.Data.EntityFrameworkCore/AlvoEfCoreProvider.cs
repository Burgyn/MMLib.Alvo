using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
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
    /// <remarks>
    /// The data path attaches here too: the driver's own <see cref="RelationalProviderRegistration.Fields"/>
    /// and <see cref="RelationalProviderRegistration.Dialect"/> become resolvable services, and
    /// <see cref="Data.IAlvoData"/> is composed from them plus the engine-agnostic core's policy engine,
    /// evaluator and predicate renderer. It is a <b>singleton</b> deliberately: it holds no per-request
    /// state, it creates one <see cref="DbContext"/> per operation and disposes it, and every member takes
    /// the caller's <c>AlvoContext</c> as a parameter precisely so no ambient scope decides who is asking.
    /// A scoped registration would imply the opposite and invite an accessor to be read instead.
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
        builder.Services.TryAddSingleton<IRuntimeSchemaWriter>(CreateRuntimeSchemaWriter);
        builder.Services.TryAddSingleton(services => new AlvoDataContextFactory(
            services.GetRequiredService<ISchemaRegistry>(),
            options => registration.ConfigureProvider(options, registration.ConnectionString(services))));
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton(registration.Fields);
        builder.Services.TryAddSingleton(registration.Dialect);
        builder.Services.TryAddSingleton<IAlvoData>(CreateData);
        builder.Services.TryAddSingleton<IAlvoDataReachability>(CreateReachability);
        builder.Services.TryAddSingleton<IOutboxStore>(CreateOutboxStore);

        return builder;
    }

    private static EfAlvoData CreateData(IServiceProvider services) => new(
        services.GetRequiredService<IPolicyEngine>(),
        services.GetRequiredService<IPredicateEvaluator>(),
        services.GetRequiredService<IBeforeHookRunner>(),
        services.GetRequiredService<IPredicateRenderer>(),
        services.GetRequiredService<IFieldSqlRenderer>(),
        services.GetRequiredService<IAlvoSqlDialect>(),
        services.GetRequiredService<AlvoDataContextFactory>(),
        services.GetRequiredService<TimeProvider>(),
        services.GetRequiredService<IOptions<AlvoOptions>>().Value);

    /// <summary>Creates the readiness probe every EF-backed driver shares (#133).</summary>
    /// <remarks>
    /// A singleton beside the other stores, holding no connection of its own: it opens one per probe through
    /// <see cref="Internal.RelationalConnectionFactory"/>, for the reason
    /// <see cref="Internal.RelationalReachability"/>'s own remarks give.
    /// </remarks>
    /// <param name="services">The application's services.</param>
    private static RelationalReachability CreateReachability(IServiceProvider services) =>
        new(services.GetRequiredService<RelationalConnectionFactory>());

    /// <summary>Creates the relational outbox store the dispatcher claims through.</summary>
    /// <remarks>
    /// A singleton beside the other stores, and the seam the core's dispatcher reaches the outbox through: the
    /// statements themselves are <see langword="internal"/> to this package, and the dispatcher depends on
    /// <c>MMLib.Alvo.Abstractions</c> alone. This is the port <c>docs/architecture/package-boundary.md</c>
    /// predicted would be earned by the first framework table no store call touches.
    /// </remarks>
    private static EfCoreOutboxStore CreateOutboxStore(IServiceProvider services) => new(
        services.GetRequiredService<RelationalConnectionFactory>(),
        services.GetRequiredService<IOptions<AlvoOptions>>().Value,
        services.GetRequiredService<TimeProvider>());

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
            connections,
            registration.Dialect,
            ComputedColumns(services, registration));
    }

    /// <summary>
    /// The CEL-to-generated-column renderer, or <see langword="null"/> when this container has no expression
    /// services.
    /// </summary>
    /// <remarks>
    /// <b><c>GetService</c>, not <c>GetRequiredService</c>, and the difference is the error a host sees.</b> A
    /// driver's <c>UseSqlite</c>/<c>UsePostgreSql</c> is attachable to a bare <see cref="IAlvoBuilder"/> that
    /// never called <c>AddAlvo()</c> — the in-repo generated-SQL snapshot suites did exactly that for six
    /// releases — so demanding the services here would turn every migration in such a container into an
    /// <see cref="InvalidOperationException"/> about <c>ICelCompiler</c>, whether or not any field is computed.
    /// Answering <see langword="null"/> instead keeps that container working for every schema that declares no
    /// <c>computed</c>, and lets <c>DescriptorModelBuilder</c> name the missing <c>AddAlvo()</c> for the one that
    /// does.
    /// </remarks>
    private static ComputedColumnSql? ComputedColumns(IServiceProvider services, RelationalProviderRegistration registration) =>
        services.GetService<ICelCompiler>() is { } compiler && services.GetService<IPredicateRenderer>() is { } renderer
            ? new ComputedColumnSql(compiler, renderer, registration.Fields)
            : null;

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
            SystemSchemaInitializer.FrameworkTableNames(schemaPrefix));
    }

    private static EfCoreDescriptorVersionStore CreateDescriptorVersionStore(IServiceProvider services)
    {
        var connections = services.GetRequiredService<RelationalConnectionFactory>();
        var options = services.GetRequiredService<IOptions<AlvoOptions>>().Value;

        return new EfCoreDescriptorVersionStore(connections, options);
    }

    private static EfCoreRuntimeSchemaWriter CreateRuntimeSchemaWriter(IServiceProvider services)
    {
        var connections = services.GetRequiredService<RelationalConnectionFactory>();
        var options = services.GetRequiredService<IOptions<AlvoOptions>>().Value;

        return new EfCoreRuntimeSchemaWriter(connections, options);
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
