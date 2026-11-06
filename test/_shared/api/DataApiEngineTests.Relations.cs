using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// A relation rule over HTTP, on every engine: an order is listed, counted, read, patched and placed only through a
/// customer the caller may list, and a hidden one answers exactly as a row an owner rule hides.
/// </summary>
public abstract partial class DataApiEngineTests
{
    private static readonly Guid _relationsTenant = Guid.NewGuid();

    private static readonly string[] _relationScopes =
    [
        "customers:read", "customers:write", "orders:read", "orders:write", "products:read", "products:write",
        "order_lines:read", "order_lines:write",
    ];

    private static readonly TestApiKey _relationsClerk = new("relations-clerk", ["clerk"], _relationScopes, _relationsTenant);

    private static readonly TestApiKey _relationsAlice = new("relations-alice", ["member"], _relationScopes, _relationsTenant);

    private static readonly TestApiKey _relationsBob = new("relations-bob", ["member"], _relationScopes, _relationsTenant);

    [Fact]
    public async Task A_relation_rule_scopes_list_get_count_and_patch_over_http_on_every_engine()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync(
            "relations-desk.alvo.json", [_relationsClerk, _relationsAlice, _relationsBob]);
        var aliceOrder = await CreateRelatedOrderAsync(world, _relationsAlice);
        var bobOrder = await CreateRelatedOrderAsync(world, _relationsBob);

        using var page = await world.SendAsync(
            HttpMethod.Get, "/api/orders", _relationsAlice, headers: new Dictionary<string, string> { ["Prefer"] = "count=exact" });
        using var hidden = await world.SendAsync(HttpMethod.Get, $"/api/orders/{bobOrder}", _relationsAlice);
        using var patched = await world.SendAsync(
            HttpMethod.Patch, $"/api/orders/{bobOrder}", _relationsAlice, body: new JsonObject { ["note"] = "x" });
        using var own = await world.SendAsync(
            HttpMethod.Patch, $"/api/orders/{aliceOrder}", _relationsAlice, body: new JsonObject { ["note"] = "mine" });
        var aliceCustomer = (await own.ReadJsonObjectAsync())["customer_id"]!.GetValue<Guid>();
        using var placed = await world.SendAsync(HttpMethod.Post, "/api/orders", _relationsBob, body: new JsonObject
        {
            ["customer_id"] = aliceCustomer,
            ["note"] = "not mine",
            ["tenant_id"] = _relationsTenant,
        });

        page.StatusCode.ShouldBe(HttpStatusCode.OK, await page.ReadTextAsync());
        var body = await page.ReadJsonObjectAsync();
        body["items"]!.AsArray().Select(row => row!["id"]!.GetValue<Guid>()).ShouldBe([aliceOrder]);
        body["count"]!.GetValue<long>().ShouldBe(1);
        hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound, await hidden.ReadTextAsync());
        patched.StatusCode.ShouldBe(HttpStatusCode.NotFound, await patched.ReadTextAsync());
        own.StatusCode.ShouldBe(HttpStatusCode.OK, await own.ReadTextAsync());
        placed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await placed.ReadTextAsync());
        (await placed.ReadJsonObjectAsync())["violations"]![0]!["code"]!.GetValue<string>().ShouldBe("unresolved-reference");
    }

    [Fact]
    public async Task A_failing_lookup_check_answers_422_with_its_message_and_pointer_on_every_engine()
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync(
            "relations-desk.alvo.json", [_relationsClerk, _relationsAlice, _relationsBob]);
        using var product = await world.SendAsync(HttpMethod.Post, "/api/products", _relationsClerk, body: new JsonObject
        {
            ["active"] = false,
            ["stock"] = 5,
            ["tenant_id"] = _relationsTenant,
        });
        product.StatusCode.ShouldBe(HttpStatusCode.Created, await product.ReadTextAsync());
        var events = await world.CountRowsAsync("alvo_outbox");

        using var line = await world.SendAsync(HttpMethod.Post, "/api/order_lines", _relationsAlice, body: new JsonObject
        {
            ["product_id"] = (await product.ReadJsonObjectAsync())["id"]!.GetValue<Guid>(),
            ["quantity"] = 1,
            ["tenant_id"] = _relationsTenant,
        });

        line.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await line.ReadTextAsync());
        var problem = await line.ReadJsonObjectAsync();
        problem["type"]!.GetValue<string>().ShouldEndWith("validation");
        var violation = problem["violations"]!.AsArray().ShouldHaveSingleItem()!;
        violation["pointer"]!.GetValue<string>().ShouldBe("/product_id");
        violation["code"]!.GetValue<string>().ShouldBe("check");
        violation["message"]!.GetValue<string>().ShouldBe("This product is not available in that quantity.");
        violation["fixSuggestion"]!.GetValue<string>().ShouldBe(
            "Point 'product_id' at a record that satisfies this check, or change the values the check compares.");
        (await world.CountRowsAsync("order_lines")).ShouldBe(0);
        (await world.CountRowsAsync("alvo_outbox")).ShouldBe(events, "a refused write emits no event");
    }

    private static async Task<Guid> CreateRelatedOrderAsync(AlvoApiWorld world, TestApiKey owner)
    {
        using var customer = await world.SendAsync(HttpMethod.Post, "/api/customers", _relationsClerk, body: new JsonObject
        {
            ["owner_id"] = owner.User,
            ["active"] = true,
            ["tenant_id"] = _relationsTenant,
        });
        customer.StatusCode.ShouldBe(HttpStatusCode.Created, await customer.ReadTextAsync());
        using var order = await world.SendAsync(HttpMethod.Post, "/api/orders", owner, body: new JsonObject
        {
            ["customer_id"] = (await customer.ReadJsonObjectAsync())["id"]!.GetValue<Guid>(),
            ["note"] = "order",
            ["tenant_id"] = _relationsTenant,
        });
        order.StatusCode.ShouldBe(HttpStatusCode.Created, await order.ReadTextAsync());
        return (await order.ReadJsonObjectAsync())["id"]!.GetValue<Guid>();
    }
}
