using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Tests.Auth;

/// <summary>
/// The app-token credential (S5 plan D3): a prefixed 22-character key id, a 43-character 256-bit secret, presented as
/// <c>keyId.secret</c>, stored only as the API-key hash of the secret, and never printed.
/// </summary>
public sealed partial class AppTokenSecretTests
{
    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex Base64Url();

    [Theory]
    [InlineData(ApiKeyKind.Personal, "alvo_pat_")]
    [InlineData(ApiKeyKind.Service, "alvo_svc_")]
    public void A_personal_and_a_service_credential_carry_their_prefixes(ApiKeyKind kind, string prefix)
    {
        var credential = AppTokenSecret.New(kind);
        var parts = credential.Presented.Split('.');

        parts.Length.ShouldBe(2, "a credential has exactly one '.'");
        parts[0].ShouldBe(credential.KeyId);
        credential.KeyId.ShouldStartWith(prefix);
        credential.KeyId.Length.ShouldBe(31);
        Base64Url().IsMatch(credential.KeyId[prefix.Length..]).ShouldBeTrue();
        parts[1].Length.ShouldBe(43);
        Base64Url().IsMatch(parts[1]).ShouldBeTrue();
        AppTokenSecret.KindOf(credential.KeyId).ShouldBe(kind);
    }

    [Fact]
    public void A_thousand_credentials_are_distinct()
    {
        var minted = Enumerable.Range(0, 1000).Select(i => AppTokenSecret.New(i % 2 == 0 ? ApiKeyKind.Personal : ApiKeyKind.Service)).ToList();

        minted.Select(credential => credential.KeyId).Distinct(StringComparer.Ordinal).Count().ShouldBe(1000);
        minted.Select(credential => credential.Presented.Split('.')[1]).Distinct(StringComparer.Ordinal).Count().ShouldBe(1000);
    }

    [Fact]
    public void The_hash_is_the_api_key_hash_of_the_secret_part()
    {
        var credential = AppTokenSecret.New(ApiKeyKind.Personal);
        var secret = credential.Presented.Split('.')[1];

        credential.Hash.ShouldBe(ApiKeyHash.Compute(secret));
        ApiKeyHash.Matches(secret, credential.Hash).ShouldBeTrue();
        ApiKeyHash.Matches(credential.Presented, credential.Hash).ShouldBeFalse();
    }

    [Theory]
    [InlineData(ApiKeyKind.Personal, "alvo_pat_***")]
    [InlineData(ApiKeyKind.Service, "alvo_svc_***")]
    public void A_credential_never_prints_its_secret(ApiKeyKind kind, string masked)
    {
        var credential = AppTokenSecret.New(kind);
        var secret = credential.Presented.Split('.')[1];

        credential.ToString().ShouldBe(masked);
        $"minted {credential}".ShouldBe($"minted {masked}");
        $"minted {credential}".ShouldNotContain(secret);
        $"minted {credential}".ShouldNotContain(credential.KeyId);
    }

    [Fact]
    public void Configured_is_not_mintable() =>
        Should.Throw<ArgumentOutOfRangeException>(() => AppTokenSecret.New(ApiKeyKind.Configured));

    [Theory]
    [InlineData("alvo_pat_AAAAAAAAAAAAAAAAAAAAAA", ApiKeyKind.Personal)]
    [InlineData("alvo_svc_AAAAAAAAAAAAAAAAAAAAAA", ApiKeyKind.Service)]
    [InlineData("alvo_pat_", ApiKeyKind.Personal)]
    [InlineData("dev", ApiKeyKind.Configured)]
    [InlineData("alvo_patx", ApiKeyKind.Configured)]
    [InlineData("ALVO_PAT_x", ApiKeyKind.Configured)]
    [InlineData("alvo_inv_x", ApiKeyKind.Configured)]
    [InlineData("", ApiKeyKind.Configured)]
    public void Kind_of_a_key_id(string keyId, ApiKeyKind expected)
    {
        AppTokenSecret.KindOf(keyId).ShouldBe(expected);
        AppTokenSecret.IsAppTokenId(keyId).ShouldBe(expected != ApiKeyKind.Configured);
    }
}
