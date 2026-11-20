using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Tokens;
using MMLib.Alvo.Accounts.Internal;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Tenants.SelfService;
using MMLib.Alvo.Tests.Accounts;
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// No secret reaches a log, a problem body or a <c>ToString</c> (spec §8, §14 AC6, S2 global constraints): ID tokens,
/// access tokens, refresh tokens and their MACs, API keys, invitation codes, and key material — across every path a
/// sign-in host runs.
/// </summary>
/// <remarks>
/// The log facts read <see cref="AccountsWorld.Logs"/> at Trace: each entry's message, its exception's <c>ToString</c>,
/// every structured value, and every scope — what a structured sink would write. A secret is matched whole and by the
/// part that makes it one (a JWT's signature segment, a refresh token's MAC), so a sink that truncates or splits a token
/// still counts as a leak.
/// </remarks>
public sealed class SecretsNeverLoggedTests
{
    private const string Entries = "/api/time_entries";

    private const string GrantType = "urn:ietf:params:oauth:grant-type:token-exchange";

    private const string IdTokenType = "urn:ietf:params:oauth:token-type:id_token";

    private const string TeamTracker = "team-tracker.alvo.json";

    private const string Preview = "/auth/v1/invitations/preview";

    private const string Accept = "/auth/v1/invitations/accept";

    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    private static readonly TestApiKey _developer = new("secrets-developer", ["authenticated"], ["*:read", "*:write"]);

