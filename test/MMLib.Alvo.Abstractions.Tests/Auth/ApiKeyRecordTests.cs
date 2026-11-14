using MMLib.Alvo.Auth;

namespace MMLib.Alvo.Abstractions.Tests.Auth;

public class ApiKeyRecordTests
{
    private const string Hash = "N4bQgYhMfWWaL+qgxVrQFaO/TxsrC4Is0V1sFbDwCgg=";

    [Fact]
    public void ToString_does_not_print_the_credential_hash()
    {
        var record = new ApiKeyRecord
        {
            KeyId = "dev",
            Sha256Hash = Hash,
            User = UserId.New(),
            RoleNames = ["authenticated"],
            Scopes = new HashSet<ApiKeyScope>(),
        };

        var text = record.ToString();

        text.ShouldNotContain(Hash);
        text.ShouldContain("KeyId = dev");
    }

    [Fact]
    public void A_record_defaults_to_configured_with_no_minter()
    {
        var record = new ApiKeyRecord
        {
            KeyId = "dev",
            Sha256Hash = Hash,
            User = UserId.New(),
            RoleNames = [],
            Scopes = new HashSet<ApiKeyScope>(),
        };

        record.Kind.ShouldBe(ApiKeyKind.Configured);
        record.MintedBy.ShouldBeNull();
    }

    [Fact]
    public void Printing_a_record_shows_kind_and_minter_never_the_hash()
    {
        var minter = UserId.New();
        var record = new ApiKeyRecord
        {
            KeyId = "alvo_pat_AAAAAAAAAAAAAAAAAAAAAA",
            Sha256Hash = Hash,
            User = minter,
            RoleNames = [],
            Scopes = new HashSet<ApiKeyScope>(),
            Kind = ApiKeyKind.Personal,
            MintedBy = minter,
        };

        var text = record.ToString();

        text.ShouldContain("Kind = Personal");
        text.ShouldContain($"MintedBy = {minter}");
        text.ShouldNotContain(Hash);
    }
}
