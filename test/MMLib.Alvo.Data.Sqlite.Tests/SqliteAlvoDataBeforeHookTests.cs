using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The before-hook pipeline at the port's three write faces, over a real SQLite database. Every fact is
/// inherited unchanged: this class supplies a store, a fixed clock and the hook counter, and nothing else.
/// </summary>
public sealed class SqliteAlvoDataBeforeHookTests : AlvoDataBeforeHookTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    protected override async Task<IAlvoDataBeforeHookWorld> WorldAsync(
        SchemaModel schema, AlvoDescriptor descriptor, TimeProvider time)
    {
        var host = await _fixture.StartAsync(schema, descriptor, time, BeforeHookRecording.Decorate);
        return new AlvoDataBeforeHookWorld(host.Data, BeforeHookRecording.RecorderOf(host.Services));
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
