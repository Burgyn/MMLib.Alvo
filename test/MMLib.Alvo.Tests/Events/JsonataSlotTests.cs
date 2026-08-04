using MMLib.Alvo.Events.Internal;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// Deviation 63's rule: a string in a <c>$defs/jsonata</c> slot is a template iff it matches
/// <c>^(?:[^{}]|\{\{[^{}]+\}\})*$</c> <b>and</b> carries at least one placeholder.
/// </summary>
/// <remarks>
/// Both clauses earn their place against <c>examples/complex-crm/crm.alvo.json</c>, and the four cases in
/// <see cref="The_four_classifier_cases_are_pinned"/> are the DoD's own list.
/// </remarks>
public class JsonataSlotTests
{
    [Theory]
    [InlineData("{\"companyIds\": records.id}", false)]
    [InlineData("$merge([new, {\"source\": \"alvo\"}])", false)]
    [InlineData("records.id", false)]
    [InlineData("{{new.title}}", true)]
    public void The_four_classifier_cases_are_pinned(string source, bool isTemplate)
        => JsonataSlot.IsTemplate(source).ShouldBe(isTemplate);

    /// <summary>
    /// The well-formedness half of the rule: one or more non-nested, non-empty placeholders and no bare brace.
    /// </summary>
    /// <remarks>
    /// The last case is what the no-bare-brace clause is <em>solely</em> load-bearing for. Measured, not
    /// reasoned: neither shipped <c>crm.alvo.json</c> payload contains <c>{{</c>, so <b>either</b> clause
    /// refuses both on its own and removing one clause leaves them green — the conjunction is proven
    /// necessary by the other two cases instead, a placeholder-embedding JSONata expression for the regex and
    /// <c>records.id</c> for the placeholder clause.
    /// </remarks>
    [Theory]
    [InlineData("Deal won: {{new.title}}", true)]
    [InlineData("{{new.title}} ({{new.amount}})", true)]
    [InlineData("{{ new.title }}", true)]
    [InlineData("{{new.{{title}}}}", false)]
    [InlineData("{{}}", false)]
    [InlineData("{{new.title}", false)]
    [InlineData("a { b", false)]
    [InlineData("", false)]
    [InlineData("$merge([new, {\"note\": \"{{new.title}}\"}])", false)]
    public void The_rule_admits_only_well_formed_non_nested_placeholders(string source, bool isTemplate)
        => JsonataSlot.IsTemplate(source).ShouldBe(isTemplate);

    /// <summary>
    /// The two units share one spelling of <c>{{</c>: a classifier that says "template" over a syntax the
    /// engine then refuses to parse would refuse a raw expression and a valid template at the same time.
    /// </summary>
    [Theory]
    [InlineData("Deal won: {{new.title}}")]
    [InlineData("{{ new.title }}")]
    [InlineData("{{new.title}}{{new.amount}}")]
    public void Everything_the_classifier_calls_a_template_the_engine_can_parse(string source)
    {
        JsonataSlot.IsTemplate(source).ShouldBeTrue();

        Should.NotThrow(() => AlvoTemplate.Parse(source));
    }
}
