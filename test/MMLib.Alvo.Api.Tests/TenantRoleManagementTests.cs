using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Management;
using System.Net;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Spec §6.2 at Management, in process: a host route calling <see cref="IAlvoManagement"/> as the caller
/// <c>.WithAlvoCaller()</c> resolved never lets a tenant role reach an <c>access</c> level, though the same role held as
/// a project role does.
/// </summary>
/// <remarks>
/// In process rather than over <c>/management</c>, because the Management API never reads a Bearer token at all
/// (<see cref="PersonCredentialTests.The_management_api_never_reads_a_bearer_token"/>): a host route is the one way a
/// person's tenant roles can be presented to an access level, so it is the way this fact has to take.
/// </remarks>
public sealed class TenantRoleManagementTests
{
    private const string Token = "acme-dispatcher";

    private static readonly Guid _acme = Guid.NewGuid();

    private static readonly TestApiKey _projectDispatcher =
        new("tr-dispatcher", ["dispatcher", "authenticated"], ["*:read", "*:write"], _acme);

    [Fact]
    public async Task A_tenant_role_never_reaches_a_management_access_level_in_process()
    {
        var people = new TestPersonResolver((Token, TestPersonResolver.Person("person:dispatcher", _acme, "dispatcher")));
        await using var world = await AlvoApiWorld.FromDescriptorAsync(
            "tenant-roles-desk.alvo.json",
            [_projectDispatcher],
            new AlvoApiWorldSetup(ConfigureServices: people.Register, ConfigureApp: MapHostInfo));

        using var person = await world.SendAsync(
            HttpMethod.Get, "/host/info", headers: [new("Authorization", $"Bearer {Token}")]);
        using var project = await world.SendAsync(HttpMethod.Get, "/host/info", _projectDispatcher);

        person.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a tenant role never satisfies an access level");
        people.Calls.Select(call => call.Presented).ShouldBe([Token], "the person was resolved, so the 403 judged them");
        project.StatusCode.ShouldBe(HttpStatusCode.OK, "the access level admits dispatcher as a project role");
    }

    private static void MapHostInfo(WebApplication app) =>
        app.MapGet("/host/info", async (IAlvoManagement management, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await management.GetInfoAsync(ct));
            }
            catch (ManagementForbiddenException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
        }).WithAlvoCaller();
}
