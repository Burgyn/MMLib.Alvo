using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>A chain-depth refusal is RFC 5842's 508, and says it must not be retried (spec 2026-10-14 X17, HX-01 acceptance 4).</summary>
public class ChainDepthProblemTests
{
    [Fact]
    public async Task A_chain_depth_refusal_is_508_with_its_own_type_and_marked_non_retryable()
    {
        var result = await ProblemResultFactory.GuardAsync(() => throw new AlvoChainDepthExceededException("too deep"));

        var (status, document) = await WrittenAsync(result);

        status.ShouldBe(StatusCodes.Status508LoopDetected);
        document["type"]!.GetValue<string>().ShouldBe(AlvoProblemTypes.UriOf(AlvoProblemTypes.ChainDepthExceeded));
        document["retryable"]!.GetValue<bool>().ShouldBeFalse();
        document["detail"]!.GetValue<string>().ShouldBe("too deep");
    }

    private static async Task<(int Status, JsonObject Document)> WrittenAsync(IResult result)
    {
        await using var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        body.Position = 0;
        var document = await JsonSerializer.DeserializeAsync<JsonObject>(body, cancellationToken: TestContext.Current.CancellationToken);
        return (context.Response.StatusCode, document!);
    }
}
