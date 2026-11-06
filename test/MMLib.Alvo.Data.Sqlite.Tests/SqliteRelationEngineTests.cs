using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Ef;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>SQLite's leg of the relation-rule engine facts.</summary>
public sealed class SqliteRelationEngineTests : RelationEngineFacts, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    private AlvoDataHost? _host;

    protected override IReadOnlyList<(string Statement, bool InTransaction)> Commands =>
        _host?.Commands ?? throw new InvalidOperationException("No host was started.");

    protected override async Task<IServiceProvider> StartAsync(SchemaModel schema, AlvoDescriptor descriptor)
    {
        _host = await _fixture.StartAsync(schema, descriptor);
        return _host.Services;
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
