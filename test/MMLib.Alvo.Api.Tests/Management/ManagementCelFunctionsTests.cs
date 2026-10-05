using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Management;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary><c>GET {m}/projects/{p}/cel/functions</c> — every function a descriptor may call here, with its signature.</summary>
public sealed class ManagementCelFunctionsTests
{
    private static readonly TestApiKey _ops = new("mgmt-ops", ["ops"], ["*:read"]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_viewer_reads_the_built_ins_with_their_signatures_profiles_and_nullability()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var functions = await ReadFunctionsAsync(world);

        functions.Select(f => f["name"]!.GetValue<string>()).Distinct().ShouldBe(CelFunctionCatalog.BuiltIns.Names);
        functions.Count(f => f["name"]!.GetValue<string>() == "math.abs").ShouldBe(2);
        var trim = Named(functions, "trim");
        trim["result"]!.GetValue<string>().ShouldBe("String");
        trim["resultMayBeNull"]!.GetValue<bool>().ShouldBeFalse();
        trim["summary"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        trim["provenance"]!.GetValue<string>().ShouldBe("BuiltIn");
        trim["profiles"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldBe(["Condition", "Mutate"]);
        trim["parameters"]![0]!["acceptsNull"]!.GetValue<bool>().ShouldBeFalse();
        trim["parameters"]![0]!["type"]!.GetValue<string>().ShouldBe("String");
        Named(functions, "lowerAscii")["provenance"]!.GetValue<string>().ShouldBe("BuiltIn");
        Named(functions, "now")["provenance"]!.GetValue<string>().ShouldBe("BuiltIn");
    }

    [Fact]
    public async Task The_answer_is_an_object_so_it_can_grow_without_a_breaking_change()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        var body = await (await world.SendAsync(HttpMethod.Get, $"{ManagedFleet.Routes}/cel/functions", _ops)).ReadJsonObjectAsync();

        body["functions"].ShouldBeOfType<JsonArray>();
    }

    [Fact]
    public async Task An_unknown_project_is_a_404()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        using var response = await world.SendAsync(HttpMethod.Get, "/management/projects/nope/cel/functions", _ops);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_anonymous_caller_the_access_block_names_nowhere_is_403_for_an_existing_project_and_403_before_404_for_a_missing_one()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        using var existing = await world.SendAsync(HttpMethod.Get, $"{ManagedFleet.Routes}/cel/functions");
        using var missing = await world.SendAsync(HttpMethod.Get, "/management/projects/nope/cel/functions");

        existing.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        missing.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "authorization is decided before the project is looked up");
    }

    [Fact]
    public async Task A_host_function_is_listed_as_the_host_registered_it()
    {
        await using var world = await CelFunctionsWorld.StartAsync(phone => phone);
        var management = ManagementInProcessAccessTests.Publish(world, "writer");

        var answer = await management.GetCelFunctionsAsync(CelFunctionsWorld.Project, Ct);
        var host = answer.Functions.Single(f => f.Provenance == CelFunctionProvenance.Host);

        host.Name.ShouldBe("normalizePhone");
        host.Summary.ShouldBe(CelFunctionsWorld.Summary);
        host.Parameters.ShouldHaveSingleItem().Name.ShouldBe("phone");
        host.Result.ShouldBe(CelValueType.String);
        host.Profiles.ShouldBe([CelProfile.Condition, CelProfile.Mutate]);
        var wire = JsonSerializer.SerializeToNode(host, JsonSerializerOptions.Web)!;
        wire["provenance"]!.GetValue<string>().ShouldBe("Host");
        wire["profiles"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldBe(["Condition", "Mutate"]);
        wire["summary"]!.GetValue<string>().ShouldBe(CelFunctionsWorld.Summary);
        wire["resultMayBeNull"].ShouldNotBeNull();
        wire["parameters"]![0]!["acceptsNull"].ShouldNotBeNull();
    }

    private static async Task<IReadOnlyList<JsonObject>> ReadFunctionsAsync(AlvoApiWorld world)
    {
        var body = await (await world.SendAsync(HttpMethod.Get, $"{ManagedFleet.Routes}/cel/functions", _ops)).ReadJsonObjectAsync();

        return [.. body["functions"]!.AsArray().Select(f => f!.AsObject())];
    }

    private static JsonObject Named(IReadOnlyList<JsonObject> functions, string name) =>
        functions.First(f => f["name"]!.GetValue<string>() == name);
}
