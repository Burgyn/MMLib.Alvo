using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Tenants.SelfService;
using System.Text.Json;
using System.Text.Json.Nodes;

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

        var printed = AppTokenResponse.From(view, secret.Presented, "X-Alvo-Api-Key", _now).ToString();

        printed.ShouldContain($"Token = {prefix}***");
        printed.ShouldContain(secret.KeyId);
        printed.ShouldContain("Header = X-Alvo-Api-Key");
        printed.ShouldContain("CreatedAt =");
        printed.ShouldNotContain(secret.Presented, Case.Sensitive);
        printed.ShouldNotContain(secret.Presented[(secret.KeyId.Length + 1)..], Case.Sensitive, "the secret part never prints");
        printed.ShouldContain("Status = active");
    }

    /// <summary>
    /// #542 item 1: a minted token answers exactly what the list will show of it — the same members, in the same order,
    /// with the same values — plus the credential and the header to send it in, right after the key id (S4 D11's shape for
    /// invitations, applied to tokens).
    /// </summary>
    [Fact]
    public void A_minted_token_answers_its_list_item_with_the_credential_and_its_header()
    {
        var secret = AppTokenSecret.New(ApiKeyKind.Service);
        var minter = UserId.New();
        var view = new AppTokenView(
            secret.KeyId, ApiKeyKind.Service, "billing", minter, "Ada Lovelace", new TenantId(Guid.NewGuid()), ["dispatcher"],
            ["jobs:read"], _now, _now.AddDays(7), null, null);

        var created = JsonSerializer.SerializeToNode(AppTokenResponse.From(view, secret.Presented, "X-Alvo-Api-Key", _now))!.AsObject();
        var listed = JsonSerializer.SerializeToNode(AppTokenListItem.From(view, _now))!.AsObject();

        created.Select(member => member.Key).ShouldBe(
            [listed.First().Key, "token", "header", .. listed.Skip(1).Select(member => member.Key)]);
        foreach (var (key, value) in listed)
        {
            JsonNode.DeepEquals(created[key], value).ShouldBeTrue($"'{key}' answers what the list shows");
        }

        created["token"]!.GetValue<string>().ShouldBe(secret.Presented);
        created["createdBy"]!.ToJsonString().ShouldBe($"{{\"id\":\"{minter.Value:D}\",\"name\":\"Ada Lovelace\"}}");
        created["status"]!.GetValue<string>().ShouldBe("active");
    }

    /// <summary>
    /// #542 item 1: a creator with no display name is named <c>null</c>, never left out; the anonymous preview's shape never
    /// carries an id at all.
    /// </summary>
    [Fact]
    public void A_person_without_a_display_name_is_named_null_and_the_preview_never_names_an_id()
    {
        var id = Guid.NewGuid();

        JsonSerializer.Serialize(new PersonReference(id, null)).ShouldBe($"{{\"id\":\"{id:D}\",\"name\":null}}");
        JsonSerializer.Serialize(new PersonName(null)).ShouldBe("{\"name\":null}");
    }
}
