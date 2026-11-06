using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Secrets;
using MMLib.Alvo.Secrets.Internal;
using MMLib.Alvo.Testing.Secrets;

namespace MMLib.Alvo.Tests.Secrets;

/// <summary>
/// A descriptor's secret references are checked by name: a value outside the grammar is refused at apply without being echoed,
/// a well-formed name the store does not hold yet is a warning, and the store is asked for names only.
/// </summary>
public sealed class SecretReferenceTests
{
    private const string PastedSecret = "sk_live_51Hq+Zs/9QwErTy==";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_secret_ref_outside_the_grammar_is_refused_at_apply()
    {
        var error = new DescriptorValidator().Validate(Descriptor(secretRef: "Erp Signing")).Errors.ShouldHaveSingleItem();

        error.Path.ShouldBe("#/webhooks/endpoints/erp/secretRef");
        error.Message.ShouldBe(SecretReferenceCheck.NotASecretName);
        error.FixSuggestion.ShouldBe(SecretReferenceCheck.NotASecretNameFix);
    }

    [Fact]
    public void A_secret_pasted_as_a_secret_ref_is_not_echoed()
    {
        var errors = new DescriptorValidator().Validate(Descriptor(secretRef: PastedSecret)).Errors;

        errors.ShouldNotBeEmpty();
        errors.ShouldAllBe(error => !error.Message.Contains(PastedSecret) && !(error.FixSuggestion ?? string.Empty).Contains(PastedSecret));
    }

    [Fact]
    public void A_headers_secret_ref_outside_the_grammar_is_refused()
    {
        var errors = new DescriptorValidator().Validate(Descriptor(headersSecretRef: PastedSecret)).Errors;

        var error = errors.Where(item => item.Path.EndsWith("/headersSecretRef", StringComparison.Ordinal)).ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/orders/hooks/afterCreate/0/action/headersSecretRef");
        error.Message.ShouldNotContain(PastedSecret);
        errors.ShouldAllBe(item => !item.Message.Contains(PastedSecret));
    }

    [Fact]
    public async Task A_well_formed_secret_ref_absent_from_the_store_is_a_warning()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync(SecretName.Parse("held-secret"), "held-value", Ct);

        var warnings = await SecretReferenceCheck.MissingAsync(AlvoDescriptor.Parse(Descriptor(secretRef: "erp-signing")), store, Ct);

        var warning = warnings.ShouldHaveSingleItem();
        (warning.Path, warning.Severity).ShouldBe(("/webhooks/endpoints/erp/secretRef", DescriptorValidationSeverity.Warning));
        warning.Message.ShouldContain("'erp-signing'");
        (await SecretReferenceCheck.MissingAsync(AlvoDescriptor.Parse(Descriptor(secretRef: "held-secret")), store, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_listing_failure_is_one_warning_and_no_refusal()
    {
        var warnings = await SecretReferenceCheck.MissingAsync(AlvoDescriptor.Parse(Descriptor(secretRef: "erp-signing")), new UnlistableStore(), Ct);

        var warning = warnings.ShouldHaveSingleItem();
        warning.Severity.ShouldBe(DescriptorValidationSeverity.Warning);
        warning.Message.ShouldContain(typeof(TimeoutException).FullName!);
        warning.Message.ShouldNotContain("the store's own message");
    }

    private static string Descriptor(string secretRef = "erp-signing", string? headersSecretRef = null)
    {
        var hook = headersSecretRef is null
            ? """{ "action": { "type": "webhook", "endpoint": "erp" } }"""
            : $$"""{ "action": { "type": "http.call", "url": "https://x.example/h", "headersSecretRef": "{{headersSecretRef}}" } }""";

        return $$"""
            {
              "apiVersion": "alvo.dev/v1",
              "name": "secrets",
              "webhooks": { "endpoints": { "erp": { "url": "https://erp.example/hook", "secretRef": "{{secretRef}}" } } },
              "entities": {
                "orders": {
                  "fields": { "number": { "type": "string", "maxLength": 20 } },
                  "hooks": { "afterCreate": [ {{hook}} ] }
                }
              }
            }
            """;
    }

    private sealed class UnlistableStore : ISecretStore
    {
        public bool CanWrite => false;

        public ValueTask<string?> GetAsync(SecretName name, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask SetAsync(SecretName name, string value, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<bool> DeleteAsync(SecretName name, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<SecretName>> ListNamesAsync(CancellationToken ct = default) =>
            throw new TimeoutException("the store's own message");
    }
}
