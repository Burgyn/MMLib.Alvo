using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Data;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// One real PostgreSQL container for the class, with a fresh database per <see cref="StartAsync"/> call.
/// A container per fact would be prohibitively slow; a shared database would break the adversarial suite's
/// per-fact isolation requirement, since several facts assert exact row counts over entities with no
/// row-scoping predicate.
/// </summary>
public sealed class PostgreSqlAlvoDataFixture : IAsyncLifetime
{
    // Built inside InitializeAsync, never in a field initializer. Testcontainers' Build() itself talks to
    // the Docker daemon, so on a host with no reachable daemon it throws while the fixture is being
    // *constructed*, which xUnit reports as every test in the sharing class failing before any of them
    // reaches its own skip. PostgresFixture was fixed for exactly this; do not reintroduce it here.
    private PostgreSqlContainer? _container;
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<SqlCapture> _captures = [];

    /// <summary>Gets whether a real engine was started, so a caller can skip rather than fail.</summary>
    public bool Available => _container is not null;

    /// <summary>
    /// The server's <c>max_connections</c>, or <see langword="null"/> for the image's default — raised by a suite whose
    /// facts hold a hundred concurrent writers open at once.
    /// </summary>
    public int? MaxConnections { get; init; }

    /// <summary>The host's connection pool size, or <see langword="null"/> for Npgsql's default of 100.</summary>
    public int? MaxPoolSize { get; init; }

    /// <summary>
    /// The server image — <c>postgres:16-alpine</c>, which every suite runs on, unless a fact is about what an older server
    /// cannot do.
    /// </summary>
    public string Image { get; init; } = "postgres:16-alpine";

    public async ValueTask InitializeAsync()
    {
        // Windows GitHub runners run Docker in Windows-container mode when they run it at all, and that
        // mode has no linux/amd64 manifest for postgres:16-alpine. Every caller self-skips, so there is
        // nothing to start; Linux stays strict, because skipping there would silently drop the whole
        // real-PostgreSQL leg of the suite.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var builder = new PostgreSqlBuilder(Image);
        var container = (MaxConnections is { } max ? builder.WithCommand("-c", $"max_connections={max}") : builder).Build();
        await container.StartAsync();
        _container = container;
    }

    /// <summary>Creates a database, migrates <paramref name="schema"/> into it and primes the policy catalog.</summary>
    /// <param name="schema">The schema to migrate and compile the rules against.</param>
    /// <param name="descriptor">The descriptor whose rules are compiled; a permissive minimal one by default.</param>
    /// <param name="time">
    /// The clock the data path stamps an <c>audit</c> entity's timestamps from; the system clock by default.
    /// Registered before the driver's own <c>TryAdd</c>, which is the same seam a host would use — the twin of
    /// the SQLite fixture's parameter, in the same position, so a fact needing a fixed clock reads identically
    /// against either engine.
    /// </param>
    /// <param name="configure">
    /// Applied to the collection <em>after</em> <c>AddAlvo</c> and before the provider is built, for a caller
    /// that has to reach a seam only a host owns — a logging provider, or the primary handler of a named
    /// <c>HttpClient</c>. The twin of the SQLite fixture's own parameter, for the same reason.
    /// </param>
    public async Task<PostgreSqlAlvoDataHost> StartAsync(
        SchemaModel schema,
        AlvoDescriptor? descriptor = null,
        TimeProvider? time = null,
        Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Assert.SkipUnless(Available, "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");

        var (connectionString, database) = await CreateDatabaseAsync();
        var services = BuildProvider(connectionString, time, configure);
        await MigrateAsync(services, schema);

        var capture = NewCapture(database);
        var host = new PostgreSqlAlvoDataHost(services, descriptor ?? MinimalDescriptor(schema), capture);
        host.RePrime(schema);
        return host;
    }

    /// <summary>
    /// Creates one database, migrates <paramref name="schema"/> into it once, and starts
    /// <paramref name="replicas"/> hosts over it — each its own container, so each has its own data path, its own
    /// ensure-once memos and its own connection pool, exactly as separate processes of one deployment would.
    /// </summary>
    /// <param name="schema">The schema to migrate and compile the rules against.</param>
    /// <param name="descriptor">The descriptor whose rules are compiled.</param>
    /// <param name="replicas">How many hosts share the database.</param>
    /// <returns>The hosts, and the database's connection string for a fact that has to act on it directly.</returns>
    public async Task<(IReadOnlyList<PostgreSqlAlvoDataHost> Hosts, string ConnectionString)> StartReplicasAsync(
        SchemaModel schema, AlvoDescriptor descriptor, int replicas)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Assert.SkipUnless(Available, "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");

        var (connectionString, database) = await CreateDatabaseAsync();
        var hosts = new List<PostgreSqlAlvoDataHost>(replicas);
        for (var replica = 0; replica < replicas; replica++)
        {
            var services = BuildProvider(connectionString, time: null, configure: null);
            if (replica == 0)
            {
                await MigrateAsync(services, schema);
            }

            var host = new PostgreSqlAlvoDataHost(services, descriptor, NewCapture(database));
            host.RePrime(schema);
            hosts.Add(host);
        }

