using MMLib.Alvo.Accounts.Internal;
using System.Security.Cryptography;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// The derivable refresh-token value (spec D6, bound to its session by #527):
/// <c>alvo_rt_&lt;session&gt;_&lt;id&gt;_&lt;base64url(HMAC-SHA256(key, session_id))&gt;</c>.
/// </summary>
public sealed class RefreshTokenFormatTests
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    private const string SessionText = "7c9e6679742540de944be07fc1f90ae7";

    private const string IdText = "0f8fad5bd9cb469fa16570867728950e";

    private static readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    private static readonly Guid _session = Guid.Parse(SessionText);

    private static readonly Guid _id = Guid.Parse(IdText);

    [Fact]
    public void A_token_round_trips_to_its_session_and_id()
    {
        var value = RefreshTokenFormat.Format(_session, _id, _key);

        value.ShouldStartWith($"alvo_rt_{SessionText}_{IdText}_");
        value.Length.ShouldBe(117);
        RefreshTokenFormat.TryParse(value, _key, out var token).ShouldBeTrue();
        token.ShouldBe(new RefreshTokenValue(_session, _id));
    }

    [Fact]
    public void The_mac_is_hmac_sha256_of_the_session_and_id_text_base64url()
    {
        var mac = Convert.ToBase64String(HMACSHA256.HashData(_key, System.Text.Encoding.ASCII.GetBytes($"{SessionText}_{IdText}")))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        RefreshTokenFormat.Format(_session, _id, _key).ShouldEndWith("_" + mac);
    }

    [Fact]
    public void A_mac_whose_first_character_is_an_underscore_parses()
    {
        var (id, value) = Enumerable.Range(0, 10_000)
            .Select(seed => new Guid(seed, 0, 0, new byte[8]))
            .Select(candidate => (candidate, RefreshTokenFormat.Format(_session, candidate, _key)))
            .First(token => token.Item2[74] == '_');

        RefreshTokenFormat.TryParse(value, _key, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(new RefreshTokenValue(_session, id));
    }

    /// <summary>
    /// The session is inside the MAC: a value moved to another session — the one thing that would let a swept ancestor
    /// pose as another session's reuse — does not parse.
    /// </summary>
    [Theory]
    [InlineData("wrong prefix")]
    [InlineData("short")]
    [InlineData("the id format of before #527")]
    [InlineData("upper-case hex")]
    [InlineData("another session")]
    [InlineData("session and id swapped")]
    [InlineData("one mac character changed")]
    [InlineData("non-canonical last character")]
    [InlineData("another key")]
    [InlineData("trailing space")]
    [InlineData("empty")]
    [InlineData("null")]
    public void A_value_is_refused_for(string defect)
    {
        var value = RefreshTokenFormat.Format(_session, _id, _key);
        var key = defect == "another key" ? RandomNumberGenerator.GetBytes(32) : _key;
        string? presented = defect switch
        {
            "wrong prefix" => "alvo_xx_" + value[8..],
            "short" => value[..^1],
            "the id format of before #527" => value[..8] + value[41..],
            "upper-case hex" => value[..8] + value[8..40].ToUpperInvariant() + value[40..],
            "another session" => value[..8] + Guid.NewGuid().ToString("N") + value[40..],
            "session and id swapped" => $"alvo_rt_{IdText}_{SessionText}{value[73..]}",
            "one mac character changed" => value[..90] + (value[90] == 'A' ? 'B' : 'A') + value[91..],
            "non-canonical last character" => value[..^1] + SameBytesOtherSpelling(value[^1]),
            "another key" => value,
            "trailing space" => value + " ",
            "empty" => string.Empty,
            _ => null,
        };

        RefreshTokenFormat.TryParse(presented, key, out var token).ShouldBeFalse();
        token.ShouldBe(default);
    }

    [Fact]
    public void The_non_canonical_spelling_decodes_to_the_same_mac()
    {
        var value = RefreshTokenFormat.Format(_session, _id, _key);
        var other = value[..^1] + SameBytesOtherSpelling(value[^1]);

        Convert.FromBase64String(Base64(other[74..])).ShouldBe(Convert.FromBase64String(Base64(value[74..])));
    }

    [Fact]
    public void An_empty_key_formats_nothing_and_parses_nothing()
    {
        Should.Throw<ArgumentException>(() => RefreshTokenFormat.Format(_session, _id, []));
        RefreshTokenFormat.TryParse(RefreshTokenFormat.Format(_session, _id, _key), [], out _).ShouldBeFalse();
    }

    private static char SameBytesOtherSpelling(char last) => Alphabet[Alphabet.IndexOf(last, StringComparison.Ordinal) ^ 1];

    private static string Base64(string base64Url) => base64Url.Replace('-', '+').Replace('_', '/') + "=";
}
