using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Ai;
using System.Net;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// <c>GET {m}/info</c>'s <c>ai.keyState</c> over the wire — the field Settings reads to stop saying "connected"
/// for a connection every request to which is refused (docs/todo-admin.md §8d item 31).
/// </summary>
/// <remarks>
/// The unit facts hold the resolver to the state; this holds the <em>serialisation</em>, because the wire is the
/// only layer a client reads, and an enum serialised as its number would be a contract nobody could branch on.
/// </remarks>
public class ManagementInfoHttpTests
{
    private static readonly TestApiKey _ops = new("mgmt-ops", ["ops"], ["*:read"]);

    /// <summary>
    /// A reference to a secret this instance does not have reaches the wire as <c>missing</c>, beside
    /// <c>configured: true</c> — both halves, because the second is what used to be read alone.
    /// </summary>
    [Fact]
    public async Task A_reference_to_a_secret_nobody_saved_is_reported_as_a_missing_key()
    {
        var ai = await InfoAiAsync(new AlvoAiOptions
        {
            Kind = "openai-compatible",
            Endpoint = "https://api.openai.com/v1",
            Model = "gpt-5",
            ApiKeySecretRef = "openai.key",
        });

        ai["configured"]!.GetValue<bool>().ShouldBeTrue();
        ai["keyState"]!.GetValue<string>().ShouldBe("missing");
    }

    /// <summary>A keyless local endpoint is reported as needing none, in the wire's camel case.</summary>
    [Fact]
    public async Task A_local_endpoint_with_no_reference_is_reported_as_needing_no_key()
    {
        var ai = await InfoAiAsync(new AlvoAiOptions
        {
            Kind = "openai-compatible",
            Endpoint = "http://localhost:11434/v1",
            Model = "qwen3:8b",
        });

        ai["keyState"]!.GetValue<string>().ShouldBe("notNeeded");
    }

    /// <summary>No connection reports <c>none</c>, never a missing key it was never asked to have.</summary>
    [Fact]
    public async Task No_connection_is_reported_as_no_key_state()
    {
        var ai = await InfoAiAsync(new AlvoAiOptions());

        ai["configured"]!.GetValue<bool>().ShouldBeFalse();
        ai["keyState"]!.GetValue<string>().ShouldBe("none");
    }

    /// <summary>The <c>ai</c> object a caller the access block names reads, under the given configuration.</summary>
    private static async Task<System.Text.Json.Nodes.JsonObject> InfoAiAsync(AlvoAiOptions configured)
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync(
            "managed-notes.alvo.json",
            [_ops],
            new AlvoApiWorldSetup(
                ConfigureServices: services => services.Configure<AlvoAiOptions>(options =>
                {
                    options.Kind = configured.Kind;
                    options.Endpoint = configured.Endpoint;
                    options.Model = configured.Model;
                    options.ApiKeySecretRef = configured.ApiKeySecretRef;
                }),
                MapManagementApi: true));

        var response = await world.SendAsync(HttpMethod.Get, "/management/info", _ops);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.ReadJsonObjectAsync())["ai"]!.AsObject();
    }
}
