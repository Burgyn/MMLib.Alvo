using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using MMLib.Alvo.Tests.Data;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The before-hook pipeline at the port's three write faces, over a real PostgreSQL engine — the criterion
/// "green on SQLite <em>and</em> PostgreSQL", proved rather than assumed. Inherits every fact unchanged.
/// </summary>
/// <remarks>
/// The engine where the transaction and the row lock are real: a <c>beforeUpdate</c> hook's <c>old.</c>
/// references read the pre-image under <c>FOR UPDATE</c> here, which SQLite's file-level write lock provides
/// by a different mechanism entirely. Both answer identically or the suite fails, which is what §0 principle 3
/// asks of a rule-engine behaviour.
/// </remarks>
public sealed class PostgreSqlAlvoDataBeforeHookTests : AlvoDataBeforeHookTests, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    protected override async Task<IAlvoDataBeforeHookWorld> WorldAsync(
        SchemaModel schema, AlvoDescriptor descriptor, TimeProvider time)
    {
        var host = await _fixture.StartAsync(schema, descriptor, time, BeforeHookRecording.Decorate);
        return new AlvoDataBeforeHookWorld(host.Data, BeforeHookRecording.RecorderOf(host.Services));
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
