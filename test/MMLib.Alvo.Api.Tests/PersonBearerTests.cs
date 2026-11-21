using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The one reading every person-only route shares (#542 item 2): a usable Bearer token is the person's, and every other
/// reading is a 401 whose challenge names Bearer — an API key of any kind included, because on these routes it is not an
/// authentication scheme at all (RFC 6750 §3.1, "attempted using an unsupported authentication method").
/// </summary>
public sealed class PersonBearerTests
{
    private const string PersonOnlyDetail =
        "This route answers a signed-in person only. Present the access token sign-in issued as 'Authorization: Bearer "
        + "<token>'; an API key, a personal token or a service token does not reach it.";

    private static readonly AlvoAuthOptions _options = new();

    private const string NoKey = "(none)";

    public static TheoryData<string> Keys() => new()
    {
        "integration.secret",
        "alvo_pat_q8.secret",
        "alvo_svc_7f.secret",
        "made-up",
        NoKey,
    };

    [Fact]
    public void A_usable_bearer_token_is_the_person_s()
    {
        var (token, refusal) = CallerResolution.PersonBearer(Request(null, ["Bearer t"]), _options, personRegistered: true);

        (token, refusal).ShouldBe(("t", null));
    }

    [Theory, MemberData(nameof(Keys))]
    public async Task Any_api_key_or_none_is_the_one_bare_bearer_challenge(string key)
    {
        var presented = key == NoKey ? null : key;

        var (token, refusal) = CallerResolution.PersonBearer(Request(presented, []), _options, personRegistered: true);

        token.ShouldBeNull();
        var (status, challenge, document) = await WrittenAsync(refusal!);
        (status, challenge).ShouldBe((StatusCodes.Status401Unauthorized, "Bearer"));
        document["type"]!.GetValue<string>().ShouldBe(AlvoProblemTypes.UriOf(AlvoProblemTypes.Unauthenticated));
        document["detail"]!.GetValue<string>().ShouldBe(PersonOnlyDetail);
    }

    [Fact]
    public async Task A_key_beside_a_bearer_names_both_and_asks_for_the_bearer_alone()
    {
        var (token, refusal) = CallerResolution.PersonBearer(Request("k", ["Bearer t"]), _options, personRegistered: true);

        token.ShouldBeNull();
        var (status, challenge, document) = await WrittenAsync(refusal!);
        (status, challenge).ShouldBe((StatusCodes.Status401Unauthorized, "Bearer error=\"invalid_request\""));
        document["detail"]!.GetValue<string>().ShouldBe(
            $"The request presents both {_options.HeaderName} and a bearer token. This route answers a signed-in person: "
            + "present only the bearer token.");
    }

    [Fact]
    public async Task A_malformed_bearer_is_invalid_request()
    {
        var (_, refusal) = CallerResolution.PersonBearer(Request(null, ["Bearer a b"]), _options, personRegistered: true);

        var (status, challenge, _) = await WrittenAsync(refusal!);
        (status, challenge).ShouldBe((StatusCodes.Status401Unauthorized, "Bearer error=\"invalid_request\""));
    }

    [Fact]
    public async Task An_app_token_as_a_bearer_names_the_api_key_header()
    {
        var (_, refusal) = CallerResolution.PersonBearer(Request(null, ["Bearer alvo_svc_7f.secret"]), _options, personRegistered: true);

        var (status, challenge, document) = await WrittenAsync(refusal!);
        (status, challenge).ShouldBe((StatusCodes.Status401Unauthorized, "Bearer error=\"invalid_token\""));
        document["detail"]!.GetValue<string>().ShouldContain(_options.HeaderName);
    }

    [Fact]
    public async Task A_bearer_resolving_to_no_person_is_403_and_names_no_route()
    {
        var (status, challenge, document) = await WrittenAsync(ProblemResultFactory.NotAPerson());

        (status, challenge).ShouldBe((StatusCodes.Status403Forbidden, string.Empty));
        document["type"]!.GetValue<string>().ShouldBe(AlvoProblemTypes.UriOf(AlvoProblemTypes.Forbidden));
        document["detail"]!.GetValue<string>().ShouldBe(
            "This route answers a signed-in person, and the bearer token names a caller that is not one.");
    }

    private static HttpRequest Request(string? key, string[] authorization)
    {
        var request = new DefaultHttpContext().Request;
        if (authorization.Length > 0)
        {
            request.Headers.Authorization = new StringValues(authorization);
        }

        if (key is not null)
        {
            request.Headers[_options.HeaderName] = key;
        }

        return request;
    }

    private static async Task<(int Status, string Challenge, JsonObject Document)> WrittenAsync(IResult result)
    {
        await using var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        body.Position = 0;
        var document = await JsonSerializer.DeserializeAsync<JsonObject>(body, cancellationToken: TestContext.Current.CancellationToken);
        return (context.Response.StatusCode, context.Response.Headers.WWWAuthenticate.ToString(), document!);
    }
}
