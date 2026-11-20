namespace MMLib.Alvo.Schema.Tests;

/// <summary>
/// <c>auth.accessTokens</c> (spec §3.1): a closed object of three keys, <c>maxLifetimeDays</c> 1–365 — and no cross-key
/// rule, which is the apply's (<c>AccessTokensCheck</c>), so the fault is reported once.
/// </summary>
/// <remarks>
/// The apply refusal the schema deliberately accepts is the negative example
/// <c>service-tokens-without-shared-tenants.json</c>; the facts here cover the shape the schema itself owns.
/// </remarks>
public class AccessTokensSchemaTests
{
    private static string Descriptor(string accessTokens) =>
        $$"""
        {
          "apiVersion": "alvo.dev/v1",
          "name": "tokens",
          "auth": { "providers": ["google"], "signUp": { "mode": "open" }, "accessTokens": {{accessTokens}} },
          "entities": { "jobs": { "fields": { "title": { "type": "string" } } } }
        }
        """;

    private static IReadOnlyList<(string Pointer, string Message)> Failures(string accessTokens) =>
        SchemaValidator.Failures(SchemaValidator.Load(), Descriptor(accessTokens));

    [Fact]
    public void All_three_keys_validate() =>
        Failures("""{ "personal": true, "service": true, "maxLifetimeDays": 365 }""").ShouldBeEmpty();

    [Fact]
    public void An_empty_block_validates() =>
        Failures("{}").ShouldBeEmpty();

    [Fact]
    public void Service_without_self_service_is_the_apply_refusal_not_the_schema_one() =>
        Failures("""{ "service": true }""").ShouldBeEmpty();

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public void Max_lifetime_outside_one_to_three_hundred_sixty_five_is_refused(int days) =>
        Failures($$"""{ "maxLifetimeDays": {{days}} }""")
            .ShouldContain(failure => failure.Pointer == "#/auth/accessTokens/maxLifetimeDays");

    [Fact]
    public void An_unknown_key_is_refused() =>
        Failures("""{ "personal": true, "scopes": ["*"] }""")
            .ShouldContain(failure => failure.Pointer.StartsWith("#/auth/accessTokens", StringComparison.Ordinal));

    [Theory]
    [InlineData("""{ "personal": "yes" }""", "#/auth/accessTokens/personal")]
    [InlineData("""{ "maxLifetimeDays": 1.5 }""", "#/auth/accessTokens/maxLifetimeDays")]
    public void Each_key_holds_its_type(string accessTokens, string at) =>
        Failures(accessTokens).ShouldContain(failure => failure.Pointer == at);
}
