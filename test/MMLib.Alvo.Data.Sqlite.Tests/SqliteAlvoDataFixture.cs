using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
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
    private readonly List<SqlCapture> _captures = [];

    /// <summary>Creates a database, migrates <paramref name="schema"/> into it and primes the policy catalog.</summary>
    /// <param name="schema">The schema to migrate and compile the rules against.</param>
    /// <param name="descriptor">The descriptor whose rules are compiled; a permissive minimal one by default.</param>
    public async Task<AlvoDataHost> StartAsync(SchemaModel schema, AlvoDescriptor? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var path = NewDatabaseFile();
        var dialect = new LockRecordingSqlDialect();
        var services = BuildProvider(path, dialect);
        await MigrateAsync(services, schema);

        var capture = NewCapture(path);
        var host = new AlvoDataHost(services, descriptor ?? MinimalDescriptor(schema), capture, dialect);
        host.RePrime(schema);
        return host;
    }

    private string NewDatabaseFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alvo-data-{Guid.NewGuid():N}.db");
        _files.Add(path);
        return path;
    }

    private SqlCapture NewCapture(string path)
    {
        var capture = new SqlCapture(path);
        _captures.Add(capture);
        return capture;
    }

    /// <summary>
    /// Builds the host container through the public entry point, with one substitution: the lock-recording
    /// dialect is registered <em>before</em> <c>UseSqlite</c>, so the driver's own <c>TryAdd</c> leaves it in
    /// place. That is the same seam a host would use to swap a dialect — the data port is still the one the
    /// container composed, not one a test built by hand.
    /// </summary>
    private ServiceProvider BuildProvider(string path, IAlvoSqlDialect dialect)
    {
        var builder = new FixtureAlvoBuilder(new ServiceCollection());
        builder.Services.AddSingleton(dialect);
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
        foreach (var capture in _captures)
        {
            capture.Dispose();
        }

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
public sealed class AlvoDataHost
{
    private readonly ServiceProvider _services;
    private readonly AlvoDescriptor _descriptor;
    private readonly SqlCapture _capture;
    private readonly LockRecordingSqlDialect _dialect;

    internal AlvoDataHost(
        ServiceProvider services, AlvoDescriptor descriptor, SqlCapture capture, LockRecordingSqlDialect dialect)
    {
        _services = services;
        _descriptor = descriptor;
        _capture = capture;
        _dialect = dialect;
        Data = services.GetRequiredService<IAlvoData>();
    }

    /// <summary>Gets the host container the data path resolves out of.</summary>
    public ServiceProvider Services => _services;

    /// <summary>Gets the data port under test, over this database.</summary>
    internal IAlvoData Data { get; }

    /// <summary>Gets every statement EF has executed against this database since the last <see cref="ClearStatements"/>.</summary>
    internal IReadOnlyList<string> Statements => _capture.Statements;

    /// <summary>Gets the most recent statement executed against this database.</summary>
    internal string LastStatement => _capture.LastStatement;

    /// <summary>Forgets every recorded statement, so a test asserts on the ones its own act produced.</summary>
    internal void ClearStatements() => _capture.Clear();

    /// <summary>Gets the row-lock modes the data path has asked this dialect for.</summary>
    internal IReadOnlyList<PreImageMutation> RequestedLocks => _dialect.RequestedLocks;

    /// <summary>Re-primes the policy catalog (and therefore the applied schema) from <paramref name="schema"/>.</summary>
    /// <remarks>Synchronous, because compiling and publishing a catalog is: nothing here awaits.</remarks>
    /// <param name="schema">The schema the rules are re-compiled against and that the read model is rebuilt from.</param>
    public void RePrime(SchemaModel schema)
    {
        var catalog = PolicyCatalog.Build(_descriptor, schema, _services.GetRequiredService<ICelCompiler>());
        _services.GetRequiredService<IPolicyCatalogProvider>().SetCurrent(_descriptor.Name, catalog);
    }
}
