using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>HX-12 over HTTP on every engine: a sequence number is the framework's to assign and nobody's to write.</summary>
public abstract partial class DataApiEngineTests
{
    private static readonly DateTimeOffset _sequenceDay = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_sequence_field_is_read_only_in_the_document()
    {
        await using var world = await StartSequenceAsync();

        var schemas = (await world.OpenApiDocumentAsync())["components"]!["schemas"]!;

        schemas["orders"]!["properties"]!["number"]!["readOnly"]!.GetValue<bool>().ShouldBeTrue();
        schemas["orders"]!["properties"]!["code"]!["readOnly"]!.GetValue<bool>().ShouldBeTrue();
        foreach (var request in new[] { "ordersCreate", "ordersPatch" })
        {
            schemas[request]!["properties"]!.AsObject().ShouldNotContainKey("number", request);
            schemas[request]!["properties"]!.AsObject().ShouldNotContainKey("code", request);
            (schemas[request]!["required"]?.AsArray().Select(name => name!.GetValue<string>()) ?? [])
                .ShouldNotContain("number", request);
        }
    }

    private Task<AlvoApiWorld> StartSequenceAsync() =>
        AlvoApiWorld.FromDescriptorAsync(
            "sequence-orders.alvo.json",
            [_clerk],
            new AlvoApiWorldSetup(
                MapOpenApiDocument: true,
                MapAlvoProblemDetails: true,
                ConfigureServicesAfterAlvo: services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(_sequenceDay))),
            Engine);
}
