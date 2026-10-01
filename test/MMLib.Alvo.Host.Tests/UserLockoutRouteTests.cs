using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// <c>DELETE {m}/projects/{p}/users/{id}/lockout</c> over the wire, as an administrator: what the route answers once
/// the gate has let the caller through (docs/todo-admin.md §8d items 39, 46(i)).
/// </summary>
/// <remarks>
/// The world's dev key is given the <c>operator</c> role, which <c>host-user-admin</c>'s access block resolves to
/// <c>admin</c>, so these facts measure the member's answers rather than the gate <c>UserAdministrationRouteTests</c>
/// already pins.
/// </remarks>
public sealed class UserLockoutRouteTests : IAsyncLifetime
{
    private AlvoHostWorld? _world;

    private AlvoHostWorld World => _world!;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
        => _world = await AlvoHostWorld.StartAsync(
            "host-user-admin.alvo.json",
            overrides: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Alvo:Auth:DevKeys:0:Roles:0"] = "operator",
            });

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_world is not null)
        {
            await _world.DisposeAsync();
        }
    }

    /// <summary>A person with no lockout is answered 200 with the person, no lockout on them: safe to repeat.</summary>
    [Fact]
    public async Task Ending_a_lockout_answers_the_person_as_they_now_are()
    {
        var id = await CreateAsync("free@alvo.test");

        using var response = await World.SendAsync(HttpMethod.Delete, Lockout(id), body: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        var person = JsonNode.Parse(await response.ReadTextAsync())!;
        person["id"]!.GetValue<Guid>().ShouldBe(id);
        person["lockedOutUntil"].ShouldBeNull();
    }

    /// <summary>
    /// A disabled person is refused 422, with the sentence that says to let them back in, and stays disabled.
    /// </summary>
    [Fact]
    public async Task A_disabled_person_is_refused_422_and_stays_disabled()
    {
        var id = await CreateAsync("disabled@alvo.test");
        using (var disabled = await World.SendAsync(
            HttpMethod.Put, $"/management/projects/host/users/{id}/disabled", JsonNode.Parse("""{"disabled":true}""")))
        {
            disabled.StatusCode.ShouldBe(HttpStatusCode.OK, await disabled.ReadTextAsync());
        }

        using var response = await World.SendAsync(HttpMethod.Delete, Lockout(id), body: null);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.ReadTextAsync());
        (await response.ReadTextAsync()).ShouldContain("Let them back in instead");
        using var list = await World.GetAsync("/management/projects/host/users?search=disabled@alvo.test");
        JsonNode.Parse(await list.ReadTextAsync())!["users"]![0]!["isDisabled"]!.GetValue<bool>()
            .ShouldBeTrue("a refused unlock left the person disabled");
    }

    private static string Lockout(Guid id) => $"/management/projects/host/users/{id}/lockout";

    private async Task<Guid> CreateAsync(string email)
    {
        using var response = await World.SendAsync(
            HttpMethod.Post, "/management/projects/host/users", new JsonObject { ["email"] = email, ["roleNames"] = new JsonArray() });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        return JsonNode.Parse(await response.ReadTextAsync())!["id"]!.GetValue<Guid>();
    }
}
