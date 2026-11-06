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

    /// <summary>
    /// Acceptance (1) over HTTP: the 201 carries the number and the formatted code the create was assigned; a create that
    /// sends a number is refused by the write guard, exactly as one that sends a computed or rollup value.
    /// </summary>
    [Fact]
    public async Task A_create_answers_its_sequence_number_and_a_sent_one_is_refused_on_every_engine()
    {
        await using var world = await StartSequenceAsync();

        var first = await CreateSequencedAsync(world, new JsonObject { ["branch"] = "north" });
        var second = await CreateSequencedAsync(world, new JsonObject { ["branch"] = "south" });

        (first["number"]!.GetValue<long>(), first["code"]!.GetValue<string>()).ShouldBe((1L, "SO-2026-0001"));
        (second["number"]!.GetValue<long>(), second["code"]!.GetValue<string>()).ShouldBe((2L, "SO-2026-0002"));
        using var sent = await world.SendAsync(
            HttpMethod.Post, "/api/orders", _clerk, body: new JsonObject { ["branch"] = "north", ["number"] = 7 });
        await ShouldBeSequenceRefusalAsync(sent);
        (await world.CountRowsAsync("orders")).ShouldBe(2);
    }

    /// <summary>A patch naming a sequence number is refused and leaves the stored number as it was.</summary>
    [Fact]
    public async Task A_patch_cannot_change_a_sequence_number()
    {
        await using var world = await StartSequenceAsync();
        var created = await CreateSequencedAsync(world, new JsonObject { ["branch"] = "north" });
        var id = created["id"]!.GetValue<Guid>();

        using var patched = await world.SendAsync(HttpMethod.Patch, $"/api/orders/{id}", _clerk, body: new JsonObject { ["number"] = 99 });

        await ShouldBeSequenceRefusalAsync(patched);
        using var read = await world.SendAsync(HttpMethod.Get, $"/api/orders/{id}", _clerk);
        (await read.ReadJsonObjectAsync())["number"]!.GetValue<long>().ShouldBe(1L);
    }

    private static async Task<JsonObject> CreateSequencedAsync(AlvoApiWorld world, JsonObject body)
    {
        using var response = await world.SendAsync(HttpMethod.Post, "/api/orders", _clerk, body: body);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.ReadTextAsync());
        return await response.ReadJsonObjectAsync();
    }

    private static async Task ShouldBeSequenceRefusalAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await response.ReadTextAsync());
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
        (await response.ReadProblemDetailAsync()).ShouldContain("Field 'number' is a sequence number assigned by the framework");
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
