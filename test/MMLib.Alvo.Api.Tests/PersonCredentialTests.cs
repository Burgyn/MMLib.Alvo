using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Spec §5.7 over HTTP, with a person resolver registered: a Bearer token is the person resolver's on the Data API
/// and the command gate, never on the Management API, and never beside an API key.
/// </summary>
/// <remarks>
/// <see cref="TestPersonResolver.Calls"/> is the witness for every refusal: an ambiguous or malformed request is
/// answered before any resolver runs, and the Management API never hands a Bearer token to one — which a status code
/// alone could not show, since an admitted person and an anonymous caller are both refused by a gate that names
/// neither.
/// </remarks>
public sealed class PersonCredentialTests
{
    private const string Token = "person-token";

    private static readonly AlvoPrincipal _person = TestPersonResolver.Person("person:p", tenant: null);

    [Fact]
    public async Task A_bearer_token_is_resolved_by_the_person_resolver_with_the_requested_tenant()
    {
        var people = People();
        await using var world = await WorldAsync(people);

        using var response = await world.SendAsync(HttpMethod.Get, "/api/bookings", tenant: "t-1", headers: Bearer(Token));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        people.Calls.ShouldBe([(Token, "t-1")]);
        world.PublishedPrincipals.ShouldContain(principal => principal != null && principal.KeyId == _person.KeyId);
    }

