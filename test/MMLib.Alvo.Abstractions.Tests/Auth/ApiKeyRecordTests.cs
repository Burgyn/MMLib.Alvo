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
}
