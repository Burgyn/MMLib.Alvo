using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary><c>POST {m}/projects/{p}/cel/check</c> — what apply would say about one expression slot.</summary>
public class ManagementExpressionCheckTests
{
    /// <summary>The check runs the validator over the caller's text exactly as apply does, so it takes apply's level.</summary>
    private static readonly TestApiKey _dev = new("mgmt-dev", ["dispatcher"], ["*:write"]);

    private static readonly TestApiKey _viewer = new("mgmt-ops", ["ops"], ["*:read"]);
    private const string ListRule = "/entities/vehicles/rules/list";

    [Fact]
    public async Task A_valid_expression_answers_no_error()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var verdict = await Check(world, ListRule, "'dispatcher' in @user.roles");

        verdict["isValid"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task A_viewer_is_refused_403_with_the_body_a_developer_is_answered()
    {
        await using var world = await ManagedFleet.StartAsync([_dev, _viewer]);
        var body = Body(ListRule, "'dispatcher' in @user.roles");

        using var asDeveloper = await world.SendAsync(HttpMethod.Post, $"{ManagedFleet.Routes}/cel/check", _dev, body: body);
        using var asViewer = await world.SendAsync(HttpMethod.Post, $"{ManagedFleet.Routes}/cel/check", _viewer, body: body);

        asDeveloper.StatusCode.ShouldBe(HttpStatusCode.OK);
        asViewer.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_expression_apply_would_refuse_answers_the_refusal_at_its_slot_with_a_fix()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        var verdict = await Check(world, ListRule, "'amdin' in @user.roles");

        verdict["isValid"]!.GetValue<bool>().ShouldBeFalse();
        var finding = verdict["findings"]!.AsArray().Single()!;
        finding["path"]!.GetValue<string>().ShouldStartWith(ListRule);
        finding["fixSuggestion"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_slot_the_descriptor_lacks_is_a_422_with_the_pointer_in_the_detail()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        using var response = await Ask(world, "/entities/nope/rules/list", "true");

        (await Refusal(response)).ShouldContain("/entities/nope/rules/list");
    }

    [Fact]
    public async Task An_unknown_project_is_404_before_a_missing_body_is_422()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        using var response = await world.SendAsync(
            HttpMethod.Post, "/management/projects/not-mine/cel/check", _dev, body: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_facet_beyond_int_is_a_not_judged_finding_not_a_500()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        var sent = ReadFleetDescriptor();
        sent["entities"]!["vehicles"]!["fields"]!["nickname"] = new JsonObject { ["type"] = "string", ["maxLength"] = 3_000_000_000 };

        using var response = await world.SendAsync(
            HttpMethod.Post, $"{ManagedFleet.Routes}/cel/check", _dev, body: Body(sent, ListRule, "'dispatcher' in @user.roles"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var verdict = await response.ReadJsonObjectAsync();
        verdict["isValid"]!.GetValue<bool>().ShouldBeFalse("nothing was judged, so nothing may pass");
        verdict["findings"]!.AsArray().Single()!["message"]!.GetValue<string>().ShouldStartWith("Not checked yet");
    }

    [Theory]
    [MemberData(nameof(LoneSurrogateInputs.Places), MemberType = typeof(LoneSurrogateInputs))]
    public async Task A_lone_surrogate_is_never_a_500(string where)
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        var text = LoneSurrogateInputs.At(ReadFleetDescriptor().ToJsonString(), where);
        text.ShouldContain("\\ud", Case.Insensitive, "the fixture really carries the escape");

        using var response = await world.SendAsync(
            HttpMethod.Post, $"{ManagedFleet.Routes}/cel/check", _dev,
            body: new JsonObject { ["descriptorJson"] = text, ["path"] = ListRule, ["source"] = "true" });

        (await Refusal(response)).ShouldContain("not valid Unicode");
    }

    [Fact]
    public async Task An_array_over_the_bound_is_a_422_naming_the_limit()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        var sent = ReadFleetDescriptor();
        sent["entities"]!["vehicles"]!["fields"]!["big"] = new JsonObject
        {
            ["type"] = "enum",
            ["values"] = new JsonArray([.. Enumerable.Range(0, 2001).Select(i => (JsonNode)JsonValue.Create($"v{i}")!)]),
        };

        using var response = await world.SendAsync(
            HttpMethod.Post, $"{ManagedFleet.Routes}/cel/check", _dev, body: Body(sent, ListRule, "true"));

        (await Refusal(response)).ShouldContain("2,000");
    }

    [Fact]
    public async Task A_missing_body_is_a_422_not_a_framework_400()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        using var response = await world.SendAsync(HttpMethod.Post, $"{ManagedFleet.Routes}/cel/check", _dev, body: null);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_descriptor_over_the_cap_is_refused_before_it_is_parsed()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        var body = Body(PaddedFleetDescriptor(1_000_001), ListRule, "true");

        using var response = await world.SendAsync(HttpMethod.Post, $"{ManagedFleet.Routes}/cel/check", _dev, body: body);

        (await Refusal(response)).ShouldContain("1,000,000");
    }

    [Fact]
    public async Task A_valid_descriptor_just_under_the_cap_is_parsed_and_judged_not_refused_for_size()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        var body = Body(PaddedFleetDescriptor(900_000), ListRule, "true");

        using var response = await world.SendAsync(HttpMethod.Post, $"{ManagedFleet.Routes}/cel/check", _dev, body: body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the padding is valid JSON, so only the cap can refuse the larger one");
    }

    /// <summary>The fleet descriptor, valid JSON, its text at least <paramref name="chars"/> long through one long string value.</summary>
    private static JsonObject PaddedFleetDescriptor(int chars)
    {
        var descriptor = ReadFleetDescriptor();
        descriptor["description"] = new string('x', chars);

        return descriptor;
    }

    [Fact]
    public async Task A_source_over_the_cap_is_refused_naming_the_cap()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        using var response = await Ask(world, ListRule, new string('a', 8001));

        (await Refusal(response)).ShouldContain("8,000");
    }

