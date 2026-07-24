using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MMLib.Alvo;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Data.PostgreSql;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.Conventions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Scaffolding.Internal;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers PostgreSQL as Alvo's database provider on an <see cref="IAlvoBuilder"/>.</summary>
public static class AlvoPostgreSqlBuilderExtensions
{
    /// <summary>The default <c>ConnectionStrings</c> entry name the configuration overloads resolve.</summary>
    public const string DefaultConnectionName = "Alvo";

    private const string MissingConnectionStringMessage =
        "No PostgreSQL connection string was configured. Pass one to UsePostgreSql(connectionString), " +
        "set PostgreSqlProviderOptions.ConnectionString inside UsePostgreSql(configure), or add a " +
        "\"ConnectionStrings:Alvo\" entry to configuration for the parameterless UsePostgreSql().";

    /// <summary>
    /// Registers PostgreSQL as Alvo's database provider using the given connection string. Today this
    /// wires the schema-registry and migration services (<see cref="MMLib.Alvo.Migrations.ISchemaMigrator"/>,
    /// <see cref="MMLib.Alvo.Schema.ISchemaIntrospector"/>, <see cref="MMLib.Alvo.Migrations.IAppliedSchemaStore"/>)
    /// to PostgreSQL; further Alvo data services attach here as the framework grows.
    /// </summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="connectionString">The PostgreSQL ADO.NET connection string (e.g. from <c>config.GetConnectionString("Alvo")</c>).</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IAlvoBuilder UsePostgreSql(this IAlvoBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        builder.Services.Configure<PostgreSqlProviderOptions>(options => options.ConnectionString = connectionString);

        return AddPostgreSqlProvider(builder);
    }

    /// <summary>
    /// Registers PostgreSQL as Alvo's database provider, configured entirely through
    /// <see cref="PostgreSqlProviderOptions"/> (set the connection string inside <paramref name="configure"/>,
    /// e.g. by binding it from configuration). Today this wires the schema-registry and migration
    /// services to PostgreSQL; further Alvo data services attach here as the framework grows.
    /// </summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="configure">Configures the PostgreSQL provider options.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IAlvoBuilder UsePostgreSql(this IAlvoBuilder builder, Action<PostgreSqlProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.Configure(configure);

        return AddPostgreSqlProvider(builder);
    }

    /// <summary>
    /// Registers PostgreSQL as Alvo's database provider using the given connection string, then
    /// applies <paramref name="configure"/> for additional tuning. Today this wires the
    /// schema-registry and migration services to PostgreSQL; further Alvo data services attach here
    /// as the framework grows.
    /// </summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="connectionString">The PostgreSQL ADO.NET connection string.</param>
    /// <param name="configure">Configures additional PostgreSQL provider options; runs after the connection string is set.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IAlvoBuilder UsePostgreSql(this IAlvoBuilder builder, string connectionString, Action<PostgreSqlProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.Configure<PostgreSqlProviderOptions>(options => options.ConnectionString = connectionString);
        builder.Services.Configure(configure);

        return AddPostgreSqlProvider(builder);
    }

    /// <summary>
    /// Registers PostgreSQL as Alvo's database provider, resolving the connection string from the
    /// application's <see cref="IConfiguration"/> when the provider is built — the standard
    /// <c>ConnectionStrings:Alvo</c> entry (see <see cref="ConfigurationExtensions.GetConnectionString"/>).
    /// Use this parameterless overload when the connection string lives in configuration
    /// (appsettings.json, environment variables, ...) under the default
    /// <see cref="DefaultConnectionName"/> name; the resolution is deferred, so the host's
    /// <see cref="IConfiguration"/> only has to be registered by the time the provider is built.
    /// </summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IAlvoBuilder UsePostgreSql(this IAlvoBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<PostgreSqlProviderOptions>()
            .Configure<IConfiguration>(static (options, configuration) =>
                options.ConnectionString ??= configuration.GetConnectionString(DefaultConnectionName));

        return AddPostgreSqlProvider(builder);
    }

    /// <summary>
    /// Registers PostgreSQL as Alvo's database provider, resolving the connection string by name from
    /// the given <see cref="IConfiguration"/> — the standard <c>ConnectionStrings:{connectionName}</c>
    /// entry. Use this when the connection string lives under a non-default name.
    /// </summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="configuration">The configuration to read the connection string from.</param>
    /// <param name="connectionName">The <c>ConnectionStrings</c> entry name (default <see cref="DefaultConnectionName"/>).</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IAlvoBuilder UsePostgreSql(this IAlvoBuilder builder, IConfiguration configuration, string connectionName = DefaultConnectionName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

        builder.Services.Configure<PostgreSqlProviderOptions>(
            options => options.ConnectionString = configuration.GetConnectionString(connectionName));

        return AddPostgreSqlProvider(builder);
    }

    private static IAlvoBuilder AddPostgreSqlProvider(IAlvoBuilder builder) =>
        builder.AddRelationalProvider(new RelationalProviderRegistration
        {
            ConnectionString = ResolveConnectionString,
            ConfigureProvider = static (options, connectionString) => options.UseNpgsql(connectionString),
            CreateModelBuilder = static () => new ModelBuilder(NpgsqlConventionSetBuilder.Build()),
            CreateDatabaseModelFactory = CreateDatabaseModelFactory,
            CreateConnection = static connectionString => new NpgsqlConnection(connectionString),
        });

    private static string ResolveConnectionString(IServiceProvider services)
    {
        var connectionString = services.GetRequiredService<IOptions<PostgreSqlProviderOptions>>().Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(MissingConnectionStringMessage);
        }

        return connectionString;
    }

    // NpgsqlDatabaseModelFactory is [EntityFrameworkInternal]: it is the concrete runtime scaffolding
    // factory EF Core itself constructs internally, but the ordinary Npgsql.EntityFrameworkCore.PostgreSQL
    // package never registers it as a service (that only happens via the design-time host in
    // Microsoft.EntityFrameworkCore.Design, which this package deliberately does not reference).
    // Constructing it directly from its single runtime-registered dependency avoids that dependency.
    // Unlike SQLite's factory, Npgsql's takes only the scaffolding logger — no type mapping source.
#pragma warning disable EF1001 // NpgsqlDatabaseModelFactory is EF-internal by design; see remarks above.
    private static NpgsqlDatabaseModelFactory CreateDatabaseModelFactory(IServiceProvider efServices) =>
        new(efServices.GetRequiredService<IDiagnosticsLogger<DbLoggerCategory.Scaffolding>>());
#pragma warning restore EF1001
}
