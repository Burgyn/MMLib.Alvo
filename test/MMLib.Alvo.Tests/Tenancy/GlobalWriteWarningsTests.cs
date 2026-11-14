using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Tenancy.SelfService;

namespace MMLib.Alvo.Tests.Tenancy;

/// <summary>
/// With <c>auth.signUp.mode: open</c>, every write rule of a <c>tenancy: global</c> entity that admits
/// <c>authenticated</c> is listed at apply (spec §3.1, plan D12): those shared rows are writable by anyone who signs up.
/// </summary>
public sealed class GlobalWriteWarningsTests
{
    private const string GlobalRegions = """
        "regions": {
          "tenancy": "global",
          "fields": { "name": { "type": "string" } },
          "rules": {
            "list": "'authenticated' in @user.roles",
            "create": "'authenticated' in @user.roles",
            "update": "'admin' in @user.roles",
            "delete": "'admin' in @user.roles || 'authenticated' in @user.roles"
          }
        }
        """;

    private static string Descriptor(string mode, string entities, bool tenancy = true) =>
        $$"""
        {
          "apiVersion": "alvo.dev/v1",
          "name": "global-writes",
          "auth": { "providers": ["google"], "signUp": { "mode": "{{mode}}" } },
          "tenancy": { "enabled": {{(tenancy ? "true" : "false")}} },
          "entities": { {{entities}} }
        }
        """;

    private static IReadOnlyList<DescriptorValidationError> Warnings(string json)
    {
        var descriptor = AlvoDescriptor.Parse(json);
        return GlobalWriteWarnings.Warnings(descriptor, DescriptorToSchemaMapper.Map(descriptor), new CelCompiler());
    }

    [Fact]
    public void An_open_sign_up_lists_each_global_write_that_admits_authenticated()
    {
        var warnings = Warnings(Descriptor("open", GlobalRegions));

        warnings.Select(warning => warning.Path).ShouldBe(["/entities/regions/rules/create", "/entities/regions/rules/delete"]);
        warnings.ShouldAllBe(warning => warning.Severity == DescriptorValidationSeverity.Warning);
        warnings[0].Message.ShouldBe(
            "'regions' is tenancy: global and its create rule admits 'authenticated'; with auth.signUp.mode open, "
            + "anyone who signs up can create these shared rows.");
        warnings[0].FixSuggestion.ShouldBe("Admit a project role an operator grants instead, or make 'regions' tenancy: scoped.");
    }

    /// <summary>The apply reports the same warnings, and they refuse nothing.</summary>
    [Fact]
    public void The_validator_reports_them_as_warnings_and_accepts_the_descriptor()
    {
        var result = new DescriptorValidator().Validate(Descriptor("open", GlobalRegions));

        result.IsValid.ShouldBeTrue();
        result.Errors.Where(error => error.Path.StartsWith("/entities/regions/rules/", StringComparison.Ordinal))
            .Select(error => error.Path)
            .ShouldBe(["/entities/regions/rules/create", "/entities/regions/rules/delete"], ignoreOrder: true);
    }

    [Fact]
    public void Invitation_only_sign_up_draws_none() =>
        Warnings(Descriptor("invitationOnly", GlobalRegions)).ShouldBeEmpty();

    [Fact]
    public void A_scoped_entity_draws_none() =>
        Warnings(Descriptor("open", GlobalRegions.Replace("\"global\"", "\"scoped\"", StringComparison.Ordinal))).ShouldBeEmpty();

    /// <summary><c>global</c> exists only with tenancy, so a level-1 app draws none (S2 D17's noise concern).</summary>
    [Fact]
    public void A_level_one_descriptor_draws_none() =>
        Warnings(Descriptor("open", GlobalRegions, tenancy: false)).ShouldBeEmpty();

    [Fact]
    public void A_read_rule_draws_none() =>
        Warnings(Descriptor("open", """
            "regions": { "tenancy": "global", "fields": { "name": { "type": "string" } },
              "rules": { "list": "'authenticated' in @user.roles", "get": "'authenticated' in @user.roles" } }
            """)).ShouldBeEmpty();

    /// <summary>An owner clause admits each person to their own rows only, so it is not "admits authenticated" (D12).</summary>
    [Fact]
    public void An_owner_clause_without_a_role_draws_none() =>
        Warnings(Descriptor("open", """
            "regions": { "tenancy": "global", "audit": true, "fields": { "name": { "type": "string" } },
              "rules": { "update": "created_by == @user.id", "delete": "created_by == @user.id" } }
            """)).ShouldBeEmpty();

    [Fact]
    public void A_rule_that_does_not_compile_draws_none() =>
        Warnings(Descriptor("open", """
            "regions": { "tenancy": "global", "fields": { "name": { "type": "string" } },
              "rules": { "create": "'authenticated' in @user.roles && nope == 1" } }
            """)).ShouldBeEmpty();
}
