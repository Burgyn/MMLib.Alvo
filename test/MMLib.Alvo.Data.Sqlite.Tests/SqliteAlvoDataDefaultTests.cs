using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// SQLite's leg of the literal-default suite (#113). It supplies a store and nothing else, so a fact cannot be
/// weakened to make the driver pass.
/// </summary>
/// <remarks>
/// SQLite stores a boolean as an integer, so what the engine puts in the column for a declared
/// <c>false</c> is a <c>0</c> that the read model maps back — which is precisely the round trip worth asking
/// of this engine and not only of PostgreSQL.
/// </remarks>
public sealed class SqliteAlvoDataDefaultTests : AlvoDataDefaultTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    protected override async Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor) =>
        (await _fixture.StartAsync(schema, descriptor)).Data;

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
