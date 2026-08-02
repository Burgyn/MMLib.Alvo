using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// PostgreSQL's leg of the constraint suite — #139's answer for the engine the field-service e2e suite actually
/// measured, re-asked here at the port so the evidence is not only end-to-end.
/// </summary>
/// <remarks>
/// The engine that names the violated <em>constraint</em> and, with Npgsql's default <c>Include Error
/// Detail</c>, no columns — the mirror image of SQLite. Both are absorbed by
/// <c>PostgreSqlSqlDialect.DecodeConstraintViolation</c>, and every fact above it is inherited unchanged.
/// </remarks>
public sealed class PostgreSqlAlvoDataConstraintTests : AlvoDataConstraintTests, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    protected override async Task<IAlvoData> CreateAsync(SchemaModel schema, AlvoDescriptor descriptor) =>
        (await _fixture.StartAsync(schema, descriptor)).Data;

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
