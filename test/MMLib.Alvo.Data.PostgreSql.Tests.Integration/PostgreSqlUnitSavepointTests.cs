using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Ef;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>PostgreSQL's leg of the savepoint facts — where a savepoint left un-rolled-back would leave the insert in the unit.</summary>
public sealed class PostgreSqlUnitSavepointTests : UnitSavepointFacts, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    protected override async Task<IServiceProvider> StartAsync(SchemaModel schema, AlvoDescriptor descriptor) =>
        (await _fixture.StartAsync(schema, descriptor)).Services;

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