    [Fact]
    public async Task No_token_or_key_appears_in_any_log()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, keys: [_developer]);
        var secrets = new Secrets();
        secrets.ApiKey(_developer.Presented);
        secrets.ApiKey(AccountsWorld.Operator.Presented);

        await ExchangeRefusalsAsync(world, secrets);
        await AdaSignsInRefreshesAndIsReplayedAsync(world, secrets);
        await BobSignsOutAndIsDisabledAsync(world, secrets);

        world.Logs.Lines.ShouldNotBeEmpty("the capture must see the host's own log lines, or this fact measures nothing");
        var leaks = (from line in world.Logs.Lines from secret in secrets.All where line.Contains(secret.Value, StringComparison.Ordinal) select $"{secret.Label}: {line}").ToList();
        leaks.ShouldBeEmpty();
    }

    /// <summary>
    /// Spec §8 for S4: an invitation code never reaches a log — not on the way in (preview, accept, a near-valid body the
    /// validator refuses, the exchange's <c>invitation_code</c>, a 429), not on the way out (the one 201 that carries it),
    /// and not as its hash, which is what the store sees. Each code is matched whole, by its 22-character random part, and
    /// as its hash.
    /// </summary>
    [Fact]
    public async Task No_invitation_code_appears_in_any_log()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, descriptor: TeamTracker, api: api => api.RateLimits.Invitations = 30);
        var secrets = new Secrets();
        var (ada, _) = await SignedInAsync(world, secrets, "ada");
        var acme = await AcmeAsync(world, ada);

        await InvitationLifecycleAsync(world, secrets, ada, acme);
        await InvitationOnlyExchangesAsync(world, secrets, ada, acme);
        await PreviewUntilThrottledAsync(world, secrets.Invitation(InvitationCode.New().Value));

        world.Logs.Lines.ShouldNotBeEmpty("the capture must see the host's own log lines, or this fact measures nothing");
        var leaks = (from line in world.Logs.Lines from secret in secrets.All where line.Contains(secret.Value, StringComparison.Ordinal) select $"{secret.Label}: {line}").ToList();
        leaks.ShouldBeEmpty();
    }

    [Fact]
    public async Task No_error_body_echoes_a_presented_token()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, keys: [_developer]);
        var (access, refresh) = await world.SignInAsync("ada");
        var idToken = world.IdToken("ada");
        var forged = Forged(access);
        var mangled = Forged(refresh);
        var (signedOut, dead) = await world.SignInAsync("bob", email: "bob@gmail.com");
        using (await world.AsPersonAsync(HttpMethod.Post, "/auth/v1/logout", signedOut))
        {
        }

        var refusals = new (string Presented, Func<Task<HttpResponseMessage>> Send)[]
        {
            (idToken, () => world.ExchangeAsync(world.Oidc.Mint("ada", audience: "another-app", issuer: AccountsWorld.GoogleIssuer))),
            (idToken, async () => { using var first = await world.ExchangeAsync(idToken); return await world.ExchangeAsync(idToken); }),
            (idToken, () => world.TokenAsync(new StringContent(new JsonObject { ["grant_type"] = GrantType, ["subject_token"] = idToken }.ToJsonString(), Encoding.UTF8, "application/json"))),
            (idToken, () => world.TokenAsync(("grant_type", GrantType), ("subject_token", idToken), ("subject_token_type", IdTokenType), ("subject_token", idToken))),
            (idToken, () => world.TokenAsync(("grant_type", GrantType), ("subject_token", idToken), ("subject_token_type", IdTokenType), ("audience", "x"))),
            (mangled, () => world.RefreshAsync(mangled)),
            (dead, () => world.RefreshAsync(dead)),
            (signedOut, () => world.AsPersonAsync(HttpMethod.Get, "/auth/v1/user", signedOut)),
            (forged, () => world.AsPersonAsync(HttpMethod.Get, Entries, forged)),
            (idToken, () => world.AsPersonAsync(HttpMethod.Get, Entries, idToken)),
            (access, () => world.Api.SendAsync(HttpMethod.Get, Entries, _developer, headers: [new KeyValuePair<string, string>("Authorization", $"Bearer {access}")])),
            (forged, () => world.AsPersonAsync(HttpMethod.Get, "/auth/v1/user", forged)),
            (idToken, () => world.AsPersonAsync(HttpMethod.Post, "/auth/v1/logout", idToken)),
        };

        foreach (var (presented, send) in refusals)
        {
            using var response = await send();
            ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(400);
            var answer = $"{response.Headers} {response.Content.Headers} {await response.ReadTextAsync()}";
            Secrets.PartsOf(presented).ShouldAllBe(part => !answer.Contains(part, StringComparison.Ordinal), answer);
        }

        await NoInvitationRefusalEchoesTheCodeAsync();
    }

    [Fact]
    public void Records_that_carry_secrets_print_none()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = ecdsa.ExportParameters(includePrivateParameters: true).D!;
        var now = AccountsWorld.Start;

        var printed = new[]
        {
            new TokenResponse("access-secret-value", null, TokenResponse.Bearer, 900, "alvo_rt_refresh-secret-value").ToString(),
            new IdentityVerification(new VerifiedIdentity("google", "sub-1", null, false, null, null), "subject-token-id-value", null).ToString(),
            new KeyCandidate(AccountKeyPurposes.Signing, 1, "kid-1", "material-secret-value", now).ToString(),
            new StoredKey(AccountKeyPurposes.Refresh, 1, "kid-2", "material-secret-value", now, null).ToString(),
            new SigningKey("kid-3", new ECDsaSecurityKey(ecdsa) { KeyId = "kid-3" }).ToString(),
        };

        string[] secrets =
        [
            "access-secret-value", "refresh-secret-value", "subject-token-id-value", "material-secret-value",
            Base64Url.EncodeToString(privateKey), Convert.ToBase64String(privateKey),
        ];
        printed.ShouldAllBe(text => secrets.All(secret => !text.Contains(secret, StringComparison.Ordinal)), string.Join(Environment.NewLine, printed));
    }

    [Fact]
    public async Task A_token_form_prints_none_of_its_values()
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/x-www-form-urlencoded";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("grant_type=refresh_token&refresh_token=alvo_rt_secret-value"));

        var form = await TokenForm.ReadAsync(http.Request, TestContext.Current.CancellationToken);

        form.ShouldNotBeNull();
        form.ToString()!.ShouldNotContain("secret-value");
    }

    [Fact]
    public async Task IdentityModel_pii_logging_stays_off()
    {
        await using var world = await AccountsWorld.StartAsync(_engine);
        var (access, refresh) = await world.SignInAsync("ada");
        using var refreshed = await world.RefreshAsync(refresh);
        using var read = await world.AsPersonAsync(HttpMethod.Get, Entries, access);
        using var refused = await world.AsPersonAsync(HttpMethod.Get, Entries, Forged(access));

        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        IdentityModelEventSource.ShowPII.ShouldBeFalse();
        IdentityModelEventSource.LogCompleteSecurityArtifact.ShouldBeFalse();
    }

    /// <summary>
    /// The invitation half of <see cref="No_error_body_echoes_a_presented_token"/>: the dead-code 404 on preview and accept,
    /// the exchange's <c>invalid_grant</c>, and a <c>/code</c> validation refusal whose code is one lone surrogate away from
    /// valid — none of them repeats the code, its random part or its hash.
    /// </summary>
    private static async Task NoInvitationRefusalEchoesTheCodeAsync()
    {
        await using var world = await AccountsWorld.StartAsync(_engine, descriptor: TeamTracker);
        var (ada, _) = await world.SignInAsync("ada");
        var code = InvitationCode.New().Value;

        var refusals = new Func<Task<HttpResponseMessage>>[]
        {
            () => world.Api.SendAsync(HttpMethod.Post, Preview, body: new JsonObject { ["code"] = code }),
            () => world.AsPersonAsync(HttpMethod.Post, Accept, ada, new JsonObject { ["code"] = code }),
            () => world.ExchangeAsync(world.IdToken("carol", "carol@gmail.com"), code),
            () => world.Api.SendRawAsync(
                HttpMethod.Post, Preview, content: new StringContent($$"""{"code":"{{code}}\ud800"}""", Encoding.UTF8, "application/json")),
        };

        foreach (var send in refusals)
        {
            using var response = await send();
            ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(400);
            var answer = $"{response.Headers} {response.Content.Headers} {await response.ReadTextAsync()}";
            Secrets.InvitationPartsOf(code).ShouldAllBe(part => !answer.Contains(part, StringComparison.Ordinal), answer);
        }
    }

    /// <summary>
    /// Ada invites twice; Carol previews the first, accepts it, accepts it again and sends it in a near-valid body; the
    /// second is revoked, and Frank — no member, so the dead answer is his — accepts it dead.
    /// </summary>
    private static async Task InvitationLifecycleAsync(AccountsWorld world, Secrets secrets, string ada, Guid acme)
    {
        var (_, first) = await InvitedAsync(world, secrets, ada, acme);
        var (secondId, second) = await InvitedAsync(world, secrets, ada, acme);
        var (carol, _) = await SignedInAsync(world, secrets, "carol");
        await ExpectAsync(world.Api.SendAsync(HttpMethod.Post, Preview, body: new JsonObject { ["code"] = first }), HttpStatusCode.OK);
        await ExpectAsync(world.AsPersonAsync(HttpMethod.Post, Accept, carol, new JsonObject { ["code"] = first }), HttpStatusCode.OK);
        await ExpectAsync(world.AsPersonAsync(HttpMethod.Post, Accept, carol, new JsonObject { ["code"] = first }), HttpStatusCode.OK);
        await ExpectAsync(
            world.AsPersonAsync(HttpMethod.Post, Accept, carol, new JsonObject { ["code"] = first, ["codes"] = second }),
            HttpStatusCode.UnprocessableEntity);
        await ExpectAsync(world.AsPersonAsync(HttpMethod.Get, $"/auth/v1/tenants/{acme:D}/invitations", ada), HttpStatusCode.OK);
        await ExpectAsync(world.AsPersonAsync(HttpMethod.Delete, $"/auth/v1/tenants/{acme:D}/invitations/{secondId:D}", ada), HttpStatusCode.NoContent);
        var (frank, _) = await SignedInAsync(world, secrets, "frank");
        await ExpectAsync(world.AsPersonAsync(HttpMethod.Post, Accept, frank, new JsonObject { ["code"] = second }), HttpStatusCode.NotFound);
    }

    /// <summary>Under <c>invitationOnly</c>, a stranger signs up with a live <c>invitation_code</c>, another with a dead one.</summary>
    private static async Task InvitationOnlyExchangesAsync(AccountsWorld world, Secrets secrets, string ada, Guid acme)
    {
        await world.ReapplyAsync(descriptor => descriptor["auth"]!["signUp"]!["mode"] = "invitationOnly");
        var (_, live) = await InvitedAsync(world, secrets, ada, acme);
        var dead = secrets.Invitation(InvitationCode.New().Value);
        await ExpectAsync(world.ExchangeAsync(secrets.Jwt(world.IdToken("dave", "dave@gmail.com")), live), HttpStatusCode.OK);
        await ExpectAsync(world.ExchangeAsync(secrets.Jwt(world.IdToken("erin", "erin@gmail.com")), dead), HttpStatusCode.BadRequest);
    }

    /// <summary>Previews <paramref name="code"/> until the invitations budget answers 429.</summary>
    private static async Task PreviewUntilThrottledAsync(AccountsWorld world, string code)
    {
        for (var sent = 0; sent < 60; sent++)
        {
            using var response = await world.Api.SendAsync(HttpMethod.Post, Preview, body: new JsonObject { ["code"] = code });
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return;
            }
        }

        throw new InvalidOperationException("The invitations budget never answered 429, so the throttled path was not exercised.");
    }

    private static async Task<Guid> AcmeAsync(AccountsWorld world, string ada)
    {
        using var response = await world.AsPersonAsync(HttpMethod.Post, "/auth/v1/tenants", ada, new JsonObject { ["name"] = "Acme" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.ReadTextAsync());
        return (await response.ReadJsonObjectAsync())["tenantId"]!.GetValue<Guid>();
    }

    private static async Task<(Guid Id, string Code)> InvitedAsync(AccountsWorld world, Secrets secrets, string access, Guid tenant)
    {
        using var response = await world.AsPersonAsync(
            HttpMethod.Post, $"/auth/v1/tenants/{tenant:D}/invitations", access, new JsonObject { ["roles"] = new JsonArray("technician") });
        var created = await response.ReadJsonObjectAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (created["id"]!.GetValue<Guid>(), secrets.Invitation(created["code"]!.GetValue<string>()));
    }

    /// <summary>Every refusal of the exchange: replay, an unknown issuer, a foreign audience, a forbidden algorithm, a stale token, a malformed request, a refused sign-up.</summary>
    private static async Task ExchangeRefusalsAsync(AccountsWorld world, Secrets secrets)
    {
        var used = secrets.Jwt(world.IdToken("replayed"));
        await ExpectAsync(world.ExchangeAsync(used), HttpStatusCode.OK);
        await ExpectAsync(world.ExchangeAsync(used), HttpStatusCode.BadRequest);
        foreach (var refused in RefusedIdTokens(world))
        {
            await ExpectAsync(world.ExchangeAsync(secrets.Jwt(refused)), HttpStatusCode.BadRequest);
        }

        var token = secrets.Jwt(world.IdToken("malformed"));
        await ExpectAsync(world.TokenAsync(new StringContent($"{{\"subject_token\":\"{token}\"}}", Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest);
        await ExpectAsync(world.TokenAsync(("grant_type", GrantType), ("subject_token", token), ("subject_token_type", IdTokenType), ("subject_token", token)), HttpStatusCode.BadRequest);
        await ExpectAsync(world.TokenAsync(("grant_type", GrantType), ("subject_token", token), ("subject_token_type", IdTokenType), ("audience", "x")), HttpStatusCode.BadRequest);
        await ExpectAsync(world.TokenAsync(("grant_type", "password"), ("subject_token", token)), HttpStatusCode.BadRequest);

        await world.ReapplyAsync(descriptor => descriptor["auth"]!["signUp"]!["mode"] = "invitationOnly");
        await ExpectAsync(world.ExchangeAsync(secrets.Jwt(world.IdToken("eve"))), HttpStatusCode.Forbidden);
        await world.ReapplyAsync(descriptor => descriptor["auth"]!["signUp"]!["mode"] = "open");
    }

    private static IEnumerable<string> RefusedIdTokens(AccountsWorld world)
    {
        yield return world.Oidc.Mint("ada", audience: AccountsWorld.ClientId);
        yield return world.Oidc.Mint("ada", audience: "another-app", issuer: AccountsWorld.GoogleIssuer);
        yield return world.Oidc.Mint("ada", audience: AccountsWorld.ClientId, issuer: AccountsWorld.GoogleIssuer, alg: "none");
        yield return world.Oidc.Mint("ada", audience: AccountsWorld.ClientId, issuer: AccountsWorld.GoogleIssuer, alg: "HS256");
        yield return world.Oidc.Mint(
            "ada", audience: AccountsWorld.ClientId, issuer: AccountsWorld.GoogleIssuer, issuedAt: world.Clock.GetUtcNow().AddMinutes(-6));
    }

    /// <summary>Ada signs in, uses the Data API and her profile, refreshes twice, and a replayed parent kills her session.</summary>
    private static async Task AdaSignsInRefreshesAndIsReplayedAsync(AccountsWorld world, Secrets secrets)
    {
        var (access, parent) = await SignedInAsync(world, secrets, "ada");
        await ExpectAsync(world.AsPersonAsync(HttpMethod.Post, Entries, access, new JsonObject { ["minutes"] = 30 }), HttpStatusCode.Created);
        await ExpectAsync(world.AsPersonAsync(HttpMethod.Get, Entries, access), HttpStatusCode.OK);
        await ExpectAsync(world.AsPersonAsync(HttpMethod.Get, "/auth/v1/user", access), HttpStatusCode.OK);
        await ExpectAsync(world.AsPersonAsync(HttpMethod.Get, Entries, secrets.Jwt(Forged(access))), HttpStatusCode.Unauthorized);
        await ExpectAsync(world.Api.SendAsync(HttpMethod.Get, Entries, _developer), HttpStatusCode.OK);
        await ExpectAsync(
            world.Api.SendAsync(HttpMethod.Get, Entries, _developer, headers: [new KeyValuePair<string, string>("Authorization", $"Bearer {access}")]),
            HttpStatusCode.Unauthorized);

        var (_, child) = await RefreshedAsync(world, secrets, parent);
        await RefreshedAsync(world, secrets, child);
        await RefreshedAsync(world, secrets, parent);
        world.Clock.Advance(TimeSpan.FromSeconds(11));
        await ExpectAsync(world.RefreshAsync(parent), HttpStatusCode.BadRequest);
    }

    /// <summary>Bob signs in, signs out, and is disabled by the operator; every later use of his tokens is refused.</summary>
    private static async Task BobSignsOutAndIsDisabledAsync(AccountsWorld world, Secrets secrets)
    {
        var (access, refresh) = await SignedInAsync(world, secrets, "bob");
        await ExpectAsync(world.AsPersonAsync(HttpMethod.Post, "/auth/v1/logout", access), HttpStatusCode.NoContent);
        await ExpectAsync(world.RefreshAsync(refresh), HttpStatusCode.BadRequest);

        var (live, _) = await SignedInAsync(world, secrets, "bob");
        await ExpectAsync(
            world.Api.SendAsync(
                HttpMethod.Put,
                $"/management/projects/time-tracker/accounts/{new JsonWebToken(live).Subject}/disabled",
                AccountsWorld.Operator,
                body: new JsonObject { ["disabled"] = true }),
            HttpStatusCode.NoContent);
        await ExpectAsync(world.AsPersonAsync(HttpMethod.Get, Entries, live), HttpStatusCode.Unauthorized);
        await ExpectAsync(world.ExchangeAsync(secrets.Jwt(world.IdToken("bob"))), HttpStatusCode.Forbidden);
    }

    private static async Task<(string Access, string Refresh)> SignedInAsync(AccountsWorld world, Secrets secrets, string subject)
        => await GrantedAsync(world.ExchangeAsync(secrets.Jwt(world.IdToken(subject))), secrets);

    private static Task<(string Access, string Refresh)> RefreshedAsync(AccountsWorld world, Secrets secrets, string refresh)
        => GrantedAsync(world.RefreshAsync(refresh), secrets);

    private static async Task<(string Access, string Refresh)> GrantedAsync(Task<HttpResponseMessage> sent, Secrets secrets)
    {
        using var response = await sent;
        var body = await response.ReadJsonObjectAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToJsonString());
        return (secrets.Jwt(body["access_token"]!.GetValue<string>()), secrets.Refresh(body["refresh_token"]!.GetValue<string>()));
    }

    private static async Task ExpectAsync(Task<HttpResponseMessage> sent, HttpStatusCode status)
    {
        using var response = await sent;
        response.StatusCode.ShouldBe(status, await response.ReadTextAsync());
    }

    /// <summary>The token with one character of its signature changed: well-formed, and refused.</summary>
    private static string Forged(string jwt) => jwt[..^2] + (jwt[^2] == 'A' ? 'B' : 'A') + jwt[^1];

    /// <summary>Every secret a run produced, with the part of each that makes it one.</summary>
    private sealed class Secrets
    {
        private readonly List<(string Label, string Value)> _all = [];

        internal IReadOnlyList<(string Label, string Value)> All => _all;

        /// <summary>
        /// The token and the part that makes it a secret: a JWT's signature segment, a refresh token's MAC — the 43
        /// characters after <c>alvo_rt_</c>, the 32-digit id and its separator (a MAC may itself contain <c>_</c>).
        /// </summary>
        /// <summary>An invitation code whole, its 22-character random part, and its hash — the form the store sees.</summary>
        internal static IEnumerable<string> InvitationPartsOf(string code) =>
            [code, code[InvitationCode.Prefix.Length..], InvitationCode.Hash(code)];

        internal static IEnumerable<string> PartsOf(string token) => token.StartsWith(RefreshTokenFormat.Prefix, StringComparison.Ordinal)
            ? [token, token[(RefreshTokenFormat.Prefix.Length + 33)..]]
            : token.Split('.') is { Length: 3 } jwt && jwt[2].Length > 0 ? [token, jwt[2]] : [token];

        internal string Jwt(string token)
        {
            Add("a JWT", token);
            return token;
        }

        internal string Refresh(string token)
        {
            Add("a refresh token", token);
            return token;
        }

        internal string Invitation(string code)
        {
            foreach (var part in InvitationPartsOf(code))
            {
                _all.Add(("an invitation code", part));
            }

            return code;
        }

        internal void ApiKey(string presented)
        {
            _all.Add(("an API key", presented));
            _all.Add(("an API key's secret", presented[(presented.IndexOf('.', StringComparison.Ordinal) + 1)..]));
        }

        private void Add(string label, string token)
        {
            foreach (var part in PartsOf(token))
            {
                _all.Add((label, part));
            }
        }
    }
}
