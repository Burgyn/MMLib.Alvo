using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>SQLite's leg of the partial index suite: <c>CREATE INDEX … WHERE</c> over its deterministic date functions.</summary>
public sealed class SqlitePartialIndexTests : AlvoDataPartialIndexTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();
    private AlvoDataHost? _host;

    protected override bool RefusesANonImmutablePredicate => false;

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
        CatalogQuery.NamesAsync(_host!.Services, "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = @table", table);

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private ISchemaMigrator Migrator => _host!.Services.GetRequiredService<ISchemaMigrator>();
}
