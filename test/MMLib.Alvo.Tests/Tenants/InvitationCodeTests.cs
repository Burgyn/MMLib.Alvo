using MMLib.Alvo.Tenants.SelfService;

namespace MMLib.Alvo.Tests.Tenants;

/// <summary>
/// An invitation's code (S4 plan D2, spec §6.1): <c>alvo_inv_</c> and 128 bits from the system's CSPRNG in base64url, of
/// which only the SHA-256 is ever stored, and which prints as <c>alvo_inv_***</c> wherever it is formatted.
/// </summary>
public sealed class InvitationCodeTests
{
    private const string Base64UrlAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    [Fact]
    public void A_code_is_the_prefix_and_128_bits_in_base64url()
    {
        var codes = Enumerable.Range(0, 1000).Select(_ => InvitationCode.New().Value).ToList();

        foreach (var code in codes)
        {
            code.Length.ShouldBe(31, code);
            code.ShouldStartWith(InvitationCode.Prefix, Case.Sensitive);
            code[InvitationCode.Prefix.Length..].ShouldAllBe(character => Base64UrlAlphabet.Contains(character, StringComparison.Ordinal));
        }

        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(codes.Count);
        InvitationCode.Prefix.ShouldBe("alvo_inv_");
    }

    [Fact]
    public void The_hash_is_lowercase_hex_sha256_of_the_presented_text()
    {
        InvitationCode.Hash("abc").ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        InvitationCode.Hash(string.Empty).ShouldBe("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        InvitationCode.Hash("not a code at all").Length.ShouldBe(64, "any presented text is hashed, never refused");

        var code = InvitationCode.New();
        code.Hashed.ShouldBe(InvitationCode.Hash(code.Value));
        code.Hashed.ShouldNotBe(InvitationCode.Hash(code.Value.ToUpperInvariant()), "the hash is of the exact text");
    }

    [Fact]
    public void A_code_never_prints_itself()
    {
        var code = InvitationCode.New();

        code.ToString().ShouldBe("alvo_inv_***");
        $"{code}".ShouldBe("alvo_inv_***");
        string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}", code).ShouldBe("alvo_inv_***");
        $"{code}".ShouldNotContain(code.Value[InvitationCode.Prefix.Length..], Case.Sensitive);
    }
}
