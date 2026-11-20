using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api.Tests;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Tests.Accounts;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// A token is minted only where it would resolve (S5 Task 9 fix round 1): a host that <em>replaced</em> Alvo's
/// <see cref="IApiKeyStore"/> gets the <c>feature-disabled</c> 404 naming <c>auth.accessTokens</c> and the cause, never a
/// credential nothing accepts; a host that <em>decorates</em> it keeps minting, and the token works.
/// </summary>
public sealed class AppTokenReachTests
{
    private const string TeamTracker = "team-tracker.alvo.json";

    private static readonly SqliteApiEngine _engine = SqliteApiEngine.Instance;

    [Fact]
    public async Task A_host_that_replaced_the_api_key_store_is_refused_the_mint()
    {
        await using var world = await AccountsWorld.StartAsync(
            _engine,
            descriptor: TeamTracker,
            services: services => services.AddSingleton<IApiKeyStore>(
                provider => new HostKeyStore(provider.GetRequiredService<InMemoryApiKeyStore>())));
        var (ada, _) = await world.SignInAsync("ada");

        using var response = await world.MintAsync(ada, Personal(world));

        await TenantRoutesTests.FeatureDisabledAsync(response, "auth.accessTokens");
        (await response.ReadProblemDetailAsync()).ShouldContain("IApiKeyStore");
        (await world.CountAsync("_api_keys")).ShouldBe(0, "nothing is minted that could never be used");
    }

    [Fact]
    public async Task A_host_that_decorates_the_api_key_store_keeps_minting_and_its_tokens_resolve()
    {
        CountingKeyStore? decorator = null;
        await using var world = await AccountsWorld.StartAsync(
            _engine,
            descriptor: TeamTracker,
            services: services =>
            {
                var alvos = services.Last(descriptor => descriptor.ServiceType == typeof(IApiKeyStore));
                services.AddSingleton<IApiKeyStore>(
                    provider => decorator = new CountingKeyStore((IApiKeyStore)alvos.ImplementationFactory!(provider)));
            });
        var (ada, _) = await world.SignInAsync("ada");

        using var minted = await world.MintAsync(ada, Personal(world));
        var body = await minted.ReadJsonObjectAsync();
        minted.StatusCode.ShouldBe(HttpStatusCode.Created, body.ToJsonString());
        var token = body["token"]!.GetValue<string>();
        using var used = await world.AsTokenAsync(HttpMethod.Get, "/api/regions", token);

        used.StatusCode.ShouldBe(HttpStatusCode.OK, await used.ReadTextAsync());
        decorator.ShouldNotBeNull().Finds.ShouldContain(token[..token.IndexOf('.', StringComparison.Ordinal)]);
    }

    private static JsonObject Personal(AccountsWorld world) => new()
    {
        ["name"] = "integration",
        ["kind"] = "personal",
        ["scopes"] = new JsonArray("regions:read"),
        ["expiresAt"] = world.Clock.GetUtcNow().AddDays(7).ToString("O", CultureInfo.InvariantCulture),
    };

    /// <summary>A host's own store that replaces Alvo's: it knows the configured keys, and nothing else.</summary>
    private sealed class HostKeyStore(InMemoryApiKeyStore configured) : IApiKeyStore
    {
        public ValueTask<ApiKeyRecord?> FindAsync(string keyId, CancellationToken cancellationToken) =>
            configured.FindAsync(keyId, cancellationToken);

        public ValueTask TouchAsync(string keyId, DateTimeOffset usedAt, CancellationToken cancellationToken) =>
            configured.TouchAsync(keyId, usedAt, cancellationToken);
    }

    /// <summary>A host's decorator over Alvo's store: it records each lookup and delegates.</summary>
    private sealed class CountingKeyStore(IApiKeyStore inner) : IApiKeyStore
    {
        internal List<string> Finds { get; } = [];

        public ValueTask<ApiKeyRecord?> FindAsync(string keyId, CancellationToken cancellationToken)
        {
            lock (Finds)
            {
                Finds.Add(keyId);
            }

            return inner.FindAsync(keyId, cancellationToken);
        }

        public ValueTask TouchAsync(string keyId, DateTimeOffset usedAt, CancellationToken cancellationToken) =>
            inner.TouchAsync(keyId, usedAt, cancellationToken);
    }
}
