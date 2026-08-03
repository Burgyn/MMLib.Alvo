using MMLib.Alvo.Testing.Events;
using MMLib.Alvo.Tests.Events;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The transition and execution-log acceptance criteria over a real PostgreSQL engine — the criterion "green on
/// SQLite <em>and</em> PostgreSQL", proved rather than assumed. Inherits every fact unchanged.
/// </summary>
public sealed class PostgreSqlAlvoEventCriteriaTests : AlvoEventCriteriaTests, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    /// <inheritdoc/>
    protected override async Task<IAlvoEventWorld> WorldAsync()
    {
        var project = Project;

        return await AlvoEventCriteriaWorld.StartAsync(project, async install =>
        {
            var host = await _fixture.StartAsync(project.Schema, project.Descriptor, configure: install);

            return (host.Data, (IServiceProvider)host.Services);
        });
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
