using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>Runs <see cref="AlvoDataRelationRuleTests"/> against PostgreSQL.</summary>
public sealed class PostgreSqlAlvoDataRelationRuleTests : AlvoDataRelationRuleTests, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    public ValueTask InitializeAsync()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "PostgreSQL Testcontainers requires a Linux Docker daemon.");
        return _fixture.InitializeAsync();
    }

    protected override async Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor) =>
        (await _fixture.StartAsync(schema, descriptor)).Data;

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
