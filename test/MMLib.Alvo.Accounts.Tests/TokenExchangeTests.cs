using Microsoft.IdentityModel.JsonWebTokens;
using MMLib.Alvo.Accounts.Internal;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Tests.Accounts;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// Signing in by token exchange over HTTP (spec §5.2, D4; S2 D10, D14): the RFC 8693 answer, single use, every RFC 6749
/// refusal as a problem document carrying <c>error</c>, and the descriptor's providers and sign-up policy.
/// </summary>
public sealed class TokenExchangeTests
{
    private const string GrantType = "urn:ietf:params:oauth:grant-type:token-exchange";

    private const string IdTokenType = "urn:ietf:params:oauth:token-type:id_token";

    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";

    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    [Fact]
    public async Task A_first_exchange_creates_the_account_and_answers_rfc_8693()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);

        using var response = await world.ExchangeAsync(world.IdToken("sub-1"));

        var body = await response.ReadJsonObjectAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        body.Select(member => member.Key).ShouldBe(["access_token", "issued_token_type", "token_type", "expires_in", "refresh_token"], ignoreOrder: true);
        body["issued_token_type"]!.GetValue<string>().ShouldBe(AccessTokenType);
        body["token_type"]!.GetValue<string>().ShouldBe("Bearer");
        body["expires_in"]!.GetValue<int>().ShouldBe(900);
        body["refresh_token"]!.GetValue<string>().ShouldStartWith("alvo_rt_");
        ShouldNotBeStored(response);
        var access = new JsonWebToken(body["access_token"]!.GetValue<string>());
        access.Audiences.ShouldBe(["time-tracker"]);
        access.GetPayloadValue<string>("idp").ShouldBe("google");
        (await world.CountAsync("_auth_accounts")).ShouldBe(1);
    }

    [Fact]
    public async Task A_later_exchange_signs_into_the_same_account()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);

        var first = await world.SignInAsync("sub-1");
        var second = await world.SignInAsync("sub-1", email: "ada.lovelace@gmail.com");

        new JsonWebToken(second.Access).Subject.ShouldBe(new JsonWebToken(first.Access).Subject);
        new JsonWebToken(second.Access).GetPayloadValue<string>("sid").ShouldNotBe(new JsonWebToken(first.Access).GetPayloadValue<string>("sid"));
        (await world.CountAsync("_auth_accounts")).ShouldBe(1);
        (await world.CountAsync("_auth_sessions")).ShouldBe(2);
    }

    [Fact]
    public async Task A_replayed_id_token_starts_no_second_session()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var idToken = world.IdToken("sub-1");

        using var first = await world.ExchangeAsync(idToken);
        using var replayed = await world.ExchangeAsync(idToken);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var problem = await RefusalAsync(replayed, HttpStatusCode.BadRequest, AlvoProblemTypes.TokenRefused, "invalid_request");
        problem["error_description"]!.GetValue<string>().ShouldBe(TokenProblems.SubjectTokenDescription);
        (await world.CountAsync("_auth_sessions")).ShouldBe(1);
    }

    [Theory]
    [InlineData("a JSON body", "invalid_request")]
    [InlineData("no grant_type", "invalid_request")]
    [InlineData("an access token as the subject token type", "invalid_request")]
    [InlineData("no subject_token", "invalid_request")]
    [InlineData("a requested token type other than an access token", "invalid_request")]
    [InlineData("grant_type=password", "unsupported_grant_type")]
    [InlineData("subject_token twice", "invalid_request")]
    [InlineData("grant_type twice", "invalid_request")]
    [InlineData("audience=x", "invalid_target")]
    [InlineData("resource=x", "invalid_target")]
    [InlineData("a 17 KiB body", "invalid_request")]
    public async Task A_malformed_request_is_an_rfc_6749_error(string defect, string error)
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var idToken = world.IdToken("sub-1");

        using var response = await world.TokenAsync(Malformed(defect, idToken));

        var problem = await RefusalAsync(response, HttpStatusCode.BadRequest, AlvoProblemTypes.TokenRefused, error);
        problem.ToJsonString().ShouldNotContain(idToken.Split('.')[1]);
        (await world.CountAsync("_auth_sessions")).ShouldBe(0);
    }

    [Fact]
    public async Task An_issuer_the_host_did_not_configure_is_invalid_request()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);

        using var response = await world.ExchangeAsync(world.Oidc.Mint("sub-1", audience: AccountsWorld.ClientId));

        var problem = await RefusalAsync(response, HttpStatusCode.BadRequest, AlvoProblemTypes.TokenRefused, "invalid_request");
        problem["error_description"]!.GetValue<string>().ShouldBe(TokenProblems.SubjectTokenDescription);
    }

    [Theory]
    [InlineData("iss null")]
    [InlineData("iss empty")]
    [InlineData("no iss")]
    public async Task A_subject_token_without_an_issuer_is_invalid_request_not_a_500(string defect)
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var subjectToken = defect switch
        {
            "iss null" => IssuerDefects.Null,
            "iss empty" => IssuerDefects.Empty,
            _ => IssuerDefects.Missing,
        };

        using var response = await world.ExchangeAsync(subjectToken);

        var problem = await RefusalAsync(response, HttpStatusCode.BadRequest, AlvoProblemTypes.TokenRefused, "invalid_request");
        problem["error_description"]!.GetValue<string>().ShouldBe(TokenProblems.SubjectTokenDescription);
        (await world.CountAsync("_auth_sessions")).ShouldBe(0);
    }

    [Fact]
    public async Task A_provider_the_descriptor_does_not_list_is_invalid_request()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, descriptor: "time-tracker-no-providers.alvo.json");

        using var response = await world.ExchangeAsync(world.IdToken("sub-1"));

        var problem = await RefusalAsync(response, HttpStatusCode.BadRequest, AlvoProblemTypes.TokenRefused, "invalid_request");
        problem["error_description"]!.GetValue<string>().ShouldBe(TokenProblems.SubjectTokenDescription);
        (await world.CountAsync("_auth_accounts")).ShouldBe(0);
        world.Oidc.Requests.ShouldBe(0, "a provider nobody may sign in through costs no discovery fetch (spec §5.2 step 1)");
    }

    [Theory]
    [InlineData("audience")]
    [InlineData("resource")]
    [InlineData("requested_token_type")]
    public async Task An_empty_parameter_counts_as_absent(string name)
    {
        await using var world = await AccountsWorld.StartAsync(_engine);

        using var response = await world.TokenAsync(Exchange(world.IdToken("sub-1"), ("subject_token_type", IdTokenType), (name, "")));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
    }

    [Fact]
    public async Task Invitation_only_refuses_a_new_person_and_admits_an_existing_one()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        await world.SignInAsync("existing");

        await world.ReapplyAsync(descriptor => descriptor["auth"]!["signUp"]!["mode"] = "invitationOnly");
        using var existing = await world.ExchangeAsync(world.IdToken("existing"));
        using var newcomer = await world.ExchangeAsync(world.IdToken("newcomer"));

        existing.StatusCode.ShouldBe(HttpStatusCode.OK);
        var problem = await RefusalAsync(newcomer, HttpStatusCode.Forbidden, AlvoProblemTypes.SignUpRefused, "access_denied");
        problem["policy"]!.GetValue<string>().ShouldBe("invitationOnly");
        (await world.CountAsync("_auth_accounts")).ShouldBe(1);
    }

    [Theory]
    [InlineData("ada@gmail.com", true, null, false)]
    [InlineData("ada@kros.sk", true, "kros.sk", true)]
    [InlineData("ada@KROS.SK", true, "kros.sk", true)]
    [InlineData("ada@sub.kros.sk", true, "sub.kros.sk", false)]
    [InlineData("ada@kros.sk", false, "kros.sk", false)]
    public async Task Allowed_domains_admit_only_a_trusted_address_in_a_listed_domain(
        string email, bool verified, string? hd, bool admitted)
    {
        await using var world = await AccountsWorld.StartAsync(_engine, descriptor: "time-tracker-kros.alvo.json");

        using var response = await world.ExchangeAsync(world.IdToken("sub-1", email, verified, hd));

        if (admitted)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.ReadTextAsync());
            return;
        }

        var problem = await RefusalAsync(response, HttpStatusCode.Forbidden, AlvoProblemTypes.SignUpRefused, "access_denied");
        problem["policy"]!.GetValue<string>().ShouldBe("allowedEmailDomains");
        (await world.CountAsync("_auth_accounts")).ShouldBe(0);
    }

    [Fact]
    public async Task A_disabled_account_is_refused_naming_no_reason()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (access, _) = await world.SignInAsync("sub-1");
        await world.Store.SetDisabledAsync(new UserId(Guid.Parse(new JsonWebToken(access).Subject)), true, world.Clock.GetUtcNow(), TestContext.Current.CancellationToken);

        using var response = await world.ExchangeAsync(world.IdToken("sub-1"));

        var problem = await RefusalAsync(response, HttpStatusCode.Forbidden, AlvoProblemTypes.SignInRefused, "access_denied");
        problem["error_description"]!.GetValue<string>().ShouldBe("This account cannot sign in.");
        problem.ContainsKey("policy").ShouldBeFalse();
    }

    /// <summary>Asserts a refusal's status, problem type, error code, the description mirrored into detail, and no-store.</summary>
    internal static async Task<JsonObject> RefusalAsync(HttpResponseMessage response, HttpStatusCode status, string slug, string error)
    {
        var text = await response.ReadTextAsync();
        response.StatusCode.ShouldBe(status, text);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.ReadProblemTypeAsync()).ShouldBe(slug);
        var problem = await response.ReadJsonObjectAsync();
        problem["error"]!.GetValue<string>().ShouldBe(error);
        problem["error_description"]!.GetValue<string>().ShouldBe(problem["detail"]!.GetValue<string>());
        ShouldNotBeStored(response);
        return problem;
    }

    private static void ShouldNotBeStored(HttpResponseMessage response)
    {
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.Pragma.ToString().ShouldBe("no-cache");
    }

    private static HttpContent Malformed(string defect, string idToken) => defect switch
    {
        "a JSON body" => new StringContent(
            new JsonObject { ["grant_type"] = GrantType, ["subject_token"] = idToken, ["subject_token_type"] = IdTokenType }.ToJsonString(),
            Encoding.UTF8,
            "application/json"),
        "no grant_type" => Form(("subject_token", idToken), ("subject_token_type", IdTokenType)),
        "an access token as the subject token type" => Exchange(idToken, ("subject_token_type", AccessTokenType)),
        "no subject_token" => Form(("grant_type", GrantType), ("subject_token_type", IdTokenType)),
        "a requested token type other than an access token" => Exchange(
            idToken, ("subject_token_type", IdTokenType), ("requested_token_type", "urn:ietf:params:oauth:token-type:refresh_token")),
        "grant_type=password" => Form(("grant_type", "password"), ("username", "ada"), ("password", "x")),
        "subject_token twice" => Exchange(idToken, ("subject_token_type", IdTokenType), ("subject_token", idToken)),
        "grant_type twice" => Exchange(idToken, ("subject_token_type", IdTokenType), ("grant_type", GrantType)),
        "audience=x" => Exchange(idToken, ("subject_token_type", IdTokenType), ("audience", "x")),
        "resource=x" => Exchange(idToken, ("subject_token_type", IdTokenType), ("resource", "https://x.test")),
        _ => Exchange(idToken, ("subject_token_type", IdTokenType), ("padding", new string('p', 17 * 1024))),
    };

    private static FormUrlEncodedContent Exchange(string idToken, params (string Name, string Value)[] more)
        => Form([("grant_type", GrantType), ("subject_token", idToken), .. more]);

    private static FormUrlEncodedContent Form(params (string Name, string Value)[] pairs)
        => new(pairs.Select(pair => new KeyValuePair<string, string>(pair.Name, pair.Value)));
}
