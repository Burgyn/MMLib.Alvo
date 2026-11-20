namespace MMLib.Alvo.Schema.Tests;

/// <summary>
/// <c>tenancy.selfService</c> (spec §3.1): a closed object of four keys, <c>maxPerAccount</c> 1–100, role lists of
/// identifiers — and no cross-key rule, which is the apply's (<c>SelfServiceCheck</c>), so each fault is reported once.
/// </summary>
/// <remarks>
/// The two apply refusals the schema deliberately accepts are the negative examples
/// <c>self-service-without-tenancy.json</c> and <c>self-service-admin-assignable.json</c>; the facts here cover the
/// shape the schema itself owns.
/// </remarks>
public class SelfServiceSchemaTests
{
    private static string Descriptor(string selfService) =>
        $$"""
        {
          "apiVersion": "alvo.dev/v1",
          "name": "self-service",
          "auth": { "providers": ["google"], "roles": ["dispatcher", "technician"], "signUp": { "mode": "open" } },
          "tenancy": { "enabled": true, "selfService": {{selfService}} },
          "entities": { "jobs": { "fields": { "title": { "type": "string" } } } }
        }
        """;

    private static IReadOnlyList<(string Pointer, string Message)> Failures(string selfService) =>
        SchemaValidator.Failures(SchemaValidator.Load(), Descriptor(selfService));

    [Fact]
    public void All_four_keys_validate() =>
        Failures("""
            {
              "personalTenant": { "roles": ["technician"] },
              "createTenants": { "creatorRoles": ["dispatcher"], "maxPerAccount": 100 },
              "invitations": true,
              "assignableRoles": ["dispatcher", "technician"]
            }
            """).ShouldBeEmpty();

    [Fact]
    public void An_empty_self_service_block_validates() =>
        Failures("{}").ShouldBeEmpty();

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Max_per_account_outside_one_to_a_hundred_is_refused(int max) =>
        Failures($$"""{ "createTenants": { "maxPerAccount": {{max}} } }""")
            .ShouldContain(failure => failure.Pointer == "#/tenancy/selfService/createTenants/maxPerAccount");

    [Theory]
    [InlineData("""{ "invites": true }""", "#/tenancy/selfService")]
    [InlineData("""{ "personalTenant": { "role": [] } }""", "#/tenancy/selfService/personalTenant")]
    [InlineData("""{ "createTenants": { "max": 3 } }""", "#/tenancy/selfService/createTenants")]
    public void An_unknown_key_is_refused(string selfService, string at) =>
        Failures(selfService).ShouldContain(failure => failure.Pointer.StartsWith(at, StringComparison.Ordinal));

    [Theory]
    [InlineData("""{ "assignableRoles": ["Dispatcher"] }""", "#/tenancy/selfService/assignableRoles/0")]
    [InlineData("""{ "createTenants": { "creatorRoles": ["not a role"] } }""", "#/tenancy/selfService/createTenants/creatorRoles/0")]
    public void A_role_list_holds_identifiers(string selfService, string at) =>
        Failures(selfService).ShouldContain(failure => failure.Pointer == at);

    [Theory]
    [InlineData("""{ "assignableRoles": ["dispatcher", "dispatcher"] }""", "#/tenancy/selfService/assignableRoles")]
    [InlineData("""{ "personalTenant": { "roles": ["technician", "technician"] } }""", "#/tenancy/selfService/personalTenant/roles")]
    [InlineData("""{ "createTenants": { "creatorRoles": ["dispatcher", "dispatcher"] } }""", "#/tenancy/selfService/createTenants/creatorRoles")]
    public void A_role_list_holds_each_role_once(string selfService, string at) =>
        Failures(selfService).ShouldContain(failure =>
            failure.Pointer.StartsWith(at, StringComparison.Ordinal) && failure.Message.Contains("uniqueItems", StringComparison.Ordinal));
}
