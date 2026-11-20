using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Accounts.Internal;
using MMLib.Alvo.Secrets;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// The deployment's keys (S2 D12, Review Focus 2): one signing key and one refresh key per store, whichever replica asks
/// first, and a reload on an unknown <c>kid</c> that a flood of invented ones cannot turn into a read per request.
/// </summary>
public sealed class AccountKeysTests : IDisposable
{
    private const string Issuer = "https://alvo.test/auth/v1";

    private const string Audience = "time-tracker";

    private static readonly DateTimeOffset _start = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private readonly string _database = Path.Combine(Path.GetTempPath(), $"alvo-keys-{Guid.NewGuid():N}.db");

    private readonly string _keyFile = Path.Combine(Path.GetTempPath(), $"alvo-keys-{Guid.NewGuid():N}.key");

    private readonly FakeTimeProvider _clock = new(_start);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc/>
    public void Dispose()
    {
        File.Delete(_database);
        File.Delete(_keyFile);
    }

    [Fact]
    public async Task Two_replicas_over_one_store_mint_tokens_each_other_validates()
    {
        await using var services = SqliteStore();
        var store = services.GetRequiredService<IAccountStore>();
        var (keysA, tokensA) = Replica(store);
        var (keysB, tokensB) = Replica(store);
        using var disposeA = keysA;
        using var disposeB = keysB;
        var subject = new AccessTokenSubject(UserId.New(), Guid.NewGuid(), "google");

        var minted = await Task.WhenAll(
            tokensA.MintAsync(subject, Issuer, Audience, Ct).AsTask(),
            tokensB.MintAsync(subject, Issuer, Audience, Ct).AsTask());

        (await tokensB.ValidateAsync(minted[0], Issuer, Audience, Ct)).ShouldBe(subject);
        (await tokensA.ValidateAsync(minted[1], Issuer, Audience, Ct)).ShouldBe(subject);
        (await keysA.SigningAsync(Ct)).Kid.ShouldBe((await keysB.SigningAsync(Ct)).Kid);
        (await store.ReadKeysAsync(AccountKeyPurposes.Signing, _start, Ct)).ShouldHaveSingleItem();
        (await keysA.RefreshMacKeyAsync(Ct)).ShouldBe(await keysB.RefreshMacKeyAsync(Ct));
        (await store.ReadKeysAsync(AccountKeyPurposes.Refresh, _start, Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_kid_is_the_rfc_7638_thumbprint()
    {
        using var keys = new AccountKeys(new KeyStoreFake(), _clock);

        var signing = await keys.SigningAsync(Ct);

        var point = signing.Key.ECDsa.ExportParameters(includePrivateParameters: false).Q;
        var canonical = $$"""{"crv":"P-256","kty":"EC","x":"{{Base64Url.EncodeToString(point.X)}}","y":"{{Base64Url.EncodeToString(point.Y)}}"}""";
        signing.Kid.ShouldBe(Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
        signing.Key.KeyId.ShouldBe(signing.Kid);
        signing.Key.ECDsa.KeySize.ShouldBe(256);
    }

    [Fact]
    public async Task An_unknown_kid_reloads_the_store_at_most_once_per_five_minutes()
    {
        var store = new KeyStoreFake();
        using var keys = new AccountKeys(store, _clock);
        await keys.SigningAsync(Ct);
        var readsAfterLoad = store.Reads;
        var rotated = new StoredKey(AccountKeyPurposes.Signing, 2, "rotated", Material(), _start, null);
        store.Add(rotated);

        var early = await FloodAsync(keys, "rotated");
        _clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        var stillEarly = await FloodAsync(keys, "rotated");
        var readsBeforeTheInterval = store.Reads;
        _clock.Advance(TimeSpan.FromSeconds(1));
        var found = await keys.FindAsync("rotated", Ct);
        var invented = await FloodAsync(keys, "invented");

        early.ShouldBeNull();
        stillEarly.ShouldBeNull();
        readsBeforeTheInterval.ShouldBe(readsAfterLoad);
        found.ShouldNotBeNull().Kid.ShouldBe("rotated");
        invented.ShouldBeNull();
        store.Reads.ShouldBe(readsAfterLoad + 1);
        (await keys.PublishedAsync(Ct)).Select(key => key.Kid).ShouldContain("rotated");
    }

    [Fact]
    public async Task The_first_lookup_ensures_the_signing_key_every_replica_converges_on()
    {
        var store = new KeyStoreFake();
        using var minting = new AccountKeys(store, _clock);
        using var validating = new AccountKeys(store, _clock);

        var kid = (await validating.PublishedAsync(Ct)).ShouldHaveSingleItem().Kid;

        (await minting.SigningAsync(Ct)).Kid.ShouldBe(kid);
        (await validating.FindAsync(kid, Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task The_refresh_key_is_32_bytes_read_once()
    {
        var store = new KeyStoreFake();
        using var keys = new AccountKeys(store, _clock);

        var first = await keys.RefreshMacKeyAsync(Ct);
        var again = await keys.RefreshMacKeyAsync(Ct);

        first.Length.ShouldBe(32);
        again.ShouldBe(first);
        store.Ensures.ShouldBe(1);
    }

    [Fact]
    public async Task A_signing_key_never_prints_its_key()
    {
        using var keys = new AccountKeys(new KeyStoreFake(), _clock);
        var signing = await keys.SigningAsync(Ct);

        signing.ToString().ShouldBe($"SigningKey {{ Kid = {signing.Kid} }}");
    }

    private (AccountKeys Keys, AccessTokens Tokens) Replica(IAccountStore store)
    {
        var keys = new AccountKeys(store, _clock);
        return (keys, new AccessTokens(keys, Options.Create(new AlvoAccountsOptions()), _clock));
    }

    private ServiceProvider SqliteStore()
    {
        File.WriteAllText(_keyFile, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlvo(alvo => alvo.UseSqlite($"Data Source={_database}"));
        services.Configure<AlvoSecretOptions>(options => options.EncryptionKeyFile = _keyFile);
        return services.BuildServiceProvider();
    }

    private static async Task<SigningKey?> FloodAsync(AccountKeys keys, string kid)
    {
        SigningKey? last = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            last = await keys.FindAsync(kid, Ct);
        }

        return last;
    }

    private static string Material()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(key.ExportPkcs8PrivateKey());
    }
}
