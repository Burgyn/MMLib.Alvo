using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
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

    /// <summary>Gets whether a real engine was started, so a caller can skip rather than fail.</summary>
    public bool Available => _container is not null;

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

        var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync();
        _container = container;
    }

    /// <summary>Creates a database, migrates <paramref name="schema"/> into it and primes the policy catalog.</summary>
    /// <param name="schema">The schema to migrate and compile the rules against.</param>
    /// <param name="descriptor">The descriptor whose rules are compiled; a permissive minimal one by default.</param>
    public async Task<PostgreSqlAlvoDataHost> StartAsync(SchemaModel schema, AlvoDescriptor? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Assert.SkipUnless(Available, "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");

        var services = BuildProvider(await CreateDatabaseAsync());
        await MigrateAsync(services, schema);

        var host = new PostgreSqlAlvoDataHost(services, descriptor ?? MinimalDescriptor(schema));
        host.RePrime(schema);
        return host;
    }

    private ServiceProvider BuildProvider(string connectionString)
    {
        var builder = new FixtureAlvoBuilder(new ServiceCollection());
        builder.UsePostgreSql(connectionString);
        builder.Services.AddAlvo();

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
    private async Task<string> CreateDatabaseAsync()
    {
        var adminConnectionString = _container!.GetConnectionString();
        var name = $"alvo_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var create = admin.CreateCommand();
        create.CommandText = $"CREATE DATABASE \"{name}\"";
        await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        return new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name }.ToString();
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

    internal PostgreSqlAlvoDataHost(ServiceProvider services, AlvoDescriptor descriptor)
    {
        _services = services;
        _descriptor = descriptor;
        Data = services.GetRequiredService<IAlvoData>();
    }

    /// <summary>Gets the host container the data path resolves out of.</summary>
    public ServiceProvider Services => _services;

    /// <summary>Gets the data port under test, over this database.</summary>
    internal IAlvoData Data { get; }

    /// <summary>Re-primes the policy catalog (and therefore the applied schema) from <paramref name="schema"/>.</summary>
    /// <param name="schema">The schema the rules are re-compiled against and that the read model is rebuilt from.</param>
    public void RePrime(SchemaModel schema)
    {
        var catalog = PolicyCatalog.Build(_descriptor, schema, _services.GetRequiredService<ICelCompiler>());
        _services.GetRequiredService<IPolicyCatalogProvider>().SetCurrent(_descriptor.Name, catalog);
    }
}
