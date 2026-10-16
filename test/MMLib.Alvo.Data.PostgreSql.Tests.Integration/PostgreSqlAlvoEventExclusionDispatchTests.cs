using MMLib.Alvo.Testing.Events;
using MMLib.Alvo.Tests.Events;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The <c>events.exclude</c> dispatch facts over a real PostgreSQL engine. Inherits every fact unchanged.
/// </summary>
/// <remarks>
/// It belongs to <see cref="DispatchedEventCollection"/>, because every dispatching suite here asserts process-wide
/// event counters and xUnit would otherwise run them at once.
/// </remarks>
[Collection(DispatchedEventCollection.Name)]
public sealed class PostgreSqlAlvoEventExclusionDispatchTests : AlvoEventExclusionDispatchTests, IAsyncLifetime
{
    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    /// <inheritdoc/>
    protected override async Task<IAlvoEventWorld> WorldAsync(AlvoEventProject project) =>
        await AlvoEventCriteriaWorld.StartAsync(project, async install =>
        {
            var host = await _fixture.StartAsync(project.Schema, project.Descriptor, configure: install);

            return (host.Data, (IServiceProvider)host.Services);
        });

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
