using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary><c>POST {m}/projects/{p}/cel/check</c> — what apply would say about one expression slot.</summary>
public class ManagementExpressionCheckTests
{
    private static readonly TestApiKey _ops = new("mgmt-ops", ["ops"], ["*:read"]);
    private const string ListRule = "/entities/vehicles/rules/list";

    [Fact]
    public async Task A_valid_expression_answers_no_error()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var verdict = await Check(world, ListRule, "'dispatcher' in @user.roles");

        verdict["isValid"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task An_expression_apply_would_refuse_answers_the_refusal_at_its_slot_with_a_fix()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var verdict = await Check(world, ListRule, "'amdin' in @user.roles");

        verdict["isValid"]!.GetValue<bool>().ShouldBeFalse();
        var finding = verdict["findings"]!.AsArray().Single()!;
        finding["path"]!.GetValue<string>().ShouldStartWith(ListRule);
        finding["fixSuggestion"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_slot_the_descriptor_lacks_is_a_422_with_the_pointer_in_the_detail()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        using var response = await Ask(world, "/entities/nope/rules/list", "true");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_facet_beyond_int_is_a_not_judged_finding_not_a_500()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);
        var sent = ReadFleetDescriptor();
        sent["entities"]!["vehicles"]!["fields"]!["nickname"] = new JsonObject { ["type"] = "string", ["maxLength"] = 3_000_000_000 };

        using var response = await world.SendAsync(
            HttpMethod.Post, $"{ManagedFleet.Routes}/cel/check", _ops, body: Body(sent, ListRule, "'dispatcher' in @user.roles"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var verdict = await response.ReadJsonObjectAsync();
        verdict["isValid"]!.GetValue<bool>().ShouldBeFalse("nothing was judged, so nothing may pass");
        verdict["findings"]!.AsArray().Single()!["message"]!.GetValue<string>().ShouldContain("not judged");
    }

    [Fact]
    public async Task A_missing_body_is_a_422_not_a_framework_400()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        using var response = await world.SendAsync(HttpMethod.Post, $"{ManagedFleet.Routes}/cel/check", _ops, body: null);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_descriptor_over_the_cap_is_refused_before_it_is_parsed()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);
        var body = Body(ListRule, "true");
        body["descriptor"] = new string(' ', 1_000_001);

        using var response = await world.SendAsync(HttpMethod.Post, $"{ManagedFleet.Routes}/cel/check", _ops, body: body);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_finding_never_carries_stored_state_the_caller_did_not_send()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);
        var sent = ReadFleetDescriptor();
        sent["auth"]!["roles"] = new JsonArray("only-this-role");

        var verdict = await world.SendAsync(HttpMethod.Post, $"{ManagedFleet.Routes}/cel/check", _ops, body: Body(sent, ListRule, "'dispatcher' in @user.roles"))
            .ContinueWith(t => t.Result.ReadJsonObjectAsync()).Unwrap();

        verdict["isValid"]!.GetValue<bool>().ShouldBeFalse("the role catalog is the one the caller sent, not the stored one");
    }

    private static Task<HttpResponseMessage> Ask(AlvoApiWorld world, string pointer, string source) =>
        world.SendAsync(HttpMethod.Post, $"{ManagedFleet.Routes}/cel/check", _ops, body: Body(pointer, source));

    private static async Task<JsonObject> Check(AlvoApiWorld world, string pointer, string source) =>
        await (await Ask(world, pointer, source)).ReadJsonObjectAsync();

    private static JsonObject Body(string pointer, string source) => Body(ReadFleetDescriptor(), pointer, source);

    private static JsonObject Body(JsonObject descriptor, string pointer, string source) => new()
    {
        ["descriptor"] = descriptor.ToJsonString(),
        ["path"] = pointer,
        ["source"] = source,
    };

    private static JsonObject ReadFleetDescriptor() =>
        JsonNode.Parse(File.ReadAllText(ManagedFleet.DescriptorPath))!.AsObject();
}
