using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The outbox guarantee at the four write sites, over a real SQLite database. Every fact is inherited
/// unchanged: this class supplies a store and its outbox reader and nothing else.
/// </summary>
public sealed class SqliteAlvoDataOutboxTests : AlvoDataOutboxTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    protected override async Task<IAlvoDataOutboxWorld> WorldAsync(
        SchemaModel schema, AlvoDescriptor descriptor, TimeProvider? time = null)
    {
        var host = await _fixture.StartAsync(schema, descriptor, time);
        return new AlvoDataOutboxWorld(host.Data, host.Services);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
