using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MMLib.Alvo.Accounts.Internal;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>Alvo's access tokens: RFC 9068's claims and header, nothing else, and a validation that negotiates nothing.</summary>
public sealed class AccessTokensTests : IDisposable
{
    private const string Issuer = "https://alvo.test/auth/v1";

    private const string Audience = "time-tracker";

    private static readonly DateTimeOffset _start = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static readonly AccessTokenSubject _subject =
        new(new UserId(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff")), Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), "google");

    private readonly FakeTimeProvider _clock = new(_start);

    private readonly KeyStoreFake _store = new();

    private readonly AccessTokens _tokens;

    private readonly AccountKeys _keys;

    /// <summary>Wires the tokens over an in-memory key store and a fixed clock.</summary>
    public AccessTokensTests()
    {
        _keys = new AccountKeys(_store, _clock);
        _tokens = new AccessTokens(_keys, Options.Create(new AlvoAccountsOptions()), _clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc/>
    public void Dispose() => _keys.Dispose();

    [Fact]
    public async Task A_token_carries_exactly_the_rfc_9068_claims_and_header()
    {
        var token = new JsonWebToken(await _tokens.MintAsync(_subject, Issuer, Audience, Ct));

        token.Claims.Select(claim => claim.Type).ShouldBe(
            ["iss", "aud", "sub", "sid", "iat", "exp", "jti", "client_id", "idp"], ignoreOrder: true);
        token.Typ.ShouldBe("at+jwt");
        token.Alg.ShouldBe("ES256");
        token.Kid.ShouldBe((await _keys.SigningAsync(Ct)).Kid);
        token.GetPayloadValue<long>("exp").ShouldBe(token.GetPayloadValue<long>("iat") + 900);
        token.GetPayloadValue<long>("iat").ShouldBe(_start.ToUnixTimeSeconds());
        token.GetPayloadValue<string>("sub").ShouldBe("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        token.GetPayloadValue<string>("sid").ShouldBe("0f8fad5bd9cb469fa16570867728950e");
        token.GetPayloadValue<string>("client_id").ShouldBe("alvo");
        token.GetPayloadValue<string>("idp").ShouldBe("google");
        token.Issuer.ShouldBe(Issuer);
        token.Audiences.ShouldBe([Audience]);
    }

    [Fact]
    public async Task A_token_s_lifetime_follows_the_configured_minutes()
    {
        var tokens = new AccessTokens(_keys, Options.Create(new AlvoAccountsOptions { AccessTokenMinutes = 1 }), _clock);

        var token = new JsonWebToken(await tokens.MintAsync(_subject, Issuer, Audience, Ct));

        (token.GetPayloadValue<long>("exp") - token.GetPayloadValue<long>("iat")).ShouldBe(60);
    }

    [Fact]
    public async Task A_valid_token_names_its_subject()
    {
        var token = await _tokens.MintAsync(_subject, Issuer, Audience, Ct);

        (await _tokens.ValidateAsync(token, Issuer, Audience, Ct)).ShouldBe(_subject);
    }

    [Fact]
    public async Task A_token_validates_within_30_seconds_past_exp_and_not_31()
    {
        var token = await _tokens.MintAsync(_subject, Issuer, Audience, Ct);

        _clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(30));
        var atTheBoundary = await _tokens.ValidateAsync(token, Issuer, Audience, Ct);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var pastIt = await _tokens.ValidateAsync(token, Issuer, Audience, Ct);

        atTheBoundary.ShouldBe(_subject);
        pastIt.ShouldBeNull();
    }

    [Theory]
    [InlineData("typ JWT")]
    [InlineData("another issuer")]
    [InlineData("another audience")]
    [InlineData("an unknown kid")]
    [InlineData("another key under the known kid")]
    [InlineData("alg none")]
    [InlineData("HS256 keyed with the public key")]
    [InlineData("a flipped signature byte")]
    [InlineData("not a JWT")]
    [InlineData("three parts of garbage")]
    public async Task A_token_is_refused_for(string defect)
    {
        var valid = await _tokens.MintAsync(_subject, Issuer, Audience, Ct);
        var signing = await _keys.SigningAsync(Ct);
        var (token, issuer, audience) = defect switch
        {
            "typ JWT" => (Sign(signing.Key, SecurityAlgorithms.EcdsaSha256, "JWT"), Issuer, Audience),
            "another issuer" => (valid, "https://other.test/auth/v1", Audience),
            "another audience" => (valid, Issuer, "other-app"),
            "an unknown kid" => (Sign(ForeignKey("unknown-kid"), SecurityAlgorithms.EcdsaSha256), Issuer, Audience),
            "another key under the known kid" => (Sign(ForeignKey(signing.Kid), SecurityAlgorithms.EcdsaSha256), Issuer, Audience),
            "alg none" => (Unsigned(valid), Issuer, Audience),
            "HS256 keyed with the public key" => (Sign(PublicKeyAsSecret(signing), SecurityAlgorithms.HmacSha256), Issuer, Audience),
            "a flipped signature byte" => (FlipSignatureByte(valid), Issuer, Audience),
            "not a JWT" => ("not-a-token", Issuer, Audience),
            _ => ("e30.e30.e30", Issuer, Audience),
        };

        (await _tokens.ValidateAsync(token, issuer, audience, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Validation_of_a_known_kid_does_no_store_read()
    {
        await _keys.SigningAsync(Ct);
        var token = await _tokens.MintAsync(_subject, Issuer, Audience, Ct);
        var (ensures, reads) = (_store.Ensures, _store.Reads);

        for (var validation = 0; validation < 100; validation++)
        {
            (await _tokens.ValidateAsync(token, Issuer, Audience, Ct)).ShouldNotBeNull();
        }

        _store.Ensures.ShouldBe(ensures);
        _store.Reads.ShouldBe(reads);
    }

    private string Sign(SecurityKey key, string algorithm, string type = AccessTokens.TokenType)
        => new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            TokenType = type,
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = _clock.GetUtcNow().UtcDateTime,
            Expires = _clock.GetUtcNow().AddMinutes(15).UtcDateTime,
            SigningCredentials = new SigningCredentials(key, algorithm),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = _subject.Account.ToString(),
                ["sid"] = _subject.Session.ToString("N"),
                ["jti"] = Guid.NewGuid().ToString(),
                ["client_id"] = "alvo",
                ["idp"] = "google",
            },
        });

    private static ECDsaSecurityKey ForeignKey(string kid) => new(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { KeyId = kid };

    private static SymmetricSecurityKey PublicKeyAsSecret(SigningKey signing)
        => new(signing.Key.ECDsa.ExportSubjectPublicKeyInfo()) { KeyId = signing.Kid };

    private static string Unsigned(string valid)
    {
        var parts = valid.Split('.');
        var header = new JsonWebToken(valid);
        var none = $$"""{"alg":"none","typ":"at+jwt","kid":"{{header.Kid}}"}""";
        return $"{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(none))}.{parts[1]}.";
    }

    private static string FlipSignatureByte(string valid)
    {
        var parts = valid.Split('.');
        var signature = Base64Url.DecodeFromChars(parts[2]);
        signature[10] ^= 0x01;
        return $"{parts[0]}.{parts[1]}.{Base64Url.EncodeToString(signature)}";
    }
}
