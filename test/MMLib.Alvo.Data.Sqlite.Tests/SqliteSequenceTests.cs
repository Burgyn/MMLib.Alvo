using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>SQLite's leg of the sequence suite: the write gate admits one writer, so the counter row is never contended.</summary>
public sealed class SqliteSequenceTests : AlvoDataSequenceTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();
    private AlvoDataHost? _host;

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
