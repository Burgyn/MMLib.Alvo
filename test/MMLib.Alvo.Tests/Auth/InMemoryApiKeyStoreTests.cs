using Microsoft.Extensions.Options;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;

namespace MMLib.Alvo.Tests.Auth;

public class InMemoryApiKeyStoreTests
{
    /// <summary>
    /// A secret clearing <see cref="Auth.Internal.AlvoAuthOptionsValidator.MinimumSecretLength"/>, so a
    /// fact about resolution or storage is not refused at options validation first (#125).
    /// </summary>
    private const string LongEnoughSecret = "s3cret-value-long-enough-for-the-floor";

    private static IApiKeyStore StoreWith(AlvoDevApiKey key)
    {
        var options = new AlvoAuthOptions();
        options.DevKeys.Add(key);
#pragma warning disable CA1859
        IApiKeyStore store = new InMemoryApiKeyStore(Options.Create(options));
#pragma warning restore CA1859
        return store;
    }

    [Fact]
    public async Task A_key_with_one_bad_scope_among_good_ones_is_not_resolvable_at_all()
    {
        var store = StoreWith(new AlvoDevApiKey
        {
            KeyId = "dev",
            Secret = LongEnoughSecret,
            User = Guid.NewGuid(),
            Roles = { "authenticated" },
            Scopes = { "orders:read", "orders:reed", "invoices:write" },
        });

        var record = await store.FindAsync("dev", TestContext.Current.CancellationToken);

        record.ShouldBeNull();
    }

    [Fact]
    public async Task A_fully_valid_key_resolves()
    {
        var store = StoreWith(new AlvoDevApiKey
        {
            KeyId = "dev",
            Secret = LongEnoughSecret,
            User = Guid.NewGuid(),
            Roles = { "authenticated" },
            Scopes = { "orders:read", "invoices:write" },
        });

        var record = await store.FindAsync("dev", TestContext.Current.CancellationToken);

        record.ShouldNotBeNull();
        record.Scopes.Count.ShouldBe(2);
    }

    [Fact]
    public async Task TouchAsync_merges_LastUsedAt_into_the_next_FindAsync()
    {
        var store = StoreWith(new AlvoDevApiKey
        {
            KeyId = "dev",
            Secret = LongEnoughSecret,
            User = Guid.NewGuid(),
            Roles = { "authenticated" },
            Scopes = { "orders:read" },
        });
        var cancellationToken = TestContext.Current.CancellationToken;

        (await store.FindAsync("dev", cancellationToken))!.LastUsedAt.ShouldBeNull();

        var usedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        await store.TouchAsync("dev", usedAt, cancellationToken);

        (await store.FindAsync("dev", cancellationToken))!.LastUsedAt.ShouldBe(usedAt);
    }
}
