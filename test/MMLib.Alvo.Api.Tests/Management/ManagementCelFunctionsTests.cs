using MMLib.Alvo.Expressions;
using System.Net;

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

        var functions = await (await world.SendAsync(HttpMethod.Get, $"{ManagedFleet.Routes}/cel/functions", _ops)).ReadJsonArrayAsync();

        functions.Select(f => f!["name"]!.GetValue<string>()).Distinct().ShouldBe(["abs", "lowerAscii", "now", "replace", "round", "size", "trim"]);
        functions.Count(f => f!["name"]!.GetValue<string>() == "abs").ShouldBe(2);
        var trim = functions.Single(f => f!["name"]!.GetValue<string>() == "trim")!;
        trim["result"]!.GetValue<string>().ShouldBe("String");
        trim["provenance"]!.GetValue<string>().ShouldBe("BuiltIn");
        trim["profiles"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldBe(["Condition", "Mutate"]);
        trim["parameters"]![0]!["acceptsNull"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task An_unknown_project_is_a_404()
    {
        await using var world = await ManagedFleet.StartAsync([_ops]);

        using var response = await world.SendAsync(HttpMethod.Get, "/management/projects/nope/cel/functions", _ops);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_host_function_is_listed_as_the_host_registered_it()
    {
        await using var world = await CelFunctionsWorld.StartAsync(phone => phone);
        var management = ManagementInProcessAccessTests.Publish(world, "writer");

        var host = (await management.GetCelFunctionsAsync(CelFunctionsWorld.Project, Ct)).Single(f => f.Provenance == CelFunctionProvenance.Host);

        host.Name.ShouldBe("normalizePhone");
        host.Summary.ShouldBe(CelFunctionsWorld.Summary);
        host.Parameters.ShouldHaveSingleItem().Name.ShouldBe("phone");
        host.Result.ShouldBe(CelValueType.String);
        host.Profiles.ShouldBe([CelProfile.Condition, CelProfile.Mutate]);
    }
}
