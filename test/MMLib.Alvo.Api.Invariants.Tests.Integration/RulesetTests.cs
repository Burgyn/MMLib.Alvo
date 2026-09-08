namespace MMLib.Alvo.Api.Tests.Invariants;

/// <summary>
/// <c>schema/openapi-ruleset.yaml</c> is clean against a real document, and every rule in it can be seen to
/// fail.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the second half is not optional.</b> In vacuum 0.30.3 a rule that matches nothing is
/// indistinguishable from a rule that passes: <c>field: "@key"</c> combined with <c>pattern</c> reported
/// <em>zero</em> violations for a deliberately impossible <c>match: "^ZZZ"</c> over every path key. The
/// ruleset avoids that form, but "avoids it today" is not a property a file keeps on its own — so each rule
/// is proven against a document mutated to violate exactly it, and the rule ids come out of the YAML rather
/// than out of a list restated here.
/// </para>
/// <para>
/// The document under test is <c>examples/vehicle-registry</c>'s, served over HTTP by the same world the
/// rest of the API suite uses. A hand-written fixture would let the ruleset drift into describing a document
/// Alvo no longer produces.
/// </para>
/// </remarks>
public class RulesetTests
{
    /// <summary>A key that authenticates and carries every scope, so nothing here turns on authorization.</summary>
    private static readonly TestApiKey _admin = new("admin-key", ["admin", "authenticated"], ["*:read", "*:write"]);

    /// <summary>A world that serves its OpenAPI document, which is what the static half lints.</summary>
    private static readonly AlvoApiWorldSetup _documented = new(MapOpenApiDocument: true);

    /// <summary>The mutations, one per rule id, as theory data.</summary>
    public static TheoryData<string> Mutations => [.. RulesetMutation.All.Select(mutation => mutation.RuleId)];

    /// <summary>Every rule the ruleset declares has a mutation, and every mutation names a declared rule.</summary>
    /// <remarks>
    /// Both directions, because each catches a different mistake: a rule with no mutation is a rule nothing
    /// holds, and a mutation naming a rule the ruleset no longer declares is a case that can never fail.
    /// </remarks>
    [Fact]
    public void Every_alvo_rule_has_a_mutation_that_proves_it_fires()
    {
        var declared = VacuumRunner.AlvoRuleIds();

        var proven = RulesetMutation.All
            .Select(mutation => mutation.RuleId)
            .Where(id => id.StartsWith("alvo-", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        proven.ShouldBe(
            declared,
            ignoreOrder: true,
            "a rule with no mutation is a rule nothing holds, and a mutation for a rule that no longer exists is a case that can never fail");
    }

    /// <summary>A real document reports nothing at error or warning severity.</summary>
    [Fact]
    public async Task A_real_documents_contract_holds_under_the_ruleset()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], _documented);

        var violations = VacuumRunner.Lint(await world.OpenApiDocumentAsync(), "vehicle-registry");

        violations.ShouldBeEmpty(
            $"the generated document must satisfy Alvo's own contract: {string.Join("; ", violations)}");
    }

    /// <summary>Each mutation is reported, and under its own rule id.</summary>
    /// <param name="ruleId">The rule the mutation violates.</param>
    [Theory]
    [MemberData(nameof(Mutations))]
    public async Task A_deliberate_violation_is_reported_under_its_own_rule(string ruleId)
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync([_admin], _documented);
        var document = await world.OpenApiDocumentAsync();
        RulesetMutation.All.Single(mutation => mutation.RuleId == ruleId).Apply(document);

        var violations = VacuumRunner.Lint(document, ruleId);

        violations.Select(violation => violation.Code).ShouldContain(
            ruleId,
            $"'{ruleId}' must report its own violation; vacuum reported: {string.Join("; ", violations)}");
    }
}
