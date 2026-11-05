using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// SQLite's leg of the CHECK constraint suite — the engine that cannot add a constraint in place and rebuilds the table.
/// </summary>
/// <remarks>
/// It supplies a store, an out-of-band connection and the migrator, and nothing else, so no fact can be weakened to make
/// this driver pass.
/// </remarks>
public sealed class SqliteCheckConstraintTests : AlvoDataCheckConstraintTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();
    private AlvoDataHost? _host;

    protected override async Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor)
    {
        _host = await _fixture.StartAsync(schema, descriptor);
        return _host.Data;
    }

    protected override Task<Exception?> ExecuteOutsideThePortAsync(string sql) =>
        OutOfBandStatement.ExecuteAsync(
            _host!.Services.GetRequiredService<AlvoDataContextFactory>(), sql, TestContext.Current.CancellationToken);

    protected override Task<MigrationPlan> PlanAsync(SchemaModel current, SchemaModel desired) =>
        Migrator.PlanAsync(current, desired, new MigrationOptions(), TestContext.Current.CancellationToken);

    protected override async Task ApplyAsync(SchemaModel current, SchemaModel desired)
    {
        await ExecuteAsync(await PlanAsync(current, desired));
        _host!.RePrime(desired);
    }

    protected override Task ExecuteAsync(MigrationPlan plan) =>
        Migrator.ApplyAsync(plan, new MigrationOptions(), TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private ISchemaMigrator Migrator => _host!.Services.GetRequiredService<ISchemaMigrator>();
}
