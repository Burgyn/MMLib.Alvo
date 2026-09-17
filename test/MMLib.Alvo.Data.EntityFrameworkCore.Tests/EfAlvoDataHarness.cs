using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// One assembled <see cref="EfAlvoData"/>, wired exactly the way <c>AlvoEfCoreProvider.CreateData</c> wires
/// the shipped one — the real <see cref="IPolicyEngine"/>, <see cref="IPredicateEvaluator"/>,
/// <see cref="IBeforeHookRunner"/> and <see cref="IPredicateRenderer"/> out of <c>AddAlvo()</c>, over the
/// project's own <see cref="TestSqlDialect"/> and an in-memory SQLite connection.
/// </summary>
/// <remarks>
/// <para>
/// <b>The applied schema and the policy catalog are supplied separately, and that is the point.</b> The
/// catalog is always compiled against the canonical fixture entity, while the schema this data path's
/// context is built from is whatever a test hands over — so a test can reach the state the port's own
/// fail-closed arms exist for: a decision that <em>allows</em> an entity the applied schema does not
/// declare. That mismatch is unreachable through the shipped registration, which is why every guard on it
/// is otherwise untested.
/// </para>
/// <para>
/// No table is ever created. Every read this harness serves is refused before a statement reaches the
/// engine, and a connection that is open is all EF needs to get that far.
/// </para>
/// </remarks>
internal sealed class EfAlvoDataHarness : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;

    private EfAlvoDataHarness(SchemaModel applied, AlvoDescriptor descriptor)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();
        Prime(descriptor);
        Data = Build(applied);
    }

    /// <summary>The assembled port, ready to answer a read.</summary>
    internal EfAlvoData Data { get; }

    /// <summary>The decision the shipped engine reaches for <paramref name="operation"/> on the fixture entity.</summary>
    internal PolicyDecision DecisionFor(DataOperation operation) =>
        _services.GetRequiredService<IPolicyEngine>()
            .Resolve(AlvoDataFixtures.Vehicle.Name, operation, AlvoDataFixtures.Caller);

    /// <summary>A harness whose applied schema declares the fixture entity the catalog is compiled against.</summary>
    /// <param name="descriptor">The descriptor whose rules the policy engine resolves against.</param>
    internal static EfAlvoDataHarness Over(AlvoDescriptor descriptor) =>
        new(new SchemaModel([AlvoDataFixtures.Vehicle]), descriptor);

    /// <summary>
    /// A harness whose applied schema declares <b>nothing</b>, while the catalog still allows the fixture
    /// entity — the catalog/schema mismatch the read path's unknown-entity arms fail closed on.
    /// </summary>
    /// <param name="descriptor">The descriptor whose rules the policy engine resolves against.</param>
    internal static EfAlvoDataHarness OverAnEmptySchema(AlvoDescriptor descriptor) =>
        new(new SchemaModel([]), descriptor);

    /// <summary>A descriptor over the fixture entity carrying whichever rules a test needs.</summary>
    /// <param name="list">The <c>list</c> rule, or <see langword="null"/> to declare none.</param>
    /// <param name="get">The <c>get</c> rule, or <see langword="null"/> to declare none.</param>
    internal static AlvoDescriptor VehicleRuledBy(string? list = null, string? get = null) => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "ef-alvo-data-harness",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [AlvoDataFixtures.Vehicle.Name] = new EntityDescriptor
            {
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal),
                Rules = new AccessRules { List = list, Get = get },
            },
        },
    };

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private void Prime(AlvoDescriptor descriptor)
    {
        var catalog = PolicyCatalog.Build(
            descriptor, new SchemaModel([AlvoDataFixtures.Vehicle]), _services.GetRequiredService<ICelCompiler>());
        _services.GetRequiredService<IPolicyCatalogProvider>().SetCurrent(descriptor.Name, catalog);
    }

    private EfAlvoData Build(SchemaModel applied) => new(
        _services.GetRequiredService<IPolicyEngine>(),
        _services.GetRequiredService<IPredicateEvaluator>(),
        _services.GetRequiredService<IBeforeHookRunner>(),
        _services.GetRequiredService<IPredicateRenderer>(),
        new TestFieldSqlRenderer(),
        new TestSqlDialect(),
        new AlvoDataContextFactory(new FixedSchemaRegistry(applied), ConfigureProvider),
        TimeProvider.System,
        new AlvoOptions());

    private void ConfigureProvider(DbContextOptionsBuilder options) =>
        options.UseSqlite(_connection, static sqlite => sqlite.UseRelationalNulls());

    private sealed class FixedSchemaRegistry(SchemaModel schema) : ISchemaRegistry
    {
        public SchemaModel GetSchema() => schema;
    }
}
