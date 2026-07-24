using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    private const string MissingConnectionStringMessage =
        "No PostgreSQL connection string was configured. Pass one to UsePostgreSql(connectionString), " +
        "or set PostgreSqlProviderOptions.ConnectionString inside UsePostgreSql(configure).";

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
