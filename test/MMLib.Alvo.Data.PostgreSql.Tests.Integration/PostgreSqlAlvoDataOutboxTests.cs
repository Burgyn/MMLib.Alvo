using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Tests.Data;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The outbox guarantee at the four write sites, over a real PostgreSQL engine — the criterion "green on
/// SQLite <em>and</em> PostgreSQL", proved rather than assumed. Inherits every fact unchanged.
/// </summary>
public sealed class PostgreSqlAlvoDataOutboxTests : AlvoDataOutboxTests, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    protected override async Task<IAlvoDataOutboxWorld> WorldAsync(
        SchemaModel schema, AlvoDescriptor descriptor, TimeProvider? time = null)
    {
        var host = await _fixture.StartAsync(schema, descriptor, time);
        return new AlvoDataOutboxWorld(host.Data, host.Services);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
