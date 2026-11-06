using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL's leg of the sequence suite — the engine whose writers really run concurrently, so acceptance (1) contends
/// on the counter row.
/// </summary>
/// <remarks>
/// Its own container with <c>max_connections=200</c> and a pool of 150, so a hundred concurrent writers neither queue on
/// nor exhaust the pool and the fact measures the counter row's lock, not the pool's.
/// </remarks>
public sealed class PostgreSqlSequenceTests : AlvoDataSequenceTests, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new() { MaxConnections = 200, MaxPoolSize = 150 };
    private PostgreSqlAlvoDataHost? _host;

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    protected override IAlvoTransactions Transactions => _host!.Services.GetRequiredService<IAlvoTransactions>();

    protected override async Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor, TimeProvider time)
    {
        _host = await _fixture.StartAsync(schema, descriptor, time);
        return _host.Data;
    }

    protected override async Task ApplyAsync(SchemaModel current, SchemaModel desired, AlvoDescriptor descriptor)
    {
        var migrator = _host!.Services.GetRequiredService<ISchemaMigrator>();
        var ct = TestContext.Current.CancellationToken;
        var plan = await migrator.PlanAsync(current, desired, new MigrationOptions(), ct);
        (await migrator.ApplyAsync(plan, new MigrationOptions(), ct)).Applied.ShouldBeTrue();
        _host.RePrime(desired, descriptor);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
