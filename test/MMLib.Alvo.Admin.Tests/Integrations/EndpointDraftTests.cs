using MMLib.Alvo.Admin.Components.Integrations;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Tests.Integrations;

/// <summary>What the endpoint sheet refuses at its fields — the apply's rules, said where they can be acted on (spec §6.1).</summary>
public class EndpointDraftTests
{
    [Theory]
    [InlineData("billing-system", null)]
    [InlineData("a", null)]
    [InlineData("Billing", "not an endpoint name")]
    [InlineData("billing_system", "not an endpoint name")]
    [InlineData("9lives", "not an endpoint name")]
    [InlineData("rental-desk", "already declared")]
    [InlineData("čaj", "not an endpoint name")]
    public void A_name_is_the_schemas_endpoint_key_and_unique(string name, string? refused)
    {
        var refusal = EndpointDraft.NameRefusal(name, ["rental-desk"]);

        if (refused is null)
        {
            refusal.ShouldBeNull();
        }
        else
        {
            refusal.ShouldNotBeNull().ShouldContain(refused);
        }
    }

    [Theory]
    [InlineData("https://billing.example/hooks", true)]
    [InlineData("http://localhost:5081/hooks", true)]
    [InlineData("http://127.0.0.1:5081/hooks/rentals", true)]
    [InlineData("http://[::1]:5081/hooks", true)]
    [InlineData("http://example.com/hooks", false)]
    [InlineData("ftp://example.com/x", false)]
    [InlineData("/hooks/relative", false)]
    [InlineData("htp://x", false)]
    [InlineData("", false)]
    public void A_url_is_accepted_exactly_as_the_apply_accepts_it(string url, bool accepted)
        => (EndpointDraft.UrlRefusal(url) is null).ShouldBe(accepted);

    [Fact]
    public void A_cleartext_url_is_refused_with_the_reason_and_without_echoing_the_url()
    {
        var refusal = EndpointDraft.UrlRefusal("http://example.com/hooks?token=s3cr3t");

        refusal.ShouldNotBeNull().ShouldContain("https");
        refusal.ShouldNotContain("s3cr3t", Case.Sensitive, "a URL can be its own bearer secret (events.md)");
    }

    [Theory]
    [InlineData("rental-desk-signing-key", true)]
    [InlineData("billing.key", true)]
    [InlineData("Sup3r+Secret/Value==", false)]
    [InlineData("", false)]
    [InlineData("whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw", false)]
    public void A_secret_name_follows_the_stores_own_name_rule(string secret, bool accepted)
        => (EndpointDraft.SecretRefusal(secret) is null).ShouldBe(accepted);

    [Fact]
    public void A_refused_secret_name_never_echoes_what_was_pasted()
        => EndpointDraft.SecretRefusal("Sup3r+Secret/Value==").ShouldNotBeNull().ShouldNotContain("Sup3r");

    [Fact]
    public void A_refused_name_never_echoes_what_was_pasted()
        => EndpointDraft.NameRefusal("https://billing.example/hooks?token=s3cr3t", ["rental-desk"])
            .ShouldNotBeNull().ShouldNotContain("s3cr3t", Case.Sensitive, "a URL or a secret pasted into the wrong box is not repeated");

    [Fact]
    public void The_secret_name_follows_the_endpoint_name_until_it_is_typed()
    {
        var draft = new EndpointDraft();

        draft.TypeName("billing");
        draft.SecretRef.ShouldBe("billing-signing-key");
        draft.TypeSecret("vault.billing");
        draft.TypeName("billing-system");
        draft.SecretRef.ShouldBe("vault.billing");
    }

    [Fact]
    public void Every_refusal_names_the_field_it_belongs_to_in_the_order_the_sheet_draws_them()
    {
        var draft = new EndpointDraft { Name = "Bad", Url = "http://example.com", SecretRef = "Bad Secret" };

        draft.Refusals(["rental-desk"], editing: false).Select(refusal => refusal.Key)
            .ShouldBe([EndpointDraft.NameField, EndpointDraft.UrlField, EndpointDraft.SecretField]);
    }

    [Fact]
    public void An_edit_does_not_refuse_its_own_name()
    {
        var draft = EndpointDraft.From("rental-desk", JsonDocument.Parse("""{"url":"http://127.0.0.1:5081/h","secretRef":"rental-desk-signing-key"}""").RootElement);

        draft.Refusals(["rental-desk"], editing: true).ShouldBeEmpty();
        (draft.Url, draft.SecretRef, draft.Description).ShouldBe(("http://127.0.0.1:5081/h", "rental-desk-signing-key", string.Empty));
    }
}
