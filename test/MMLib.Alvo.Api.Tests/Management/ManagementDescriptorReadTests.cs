using System.Net;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// <c>GET {m}/projects</c> and <c>GET {m}/projects/{p}/descriptor</c> — the export.
/// </summary>
/// <remarks>
/// <b>The world primes through the boot rather than ahead of it</b>
/// (<see cref="AlvoApiWorldSetup.MapBeforePriming"/>), because that is the only path that appends a
/// descriptor revision: <c>SchemaMigrationRunner</c> records an applied <em>snapshot</em> and no history at
/// all, so a world primed that way would answer "revision 0, no descriptor" and the export fact would be
/// measuring the fixture rather than the endpoint.
/// </remarks>
public class ManagementDescriptorReadTests
{
    private static readonly TestApiKey _ops = new("mgmt-ops", ["ops"], ["*:read"]);

    [Fact]
    public async Task The_project_list_carries_the_one_project_this_build_serves()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var projects = await (await world.SendAsync(HttpMethod.Get, "/management/projects", _ops))
            .ReadJsonArrayAsync();

        projects.Count.ShouldBe(1, "multi-project standalone is not built; the switcher degrades to one row");
        projects[0]["name"]!.GetValue<string>().ShouldBe("managed-fleet");
        projects[0]["revision"]!.GetValue<int>().ShouldBe(1);
        projects[0]["phase"]!.GetValue<string>().ShouldBe("ready");
    }

    [Fact]
    public async Task The_descriptor_is_returned_verbatim_with_its_revision()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var response = await world.SendAsync(
            HttpMethod.Get, "/management/projects/managed-fleet/descriptor", _ops);
        var body = await response.ReadJsonObjectAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body["revision"]!.GetValue<int>().ShouldBe(1);
        body["descriptorJson"]!.GetValue<string>().ShouldBe(
            await File.ReadAllTextAsync(ManagedFleet.DescriptorPath, TestContext.Current.CancellationToken),
            "the export is the stored descriptor text, not a re-serialisation of a parsed model");
    }

    [Fact]
    public async Task A_project_this_instance_does_not_serve_is_404()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var response = await world.SendAsync(HttpMethod.Get, "/management/projects/not-mine/descriptor", _ops);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.NotFound);
    }

    /// <summary>
    /// The 404 names the project asked for and the ones that exist, which the Data API's 404 deliberately
    /// never does.
    /// </summary>
    /// <remarks>
    /// Nothing is hidden by naming them: which projects an instance serves is configuration rather than
    /// row-level data, and a caller the access block already admitted reads the same list from
    /// <c>GET projects</c>.
    /// </remarks>
    [Fact]
    public async Task The_404_names_what_was_asked_for_and_what_exists()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var detail = await (await world.SendAsync(
            HttpMethod.Get, "/management/projects/not-mine/descriptor", _ops)).ReadProblemDetailAsync();

        detail.ShouldContain("not-mine");
        detail.ShouldContain("managed-fleet");
    }
}
