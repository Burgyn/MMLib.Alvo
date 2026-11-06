using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Net;

namespace MMLib.Alvo.Api.Tests.Commands;

/// <summary>
/// A mapped command's access rule reads <c>now()</c> at the request's instant (HX-11 Task 2 review C1): command routes run
/// <c>CommandEndpointFilter</c>, not <c>AlvoCallerFilter</c>, so the filter binds the instant itself — a <c>now()</c> rule
/// answers its verdict, never a 500.
/// </summary>
public sealed class CommandNowAccessTests
{
    [Theory]
    [InlineData("now() == now()", HttpStatusCode.OK)]
    [InlineData("now() != now()", HttpStatusCode.Forbidden)]
    public async Task A_now_access_rule_on_an_http_command_answers_its_verdict(string access, HttpStatusCode expected)
    {
        await using var world = await AlvoApiWorld.FromDescriptorAsync("command-desk.alvo.json", [], new AlvoApiWorldSetup(
            ConfigureApp: app => app.MapAlvoCommand("quote_stay", "/commands/quote", access, () => TypedResults.Ok())));

        using var response = await world.SendAsync(HttpMethod.Post, "/commands/quote");

        response.StatusCode.ShouldBe(expected);
    }
}
