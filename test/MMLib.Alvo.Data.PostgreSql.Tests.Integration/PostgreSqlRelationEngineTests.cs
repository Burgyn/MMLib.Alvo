using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Tests.Ef;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>PostgreSQL's leg of the relation-rule engine facts.</summary>
public sealed class PostgreSqlRelationEngineTests : RelationEngineFacts, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    public ValueTask InitializeAsync()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "PostgreSQL Testcontainers requires a Linux Docker daemon.");
        return _fixture.InitializeAsync();
    }

    private PostgreSqlAlvoDataHost? _host;

    protected override IReadOnlyList<(string Statement, bool InTransaction)> Commands =>
        _host?.Commands ?? throw new InvalidOperationException("No host was started.");

    protected override async Task<IServiceProvider> StartAsync(SchemaModel schema, AlvoDescriptor descriptor)
    {
        _host = await _fixture.StartAsync(schema, descriptor);
        return _host.Services;
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