    [Fact]
    public async Task A_path_over_the_cap_is_refused_naming_the_cap()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);

        using var response = await Ask(world, "/entities/" + new string('p', 1100), "true");

        (await Refusal(response)).ShouldContain("1,024");
    }

    [Fact]
    public async Task A_refusal_does_not_echo_a_long_path_back_whole()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        var path = "/entities/" + new string('p', 900);

        using var response = await Ask(world, path, "true");

        var detail = await Refusal(response);
        detail.ShouldNotContain(path);
        detail.ShouldContain("…");
    }

    [Fact]
    public async Task A_finding_never_carries_stored_state_the_caller_did_not_send()
    {
        await using var world = await ManagedFleet.StartAsync([_dev]);
        var sent = ReadFleetDescriptor();
        sent["auth"]!["roles"] = new JsonArray("only-this-role");

        var verdict = await world.SendAsync(HttpMethod.Post, $"{ManagedFleet.Routes}/cel/check", _dev, body: Body(sent, ListRule, "'dispatcher' in @user.roles"))
            .ContinueWith(t => t.Result.ReadJsonObjectAsync()).Unwrap();

        verdict["isValid"]!.GetValue<bool>().ShouldBeFalse("the role catalog is the one the caller sent, not the stored one");
    }

    private static async Task<string> Refusal(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        return (await response.ReadJsonObjectAsync())["detail"]!.GetValue<string>();
    }

    private static Task<HttpResponseMessage> Ask(AlvoApiWorld world, string pointer, string source) =>
        world.SendAsync(HttpMethod.Post, $"{ManagedFleet.Routes}/cel/check", _dev, body: Body(pointer, source));

    private static async Task<JsonObject> Check(AlvoApiWorld world, string pointer, string source) =>
        await (await Ask(world, pointer, source)).ReadJsonObjectAsync();

    private static JsonObject Body(string pointer, string source) => Body(ReadFleetDescriptor(), pointer, source);

    private static JsonObject Body(JsonObject descriptor, string pointer, string source) => new()
    {
        ["descriptorJson"] = descriptor.ToJsonString(),
        ["path"] = pointer,
        ["source"] = source,
    };

    private static JsonObject ReadFleetDescriptor() =>
        JsonNode.Parse(File.ReadAllText(ManagedFleet.DescriptorPath))!.AsObject();
}
