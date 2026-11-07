namespace MMLib.Alvo.Schema.Tests;

/// <summary>A <c>secretRef</c> is a secret's name in the store — the schema holds it to the name grammar, never a value.</summary>
public class SecretRefSchemaTests
{
    [Theory]
    [InlineData("erp-signing")]
    [InlineData("erp.signing_key")]
    public void A_name_shaped_secret_ref_is_valid(string secretRef) =>
        Failures(secretRef).ShouldBeEmpty();

    [Theory]
    [InlineData("Erp Signing")]
    [InlineData("sk_live_51Hq+Zs/9QwErTy==")]
    [InlineData("1-starts-with-a-digit")]
    public void A_secret_ref_outside_the_name_grammar_is_invalid(string secretRef) =>
        Failures(secretRef).ShouldNotBeEmpty("a value that is not a name may be the secret itself, pasted in the wrong place");

    private static IReadOnlyList<(string Pointer, string Message)> Failures(string secretRef) =>
        SchemaValidator.Failures(SchemaValidator.Load(), $$"""
            {
              "$schema": "https://alvo.dev/schema/v1/project.json",
              "apiVersion": "alvo.dev/v1",
              "name": "secret-ref",
              "description": "One endpoint and its secret's name.",
              "webhooks": { "endpoints": { "erp": { "url": "https://erp.example/hook", "secretRef": "{{secretRef}}" } } },
              "entities": {
                "orders": { "description": "Orders.", "fields": { "total": { "type": "decimal", "precision": 12, "scale": 2 } } }
              }
            }
            """);
}
