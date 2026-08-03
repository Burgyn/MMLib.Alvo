using MMLib.Alvo.Testing.Events;
using MMLib.Alvo.Tests.Events;

using Xunit;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The transition and execution-log acceptance criteria over a real SQLite database. Every fact is inherited
/// unchanged: this class supplies a started backend and nothing else.
/// </summary>
/// <remarks>
/// It belongs to <see cref="DispatchedEventCollection"/> together with <see cref="SqliteOutboxChaosTests"/>,
/// because both assert process-wide event counters by value and xUnit would otherwise run them at once.
/// </remarks>
[Collection(DispatchedEventCollection.Name)]
public sealed class SqliteAlvoEventCriteriaTests : AlvoEventCriteriaTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

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
