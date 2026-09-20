using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The standalone image's management surface: <c>MapAlvo</c> mounts it, it admits nobody the descriptor's
/// <c>access</c> block does not name, and it stays out of the published OpenAPI document.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured over <see cref="AlvoHost"/>'s own composition, never a hand-rolled application.</b> The claim
/// is that the shipped image mounts the surface, so a fixture calling <c>MapAlvoManagementApi()</c> itself
/// would prove the extension method works and say nothing about whether the standalone host calls it.
/// </para>
/// <para>
/// <b>The third fact is the one with teeth outside this suite.</b> The TeaPie e2e suite pins the published
/// document's path set by <em>equality</em>, so a single management route reaching the document turns that
/// suite red in a run nothing here would explain. <c>ManagementEndpoints</c> carries
/// <c>ExcludeFromDescription</c> and <c>ManagementContractTests</c> pins the metadata; this pins the
/// consequence, on the host that actually serves the document.
/// </para>
/// </remarks>
public class ManagementRouteTests
{
    /// <summary>The prefix the management surface mounts under when nothing configures one.</summary>
    private const string DefaultManagementPrefix = "/management";

    /// <summary>
    /// <c>MapAlvo</c> mounts the surface, and a project with no <c>access</c> block admits nobody to it —
    /// including the deployment's own configured key.
    /// </summary>
    /// <remarks>
    /// <b>403 rather than 404 is the whole fact.</b> A 404 would mean the surface is not mounted and a 401
    /// would mean the credential was not read, so only a refusal <em>after</em> authentication says the route
    /// exists and the gate answered. The key presented here is the world's own administrator key, which
    /// reaches every entity route in this suite: <c>access</c> governs management and scopes do not.
    /// </remarks>
    [Fact]
    public async Task MapAlvo_mounts_the_management_surface_and_it_is_closed_by_default()
    {
        await using var world = await AlvoHostWorld.StartAsync();

        using var response = await world.GetAsync($"{DefaultManagementPrefix}/info");

        response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            $"the surface must be mounted and refuse a caller no access block names: {await response.ReadTextAsync()}");
    }

    /// <summary>The standalone image describes itself as <c>standalone</c> to a caller its access block admits.</summary>
    /// <remarks>
    /// <b>Over the wire rather than off the options.</b> <c>info</c>'s <c>mode</c> is the one thing an agent
    /// branches on to tell an embedded Alvo from this image, and it is computed from the registered
    /// <see cref="AlvoMode"/> — so reading it back through the host's own pipeline is what proves the shipped
    /// composition leaves that default alone.
    /// </remarks>
    [Fact]
    public async Task The_standalone_host_says_it_is_standalone()
    {
        await using var world = await AlvoHostWorld.StartAsync(ManagedDescriptor);

        using var response = await world.GetAsync($"{DefaultManagementPrefix}/info");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        var info = await response.ReadJsonObjectAsync();
        info["mode"]!.GetValue<string>().ShouldBe("standalone");
    }

    /// <summary>No management route reaches the OpenAPI document the standalone host publishes.</summary>
    /// <remarks>
    /// The e2e suite compares the document's path set for equality, so one leaked route breaks a suite that
    /// cannot name this cause. Asserted on the document the host serves, because that is the artefact TeaPie
    /// reads.
    /// </remarks>
    [Fact]
    public async Task No_management_route_reaches_the_published_document()
    {
        await using var world = await AlvoHostWorld.StartAsync(ManagedDescriptor);

        using var response = await world.SendAnonymouslyAsync(HttpMethod.Get, AlvoHost.OpenApiDocumentPath);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        var paths = await PathsOfAsync(response);
        paths.ShouldNotBeEmpty("a document with no paths would make the assertion below vacuous");
        paths.ShouldAllBe(
            path => !path.StartsWith(DefaultManagementPrefix, StringComparison.Ordinal),
            "the published document is the generated Data API contract, and the e2e suite pins its path set by equality");
    }

    /// <summary>The descriptor whose <c>access</c> block admits this suite's administrator key as a viewer.</summary>
    private const string ManagedDescriptor = "host-management.alvo.json";

    /// <summary>Every path key in the served document.</summary>
    /// <param name="response">The response carrying the OpenAPI document.</param>
    private static async Task<IReadOnlyList<string>> PathsOfAsync(HttpResponseMessage response)
    {
        var document = await response.ReadJsonObjectAsync();

        return [.. document["paths"]!.AsObject().Select(path => path.Key)];
    }
}
