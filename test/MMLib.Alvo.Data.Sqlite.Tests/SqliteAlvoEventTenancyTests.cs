using MMLib.Alvo.Testing.Events;
using MMLib.Alvo.Tests.Events;

using Xunit;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The two-tenant after-hook fact over a real SQLite database. Inherits every fact unchanged.
/// </summary>
/// <remarks>
/// It belongs to <see cref="DispatchedEventCollection"/>, because the event counters are process-wide statics and no
/// two dispatching suites in this assembly may run at once.
/// </remarks>
[Collection(DispatchedEventCollection.Name)]
public sealed class SqliteAlvoEventTenancyTests : AlvoEventTenancyTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

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