        return (hosts, connectionString);
    }

    /// <summary>Gets the container's superuser connection string, for a fact that provisions a database or a role of its own.</summary>
    internal string AdminConnectionString => PostgresTestContainer.ConnectionStringOf(_container!);

    /// <summary>
    /// Builds a host container over <paramref name="connectionString"/> — another role's, say — without migrating anything,
    /// disposed with the fixture.
    /// </summary>
    /// <param name="connectionString">The database and role the host connects as.</param>
    internal ServiceProvider ServicesFor(string connectionString) => BuildProvider(connectionString, time: null, configure: null);

    /// <summary>
    /// Starts recording after the migration, so a fact sees the statements its own act produced rather than the
    /// DDL the fixture ran. The generated database name is the marker, exactly as the SQLite fixture's file
    /// name is.
    /// </summary>
    private SqlCapture NewCapture(string database)
    {
        var capture = new SqlCapture(database);
        _captures.Add(capture);
        return capture;
    }

    private ServiceProvider BuildProvider(
        string connectionString, TimeProvider? time, Action<IServiceCollection>? configure)
    {
        var builder = new FixtureAlvoBuilder(new ServiceCollection());
        if (time is not null)
        {
            builder.Services.AddSingleton(time);
        }

        builder.UsePostgreSql(connectionString);
        builder.Services.AddAlvo();
        configure?.Invoke(builder.Services);

        var services = builder.Services.BuildServiceProvider();
        _providers.Add(services);
        return services;
    }

    private static async Task MigrateAsync(IServiceProvider services, SchemaModel schema)
    {
        var migrator = services.GetRequiredService<ISchemaMigrator>();
        var options = new MigrationOptions();
        var cancellationToken = TestContext.Current.CancellationToken;

        var plan = await migrator.PlanAsync(new SchemaModel([]), schema, options, cancellationToken);
        await migrator.ApplyAsync(plan, options, cancellationToken);
    }

    /// <summary>
    /// A fresh database per call, created off the container's own admin connection. The name is a
    /// <see cref="Guid"/>, so it cannot collide, and it is also the marker that identifies this database's
    /// own statements in a shared connection string.
    /// </summary>
    private async Task<(string ConnectionString, string Database)> CreateDatabaseAsync()
    {
        var adminConnectionString = PostgresTestContainer.ConnectionStringOf(_container!);
        var name = $"alvo_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var create = admin.CreateCommand();
        create.CommandText = $"CREATE DATABASE \"{name}\"";
        await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name };
        if (MaxPoolSize is { } pool)
        {
            connectionString.MaxPoolSize = pool;
        }

        return (connectionString.ToString(), name);
    }

    /// <summary>
    /// The descriptor a caller that only cares about the data path gets: every entity readable, no field
    /// descriptors, no row scoping. A test that needs a real policy passes its own.
    /// </summary>
    private static AlvoDescriptor MinimalDescriptor(SchemaModel schema) => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "data-path-fixture",
        Entities = schema.Entities.ToDictionary(
            entity => entity.Name,
            entity => new EntityDescriptor
            {
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal),
                Rules = new AccessRules { List = "true", Get = "true" },
            },
            StringComparer.Ordinal),
    };

    public async ValueTask DisposeAsync()
    {
        foreach (var capture in _captures)
        {
            capture.Dispose();
        }

        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private sealed class FixtureAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}

/// <summary>
/// One started PostgreSQL database plus the descriptor whose policy is primed against it — the twin of
/// <c>MMLib.Alvo.Data.Sqlite.Tests.AlvoDataHost</c>.
/// </summary>
/// <remarks>
/// Declared per test assembly rather than shared from <c>MMLib.Alvo.Testing</c>: that library references
/// <c>MMLib.Alvo.Abstractions</c> alone, and this type needs the core's <see cref="PolicyCatalog"/> and a
/// <see cref="ServiceProvider"/>. Two ~20-line twins in two test assemblies is the right trade against
/// making a shipped library depend on the core.
/// </remarks>
public sealed class PostgreSqlAlvoDataHost
{
    private readonly ServiceProvider _services;
    private readonly AlvoDescriptor _descriptor;
    private readonly SqlCapture _capture;

    internal PostgreSqlAlvoDataHost(ServiceProvider services, AlvoDescriptor descriptor, SqlCapture capture)
    {
        _services = services;
        _descriptor = descriptor;
        _capture = capture;
        Data = services.GetRequiredService<IAlvoData>();
    }

    /// <summary>Gets the host container the data path resolves out of.</summary>
    public ServiceProvider Services => _services;

    /// <summary>Gets the data port under test, over this database.</summary>
    internal IAlvoData Data { get; }

    /// <summary>Gets every statement EF has executed against this database since the last <see cref="ClearStatements"/>.</summary>
    internal IReadOnlyList<string> Statements => _capture.Statements;

    /// <summary>Gets every statement since the last <see cref="ClearStatements"/>, with whether it ran inside a transaction.</summary>
    internal IReadOnlyList<(string Statement, bool InTransaction)> Commands => _capture.Commands;

    /// <summary>Forgets every recorded statement, so a fact asserts on the ones its own act produced.</summary>
    internal void ClearStatements() => _capture.Clear();

    /// <summary>Re-primes the policy catalog (and therefore the applied schema) from <paramref name="schema"/>.</summary>
    /// <param name="schema">The schema the rules are re-compiled against and that the read model is rebuilt from.</param>
    public void RePrime(SchemaModel schema) => RePrime(schema, _descriptor);

    /// <summary>Re-primes the policy catalog from <paramref name="schema"/> and a descriptor an apply changed.</summary>
    /// <param name="schema">The schema the rules are re-compiled against and that the read model is rebuilt from.</param>
    /// <param name="descriptor">The descriptor <paramref name="schema"/> maps from — a rename changes its entity names.</param>
    public void RePrime(SchemaModel schema, AlvoDescriptor descriptor)
    {
        var catalog = PolicyCatalog.Build(descriptor, schema, _services.GetRequiredService<ICelCompiler>());
        _services.GetRequiredService<IPolicyCatalogProvider>().SetCurrent(descriptor.Name, catalog);
    }
}
