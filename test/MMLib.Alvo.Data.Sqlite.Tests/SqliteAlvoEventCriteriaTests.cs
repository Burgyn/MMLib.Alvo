using MMLib.Alvo.Testing.Events;
using MMLib.Alvo.Tests.Events;

using Xunit;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The transition and execution-log acceptance criteria over a real SQLite database. Every fact is inherited
/// unchanged: this class supplies a started backend and nothing else.
/// </summary>
/// <remarks>
/// It belongs to <see cref="DispatchedEventCollection"/>, because the event counters are process-wide statics and
/// every event criterion asserts one by value — so no two dispatching suites in this assembly may run at once.
/// The chaos criterion, which used to be the other member here, now lives in
/// <c>MMLib.Alvo.Data.Sqlite.Tests.Integration</c>: it is a ~27 s numeric criterion and this project is a ring0
/// module.
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
