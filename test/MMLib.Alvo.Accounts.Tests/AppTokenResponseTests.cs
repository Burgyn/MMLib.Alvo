using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Tenants.SelfService;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>
/// The 201 of <c>POST {prefix}/tokens</c> is the one value that holds a credential, so its <c>ToString</c> — what a log
/// line, an exception message or a debugger shows — masks it: the kind's prefix and <c>***</c>, never the secret.
/// </summary>
public sealed class AppTokenResponseTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ApiKeyKind.Personal, "alvo_pat_")]
    [InlineData(ApiKeyKind.Service, "alvo_svc_")]
    public void Printing_the_response_masks_the_credential_and_keeps_the_key_id(ApiKeyKind kind, string prefix)
    {
        var secret = AppTokenSecret.New(kind);
        var view = new AppTokenView(
            secret.KeyId, kind, "integration", UserId.New(), null, new TenantId(Guid.NewGuid()), [], ["jobs:read"], _now, _now.AddDays(7), null, null);

        var printed = AppTokenResponse.From(view, secret.Presented, "X-Alvo-Api-Key").ToString();

        printed.ShouldContain($"Token = {prefix}***");
        printed.ShouldContain(secret.KeyId);
        printed.ShouldContain("Header = X-Alvo-Api-Key");
        printed.ShouldContain("CreatedAt =");
        printed.ShouldNotContain(secret.Presented, Case.Sensitive);
        printed.ShouldNotContain(secret.Presented[(secret.KeyId.Length + 1)..], Case.Sensitive, "the secret part never prints");
    }
}
