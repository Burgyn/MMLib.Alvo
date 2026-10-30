using MMLib.Alvo.InboundWebhooks.Internal;
using System.Text;

namespace MMLib.Alvo.Tests.InboundWebhooks;

/// <summary>The Standard Webhooks signature (acceptance (4), first half): <c>v1,</c> HMAC-SHA256 over <c>id.timestamp.body</c>.</summary>
public sealed class StandardWebhookSignatureTests
{
    private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private const string Id = "msg_p5jXN8AQM9LWM0D4loKWxJek";
    private const string Timestamp = "1614265330";
    private static readonly byte[] _body = Encoding.UTF8.GetBytes("{\"test\": 2432232314}");

    /// <summary>
    /// The reference libraries' own test vector. If this fails the implementation is wrong, not the vector: the signed
    /// content is <c>{id}.{timestamp}.{body}</c> and the key is the base64-decoded secret after <c>whsec_</c>.
    /// </summary>
    [Fact]
    public void A_reference_signature_verifies()
    {
        var keys = StandardWebhookSignature.Keys(Secret);

        StandardWebhookSignature.Sign(keys[0], Id, Timestamp, _body).ShouldBe("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=");
        StandardWebhookSignature.Verifies(keys, Id, Timestamp, _body, "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=").ShouldBeTrue();
    }

    [Fact]
    public void A_body_changed_by_one_byte_is_refused()
    {
        var tampered = (byte[])_body.Clone();
        tampered[^2]++;

        StandardWebhookSignature.Verifies(StandardWebhookSignature.Keys(Secret), Id, Timestamp, tampered, "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=").ShouldBeFalse();
    }

    [Fact]
    public void Another_id_or_timestamp_is_refused()
    {
        var keys = StandardWebhookSignature.Keys(Secret);
        const string Header = "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=";

        StandardWebhookSignature.Verifies(keys, "msg_other", Timestamp, _body, Header).ShouldBeFalse();
        StandardWebhookSignature.Verifies(keys, Id, "1614265331", _body, Header).ShouldBeFalse();
    }

    [Fact]
    public void A_header_with_an_old_and_a_rotated_signature_verifies_on_the_rotated_one()
    {
        var rotated = StandardWebhookSignature.Keys("whsec_" + Convert.ToBase64String(Encoding.UTF8.GetBytes("the-rotated-secret-of-32-bytes!!")));
        var signedWithRotated = StandardWebhookSignature.Sign(rotated[0], Id, Timestamp, _body);

        StandardWebhookSignature.Verifies(rotated, Id, Timestamp, _body, $"v1,c3RhbGU= {signedWithRotated}").ShouldBeTrue();
    }

    [Fact]
    public void A_stored_value_holding_two_secrets_accepts_a_signature_by_either()
    {
        var other = "whsec_" + Convert.ToBase64String(Encoding.UTF8.GetBytes("the-next-secret-of-thirty-two-b!"));
        var keys = StandardWebhookSignature.Keys($"{other} {Secret}");

        StandardWebhookSignature.Verifies(keys, Id, Timestamp, _body, "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=").ShouldBeTrue();
    }

    [Fact]
    public void Entries_of_another_version_are_ignored() =>
        StandardWebhookSignature.Verifies(StandardWebhookSignature.Keys(Secret), Id, Timestamp, _body, "v1a,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=").ShouldBeFalse();

    /// <summary>An empty key is one anyone can sign with: a stored value that decodes to none is unusable, not a key.</summary>
    [Theory]
    [InlineData("whsec_")]
    [InlineData("   ")]
    [InlineData("whsec_ whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw")]
    public void A_stored_value_holding_an_empty_secret_or_none_is_unusable(string stored) =>
        Should.Throw<FormatException>(() => StandardWebhookSignature.Keys(stored));

    /// <summary>Ruling 54: Standard Webhooks' secrets are 24 to 64 bytes; shorter is refused, longer is not.</summary>
    [Theory]
    [InlineData(23, false)]
    [InlineData(24, true)]
    [InlineData(65, true)]
    public void A_key_shorter_than_24_bytes_is_refused_and_a_longer_one_is_not(int length, bool accepted)
    {
        var stored = "whsec_" + Convert.ToBase64String(Enumerable.Repeat((byte)7, length).ToArray());

        if (accepted)
        {
            StandardWebhookSignature.Keys(stored).ShouldHaveSingleItem().Length.ShouldBe(length);
        }
        else
        {
            var refusal = Should.Throw<FormatException>(() => StandardWebhookSignature.Keys(stored));
            refusal.Message.ShouldContain("24 bytes");
            refusal.Message.ShouldNotContain(stored["whsec_".Length..], Case.Sensitive, "the reason names no key");
        }
    }

    [Fact]
    public void A_value_holding_one_short_secret_beside_a_good_one_is_refused_whole() =>
        Should.Throw<FormatException>(() => StandardWebhookSignature.Keys($"{Secret} whsec_{Convert.ToBase64String(new byte[16])}"));

    [Theory]
    [InlineData("")]
    [InlineData("v1,")]
    [InlineData("v1,not-base64!")]
    [InlineData("garbage")]
    public void A_malformed_header_verifies_nothing_and_throws_nothing(string header) =>
        StandardWebhookSignature.Verifies(StandardWebhookSignature.Keys(Secret), Id, Timestamp, _body, header).ShouldBeFalse();
}