    [Fact]
    public async Task A_bearer_token_the_resolver_refuses_is_401_with_a_bearer_challenge()
    {
        await using var world = await WorldAsync(People());

        using var response = await world.SendAsync(HttpMethod.Get, "/api/bookings", headers: Bearer("nobody"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Unauthenticated);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        challenge.ShouldContain("Bearer");
        challenge.ShouldContain("invalid_token");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldNotContain("nobody", Case.Sensitive, "a bearer token is a secret and never echoed");
    }

    [Fact]
    public async Task A_malformed_bearer_is_401_and_reaches_no_resolver()
    {
        var people = People();
        await using var world = await WorldAsync(people);

        using var response = await world.SendAsync(HttpMethod.Get, "/api/bookings", headers: [new("Authorization", "Bearer")]);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("invalid_request");
        people.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_api_key_and_a_bearer_token_together_are_401_and_neither_is_resolved()
    {
        var people = People();
        await using var world = await WorldAsync(people);

        using var data = await world.SendAsync(HttpMethod.Get, "/api/bookings", CommandWorld.Staff, headers: Bearer(Token));
        using var command = await world.SendAsync(
            HttpMethod.Post, "/commands/quote", CommandWorld.Staff, body: Nights(2), headers: Bearer(Token));

        data.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var challenge = data.Headers.WwwAuthenticate.ToString();
        challenge.ShouldContain("AlvoApiKey");
        challenge.ShouldContain("Bearer");
        command.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the command gate reads the same way");
        people.Calls.ShouldBeEmpty("an ambiguous credential is never resolved by precedence");
    }

    /// <summary>
    /// An API key beside a <em>malformed</em> Bearer is still two credentials: ambiguous, not an API-key request with
    /// some noise beside it — a broken Bearer must not be read as "no Bearer" and let the key through.
    /// </summary>
    [Theory]
    [InlineData("Bearer")]
    [InlineData("Bearer a b")]
    public async Task An_api_key_and_a_malformed_bearer_together_are_401_and_neither_is_resolved(string authorization)
    {
        var people = People();
        await using var world = await WorldAsync(people);

        using var response = await world.SendAsync(
            HttpMethod.Get, "/api/bookings", CommandWorld.Staff, headers: [new("Authorization", authorization)]);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the staff key alone would read the bookings");
        var challenge = response.Headers.WwwAuthenticate.ToString();
        challenge.ShouldContain("AlvoApiKey");
        challenge.ShouldContain("Bearer");
        people.Calls.ShouldBeEmpty();
    }

    /// <summary>Two <c>Authorization</c> lines with a Bearer among them are malformed over HTTP too, never "take the first".</summary>
    [Fact]
    public async Task Two_authorization_lines_with_a_bearer_are_401_and_reach_no_resolver()
    {
        var people = People();
        await using var world = await WorldAsync(people);

        using var response = await world.SendAsync(
            HttpMethod.Get, "/api/bookings", headers: [new("Authorization", $"Bearer {Token}"), new("Authorization", "Bearer other")]);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the first line alone names an admitted person");
        response.Headers.WwwAuthenticate.ToString().ShouldContain("invalid_request");
        people.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_command_runs_as_the_person_a_bearer_token_names()
    {
        var people = People();
        await using var world = await WorldAsync(people);

        using var response = await world.SendAsync(HttpMethod.Post, "/commands/quote", body: Nights(2), headers: Bearer(Token));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        people.Calls.Select(call => call.Presented).ShouldBe([Token]);
    }

    [Fact]
    public async Task The_management_api_never_reads_a_bearer_token()
    {
        var people = People();
        await using var world = await WorldAsync(people);

        using var bearerOnly = await world.SendAsync(HttpMethod.Get, "/management/info", headers: Bearer(Token));
        using var nothing = await world.SendAsync(HttpMethod.Get, "/management/info");
        using var keyAndBearer = await world.SendAsync(HttpMethod.Get, "/management/info", CommandWorld.Staff, headers: Bearer(Token));
        using var keyOnly = await world.SendAsync(HttpMethod.Get, "/management/info", CommandWorld.Staff);

        bearerOnly.StatusCode.ShouldBe(nothing.StatusCode, "a bearer token alone is no credential to Management");
        keyAndBearer.StatusCode.ShouldBe(keyOnly.StatusCode, "beside a key, Management judges the key alone — never 401");
        keyAndBearer.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
        people.Calls.ShouldBeEmpty();
    }

    /// <summary>
    /// Review Focus 2: a host whose API-key header is <c>Authorization</c> keeps it on the API-key channel even with
    /// a person resolver registered — the person resolver is never handed it.
    /// </summary>
    [Fact]
    public async Task An_api_key_header_named_authorization_keeps_the_header()
    {
        var people = People();
        await using var world = await WorldAsync(people, services => services.Configure<AlvoAuthOptions>(
            new ConfigurationBuilder()
                .AddInMemoryCollection([new("HeaderName", "Authorization")])
                .Build()));

        using var response = await world.SendAsync(HttpMethod.Get, "/api/bookings", CommandWorld.Staff);
        using var bearerShaped = await world.SendAsync(HttpMethod.Get, "/api/bookings", headers: Bearer(Token));

        world.CredentialHeaderName.ShouldBe("Authorization", "or this world never presented its key in Authorization");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        bearerShaped.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        bearerShaped.Headers.WwwAuthenticate.ToString().ShouldBe(
            "AlvoApiKey header=\"Authorization\"",
            "a Bearer-shaped value in the API-key header is an API key that cannot be used, not a person or an ambiguity");
        people.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_non_bearer_scheme_is_ignored_even_with_a_person_resolver()
    {
        var people = People();
        await using var world = await WorldAsync(people);

        using var basic = await world.SendAsync(
            HttpMethod.Post, "/commands/quote", body: Nights(2), headers: [new("Authorization", "Basic dXNlcjpwYXNz")]);
        using var nothing = await world.SendAsync(HttpMethod.Post, "/commands/quote", body: Nights(2));

        basic.StatusCode.ShouldBe(nothing.StatusCode);
        basic.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
        people.Calls.ShouldBeEmpty();
    }

    private static TestPersonResolver People() => new((Token, _person));

    private static Task<AlvoApiWorld> WorldAsync(
        TestPersonResolver people, Action<IServiceCollection>? afterAlvo = null) =>
        AlvoApiWorld.FromDescriptorAsync(
            "command-desk.alvo.json",
            [CommandWorld.Staff],
            new AlvoApiWorldSetup(
                ConfigureServices: people.Register,
                ConfigureServicesAfterAlvo: afterAlvo,
                ConfigureApp: app => app.MapAlvoCommand(
                    "quote_stay", "/commands/quote", "'authenticated' in @user.roles", () => TypedResults.Ok()),
                MapManagementApi: true));

    private static KeyValuePair<string, string>[] Bearer(string token) => [new("Authorization", $"Bearer {token}")];

    private static JsonObject Nights(int nights) => new() { ["nights"] = nights };
}
