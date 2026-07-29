using Testcontainers.PostgreSql;

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// Builds and starts the one <c>postgres:16-alpine</c> container every Testcontainers-backed PostgreSQL
/// fixture in this repository needs, so a change to the image tag or the build arguments lands once rather
/// than being copied into a third fixture that quietly drifts from the other two.
/// </summary>
/// <remarks>
/// <b>Call this from <c>InitializeAsync</c>, never from a field initializer or a constructor.</b> Talking to
/// the Docker daemon while a fixture is being <em>constructed</em> means a host with no reachable daemon
/// throws before any test reaches its own skip — which xUnit reports as every test in the sharing class
/// failing. PR1 lost 28 tests to exactly that on a Windows runner; do not reintroduce it.
/// <para>
/// Only the container-creation mechanics live here. Each caller keeps its own policy for what "unavailable"
/// means to it — a Windows-only skip, or a broader daemon-reachability one — because that policy is a
/// property of the tests the caller runs, not of the container itself.
/// </para>
/// </remarks>
internal static class PostgresTestContainer
{
    /// <summary>Builds and starts one <c>postgres:16-alpine</c> container. Propagates whatever it throws.</summary>
    public static async Task<PostgreSqlContainer> BuildAndStartAsync()
    {
        // Explicit tag: PostgreSqlBuilder's parameterless ctor and its PostgreSqlImage constant are both
        // obsolete in Testcontainers.PostgreSql 4.13 in favour of an explicit image argument.
        var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync();
        return container;
    }
}
