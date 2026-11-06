using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>Runs <see cref="AlvoDataRelationRuleTests"/> against SQLite.</summary>
public sealed class SqliteAlvoDataRelationRuleTests : AlvoDataRelationRuleTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    protected override async Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor) =>
        (await _fixture.StartAsync(schema, descriptor)).Data;

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
