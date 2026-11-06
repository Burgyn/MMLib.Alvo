using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>HX-11 over HTTP on every engine: validations are CHECK constraints, a rollup is filtered, now() is one instant.</summary>
public abstract partial class DataApiEngineTests
{
    private const string IntegrityDescriptorPath = "/management/projects/integrity-inventory/descriptor";

    private static readonly TestApiKey _clerk = new("clerk", ["clerk"], ["*:read", "*:write"]);
    private static readonly DateTimeOffset _opening = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Ruling 73 over the Management API: a validation added or changed in the apply that changes the rollup it reads is
    /// refused when it is planned, at the validation, naming the order that works — never judged against the old aggregate.
    /// </summary>
    [Fact]
    public async Task A_validation_changed_with_the_rollup_it_reads_is_refused_at_its_pointer_on_every_engine()
    {
        await using var world = await StartIntegrityAsync();
        await CreateIntegrityAsync(world, "products", new JsonObject { ["name"] = "Lamp", ["stock"] = 3 });

        using var refused = await ApplyIntegrityAsync(world, fields =>
        {
            fields["reserved"]!["rollup"]!["where"] = "status == 'released'";
            fields["reserved"]!["validation"] = "stock >= value";
        }, dryRun: false);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        var text = await refused.ReadTextAsync();
        text.ShouldContain("/entities/products/fields/reserved/validation");
        text.ShouldContain("Apply the rollup change first, then the validation.", Case.Sensitive, text);
        (await IntegrityRevisionAsync(world)).ShouldBe(1, "nothing was applied");

        using var alone = await ApplyIntegrityAsync(world, fields => fields["reserved"]!["rollup"]!["where"] = "status == 'released'", dryRun: true);
        alone.StatusCode.ShouldBe(HttpStatusCode.OK, await alone.ReadTextAsync());
    }

    private Task<AlvoApiWorld> StartIntegrityAsync(FakeTimeProvider? clock = null, Action<IAlvoBuilder>? configureAlvo = null) =>
        AlvoApiWorld.FromDescriptorAsync(
            "integrity-inventory.alvo.json",
            [_clerk],
            new AlvoApiWorldSetup(
                MapAlvoProblemDetails: true,
                MapManagementApi: true,
                ConfigureServicesAfterAlvo: clock is null ? null : services => services.AddSingleton<TimeProvider>(clock),
                ConfigureAlvo: configureAlvo),
            Engine);

    private static async Task<Guid> CreateIntegrityAsync(AlvoApiWorld world, string entity, JsonObject body) =>
        (await CreateIntegrityRowAsync(world, entity, body))["id"]!.GetValue<Guid>();

    private static async Task<JsonObject> CreateIntegrityRowAsync(AlvoApiWorld world, string entity, JsonObject body)
    {
        using var response = await world.SendAsync(HttpMethod.Post, $"/api/{entity}", _clerk, body: body);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.ReadTextAsync());
        return await response.ReadJsonObjectAsync();
    }

    private static async Task<JsonObject> ReadIntegrityAsync(AlvoApiWorld world, string entity, Guid id)
    {
        using var response = await world.SendAsync(HttpMethod.Get, $"/api/{entity}/{id}", _clerk);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
        return await response.ReadJsonObjectAsync();
    }

    private static async Task<int> IntegrityRevisionAsync(AlvoApiWorld world)
    {
        using var response = await world.SendAsync(HttpMethod.Get, IntegrityDescriptorPath, _clerk);
        return (await response.ReadJsonObjectAsync())["revision"]!.GetValue<int>();
    }

    /// <summary>
    /// Applies the current descriptor with one edit to <c>products</c>' fields over the Management API, and hands back the
    /// response — a real apply (<paramref name="dryRun"/> false) goes through the runtime schema writer.
    /// </summary>
    /// <param name="world">The running world.</param>
    /// <param name="edit">The edit to make to the <c>fields</c> object, if any.</param>
    /// <param name="dryRun">Whether to send <c>?dryRun=true</c>.</param>
    /// <param name="addDynamic">Whether to add a dynamic entity whose field declares a validation.</param>
    private static async Task<HttpResponseMessage> ApplyIntegrityAsync(
        AlvoApiWorld world, Action<JsonObject>? edit, bool dryRun, bool addDynamic = false)
    {
        using var read = await world.SendAsync(HttpMethod.Get, IntegrityDescriptorPath, _clerk);
        var current = await read.ReadJsonObjectAsync();
        var descriptor = JsonNode.Parse(current["descriptorJson"]!.GetValue<string>())!.AsObject();
        edit?.Invoke(descriptor["entities"]!["products"]!["fields"]!.AsObject());
        if (addDynamic)
        {
            descriptor["entities"]!["ledgers"] = DynamicLedger();
        }

        var revision = current["revision"]!.GetValue<int>();
        return await world.SendAsync(
            HttpMethod.Put,
            dryRun ? $"{IntegrityDescriptorPath}?dryRun=true" : IntegrityDescriptorPath,
            _clerk,
            body: new JsonObject
            {
                ["descriptorJson"] = descriptor.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                ["author"] = "the-suite",
                ["reason"] = "an integrity fact",
            },
            headers: [new KeyValuePair<string, string>("If-Match", $"\"{revision}\"")]);
    }

    private static JsonObject DynamicLedger() => new()
    {
        ["storage"] = "dynamic",
        ["fields"] = new JsonObject { ["amount"] = new JsonObject { ["type"] = "integer", ["validation"] = "value > 0" } },
        ["rules"] = new JsonObject { ["list"] = "true", ["get"] = "true", ["create"] = "true", ["update"] = "true", ["delete"] = "true" },
    };
}
