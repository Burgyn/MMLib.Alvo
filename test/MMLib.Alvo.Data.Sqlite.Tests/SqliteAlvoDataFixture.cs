using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// Stands up one isolated, real SQLite database per <see cref="StartAsync"/> call: a fresh temp file, a
/// fresh service provider wired through the public <c>UseSqlite</c> entry point, the physical tables
/// created by the production <see cref="ISchemaMigrator"/>, and the policy catalog primed from the same
/// descriptor. Per-call isolation is not optional — several adversarial facts assert exact row counts
/// over entities with no row-scoping predicate at all.
/// </summary>
public sealed class SqliteAlvoDataFixture : IAsyncDisposable
{
    private readonly List<string> _files = [];
    private readonly List<ServiceProvider> _providers = [];

    /// <summary>Creates a database, migrates <paramref name="schema"/> into it and primes the policy catalog.</summary>
    /// <param name="schema">The schema to migrate and compile the rules against.</param>
    /// <param name="descriptor">The descriptor whose rules are compiled; a permissive minimal one by default.</param>
    public async Task<AlvoDataHost> StartAsync(SchemaModel schema, AlvoDescriptor? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var services = BuildProvider(NewDatabaseFile());
        await MigrateAsync(services, schema);

        var host = new AlvoDataHost(services, descriptor ?? MinimalDescriptor(schema));
        await host.RePrimeAsync(schema);
        return host;
    }

    private string NewDatabaseFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alvo-data-{Guid.NewGuid():N}.db");
        _files.Add(path);
        return path;
    }

    private ServiceProvider BuildProvider(string path)
    {
        var builder = new FixtureAlvoBuilder(new ServiceCollection());
        builder.UseSqlite($"Data Source={path}");
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

        foreach (var file in _files.Where(File.Exists))
        {
            TryDelete(file);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FixtureAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}

/// <summary>One started database plus the descriptor whose policy is primed against it.</summary>
public sealed class AlvoDataHost(ServiceProvider services, AlvoDescriptor descriptor)
{
    /// <summary>Gets the host container the data path resolves out of.</summary>
    public ServiceProvider Services => services;

    /// <summary>Re-primes the policy catalog (and therefore the applied schema) from <paramref name="schema"/>.</summary>
    /// <param name="schema">The schema the rules are re-compiled against and that the read model is rebuilt from.</param>
    public Task RePrimeAsync(SchemaModel schema)
    {
        var catalog = PolicyCatalog.Build(descriptor, schema, services.GetRequiredService<ICelCompiler>());
        services.GetRequiredService<IPolicyCatalogProvider>().SetCurrent(descriptor.Name, catalog);
        return Task.CompletedTask;
    }
}
