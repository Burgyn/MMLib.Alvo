using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The user routes' refusals once the level gate has admitted the caller (#526): a project this build does not serve,
/// and a <c>disabled</c> body that does not say which way.
/// </summary>
/// <remarks>
/// The world's dev key is given the <c>operator</c> role, which <c>host-user-admin</c>'s access block resolves to
/// <c>admin</c>, so the answers measured here are the delegates', not the gate's <c>UserAdministrationRouteTests</c>
/// already pins.
/// </remarks>
public sealed class UserRouteRefusalTests : IAsyncLifetime
{
    private const string ServedProject = "host-user-admin";

    private const string UnservedProject = "nowhere";

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

    [Fact]
    public async Task Every_user_route_answers_a_project_this_build_does_not_serve_with_the_named_404()
    {
        foreach (var (method, path, body) in UserRoutes(UnservedProject))
        {
            using var response = await World.SendAsync(method, path, body);
            var text = await response.ReadTextAsync();

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{method} {path}: {text}");
            text.ShouldContain(
                $"serves no project '{UnservedProject}'",
                customMessage: $"{method} {path} must be the named 404, not an unmapped route");
        }
    }

    [Fact]
    public async Task The_named_404_for_an_unserved_project_leaves_the_person_untouched()
    {
        var id = await CreateAsync("untouched@alvo.test");

        using var response = await World.SendAsync(
            HttpMethod.Put, Disabled(UnservedProject, id), JsonNode.Parse("""{"disabled":true}"""));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.ReadTextAsync());
        (await IsDisabledAsync("untouched@alvo.test")).ShouldBeFalse("a refused project must not reach the store");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData(null)]
    public async Task A_disabled_body_that_does_not_say_is_refused_422_and_never_read_as_enable(string? body)
    {
        var id = await CreateAsync($"banned-{body?.Length ?? 0}@alvo.test");
        using (var ban = await World.SendAsync(
            HttpMethod.Put, Disabled(ServedProject, id), JsonNode.Parse("""{"disabled":true}""")))
        {
            ban.StatusCode.ShouldBe(HttpStatusCode.OK, await ban.ReadTextAsync());
        }

        using var response = await World.SendAsync(
            HttpMethod.Put, Disabled(ServedProject, id), body is null ? null : JsonNode.Parse(body));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.ReadTextAsync());
        JsonNode.Parse(await response.ReadTextAsync())!["detail"]!.GetValue<string>()
            .ShouldContain("""{"disabled": false}""", customMessage: "the refusal must say how to send it");
        (await IsDisabledAsync($"banned-{body?.Length ?? 0}@alvo.test"))
            .ShouldBeTrue("an unsaid body must not lift the ban");
    }

    private static (HttpMethod Method, string Path, JsonNode? Body)[] UserRoutes(string project) =>
    [
        (HttpMethod.Get, $"/management/projects/{project}/users", null),
        (HttpMethod.Post, $"/management/projects/{project}/users",
            JsonNode.Parse("""{"email":"someone@alvo.test","roleNames":[]}""")),
        (HttpMethod.Put, $"/management/projects/{project}/users/{Guid.Empty}/roles",
            JsonNode.Parse("""{"roleNames":[]}""")),
        (HttpMethod.Put, $"/management/projects/{project}/users/{Guid.Empty}/tenant",
            JsonNode.Parse("""{"tenant":null}""")),
        (HttpMethod.Put, Disabled(project, Guid.Empty), JsonNode.Parse("""{"disabled":true}""")),
        (HttpMethod.Post, $"/management/projects/{project}/users/{Guid.Empty}/credential-reset", null),
        (HttpMethod.Delete, $"/management/projects/{project}/users/{Guid.Empty}/lockout", null),
    ];

    private static string Disabled(string project, Guid id) => $"/management/projects/{project}/users/{id}/disabled";

    private async Task<Guid> CreateAsync(string email)
    {
        using var response = await World.SendAsync(
            HttpMethod.Post, $"/management/projects/{ServedProject}/users", new JsonObject { ["email"] = email, ["roleNames"] = new JsonArray() });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        return JsonNode.Parse(await response.ReadTextAsync())!["id"]!.GetValue<Guid>();
    }

    private async Task<bool> IsDisabledAsync(string email)
    {
        using var list = await World.GetAsync($"/management/projects/{ServedProject}/users?search={email}");
        return JsonNode.Parse(await list.ReadTextAsync())!["users"]![0]!["isDisabled"]!.GetValue<bool>();
    }
}
