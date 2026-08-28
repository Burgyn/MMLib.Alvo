using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// A wildcard subscription — <c>entity.orders.*</c> — is refused when the descriptor is applied, on both
/// passes, and an exact pattern still applies.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ruling, so a reader of these facts does not have to reconstruct it.</b>
/// <c>alvo-specifikacia.md:141</c> makes the wildcard a hard guarantee and <c>baas-analyza.md:657</c> makes
/// tenant isolation of rules a watch-out; <c>docs/architecture/events.md</c> resolves the pair by requiring
/// either a matcher with every subscription scoped to the envelope's tenant <em>and</em> a named adversarial
/// cross-tenant fact, or a refusal at apply until that exists. The first branch is unavailable — an
/// <c>AlvoEvent</c> carries no tenant attribute at all (#153), so nothing at delivery could scope a
/// subscription and the adversarial fact would have no tenant on either side of its comparison. These facts
/// hold the second branch.
/// </para>
/// <para>
/// <b>Both passes are asserted from one descriptor string</b>, because the two exist to catch the same
/// declaration from two directions: the typed pass is what an embedded host reaches through
/// <c>FromDescriptor</c>, and the raw-JSON pass is what gives a CLI or an agent a pointer and a fix. A fact
/// that only drove one would let the other be deleted silently.
/// </para>
/// </remarks>
public sealed class WildcardSubscriptionTests
{
    [Theory]
    [InlineData("entity.orders.*")]
    [InlineData("entity.*.created")]
    [InlineData("entity.*.*")]
    [InlineData("entity.orders.*.batch")]
    public void A_wildcard_automation_trigger_is_refused_at_apply(string pattern)
        => Refusal(WithAutomation(pattern))
            .Message.ShouldContain(UnhonouredFeatures.WildcardSubscription.Fix);

    [Fact]
    public void A_wildcard_function_trigger_is_refused_at_apply()
        => Refusal(WithFunction("entity.orders.*"))
            .Message.ShouldContain(UnhonouredFeatures.WildcardSubscription.Fix);

    /// <summary>The refusal names the rule that declared it, so an author knows which line to edit.</summary>
    [Fact]
    public void The_refusal_names_the_rule_and_the_pattern()
    {
        var refusal = Refusal(WithAutomation("entity.orders.*"));

        refusal.Message.ShouldContain("deal-won");
        refusal.Message.ShouldContain("entity.orders.*");
    }

    /// <summary>
    /// An exact pattern still applies, so the refusal is about the wildcard and not about subscriptions.
    /// </summary>
    /// <remarks>
    /// This is the fact a refusal that simply threw for any <c>trigger.event</c> would fail, and it is why
    /// <c>complex-crm</c>'s two exact triggers still reach their own unhonoured-feature refusal rather than
    /// this one.
    /// </remarks>
    [Theory]
    [InlineData("entity.deals.updated")]
    [InlineData("entity.companies.created.batch")]
    public void A_pattern_without_a_wildcard_still_applies(string pattern)
        => DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(WithAutomation(pattern)))
            .Entities.ShouldNotBeEmpty();

    /// <summary>
    /// The same declaration is reported as a structured error, with the JSON Pointer of the slot and the same
    /// fix suggestion the exception carries.
    /// </summary>
    [Theory]
    [InlineData("automation", "/automation/deal-won/trigger/event")]
    [InlineData("functions", "/functions/reindex/trigger/event")]
    public void A_wildcard_trigger_is_reported_as_a_structured_error(string block, string slotPath)
    {
        var json = string.Equals(block, "automation", StringComparison.Ordinal)
            ? WithAutomation("entity.orders.*")
            : WithFunction("entity.orders.*");

        var error = new DescriptorValidator().Validate(json).Errors
            .ShouldHaveSingleItem(
                "a wildcard trigger is one defect and must be reported once, on the slot that declares it");

        error.Path.ShouldBe(slotPath);
        error.Severity.ShouldBe(DescriptorValidationSeverity.Error);
        error.FixSuggestion.ShouldBe(UnhonouredFeatures.WildcardSubscription.Fix);
    }

    private static InvalidDataException Refusal(string descriptorJson)
        => Should.Throw<InvalidDataException>(
            () => DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(descriptorJson)));

    private static string WithAutomation(string pattern) => Descriptor($$"""
        "automation": {
          "deal-won": {
            "trigger": { "event": "{{pattern}}" },
            "actions": [{ "type": "webhook", "endpoint": "invoicing" }]
          }
        }
        """);

    private static string WithFunction(string pattern) => Descriptor($$"""
        "functions": {
          "reindex": {
            "script": "reindex.csx",
            "trigger": { "event": "{{pattern}}" }
          }
        }
        """);

    /// <summary>
    /// One minimal, otherwise-clean descriptor, so the only thing a fact here can be refused for is its
    /// trigger.
    /// </summary>
    /// <param name="block">The top-level block under test, already serialized.</param>
    private static string Descriptor(string block) => $$"""
        {
          "apiVersion": "alvo.dev/v1",
          "name": "wildcards",
          "entities": {
            "orders": {
              "fields": { "title": { "type": "string" } }
            }
          },
          {{block}}
        }
        """;
}
