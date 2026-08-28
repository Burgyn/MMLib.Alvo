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

    /// <summary>
    /// <b>A malformed entry is reported, never thrown on.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>JsonElement.TryGetProperty</c> throws on a non-object instead of answering <see langword="false"/>,
    /// and this pass walks raw input <em>before</em> the schema pass has gated anything — so a syntactically
    /// valid <c>"automation": { "deal-won": "not-an-object" }</c> took the whole validator down with an
    /// unhandled <c>InvalidOperationException</c>. <see cref="IDescriptorValidator"/>'s contract is to report
    /// on arbitrary input and never throw; the apply path is reachable from a CLI, a dashboard and an agent,
    /// so a crash there is an availability bug on caller-controlled input. Found by review, not by this fact —
    /// which is why the fact exists.
    /// </para>
    /// <para>
    /// <b>Both blocks, though one guard serves them.</b> The re-review pointed out that covering only
    /// <c>automation</c> leaves the <c>functions</c> half resting on the reader's knowledge that
    /// <c>WildcardErrorFor</c> is shared — which is exactly the kind of thing a later refactor splits without
    /// anything going red.
    /// </para>
    /// <param name="block">The top-level block the malformed entry sits in.</param>
    /// <param name="malformed">One entry that is valid JSON and not an object.</param>
    [Theory]
    [InlineData("automation", "\"not-an-object\"")]
    [InlineData("automation", "42")]
    [InlineData("automation", "null")]
    [InlineData("automation", "[]")]
    [InlineData("functions", "\"not-an-object\"")]
    [InlineData("functions", "42")]
    [InlineData("functions", "null")]
    [InlineData("functions", "[]")]
    public void A_malformed_entry_is_reported_rather_than_thrown_on(string block, string malformed)
    {
        var json = Descriptor($$"""
            "{{block}}": { "entry": {{malformed}} }
            """);

        var result = Should.NotThrow(() => new DescriptorValidator().Validate(json));

        result.Errors.ShouldNotBeEmpty($"the schema pass still has to refuse a non-object '{block}' entry");
    }

    /// <summary>
    /// <b>A null trigger, or a null entry, is not a crash on the apply path.</b>
    /// </summary>
    /// <remarks>
    /// <c>AutomationRule.Trigger</c> is <c>required</c>, but <c>System.Text.Json</c>'s <c>required</c>
    /// enforces <em>presence</em> and never non-null on a reference type — so all four shapes below parse
    /// cleanly through <c>AlvoDescriptor.Parse</c> and reached the wildcard walk as a
    /// <see cref="NullReferenceException"/> out of <c>Map</c> rather than as a structured refusal. The mapper
    /// is documented as the apply path a host that skips <see cref="IDescriptorValidator"/> still takes, so
    /// this is the same availability bug as the validator's own <c>ValueKind</c> crash. Found by review.
    /// </remarks>
    /// <param name="block">The top-level block the entry sits in.</param>
    /// <param name="entry">One entry whose trigger, or whose own value, is null.</param>
    [Theory]
    [InlineData("automation", """{ "trigger": null, "actions": [] }""")]
    [InlineData("automation", "null")]
    [InlineData("functions", """{ "script": "x.csx", "trigger": null }""")]
    [InlineData("functions", "null")]
    public void A_null_trigger_does_not_crash_the_apply_path(string block, string entry)
    {
        var json = Descriptor($$"""
            "{{block}}": { "entry": {{entry}} }
            """);

        var descriptor = AlvoDescriptor.Parse(json);

        Should.NotThrow(() => DescriptorToSchemaMapper.Map(descriptor))
            .Entities.ShouldNotBeEmpty();
    }

    /// <summary>
    /// <b>A name carrying <c>/</c> or <c>~</c> still produces a pointer that addresses the slot it refused.</b>
    /// </summary>
    /// <remarks>
    /// JSON Pointer reserves both characters (RFC 6901 §3): <c>~</c> is written <c>~0</c> and <c>/</c> is
    /// written <c>~1</c>, or the path resolves somewhere else entirely — and the schema's own
    /// <c>propertyNames</c> forbids neither in a rule name. Interpolating the raw name gave an agent or a
    /// dashboard a path to the wrong location. Caught in review.
    /// </remarks>
    /// <param name="ruleName">A rule name containing a character JSON Pointer reserves.</param>
    /// <param name="expected">The escaped token the pointer must carry.</param>
    [Theory]
    [InlineData("a/b", "a~1b")]
    [InlineData("a~b", "a~0b")]
    [InlineData("a~/b", "a~0~1b")]
    public void A_pointer_escapes_the_characters_json_pointer_reserves(string ruleName, string expected)
    {
        var json = Descriptor($$"""
            "automation": {
              "{{ruleName}}": {
                "trigger": { "event": "entity.orders.*" },
                "actions": [{ "type": "webhook", "endpoint": "invoicing" }]
              }
            }
            """);

        new DescriptorValidator().Validate(json).Errors
            .ShouldContain(error => error.Path == $"/automation/{expected}/trigger/event");
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
