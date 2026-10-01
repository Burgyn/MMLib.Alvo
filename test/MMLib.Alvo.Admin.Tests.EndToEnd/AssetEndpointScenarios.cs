using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticAssets;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// <b>The dashboard's static assets are the endpoints the cookie re-check skips</b> — in the one composition
/// that maps them as endpoints.
/// </summary>
/// <remarks>
/// The identity package skips the re-check only for an endpoint carrying <see cref="StaticAssetDescriptor"/>
/// or an explicit <c>[AllowAnonymous]</c>. <c>MapStaticAssets</c> is used only where the static-web-assets
/// manifest ships beside the entry assembly, which is this suite's host and the container, not the unit
/// suites — so this is where the descriptor's presence on every <c>_content/</c> and <c>_framework/</c> file is
/// pinned. Without it every asset of a first page load would be re-checked against the store again.
/// </remarks>
/// <param name="world">The running host.</param>
public sealed class AssetEndpointScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact]
    public void Every_mapped_asset_endpoint_carries_the_static_asset_descriptor()
    {
        var assets = world.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(route => route.RoutePattern.RawText is { } raw
                && (raw.StartsWith("_content/", StringComparison.Ordinal) || raw.StartsWith("_framework/", StringComparison.Ordinal)))
            .ToList();

        assets.ShouldNotBeEmpty("this host maps its assets as endpoints, or the fact measures nothing");
        assets.Where(route => route.Metadata.GetMetadata<StaticAssetDescriptor>() == null)
            .Select(route => route.RoutePattern.RawText).ShouldBeEmpty();
    }
}
