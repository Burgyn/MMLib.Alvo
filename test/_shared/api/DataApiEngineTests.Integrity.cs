using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
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

    /// <summary>Acceptance (1): ten concurrent reservations of one item against a stock of three.</summary>
    [Fact]
    public async Task A_concurrent_oversell_is_refused_422_with_a_pointer_on_every_engine()
    {
        await using var world = await StartIntegrityAsync();
        var product = await CreateIntegrityAsync(world, "products", new JsonObject { ["name"] = "Lamp", ["stock"] = 3 });

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => world.SendAsync(
            HttpMethod.Post, "/api/reservations", _clerk,
            body: new JsonObject { ["product_id"] = product, ["quantity"] = 1, ["status"] = "held" })));

        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.UnprocessableEntity,
            "SQLite's write gate queues the ten writers; none may fail busy or 500 (Ruling 53)");
        responses.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBe(3);
        foreach (var refused in responses.Where(response => response.StatusCode != HttpStatusCode.Created))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
            (await refused.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Validation);
            (await refused.ReadViolationsAsync()).ShouldBe([("/quantity", "check")]);
        }

        (await ReadIntegrityAsync(world, "products", product))["reserved"]!.GetValue<long>().ShouldBe(3);
        (await world.CountRowsAsync("reservations")).ShouldBe(3);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task A_patch_breaking_a_validation_is_422_at_its_field_and_leaves_the_row()
    {
        await using var world = await StartIntegrityAsync();
        var product = await CreateIntegrityAsync(world, "products", new JsonObject { ["name"] = "Lamp", ["stock"] = 3 });

        using var refused = await world.SendAsync(HttpMethod.Patch, $"/api/products/{product}", _clerk, body: new JsonObject { ["stock"] = -1 });

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        (await refused.ReadViolationsAsync()).ShouldBe([("/stock", "check")]);
        // Without the traceId: a W3C trace id is random hex between dashes, so "00-1f…" would contain "-1" by chance.
        var problem = await refused.ReadJsonObjectAsync();
        problem.Remove("traceId");
        problem.ToJsonString().ShouldNotContain("-1", Case.Sensitive, "the refused value is never echoed");
        (await ReadIntegrityAsync(world, "products", product))["stock"]!.GetValue<long>().ShouldBe(3);
    }

    [Fact]
    public async Task A_patch_of_a_field_another_validation_reads_is_refused_at_the_validated_field()
    {
        await using var world = await StartIntegrityAsync();
        var product = await CreateIntegrityAsync(world, "products", new JsonObject
        {
            ["name"] = "Lamp",
            ["stock"] = 3,
            ["sale_starts_at"] = "2026-11-01T00:00:00Z",
            ["sale_ends_at"] = "2026-11-20T00:00:00Z",
        });

        using var refused = await world.SendAsync(HttpMethod.Patch, $"/api/products/{product}", _clerk,
            body: new JsonObject { ["sale_starts_at"] = "2026-09-01T00:00:00Z" });

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        (await refused.ReadViolationsAsync()).ShouldBe([("/sale_ends_at", "check")]);
    }

    [Fact]
    public async Task A_hidden_field_s_validation_refuses_without_naming_it()
    {
        await using var world = await StartIntegrityAsync();
        using var refused = await world.SendAsync(HttpMethod.Post, "/api/products", _clerk,
            body: new JsonObject { ["name"] = "Lamp", ["stock"] = 1, ["cost"] = -1 });

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        (await refused.ReadViolationsAsync()).ShouldBe([("", "check")]);
        (await refused.ReadTextAsync()).ShouldNotContain("cost");
    }

    [Fact]
    public async Task A_released_reservation_leaves_the_filtered_rollup()
    {
        await using var world = await StartIntegrityAsync();
        var product = await CreateIntegrityAsync(world, "products", new JsonObject { ["name"] = "Lamp", ["stock"] = 3 });
        var reservation = await CreateIntegrityAsync(world, "reservations", new JsonObject { ["product_id"] = product, ["quantity"] = 2, ["status"] = "held" });

        using var released = await world.SendAsync(HttpMethod.Patch, $"/api/reservations/{reservation}", _clerk, body: new JsonObject { ["status"] = "released" });
        released.StatusCode.ShouldBe(HttpStatusCode.OK, await released.ReadTextAsync());

        (await ReadIntegrityAsync(world, "products", product))["reserved"].ShouldBeNull();
    }

    /// <summary>Acceptance (2) over the Management API.</summary>
    [Fact]
    public async Task Adding_a_validation_over_rows_that_break_it_is_refused_with_their_count_on_every_engine()
    {
        await using var world = await StartIntegrityAsync();
        await CreateIntegrityAsync(world, "products", new JsonObject { ["name"] = "Long", ["stock"] = 3, ["sale_starts_at"] = "2026-11-01T00:00:00Z" });
        await CreateIntegrityAsync(world, "products", new JsonObject { ["name"] = "Short", ["stock"] = 3 });

        using var refused = await ApplyIntegrityAsync(world, fields => fields["name"]!["validation"] = "value != 'Long'", dryRun: true);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        var text = await refused.ReadTextAsync();
        text.ShouldContain("/entities/products/fields/name/validation");
        text.ShouldContain("1 row(s) of 'products' break this validation");
    }

    /// <summary>Review I1: the Management API applies through the runtime writer, which must translate the engine's CHECK refusal too.</summary>
    [Fact]
    public async Task Renaming_a_validated_field_over_violating_rows_is_422_at_its_validation_never_500()
    {
        await using var world = await StartIntegrityAsync();
        await CreateIntegrityAsync(world, "products", new JsonObject { ["name"] = "Lamp", ["stock"] = 3, ["sale_starts_at"] = "2026-11-01T00:00:00Z", ["sale_ends_at"] = "2026-11-20T00:00:00Z" });

        using var refused = await ApplyIntegrityAsync(world, fields =>
        {
            fields["sale_ends"] = fields["sale_ends_at"]!.DeepClone();
            fields["sale_ends"]!["renamedFrom"] = "sale_ends_at";
            fields["sale_ends"]!["validation"] = "!has(sale_starts_at) || value - sale_starts_at <= duration('24h')";
            fields.Remove("sale_ends_at");
        }, dryRun: false);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        var text = await refused.ReadTextAsync();
        text.ShouldContain("/entities/products/fields/sale_ends/validation");
        text.ShouldContain("could not be added or kept");
        (await ReadIntegrityAsync(world, "products", await FirstIntegrityIdAsync(world, "products")))["sale_ends_at"].ShouldNotBeNull("nothing was applied");
    }

    /// <summary>Acceptance (7) over the Management API.</summary>
    [Fact]
    public async Task A_validation_on_a_dynamic_entity_is_refused_naming_41()
    {
        await using var world = await StartIntegrityAsync();
        using var refused = await ApplyIntegrityAsync(world, edit: null, dryRun: true, addDynamic: true);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.ReadTextAsync());
        (await refused.ReadTextAsync()).ShouldContain("#41");
    }

    /// <summary>Acceptance (4) over HTTP: the request filter binds the instant; the clock moving mid-request moves nothing.</summary>
    /// <remarks>
    /// The clock moves in an endpoint filter that runs inside Alvo's request filter and before the data port, so only an
    /// instant bound by the request filter — not the data port's own fallback — still reads the opening instant.
    /// </remarks>
    [Fact]
    public async Task One_request_is_one_instant_on_every_engine()
    {
        var clock = new FakeTimeProvider(_opening);
        await using var world = await StartIntegrityAsync(
            clock,
            configureAlvo: alvo => alvo.Entity("products", products => products
                .BeforeWrite("probe", (BeforeWrite<AlvoRecord> write) =>
                {
                    write.Set("name", "judged at the request's instant");
                    return ValueTask.CompletedTask;
                }, when: "now() == timestamp('2026-11-09T08:00:00Z')")),
            routes: routes => routes.Add(endpoint => endpoint.FilterFactories.Add((_, next) => invocation =>
            {
                clock.Advance(TimeSpan.FromSeconds(30));
                return next(invocation);
            })));

        var created = await CreateIntegrityRowAsync(world, "products", new JsonObject { ["name"] = "Lamp", ["stock"] = 1 });

        created["name"]!.GetValue<string>().ShouldBe("judged at the request's instant", "now() stayed the instant the request filter bound");
        created["created_at"]!.GetValue<DateTimeOffset>().ShouldBe(_opening.AddSeconds(30), "the stamp is a per-write clock read (Ruling 2)");
    }

    private Task<AlvoApiWorld> StartIntegrityAsync(
        FakeTimeProvider? clock = null, Action<IAlvoBuilder>? configureAlvo = null, Action<IEndpointConventionBuilder>? routes = null) =>
        AlvoApiWorld.FromDescriptorAsync(
            "integrity-inventory.alvo.json",
            [_clerk],
            new AlvoApiWorldSetup(
                MapAlvoProblemDetails: true,
                MapManagementApi: true,
                ConfigureServicesAfterAlvo: clock is null ? null : services => services.AddSingleton<TimeProvider>(clock),
                ConfigureAlvo: configureAlvo,
                ConfigureDataApiRoutes: routes),
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

    private static async Task<Guid> FirstIntegrityIdAsync(AlvoApiWorld world, string entity)
    {
        using var response = await world.SendAsync(HttpMethod.Get, $"/api/{entity}?limit=1", _clerk);
        return (await response.ReadItemsAsync())[0]["id"]!.GetValue<Guid>();
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
