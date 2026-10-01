using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL's leg of the literal-default suite (#113), so "identical behaviour on every engine" is measured
/// on the engine a production deployment actually runs.
/// </summary>
/// <remarks>
/// The engine with a real <c>boolean</c> type and a real <c>DEFAULT</c> clause in its <c>CREATE TABLE</c> —
/// where a wrongly-typed literal would be the engine's error rather than a mapping, which is why the refusal
/// that prevents one lives at apply.
/// </remarks>
public sealed class PostgreSqlAlvoDataDefaultTests : AlvoDataDefaultTests, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    protected override async Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor) =>
        (await _fixture.StartAsync(schema, descriptor)).Data;

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
