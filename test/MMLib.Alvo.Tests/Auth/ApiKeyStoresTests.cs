using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;

namespace MMLib.Alvo.Tests.Auth;

/// <summary>
/// The routing key store (S5 plan D4): the prefix alone sends a key id to the app-token registry or to the configured dev
/// keys, never both; an app token's touch is throttled to one a minute per replica, a dev key's is not.
/// </summary>
public sealed class ApiKeyStoresTests
{
    private const string DevKeyId = "dev";
    private const string TokenId = AppTokenSecret.PersonalPrefix + "AAAAAAAAAAAAAAAAAAAAAA";

    private readonly RecordingAppTokenStore _registry = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-11-14T12:00:00Z", null));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InMemoryApiKeyStore DevKeys()
    {
        var options = new AlvoAuthOptions();
        options.DevKeys.Add(new AlvoDevApiKey
        {
            KeyId = DevKeyId,
            Secret = "s3cret-value-long-enough-for-the-floor",
            User = Guid.NewGuid(),
            Roles = { "authenticated" },
        });
        return new InMemoryApiKeyStore(Options.Create(options));
    }

    private static ApiKeyRecord Token(string keyId) => new()
    {
        KeyId = keyId,
        Sha256Hash = ApiKeyHash.Compute("secret"),
        User = UserId.New(),
        RoleNames = [],
        Scopes = new HashSet<ApiKeyScope>(),
        Kind = AppTokenSecret.KindOf(keyId),
    };

    private ApiKeyStores Stores(bool withRegistry = true) => new(DevKeys(), withRegistry ? _registry : null, _clock);

    [Fact]
    public async Task Dev_keys_resolve_from_configuration_and_app_tokens_from_the_registry()
    {
        var token = _registry.Put(Token(TokenId));
        var stores = Stores();

        (await stores.FindAsync(DevKeyId, Ct)).ShouldNotBeNull().KeyId.ShouldBe(DevKeyId);
        (await stores.FindAsync(TokenId, Ct)).ShouldBeSameAs(token);
    }

    [Fact]
    public async Task A_prefixed_id_never_reaches_the_dev_keys_and_an_unprefixed_one_never_the_registry()
    {
        var stores = Stores();

        (await stores.FindAsync(AppTokenSecret.ServicePrefix + DevKeyId, Ct)).ShouldBeNull();
        (await stores.FindAsync("unknown", Ct)).ShouldBeNull();
        (await stores.FindAsync(DevKeyId, Ct)).ShouldNotBeNull();

        _registry.Finds.ShouldBe([AppTokenSecret.ServicePrefix + DevKeyId]);
    }

    [Fact]
    public async Task Without_a_registry_an_app_token_is_unknown()
    {
        var stores = Stores(withRegistry: false);

        (await stores.FindAsync(TokenId, Ct)).ShouldBeNull();
        await stores.TouchAsync(TokenId, _clock.GetUtcNow(), Ct);
    }

    [Fact]
    public async Task App_token_touches_are_throttled_to_one_a_minute()
    {
        var stores = Stores();
        var start = _clock.GetUtcNow();

        await stores.TouchAsync(TokenId, _clock.GetUtcNow(), Ct);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await stores.TouchAsync(TokenId, _clock.GetUtcNow(), Ct);
        _clock.Advance(TimeSpan.FromSeconds(29));
        await stores.TouchAsync(TokenId, _clock.GetUtcNow(), Ct);
        _registry.Touches.Count.ShouldBe(1);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await stores.TouchAsync(TokenId, _clock.GetUtcNow(), Ct);
        _registry.Touches.Select(touch => touch.UsedAt).ShouldBe([start, _clock.GetUtcNow()]);
    }

    [Fact]
    public async Task Dev_key_touches_are_unthrottled()
    {
        var stores = Stores();
        var first = _clock.GetUtcNow();
        var second = first.AddSeconds(1);

        await stores.TouchAsync(DevKeyId, first, Ct);
        await stores.TouchAsync(DevKeyId, second, Ct);

        (await stores.FindAsync(DevKeyId, Ct)).ShouldNotBeNull().LastUsedAt.ShouldBe(second);
        _registry.Touches.ShouldBeEmpty();
    }
}
