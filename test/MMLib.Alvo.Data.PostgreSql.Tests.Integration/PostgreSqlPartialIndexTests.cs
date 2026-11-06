using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Tests.Data;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL's leg of the partial index suite — the engine that demands an <c>IMMUTABLE</c> predicate and answers
/// <c>42P17</c> otherwise.
/// </summary>
public sealed class PostgreSqlPartialIndexTests : AlvoDataPartialIndexTests, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();
    private PostgreSqlAlvoDataHost? _host;

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    protected override bool RefusesANonImmutablePredicate => true;

    protected override async Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor)
    {
        _host = await _fixture.StartAsync(schema, descriptor);
        return _host.Data;
    }

    protected override Task<MigrationPlan> PlanAsync(SchemaModel current, SchemaModel desired) =>
        Migrator.PlanAsync(current, desired, new MigrationOptions(), TestContext.Current.CancellationToken);

    protected override async Task ApplyAsync(SchemaModel current, SchemaModel desired, AlvoDescriptor descriptor)
    {
        var ct = TestContext.Current.CancellationToken;
        var plan = await PlanAsync(current, desired);
        (await Migrator.ApplyAsync(plan, new MigrationOptions(), ct)).Applied.ShouldBeTrue();
        _host!.RePrime(desired, descriptor);
    }

    protected override Task<IReadOnlyList<string>> IndexNamesAsync(string table) =>
        CatalogQuery.NamesAsync(_host!.Services, "SELECT indexname FROM pg_indexes WHERE tablename = @table", table);

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private ISchemaMigrator Migrator => _host!.Services.GetRequiredService<ISchemaMigrator>();
}
