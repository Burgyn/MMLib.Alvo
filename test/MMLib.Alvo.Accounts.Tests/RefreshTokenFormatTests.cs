using MMLib.Alvo.Accounts.Internal;
using System.Security.Cryptography;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>The derivable refresh-token value (spec D6): <c>alvo_rt_&lt;id&gt;_&lt;base64url(HMAC-SHA256(key, id))&gt;</c>.</summary>
public sealed class RefreshTokenFormatTests
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    private static readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    private static readonly Guid _id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    [Fact]
    public void A_token_round_trips_to_its_id()
    {
        var value = RefreshTokenFormat.Format(_id, _key);

        value.ShouldStartWith("alvo_rt_0f8fad5bd9cb469fa16570867728950e_");
        value.Length.ShouldBe(84);
        RefreshTokenFormat.TryParse(value, _key, out var id).ShouldBeTrue();
        id.ShouldBe(_id);
    }

    [Fact]
    public void The_mac_is_hmac_sha256_of_the_id_text_base64url()
    {
        var mac = Convert.ToBase64String(HMACSHA256.HashData(_key, "0f8fad5bd9cb469fa16570867728950e"u8))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        RefreshTokenFormat.Format(_id, _key).ShouldEndWith("_" + mac);
    }

    [Fact]
    public void A_mac_whose_first_character_is_an_underscore_parses()
    {
        var (id, value) = Enumerable.Range(0, 10_000)
            .Select(seed => new Guid(seed, 0, 0, new byte[8]))
            .Select(candidate => (candidate, RefreshTokenFormat.Format(candidate, _key)))
            .First(token => token.Item2[41] == '_');

        RefreshTokenFormat.TryParse(value, _key, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(id);
    }

    [Theory]
    [InlineData("wrong prefix")]
    [InlineData("short")]
    [InlineData("upper-case hex")]
    [InlineData("one mac character changed")]
    [InlineData("non-canonical last character")]
    [InlineData("another key")]
    [InlineData("trailing space")]
    [InlineData("empty")]
    [InlineData("null")]
    public void A_value_is_refused_for(string defect)
    {
        var value = RefreshTokenFormat.Format(_id, _key);
        var key = _key;
        string? presented = defect switch
        {
            "wrong prefix" => "alvo_xx_" + value[8..],
            "short" => value[..^1],
            "upper-case hex" => value[..8] + value[8..40].ToUpperInvariant() + value[40..],
            "one mac character changed" => value[..60] + (value[60] == 'A' ? 'B' : 'A') + value[61..],
            "non-canonical last character" => value[..^1] + SameBytesOtherSpelling(value[^1]),
            "another key" => value,
            "trailing space" => value + " ",
            "empty" => string.Empty,
            _ => null,
        };
        if (defect == "another key")
        {
            key = RandomNumberGenerator.GetBytes(32);
        }

        RefreshTokenFormat.TryParse(presented, key, out var id).ShouldBeFalse();
        id.ShouldBe(Guid.Empty);
    }

    [Fact]
    public void The_non_canonical_spelling_decodes_to_the_same_mac()
    {
        var value = RefreshTokenFormat.Format(_id, _key);
        var other = value[..^1] + SameBytesOtherSpelling(value[^1]);

        Convert.FromBase64String(Base64(other[41..])).ShouldBe(Convert.FromBase64String(Base64(value[41..])));
    }

    [Fact]
    public void An_empty_key_formats_nothing_and_parses_nothing()
    {
        Should.Throw<ArgumentException>(() => RefreshTokenFormat.Format(_id, []));
        RefreshTokenFormat.TryParse(RefreshTokenFormat.Format(_id, _key), [], out _).ShouldBeFalse();
    }

    private static char SameBytesOtherSpelling(char last) => Alphabet[Alphabet.IndexOf(last, StringComparison.Ordinal) ^ 1];

    private static string Base64(string base64Url) => base64Url.Replace('-', '+').Replace('_', '/') + "=";
}
