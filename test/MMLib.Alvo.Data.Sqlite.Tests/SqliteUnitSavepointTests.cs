using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Ef;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>SQLite's leg of the savepoint facts.</summary>
public sealed class SqliteUnitSavepointTests : UnitSavepointFacts, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    protected override async Task<IServiceProvider> StartAsync(SchemaModel schema, AlvoDescriptor descriptor) =>
        (await _fixture.StartAsync(schema, descriptor)).Services;

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
