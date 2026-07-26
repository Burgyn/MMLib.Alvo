using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Sqlite.Scaffolding.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MMLib.Alvo;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Data.Sqlite;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers SQLite as Alvo's database provider on an <see cref="IAlvoBuilder"/>.</summary>
public static class AlvoSqliteBuilderExtensions
{
    /// <summary>The default <c>ConnectionStrings</c> entry name the configuration overloads resolve.</summary>
    public const string DefaultConnectionName = "Alvo";

    private const string MissingConnectionStringMessage =
        "No SQLite connection string was configured. Pass one to UseSqlite(connectionString), set " +
        "SqliteProviderOptions.ConnectionString inside UseSqlite(configure), or add a " +
        "\"ConnectionStrings:Alvo\" entry to configuration for the parameterless UseSqlite().";

    /// <summary>
    /// Registers SQLite as Alvo's database provider using the given connection string. Today this
    /// wires the schema-registry and migration services (<see cref="MMLib.Alvo.Migrations.ISchemaMigrator"/>,
    /// <see cref="MMLib.Alvo.Schema.ISchemaIntrospector"/>, <see cref="MMLib.Alvo.Migrations.IAppliedSchemaStore"/>)
    /// to SQLite; further Alvo data services attach here as the framework grows.
    /// </summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="connectionString">The SQLite ADO.NET connection string (e.g. from <c>config.GetConnectionString("Alvo")</c>).</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IAlvoBuilder UseSqlite(this IAlvoBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        builder.Services.Configure<SqliteProviderOptions>(options => options.ConnectionString = connectionString);

        return AddSqliteProvider(builder);
    }

    /// <summary>
    /// Registers SQLite as Alvo's database provider, configured entirely through
    /// <see cref="SqliteProviderOptions"/> (set the connection string inside <paramref name="configure"/>,
    /// e.g. by binding it from configuration). Today this wires the schema-registry and migration
    /// services to SQLite; further Alvo data services attach here as the framework grows.
    /// </summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="configure">Configures the SQLite provider options.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IAlvoBuilder UseSqlite(this IAlvoBuilder builder, Action<SqliteProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.Configure(configure);

        return AddSqliteProvider(builder);
    }

    /// <summary>
    /// Registers SQLite as Alvo's database provider using the given connection string, then applies
    /// <paramref name="configure"/> for additional tuning. Today this wires the schema-registry and
    /// migration services to SQLite; further Alvo data services attach here as the framework grows.
    /// </summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="connectionString">The SQLite ADO.NET connection string.</param>
    /// <param name="configure">Configures additional SQLite provider options; runs after the connection string is set.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IAlvoBuilder UseSqlite(this IAlvoBuilder builder, string connectionString, Action<SqliteProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.Configure<SqliteProviderOptions>(options => options.ConnectionString = connectionString);
        builder.Services.Configure(configure);

        return AddSqliteProvider(builder);
    }

    /// <summary>
    /// Registers SQLite as Alvo's database provider, resolving the connection string from the
    /// application's <see cref="IConfiguration"/> when the provider is built — the standard
    /// <c>ConnectionStrings:Alvo</c> entry (see <see cref="ConfigurationExtensions.GetConnectionString"/>).
    /// Use this parameterless overload when the connection string lives in configuration
    /// (appsettings.json, environment variables, ...) under the default
    /// <see cref="DefaultConnectionName"/> name; the resolution is deferred, so the host's
    /// <see cref="IConfiguration"/> only has to be registered by the time the provider is built.
    /// </summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IAlvoBuilder UseSqlite(this IAlvoBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Resolve IConfiguration optionally (GetService, not GetRequiredService): if the host never
        // registered it, the connection string stays null and ResolveConnectionString fails fast
        // with the crafted MissingConnectionStringMessage — the same helpful error as every other
        // overload — instead of a raw "no service for IConfiguration" DI exception.
        builder.Services.AddOptions<SqliteProviderOptions>();
        builder.Services.AddSingleton<IConfigureOptions<SqliteProviderOptions>>(services =>
            new ConfigureNamedOptions<SqliteProviderOptions>(
                string.Empty,
                options => options.ConnectionString ??= services.GetService<IConfiguration>()?.GetConnectionString(DefaultConnectionName)));

        return AddSqliteProvider(builder);
    }

    /// <summary>
    /// Registers SQLite as Alvo's database provider, resolving the connection string by name from the
    /// given <see cref="IConfiguration"/> — the standard <c>ConnectionStrings:{connectionName}</c>
    /// entry. Use this when the connection string lives under a non-default name.
    /// </summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="configuration">The configuration to read the connection string from.</param>
    /// <param name="connectionName">The <c>ConnectionStrings</c> entry name (default <see cref="DefaultConnectionName"/>).</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IAlvoBuilder UseSqlite(this IAlvoBuilder builder, IConfiguration configuration, string connectionName = DefaultConnectionName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

        builder.Services.Configure<SqliteProviderOptions>(
            options => options.ConnectionString = configuration.GetConnectionString(connectionName));

        return AddSqliteProvider(builder);
    }

    private static IAlvoBuilder AddSqliteProvider(IAlvoBuilder builder) =>
        builder.AddRelationalProvider(new RelationalProviderRegistration
        {
            ConnectionString = ResolveConnectionString,
            ConfigureProvider = static (options, connectionString) =>
                options.UseSqlite(connectionString, static sqlite => sqlite.UseRelationalNulls()),
            CreateModelBuilder = static () => new ModelBuilder(SqliteConventionSetBuilder.Build()),
            CreateDatabaseModelFactory = CreateDatabaseModelFactory,
            CreateConnection = static connectionString => new SqliteConnection(WithoutPooling(connectionString)),
            Fields = new SqliteFieldSqlRenderer(),
            Dialect = new SqliteSqlDialect(),
        });

    private static string ResolveConnectionString(IServiceProvider services)
    {
        var connectionString = services.GetRequiredService<IOptions<SqliteProviderOptions>>().Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(MissingConnectionStringMessage);
        }

        return connectionString;
    }

    // SqliteDatabaseModelFactory is [EntityFrameworkInternal]: it is the concrete runtime scaffolding
    // factory EF Core itself constructs internally, but the ordinary Microsoft.EntityFrameworkCore.Sqlite
    // package never registers it as a service (that only happens via the design-time host in
    // Microsoft.EntityFrameworkCore.Design, which this package deliberately does not reference).
    // Constructing it directly from its two runtime-registered dependencies avoids that dependency.
#pragma warning disable EF1001 // SqliteDatabaseModelFactory is EF-internal by design; see remarks above.
    private static SqliteDatabaseModelFactory CreateDatabaseModelFactory(IServiceProvider efServices) =>
        new(
            efServices.GetRequiredService<IDiagnosticsLogger<DbLoggerCategory.Scaffolding>>(),
            efServices.GetRequiredService<IRelationalTypeMappingSource>());
#pragma warning restore EF1001

    // The migrator and introspector each own one long-lived connection for the container's lifetime
    // and dispose it deterministically. Microsoft.Data.Sqlite pools connections by default, which
    // keeps the underlying file handle alive even after Dispose() — on Windows that leaves the .db
    // file locked until the pool is cleared. Disabling pooling for these administrative connections
    // releases the file handle on Dispose(), which is what a caller (e.g. a test deleting the file
    // right after) needs. This is not a hot path, so the setup cost pooling would amortize is
    // irrelevant here.
    private static string WithoutPooling(string connectionString) =>
        new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ToString();
}
