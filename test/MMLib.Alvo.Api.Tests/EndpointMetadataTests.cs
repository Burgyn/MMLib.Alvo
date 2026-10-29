using Microsoft.AspNetCore.Routing;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Api.Tests;

/// <summary>The public endpoint metadata every generated route carries beside the internal marker (spec 2026-10-14 §4.9).</summary>
public sealed class EndpointMetadataTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    [Fact]
    public async Task Every_generated_endpoint_carries_public_metadata_agreeing_with_the_internal_marker()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin]);

        var generated = world.Endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<DataApiOperationMetadata>() is not null).ToList();

        generated.Count.ShouldBe(30, "three entities, ten routes each");
        foreach (var endpoint in generated)
        {
            var marker = endpoint.Metadata.GetMetadata<DataApiOperationMetadata>()!;
            var metadata = endpoint.Metadata.GetMetadata<AlvoEndpointMetadata>().ShouldNotBeNull();
            metadata.ShouldBe(new AlvoEndpointMetadata(marker.Entity, marker.Operation, marker.Kind.ToPublic()));
        }
    }

    [Fact]
    public void The_public_kinds_mirror_the_internal_ones_member_for_member() =>
        Enum.GetValues<DataApiEndpointKind>()
            .Select(kind => (Name: kind.ToString(), Value: (int)kind, Public: kind.ToPublic()))
            .ShouldAllBe(entry => entry.Public.ToString() == entry.Name && (int)entry.Public == entry.Value);

    [Fact]
    public void Every_public_kind_has_an_internal_twin() =>
        Enum.GetNames<AlvoEndpointKind>().ShouldBe(Enum.GetNames<DataApiEndpointKind>());
}
