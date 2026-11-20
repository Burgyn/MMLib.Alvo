using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Ef;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>SQLite's leg of the refused-reference wording facts (#161).</summary>
public sealed class SqliteReferenceRefusalWordingTests : ReferenceRefusalWordingFacts, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    protected override async Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor) =>
        (await _fixture.StartAsync(schema, descriptor)).Data;

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
