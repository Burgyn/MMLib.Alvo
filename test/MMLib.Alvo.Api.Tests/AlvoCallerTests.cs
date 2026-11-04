using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Auth;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>Acceptance (9): <c>.WithAlvoCaller()</c> resolves the caller exactly as the generated routes do.</summary>
public sealed class AlvoCallerTests
{
    private static readonly TestApiKey _clerk = new("clerk-key", ["admin", "authenticated"], ["owners:read"], Guid.NewGuid());

    [Fact]
    public async Task No_credential_on_an_alvo_caller_endpoint_binds_anonymous()
    {
        await using var world = await WorldAsync();

        using var response = await world.SendAsync(HttpMethod.Get, "/whoami", key: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonNode.Parse(await response.ReadTextAsync())!;
        body["actor"]!.GetValue<string>().ShouldBe(nameof(AlvoActorKind.Anonymous));
        body["user"]!.GetValue<Guid>().ShouldBe(Guid.Empty);
    }

    [Fact]
    public async Task A_bad_key_on_an_alvo_caller_endpoint_is_401_before_the_handler()
    {
        var calls = 0;
        await using var world = await WorldAsync(() => calls++);

        using var response = await world.SendAsync(HttpMethod.Get, "/whoami", new TestApiKey("ghost-key", ["admin"], ["*:read"]));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.ReadTextAsync()).ShouldContain("unauthenticated");
        calls.ShouldBe(0, "a refused credential never reaches the handler");
    }

    [Fact]
    public async Task A_good_key_binds_the_key_s_caller_and_publishes_it_to_the_accessor()
    {
        await using var world = await WorldAsync();

        using var response = await world.SendAsync(HttpMethod.Get, "/whoami", _clerk);

        var body = JsonNode.Parse(await response.ReadTextAsync())!;
        body["user"]!.GetValue<Guid>().ShouldBe(_clerk.User);
        body["accessor"]!.GetValue<Guid>().ShouldBe(_clerk.User, "the same caller IAlvoContextAccessor publishes");
        body["tenant"]!.GetValue<Guid>().ShouldBe(_clerk.Tenant!.Value, "the key's own tenant, as the Data API resolves it");
        body["roles"]!.AsArray().Select(role => role!.GetValue<string>())
            .ShouldBe(["admin", "authenticated"], ignoreOrder: true, "the key's roles, as the Data API resolves them");
    }

    [Fact]
    public async Task A_tenant_header_the_key_was_not_issued_for_is_401_before_the_handler()
    {
        var calls = 0;
        await using var world = await WorldAsync(() => calls++);

        using var response = await world.SendAsync(HttpMethod.Get, "/whoami", _clerk, tenant: Guid.NewGuid().ToString());

        response.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized, "the plain endpoint inherits the Data API's tenant-mismatch arm");
        calls.ShouldBe(0, "a refused credential never reaches the handler");
    }

    [Fact]
    public async Task A_plain_endpoint_has_no_scope_gate_of_its_own()
    {
        await using var world = await WorldAsync();

        using var response = await world.SendAsync(HttpMethod.Get, "/whoami", _clerk);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "an owners:read key reaches a plain endpoint; the host's code decides");
    }

    [Fact]
    public async Task Reading_an_alvo_caller_on_an_endpoint_without_the_filter_throws_instead_of_answering_anonymous()
    {
        await using var world = await WorldAsync();

        using var response = await world.SendAsync(HttpMethod.Get, "/forgot-the-filter", _clerk);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public void Reading_an_unbound_alvo_caller_throws_an_actionable_invalid_operation() =>
        Should.Throw<InvalidOperationException>(() => AlvoCaller.For(new DefaultHttpContext()).Context)
            .Message.ShouldContain(".WithAlvoCaller()");

    private static Task<AlvoApiWorld> WorldAsync(Action? onCall = null) =>
        AlvoApiWorld.VehicleRegistryAsync([_clerk], new AlvoApiWorldSetup(MapAlvoProblemDetails: true, ConfigureApp: app =>
        {
            app.MapGet("/whoami", (AlvoCaller caller, IAlvoContextAccessor accessor) =>
            {
                onCall?.Invoke();
                return Results.Json(new
                {
                    user = caller.Context.User.Value,
                    actor = caller.Context.ActorKind.ToString(),
                    tenant = caller.Context.Tenant?.Value,
                    roles = caller.Context.Roles.Select(role => role.Name),
                    accessor = accessor.Principal?.Context.User.Value ?? Guid.Empty,
                });
            }).WithAlvoCaller();
            app.MapGet("/forgot-the-filter", (AlvoCaller caller) => Results.Ok(caller.Context.User.Value));
        }));
}
