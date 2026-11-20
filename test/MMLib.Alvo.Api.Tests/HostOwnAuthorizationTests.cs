using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Auth;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Review Focus 1: a host that already uses <c>Authorization</c> for a scheme of its own — Bearer or Basic — and has
/// registered no person resolver sees every Alvo route answer exactly as before: anonymous or by API key, never 401.
/// </summary>
/// <remarks>
/// The host's scheme is a hand-written <see cref="AuthenticationHandler{TOptions}"/> named <c>Bearer</c> rather than
/// the JwtBearer package (the plan's D9): the property under test is that Alvo does not read the header, not how the
/// host validates it — and the handler's own endpoint is the control that the header really reached the host.
/// </remarks>
public sealed class HostOwnAuthorizationTests
{
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    [Theory]
    [InlineData("Bearer host-token")]
    [InlineData("Bearer not-a-token")]
    [InlineData("Basic dXNlcjpwYXNz")]
    public async Task A_host_authorization_header_leaves_every_alvo_route_as_it_was(string authorization)
    {
        await using var world = await HostWorldAsync();

        using var anonymous = await world.SendAsync(
            HttpMethod.Post, "/api/owners", body: Owner("Anonymous Ltd"), headers: Authorization(authorization));
        using var keyed = await world.SendAsync(
            HttpMethod.Post, "/api/owners", _admin, body: Owner("Acme Ltd"), headers: Authorization(authorization));
        using var management = await world.SendAsync(
            HttpMethod.Get, "/management/info", headers: Authorization(authorization));
        using var managementControl = await world.SendAsync(HttpMethod.Get, "/management/info");

        anonymous.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden, "the host's own header leaves the caller anonymous, whom the policy denies — never 401");
        (await anonymous.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
        keyed.StatusCode.ShouldBe(
            HttpStatusCode.Created, "beside the host's own header, the API key is the one credential Alvo reads");
        management.StatusCode.ShouldBe(
            managementControl.StatusCode, "the Management API answers as though the header were not there");
    }

    [Fact]
    public async Task The_host_s_own_scheme_still_sees_its_user()
    {
        await using var world = await HostWorldAsync();

        using var me = await world.SendAsync(HttpMethod.Get, "/host/me", headers: Authorization("Bearer host-token"));

        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await me.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("host-user");
    }

    /// <summary>
    /// The Bearer channel opens for the one key the plan fixed. A keyed resolver under any other key — the
    /// dashboard's <c>alvo-identity</c> is the one a real host has — claims nothing, even one that would admit anyone.
    /// </summary>
    [Fact]
    public async Task A_keyed_resolver_under_another_key_does_not_claim_the_bearer_channel()
    {
        var bystander = new AdmittingResolver();
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin],
            new AlvoApiWorldSetup(
                ConfigureServices: services => services.AddKeyedSingleton<IAlvoContextResolver>("alvo-identity", bystander)));

        using var response = await world.SendAsync(
            HttpMethod.Post, "/api/owners", body: Owner("Anonymous Ltd"), headers: Authorization("Bearer t"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
        bystander.Calls.ShouldBeEmpty("a resolver under another key is never handed the bearer token");
    }

    /// <summary>
    /// A resolver registered under <see cref="KeyedService.AnyKey"/> answers for every key, <c>alvo-person</c>
    /// included — but it was not registered <em>as</em> the person resolver, so it does not open the Bearer channel
    /// and a host's own <c>Authorization</c> stays the host's.
    /// </summary>
    [Fact]
    public async Task A_resolver_registered_under_any_key_does_not_claim_the_bearer_channel()
    {
        var bystander = new AdmittingResolver();
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin],
            new AlvoApiWorldSetup(
                ConfigureServices: services => services.AddKeyedSingleton<IAlvoContextResolver>(KeyedService.AnyKey, bystander)));

        using var response = await world.SendAsync(
            HttpMethod.Post, "/api/owners", body: Owner("Anonymous Ltd"), headers: Authorization("Bearer t"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "Bearer is ignored, as with no person resolver at all");
        (await response.ReadProblemTypeAsync()).ShouldBe(AlvoProblemTypes.Forbidden);
        bystander.Calls.ShouldBeEmpty("a catch-all keyed resolver is never handed the bearer token");
    }

    private static Task<AlvoApiWorld> HostWorldAsync() =>
        AlvoApiWorld.VehicleRegistryAsync(
            [_admin],
            new AlvoApiWorldSetup(
                ConfigureServices: services => services
                    .AddAuthentication(HostBearerHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, HostBearerHandler>(HostBearerHandler.SchemeName, null),
                ConfigureApp: app =>
                {
                    app.UseAuthentication();
                    app.MapGet("/host/me", (HttpContext http) => http.User.Identity?.Name ?? string.Empty);
                },
                MapManagementApi: true));

    private static KeyValuePair<string, string>[] Authorization(string value) => [new("Authorization", value)];

    private static JsonObject Owner(string name) => new() { ["name"] = name };

    /// <summary>The host's own Bearer scheme: one token it knows, and no opinion on anything else.</summary>
    private sealed class HostBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "Bearer";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.Authorization.ToString() != "Bearer host-token")
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "host-user")], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    /// <summary>A resolver that would admit anyone — so its never being called is the whole fact.</summary>
    private sealed class AdmittingResolver : IAlvoContextResolver
    {
        internal ConcurrentQueue<string?> Calls { get; } = new();

        public ValueTask<AlvoPrincipal?> ResolveAsync(
            string? presentedKey, string? requestedTenant, CancellationToken cancellationToken)
        {
            Calls.Enqueue(presentedKey);
            return ValueTask.FromResult<AlvoPrincipal?>(TestPersonResolver.Person("bystander", tenant: null));
        }
    }
}
